using System.Net;
using ORelay.Configuration;

namespace ORelay.Server;

/// <summary>
/// Settings needed by the relay HTTP surface. The CLI/configuration layer owns
/// loading these values and can map its settings type to this small contract.
/// </summary>
public sealed class RelayServerOptions
{
    public const int DefaultPort = 12987;
    public const string DefaultBind = "127.0.0.1";
    public const int DefaultLeaseSeconds = 300;
    public const int DefaultMaxRegistrations = 1000;
    public const string DefaultCallbackPath = "/callback";

    public int Port { get; init; } = DefaultPort;

    public string Bind { get; init; } = DefaultBind;

    /// <summary>
    /// The public base URL used to construct the fixed callback URL. It is
    /// intentionally separate from <see cref="Bind"/>. A value that already
    /// ends in <see cref="CallbackPath"/> is treated as the complete callback
    /// URL; otherwise the callback path is appended.
    /// </summary>
    public Uri? PublicUrl { get; init; }

    public string? Hostname { get; init; }

    public string AutoDiscovery { get; init; } = "none";

    public int LeaseSeconds { get; init; } = DefaultLeaseSeconds;

    public int MaxRegistrations { get; init; } = DefaultMaxRegistrations;

    /// <summary>File owned by this relay instance for durable registrations.</summary>
    public string? RegistrationDatabasePath { get; init; }

    public string CallbackPath { get; init; } = DefaultCallbackPath;

    /// <summary>
    /// The configuration layer sets this when the operator explicitly selected
    /// a non-loopback listener. It is false for the loopback default.
    /// </summary>
    public bool SharedBindingExplicit { get; init; }

    public TimeSpan LeaseDuration => TimeSpan.FromSeconds(LeaseSeconds);

    public bool AllowsNonLoopbackDestinations =>
        !IsLoopbackBind(Bind);

    public string RelayCallbackUrl => BuildRelayCallbackUrl();

    public string CallbackRoutePath =>
        PublicUrl is not null
            ? PathMatchesCallback(PublicUrl.AbsolutePath)
                ? PublicUrl.AbsolutePath
                : CombinePaths(PublicUrl.AbsolutePath, CallbackPath)
            : CallbackPath;

    public static RelayServerOptions FromSettings(RelaySettings settings, string? registrationDatabasePath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Uri? publicUrl = null;
        if (!string.IsNullOrWhiteSpace(settings.PublicUrl))
        {
            if (!Uri.TryCreate(settings.PublicUrl, UriKind.Absolute, out publicUrl))
            {
                throw new ArgumentException("PublicUrl is not a valid absolute URL.", nameof(settings));
            }
        }

        return new RelayServerOptions
        {
            Port = settings.Port,
            Bind = settings.Bind,
            PublicUrl = publicUrl,
            Hostname = settings.Hostname,
            AutoDiscovery = settings.AutoDiscovery,
            LeaseSeconds = settings.LeaseSeconds,
            MaxRegistrations = settings.MaxRegistrations,
            SharedBindingExplicit = !IsLoopbackBind(settings.Bind),
            RegistrationDatabasePath = registrationDatabasePath,
        };
    }

    public static bool IsLoopbackBind(string? bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return false;
        }

