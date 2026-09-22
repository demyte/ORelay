using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ORelay.Discovery;

/// <summary>The source used to choose a worktree callback host.</summary>
public enum CallbackDiscoveryMode
{
    Local,
    Tailscale,
    None,
}

/// <summary>The reason a callback address could not be selected.</summary>
public enum CallbackDiscoveryFailureCode
{
    InvalidExplicitUrl,
    InvalidHostname,
    MissingEndpoint,
    InvalidEndpoint,
    IncompatibleBinding,
    TailscaleUnavailable,
    TailscaleAmbiguous,
    TailscaleUnsupported,
    InvalidCallbackPath,
}

/// <summary>A URI allocated by Aspire for the application receiving the callback.</summary>
public sealed record AspireApplicationEndpoint
{
    public AspireApplicationEndpoint(Uri address, bool isLoopbackOnly = false)
    {
        Address = address ?? throw new ArgumentNullException(nameof(address));
        IsLoopbackOnly = isLoopbackOnly;
    }

    public AspireApplicationEndpoint(string address, bool isLoopbackOnly = false)
        : this(CreateAddress(address), isLoopbackOnly)
    {
    }

    public Uri Address { get; }

    /// <summary>
    /// Set this when the application is known to listen only on loopback.
    /// The allocated URI alone does not establish the application's binding.
    /// </summary>
    public bool IsLoopbackOnly { get; }

    private static Uri CreateAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? uri
            : throw new ArgumentException("The application endpoint must be an absolute URI.", nameof(address));
}

/// <summary>Inputs owned by a worktree when it resolves its browser callback URL.</summary>
public sealed record CallbackDiscoveryRequest
{
    public CallbackDiscoveryMode Mode { get; init; } = CallbackDiscoveryMode.Local;

    /// <summary>An exact HTTP(S) callback URL. It takes precedence over every other input.</summary>
    public string? ExplicitUrl { get; init; }

    /// <summary>A host override combined with the endpoint scheme, port, and path.</summary>
    public string? Hostname { get; init; }

    public AspireApplicationEndpoint? AspireEndpoint { get; init; }

    /// <summary>
    /// An optional callback path. When absent, the allocated endpoint path is
    /// preserved. The path must start with '/'.
    /// </summary>
    public string? CallbackPath { get; init; }

    /// <summary>Optional process-boundary provider used when Mode is Tailscale.</summary>
    public ITailscaleStatusProvider? TailscaleProvider { get; init; }
}

/// <summary>A candidate host returned by Tailscale discovery.</summary>
public sealed record TailscaleCandidate(string Host, string Source);

