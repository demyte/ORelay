using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using ORelay.Cli;
using ORelay.Configuration;
using ORelay.Discovery;
using ORelay.Server;

namespace ORelay.Diagnostics;

public static class DoctorExitCodes
{
    public const int Success = 0;
    public const int DiagnosticFailure = 1;
}

/// <summary>The result of the read-only relay health request.</summary>
public sealed record RelayHealthProbeResult(
    bool Reachable,
    bool IsRelay,
    string? Identity,
    string? Status,
    int? HttpStatusCode,
    string? Message)
{
    public static RelayHealthProbeResult Unreachable(string message) =>
        new(false, false, null, null, null, message);
}

/// <summary>Checks one relay health endpoint. Implementations must not mutate the relay.</summary>
public interface IRelayHealthProbe
{
    Task<RelayHealthProbeResult> CheckAsync(Uri healthUrl, CancellationToken cancellationToken = default);
}

/// <summary>The result of checking whether another process is listening on the relay port.</summary>
public sealed record PortOccupancyProbeResult(bool Connected, string? Message);

public interface IPortOccupancyProbe
{
    Task<PortOccupancyProbeResult> CheckAsync(
        string bind,
        int port,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional platform-specific checks supplied by the service tickets.</summary>
public interface IDoctorServiceCheck
{
    Task<DoctorCheck?> CheckAsync(
        RelaySettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>Dependencies at the edges of doctor, kept injectable for focused tests.</summary>
public sealed class DoctorRuntime
{
    public IRelayHealthProbe HealthProbe { get; init; } = new HttpRelayHealthProbe();

    public IPortOccupancyProbe PortProbe { get; init; } = new TcpPortOccupancyProbe();

    public ITailscaleStatusProvider TailscaleProvider { get; init; } = new TailscaleStatusProcessProvider();

    public IDoctorServiceCheck? ServiceCheck { get; init; }
}

public static class DoctorCommand
{
    /// <summary>
    /// Runs doctor. The default path only reads state. Fix mode creates a
    /// missing selected configuration file and then runs the same checks.
    /// </summary>
    public static Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error) => ExecuteAsync(options, output, error, runtime: null);

    public static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        DoctorRuntime? runtime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        runtime ??= new DoctorRuntime();

        var fix = options.Doctor?.Fix == true;
        var store = new RelayConfigurationStore(options.ConfigFile);
        var checks = new List<DoctorCheck>();
        var changed = false;
        RelaySettings? settings = null;

        if (!store.Exists && fix)
        {
            try
            {
                settings = store.Init();
                changed = true;
                checks.Add(DoctorCheck.Changed(
                    "configuration",
                    $"Created the selected configuration file '{store.FilePath}'."));
            }
            catch (RelayConfigurationException ex)
            {
                checks.Add(DoctorCheck.Failed("configuration", ex.Message, "Check the selected path and its permissions."));
            }
        }

        if (settings is null)
        {
            try
            {
                settings = store.Read();
                if (store.Exists)
                {
                    checks.Add(DoctorCheck.Passed("configuration", $"The selected configuration file '{store.FilePath}' is valid."));
                }
                else
                {
                    checks.Add(DoctorCheck.Failed(
                        "configuration",
                        $"The selected configuration file '{store.FilePath}' is missing.",
                        "Run 'orelay doctor --fix' or 'orelay init' to create it."));
                }
            }
            catch (RelayConfigurationException ex)
            {
                checks.Add(DoctorCheck.Failed("configuration", ex.Message, "Fix the selected file, then run doctor again."));
            }
        }

        if (settings is not null)
        {
            var effectiveSettings = settings;
            var relaySettingsDiscovery = await RelaySettingsDiscovery.ResolveAsync(
                settings,
                runtime.TailscaleProvider,
                cancellationToken).ConfigureAwait(false);
            if (relaySettingsDiscovery.Succeeded && relaySettingsDiscovery.Settings is not null)
            {
                effectiveSettings = relaySettingsDiscovery.Settings;
                if (string.Equals(settings.AutoDiscovery, "tailscale", StringComparison.OrdinalIgnoreCase))
                {
                    checks.Add(DoctorCheck.Passed(
                        "discovery",
                        relaySettingsDiscovery.Source == "tailscale"
                            ? "Tailscale supplied the advertised relay hostname."
                            : "An explicit advertised address takes precedence over Tailscale discovery."));
                }
            }
            else if (string.Equals(settings.AutoDiscovery, "tailscale", StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(DoctorCheck.Failed(
                    "discovery",
                    relaySettingsDiscovery.Message ?? "Tailscale discovery could not resolve the advertised host.",
                    relaySettingsDiscovery.NextStep ?? "Set an explicit publicUrl or hostname."));
            }

            var serverOptions = TryCreateServerOptions(effectiveSettings, checks);
            if (serverOptions is not null)
            {
                CheckBindingAndAdvertisement(serverOptions, checks);

                var healthUrl = BuildHealthUrl(serverOptions);
                RelayHealthProbeResult health;
                try
                {
                    health = await runtime.HealthProbe.CheckAsync(healthUrl, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
                {
                    health = RelayHealthProbeResult.Unreachable($"The health request failed: {ex.Message}");
                }

                await CheckHealthAndPort(serverOptions, healthUrl, health, runtime.PortProbe, checks, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(settings.AutoDiscovery, "tailscale", StringComparison.OrdinalIgnoreCase))
                {
                    await CheckDiscoveryPrerequisites(settings, runtime.TailscaleProvider, checks, cancellationToken).ConfigureAwait(false);
                }

                if (runtime.ServiceCheck is not null)
                {
                    try
                    {
                        var serviceCheck = await runtime.ServiceCheck.CheckAsync(effectiveSettings, cancellationToken).ConfigureAwait(false);
                        if (serviceCheck is not null)
                        {
                            checks.Add(serviceCheck);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException)
                    {
                        checks.Add(DoctorCheck.Failed("service", $"The optional service check failed: {ex.Message}"));
                    }
                }
            }
        }

        var healthy = checks.All(check => check.Status is DoctorCheckStatus.Passed or DoctorCheckStatus.Changed or DoctorCheckStatus.Skipped);
        var report = new DoctorReport(
            store.FilePath,
            fix,
            healthy,
            changed,
            checks.ToArray());

        if (options.IsJson)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, DoctorJsonContext.Default.DoctorReport)).ConfigureAwait(false);
        }
        else
        {
            await WriteHumanReport(report, output).ConfigureAwait(false);
        }

        return healthy ? DoctorExitCodes.Success : DoctorExitCodes.DiagnosticFailure;
    }

    private static RelayServerOptions? TryCreateServerOptions(
        RelaySettings settings,
        List<DoctorCheck> checks)
    {
        try
        {
            var options = RelayServerOptions.FromSettings(settings);
            options.Validate();
            checks.Add(DoctorCheck.Passed("listener", $"The intended listener is {options.Bind}:{options.Port}."));
            return options;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            checks.Add(DoctorCheck.Failed(
                "listener",
                $"The configured listener or advertised URL is invalid: {ex.Message}",
                "Set a valid bind address and an explicit advertised URL or hostname for wildcard bindings."));
            return null;
        }
    }

    private static void CheckBindingAndAdvertisement(
        RelayServerOptions options,
        List<DoctorCheck> checks)
    {
        Uri callback;
        try
        {
            callback = new Uri(options.RelayCallbackUrl, UriKind.Absolute);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            checks.Add(DoctorCheck.Failed(
                "advertised-url",
                $"The advertised callback URL could not be constructed: {ex.Message}",
                "Set publicUrl or hostname to a browser-reachable HTTP(S) address."));
            return;
        }

        if (RelayServerOptions.IsWildcardBind(options.Bind))
        {
            checks.Add(DoctorCheck.Passed(
                "advertised-url",
                $"The wildcard listener advertises {callback.AbsoluteUri}."));
        }
        else if (RelayServerOptions.IsLoopbackBind(options.Bind) && !IsLoopbackHost(callback.Host))
        {
            checks.Add(DoctorCheck.Failed(
                "binding-mismatch",
                $"The listener binds to loopback but advertises the remote host '{callback.Host}'.",
                "Bind the application to a reachable interface or advertise a loopback URL."));
        }
        else
        {
            checks.Add(DoctorCheck.Passed("advertised-url", $"The callback address is {callback.AbsoluteUri}."));
        }
    }

    private static async Task CheckHealthAndPort(
        RelayServerOptions options,
        Uri healthUrl,
        RelayHealthProbeResult health,
        IPortOccupancyProbe portProbe,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken)
    {
        if (health.Reachable && health.IsRelay &&
            string.Equals(health.Identity, "orelay", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(DoctorCheck.Passed("management-health", $"The ORelay health endpoint responded at {healthUrl.AbsoluteUri}."));
            checks.Add(DoctorCheck.Passed("port", $"Port {options.Port} is occupied by the ORelay listener."));
            return;
        }

        var message = health.Reachable
            ? $"The endpoint responded, but it did not identify as a healthy ORelay listener (identity '{health.Identity ?? "unknown"}')."
            : $"The ORelay health endpoint is unreachable: {health.Message ?? "no response"}";
        checks.Add(DoctorCheck.Failed(
            "management-health",
            message,
            "Start ORelay on the selected bind and port, then run doctor again. A relay-side probe does not prove browser reachability."));

        PortOccupancyProbeResult occupancy;
        try
        {
            occupancy = await portProbe.CheckAsync(options.Bind, options.Port, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            checks.Add(DoctorCheck.Failed("port", $"Port occupancy could not be checked: {ex.Message}"));
            return;
        }

        if (occupancy.Connected)
        {
            checks.Add(DoctorCheck.Failed(
                "port",
                $"Port {options.Port} is occupied, but the listener did not identify as ORelay. {occupancy.Message ?? string.Empty}".Trim(),
                "Stop the unrelated listener or choose another port. Doctor does not stop processes."));
        }
        else
        {
            checks.Add(DoctorCheck.Passed("port", $"Port {options.Port} is available for the intended ORelay listener."));
        }
    }

    private static async Task CheckDiscoveryPrerequisites(
        RelaySettings settings,
        ITailscaleStatusProvider provider,
        List<DoctorCheck> checks,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.AutoDiscovery, "tailscale", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(DoctorCheck.Skipped("discovery", "Tailscale discovery is not enabled for this configuration."));
            return;
        }

        TailscaleStatusSnapshot status;
        try
        {
            status = await provider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            checks.Add(DoctorCheck.Failed(
                "discovery",
                $"Tailscale discovery could not run: {ex.Message}",
                "Start Tailscale or set autoDiscovery to local/none, or provide an explicit hostname."));
            return;
        }

        if (!status.Available)
        {
            checks.Add(DoctorCheck.Failed(
                "discovery",
                status.Error ?? "Tailscale status is unavailable.",
                "Start Tailscale on this host or set an explicit advertised hostname."));
            return;
        }

        var candidates = status.Candidates;
        if (candidates.Count == 0)
        {
            checks.Add(DoctorCheck.Failed(
                "discovery",
                "Tailscale is running but returned no usable local address.",
                "Provide an explicit advertised hostname or URL."));
        }
        else
        {
            checks.Add(DoctorCheck.Passed(
                "discovery",
                $"Tailscale discovery found {candidates.Count} local candidate address{(candidates.Count == 1 ? string.Empty : "es")}."));
        }
    }

    private static Uri BuildHealthUrl(RelayServerOptions options)
    {
        var callback = new Uri(options.RelayCallbackUrl, UriKind.Absolute);
        var builder = new UriBuilder(callback)
        {
            Path = "/health",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    private static async Task WriteHumanReport(DoctorReport report, TextWriter output)
    {
        await output.WriteLineAsync($"ORelay doctor: {(report.Healthy ? "healthy" : "failures remain")}").ConfigureAwait(false);
        foreach (var check in report.Checks)
        {
            await output.WriteLineAsync($"{check.Status}: {check.Name}: {check.Message}").ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(check.NextStep))
            {
                await output.WriteLineAsync($"  next: {check.NextStep}").ConfigureAwait(false);
            }
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<DoctorCheckStatus>))]
public enum DoctorCheckStatus
{
    Passed,
    Failed,
    Skipped,
    Changed,
}

public sealed record DoctorCheck(
    string Name,
    DoctorCheckStatus Status,
    string Message,
    string? NextStep)
{
    public static DoctorCheck Passed(string name, string message) => new(name, DoctorCheckStatus.Passed, message, null);

    public static DoctorCheck Changed(string name, string message) => new(name, DoctorCheckStatus.Changed, message, null);

    public static DoctorCheck Skipped(string name, string message) => new(name, DoctorCheckStatus.Skipped, message, null);

    public static DoctorCheck Failed(string name, string message, string? nextStep = null) => new(name, DoctorCheckStatus.Failed, message, nextStep);
}

public sealed record DoctorReport(
    string ConfigFile,
    bool FixRequested,
    bool Healthy,
    bool Changed,
    DoctorCheck[] Checks);

internal sealed class HttpRelayHealthProbe : IRelayHealthProbe
{
    public async Task<RelayHealthProbeResult> CheckAsync(Uri healthUrl, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };

        try
        {
            using var response = await client.GetAsync(healthUrl, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            RelayHealthPayload? health;
            try
            {
                health = JsonSerializer.Deserialize(payload, DoctorJsonContext.Default.RelayHealthPayload);
            }
            catch (JsonException)
            {
                health = null;
            }

            var identity = health?.Identity;
            var status = health?.Status;
            return new RelayHealthProbeResult(
                Reachable: true,
                IsRelay: string.Equals(identity, "orelay", StringComparison.OrdinalIgnoreCase),
                Identity: identity,
                Status: status,
                HttpStatusCode: (int)response.StatusCode,
                Message: response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return RelayHealthProbeResult.Unreachable(ex.Message);
        }
    }
}

internal sealed class TcpPortOccupancyProbe : IPortOccupancyProbe
{
    public async Task<PortOccupancyProbeResult> CheckAsync(
        string bind,
        int port,
        CancellationToken cancellationToken = default)
    {
        var host = RelayServerOptions.IsWildcardBind(bind) ? "127.0.0.1" : bind.Trim('[', ']');
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new PortOccupancyProbeResult(true, "A TCP listener accepted a connection.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            if (ex is OperationCanceledException)
            {
                throw;
            }

            return new PortOccupancyProbeResult(false, null);
        }
    }
}

internal sealed record RelayHealthPayload(
    [property: JsonPropertyName("identity")] string? Identity,
    [property: JsonPropertyName("status")] string? Status);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(DoctorCheck))]
[JsonSerializable(typeof(DoctorCheck[]))]
[JsonSerializable(typeof(RelayHealthPayload))]
internal sealed partial class DoctorJsonContext : JsonSerializerContext;