        var value = bind.Trim();
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(value.Trim('[', ']'), out var address))
        {
            return false;
        }

        return IPAddress.IsLoopback(address);
    }

    public static bool IsWildcardBind(string? bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return false;
        }

        var value = bind.Trim().Trim('[', ']');
        return value is "0.0.0.0" or "::" or "*" or "+";
    }

    public void Validate()
    {
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "Port must be between 1 and 65535.");
        }

        var normalizedBind = Bind.Trim('[', ']');
        if (string.IsNullOrWhiteSpace(Bind) || Bind.Any(char.IsWhiteSpace) || Bind.Contains('/') ||
            (!normalizedBind.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
             !IsWildcardBind(normalizedBind) &&
             !IPAddress.TryParse(normalizedBind, out _)))
        {
            throw new ArgumentException(
                "Bind must be localhost, an IP address, or an explicit wildcard address. Use Hostname or PublicUrl for the advertised name.",
                nameof(Bind));
        }

        if (LeaseSeconds is < 1 or > 86_400)
        {
            throw new ArgumentOutOfRangeException(nameof(LeaseSeconds), "LeaseSeconds must be between 1 and 86400.");
        }

        if (MaxRegistrations is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRegistrations), "MaxRegistrations must be between 1 and 1000000.");
        }

        if (RegistrationDatabasePath is not null &&
            (string.IsNullOrWhiteSpace(RegistrationDatabasePath) || RegistrationDatabasePath == ":memory:"))
        {
            throw new ArgumentException("RegistrationDatabasePath must be a file path.", nameof(RegistrationDatabasePath));
        }

        if (CallbackPath.Length == 0 || CallbackPath[0] != '/' ||
            CallbackPath.Contains('?') ||
            CallbackPath.Contains('#'))
        {
            throw new ArgumentException("CallbackPath must be an absolute path without a query or fragment.", nameof(CallbackPath));
        }

        if (PublicUrl is not null)
        {
            ValidatePublicUrl(PublicUrl);
        }

        if (!string.IsNullOrWhiteSpace(Hostname) && IsWildcardHost(Hostname))
        {
            throw new ArgumentException("Hostname must identify a reachable host, not a wildcard bind address.", nameof(Hostname));
        }

        if (Hostname is not null &&
            (string.IsNullOrWhiteSpace(Hostname) || Hostname.Any(char.IsWhiteSpace) || Hostname.Contains('/') ||
             (!IPAddress.TryParse(Hostname, out _) && Uri.CheckHostName(Hostname) == UriHostNameType.Unknown)))
        {
            throw new ArgumentException("Hostname must be an IP address or host name without a URL path.", nameof(Hostname));
        }

        if (IsWildcardBind(Bind) && PublicUrl is null && string.IsNullOrWhiteSpace(Hostname))
        {
            throw new ArgumentException(
                "A wildcard bind requires an explicit PublicUrl or Hostname so the provider callback address is usable.",
                nameof(PublicUrl));
        }

        // Resolve the advertised callback while validating configuration so a
        // bad address fails before Kestrel starts serving requests.
        _ = BuildRelayCallbackUrl();
    }

    private string BuildRelayCallbackUrl()
    {
        if (PublicUrl is not null && PathMatchesCallback(PublicUrl.AbsolutePath))
        {
            return PublicUrl.AbsoluteUri;
        }

        var baseUrl = PublicUrl ?? new Uri($"http://{GetAdvertisedHost()}:{Port}", UriKind.Absolute);
        ValidatePublicUrl(baseUrl);

        var builder = new UriBuilder(baseUrl)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = CombinePaths(baseUrl.AbsolutePath, CallbackPath),
        };

        return builder.Uri.AbsoluteUri;
    }

    private bool PathMatchesCallback(string path) =>
        string.Equals(path, CallbackPath, StringComparison.Ordinal) ||
        path.EndsWith("/" + CallbackPath.TrimStart('/'), StringComparison.Ordinal);

    private string GetAdvertisedHost()
    {
        if (!string.IsNullOrWhiteSpace(Hostname))
        {
            if (Hostname.Contains(':') && Hostname[0] != '[')
            {
                return $"[{Hostname}]";
            }

            return Hostname;
        }

        if (IsWildcardBind(Bind))
        {
            throw new InvalidOperationException(
                "A wildcard bind needs an explicit PublicUrl or Hostname before the relay can advertise a callback.");
        }

        return Bind.Contains(':') && Bind[0] != '[' ? $"[{Bind}]" : Bind;
    }

    private static string CombinePaths(string basePath, string callbackPath)
    {
        var left = string.IsNullOrEmpty(basePath) || basePath == "/" ? string.Empty : basePath.TrimEnd('/');
        return $"{left}/{callbackPath.TrimStart('/')}";
    }

    private static void ValidatePublicUrl(Uri value)
    {
        if (!value.IsAbsoluteUri || (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps) ||
            value.Port is < 1 or > 65_535 || !string.IsNullOrEmpty(value.UserInfo) ||
            value.GetLeftPart(UriPartial.Authority).Contains('@', StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment) || IsWildcardHost(value.Host))
        {
            throw new ArgumentException("PublicUrl must be an HTTP(S) URL with a usable host and no credentials, query, or fragment.", nameof(value));
        }
    }

    private static bool IsWildcardHost(string host) =>
        host.Trim('[', ']') is "0.0.0.0" or "::" or "*" or "+";
}