/// <summary>A normalized snapshot of the local machine's Tailscale status.</summary>
public sealed record TailscaleStatusSnapshot(
    bool Available,
    string? BackendState,
    string? DnsName,
    string? HostName,
    string[] Addresses,
    string? Error = null)
{
    public static TailscaleStatusSnapshot Unavailable(string error) =>
        new(false, null, null, null, Array.Empty<string>(), error);

    public IReadOnlyList<TailscaleCandidate> Candidates
    {
        get
        {
            var candidates = new List<TailscaleCandidate>();
            Add(DnsName, "dnsName");
            Add(HostName, "hostName");
            foreach (var address in Addresses)
            {
                Add(address, "tailscaleIp");
            }

            return candidates;

            void Add(string? value, string source)
            {
                var normalized = value?.Trim().TrimEnd('.');
                if (string.IsNullOrWhiteSpace(normalized) ||
                    !CallbackAddressDiscovery.IsUsableHost(normalized) ||
                    candidates.Any(candidate => string.Equals(candidate.Host, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                candidates.Add(new TailscaleCandidate(normalized, source));
            }
        }
    }
}

/// <summary>Runs Tailscale discovery without coupling the hosting package to an executable.</summary>
public interface ITailscaleStatusProvider
{
    Task<TailscaleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>The public result returned to an Aspire host or another caller.</summary>
public sealed record CallbackDiscoveryResult(
    bool Succeeded,
    string? CallbackUrl,
    string Source,
    string? ErrorCode,
    string? Message,
    string? NextStep,
    string[] Candidates)
{
    public static CallbackDiscoveryResult Success(Uri callbackUrl, string source) =>
        new(true, callbackUrl.AbsoluteUri, source, null, null, null, Array.Empty<string>());

    public static CallbackDiscoveryResult Failure(
        CallbackDiscoveryFailureCode code,
        string message,
        string nextStep,
        IEnumerable<string>? candidates = null) =>
        new(false, null, "none", code.ToString(), message, nextStep, candidates?.ToArray() ?? Array.Empty<string>());
}

/// <summary>Resolves the exact callback destination for one worktree.</summary>
public sealed class CallbackAddressDiscovery
{
    private readonly ITailscaleStatusProvider _tailscaleProvider;

    public CallbackAddressDiscovery(ITailscaleStatusProvider? tailscaleProvider = null)
    {
        _tailscaleProvider = tailscaleProvider ?? new TailscaleStatusProcessProvider();
    }

    public async Task<CallbackDiscoveryResult> ResolveAsync(
        CallbackDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.ExplicitUrl))
        {
            return TryBuildExplicitUrl(request.ExplicitUrl!);
        }

        if (!TryValidateCallbackPath(request.CallbackPath, out var callbackPath, out var pathError))
        {
            return CallbackDiscoveryResult.Failure(
                CallbackDiscoveryFailureCode.InvalidCallbackPath,
                pathError!,
                "Set callbackPath to an absolute path such as /oauth/callback.");
        }

        var endpoint = request.AspireEndpoint;
        if (endpoint is null)
        {
            return CallbackDiscoveryResult.Failure(
                CallbackDiscoveryFailureCode.MissingEndpoint,
                "An Aspire application endpoint is required when no full callback URL is supplied.",
                "Provide an allocated endpoint or set an explicit callback URL.");
        }

        if (!TryValidateEndpoint(endpoint.Address, out var endpointError))
        {
            return CallbackDiscoveryResult.Failure(
                CallbackDiscoveryFailureCode.InvalidEndpoint,
                endpointError!,
                "Provide the actual HTTP(S) endpoint allocated to the worktree.");
        }

        var hostname = request.Hostname?.Trim();
        string source;
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            if (!IsUsableHost(hostname) || IsWildcardHost(hostname))
            {
                return CallbackDiscoveryResult.Failure(
                    CallbackDiscoveryFailureCode.InvalidHostname,
                    "The hostname override is not a valid host name or IP address.",
                    "Use a DNS name, IPv4 address, or IPv6 address without a scheme or path.");
            }

            source = "hostname-override";
        }
        else if (request.Mode == CallbackDiscoveryMode.Tailscale)
        {
            var provider = request.TailscaleProvider ?? _tailscaleProvider;
            var status = await provider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.Available)
            {
                return CallbackDiscoveryResult.Failure(
                    CallbackDiscoveryFailureCode.TailscaleUnavailable,
                    status.Error ?? "Tailscale status is unavailable on the worktree host.",
                    "Install or start Tailscale on the worktree host, or provide an explicit callback URL or hostname.");
            }

            var candidates = status.Candidates;
            if (candidates.Count == 0)
            {
                return CallbackDiscoveryResult.Failure(
                    CallbackDiscoveryFailureCode.TailscaleUnsupported,
                    "Tailscale returned no usable local hostname or address.",
                    "Provide an explicit callback URL or hostname.");
            }

            if (candidates.Count > 1 &&
                !string.Equals(candidates[0].Source, "dnsName", StringComparison.Ordinal) &&
                !string.Equals(candidates[0].Source, "hostName", StringComparison.Ordinal))
            {
                return CallbackDiscoveryResult.Failure(
                    CallbackDiscoveryFailureCode.TailscaleAmbiguous,
                    "Tailscale returned more than one address and no preferred hostname.",
                    "Set an explicit callback hostname or URL.",
                    candidates.Select(candidate => candidate.Host));
            }

            hostname = candidates[0].Host;
            source = "tailscale";
        }
        else
        {
            hostname = endpoint.Address.Host;
            source = request.Mode == CallbackDiscoveryMode.None ? "endpoint" : "local";
        }

        if (!IsUsableHost(hostname))
        {
            return CallbackDiscoveryResult.Failure(
                CallbackDiscoveryFailureCode.InvalidEndpoint,
                "The endpoint does not contain a usable callback host.",
                "Use an endpoint with a DNS name or IP address, or provide an explicit hostname.");
        }

        // An allocated Aspire URL often uses localhost even when the
        // application listens on a shared interface. Only the integration
        // can know the actual listener binding, so it must opt in with
        // IsLoopbackOnly instead of this resolver guessing from the URL.
        var endpointIsLoopback = endpoint.IsLoopbackOnly;
        if (endpointIsLoopback && !IsLoopbackHost(hostname))
        {
            return CallbackDiscoveryResult.Failure(
                CallbackDiscoveryFailureCode.IncompatibleBinding,
                "A remote callback hostname was selected for an application endpoint bound only to loopback.",
                "Bind the application to a reachable interface, or use a loopback callback URL.");
        }

        var builder = new UriBuilder(endpoint.Address)
        {
            Host = hostname,
            Path = callbackPath ?? NormalizePath(endpoint.Address.AbsolutePath),
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return CallbackDiscoveryResult.Success(builder.Uri, source);
    }

    private static CallbackDiscoveryResult TryBuildExplicitUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !IsUsableHost(uri.Host) ||
            string.IsNullOrEmpty(uri.AbsolutePath) ||
            uri.Port is 0 or > 65_535 ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            IsWildcardHost(uri.Host))
        {
            return CallbackDiscoveryResult.Failure(
                CallbackDiscoveryFailureCode.InvalidExplicitUrl,
                "The explicit callback URL must be an HTTP(S) URL with a host and path, without credentials, query, or fragment.",
                "Set an exact URL such as http://127.0.0.1:5000/oauth/callback.");
        }

        return CallbackDiscoveryResult.Success(uri, "explicit-url");
    }

    private static bool TryValidateEndpoint(Uri endpoint, out string? error)
    {
        if (!endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
            !IsUsableHost(endpoint.Host) || endpoint.UserInfo.Length != 0 ||
            endpoint.Port is 0 or > 65_535 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || IsWildcardHost(endpoint.Host))
        {
            error = "The Aspire endpoint must be an HTTP(S) URL with a usable host, without credentials, query, or fragment.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateCallbackPath(string? value, out string? path, out string? error)
    {
        if (value is null)
        {
            path = null;
            error = null;
            return true;
        }

        path = NormalizePath(value);
        if (value.Length == 0 || value[0] != '/' || value.Contains('?') || value.Contains('#'))
        {
            path = null;
            error = "The callback path must start with '/' and must not contain a query or fragment.";
            return false;
        }

        error = null;
        return true;
    }

    private static string NormalizePath(string path) => string.IsNullOrEmpty(path) ? "/" : path;

    internal static bool IsUsableHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Contains('/') ||
            host.Contains('?') || host.Contains('#') || IsWildcardHost(host))
        {
            return false;
        }

        var normalized = host.Trim('[', ']');
        return IPAddress.TryParse(normalized, out _) ||
            Uri.CheckHostName(normalized) == UriHostNameType.Dns;
    }

    private static bool IsIpv6Host(string? host) =>
        IPAddress.TryParse(host?.Trim('[', ']'), out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    private static bool IsLoopbackHost(string? host) =>
        host is not null &&
        (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static bool IsWildcardHost(string host)
    {
        var normalized = host.Trim().Trim('[', ']');
        return normalized is "0.0.0.0" or "::" or "*" or "+";
    }
}

/// <summary>Source-generated metadata for Tailscale's status JSON.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TailscaleStatusDocument))]
internal sealed partial class TailscaleJsonContext : JsonSerializerContext;

internal sealed class TailscaleStatusDocument
{
    [JsonPropertyName("BackendState")]
    public string? BackendState { get; set; }

    [JsonPropertyName("Self")]
    public TailscaleSelfDocument? Self { get; set; }
}

internal sealed class TailscaleSelfDocument
{
    [JsonPropertyName("HostName")]
    public string? HostName { get; set; }

    [JsonPropertyName("DNSName")]
    public string? DnsName { get; set; }

    [JsonPropertyName("TailscaleIPs")]
    public string[]? TailscaleIps { get; set; }
}
