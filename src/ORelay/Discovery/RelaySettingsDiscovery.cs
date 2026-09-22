using ORelay.Configuration;

namespace ORelay.Discovery;

/// <summary>The effective settings used for one server start.</summary>
public sealed record RelaySettingsDiscoveryResult(
    bool Succeeded,
    RelaySettings? Settings,
    string Source,
    string? ErrorCode,
    string? Message,
    string? NextStep,
    string[] Candidates)
{
    public static RelaySettingsDiscoveryResult Success(RelaySettings settings, string source) =>
        new(true, settings, source, null, null, null, Array.Empty<string>());

    public static RelaySettingsDiscoveryResult Failure(
        string code,
        string message,
        string nextStep,
        IEnumerable<string>? candidates = null) =>
        new(false, null, "none", code, message, nextStep, candidates?.ToArray() ?? Array.Empty<string>());
}

/// <summary>Resolves only the relay server's advertised host.</summary>
public static class RelaySettingsDiscovery
{
    public static async Task<RelaySettingsDiscoveryResult> ResolveAsync(
        RelaySettings settings,
        ITailscaleStatusProvider? tailscaleProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.PublicUrl))
        {
            return RelaySettingsDiscoveryResult.Success(settings, "explicit-public-url");
        }

        if (!string.IsNullOrWhiteSpace(settings.Hostname))
        {
            return RelaySettingsDiscoveryResult.Success(settings, "explicit-hostname");
        }

        if (!string.Equals(settings.AutoDiscovery, "tailscale", StringComparison.OrdinalIgnoreCase))
        {
            return RelaySettingsDiscoveryResult.Success(settings, "configured");
        }

        var provider = tailscaleProvider ?? new TailscaleStatusProcessProvider();
        var status = await provider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Available)
        {
            return RelaySettingsDiscoveryResult.Failure(
                "tailscale_unavailable",
                status.Error ?? "Tailscale status is unavailable on the relay host.",
                "Start Tailscale or set an explicit publicUrl or hostname.");
        }

        var candidates = status.Candidates;
        if (candidates.Count == 0)
        {
            return RelaySettingsDiscoveryResult.Failure(
                "tailscale_unsupported",
                "Tailscale returned no usable local hostname or address.",
                "Set an explicit publicUrl or hostname.");
        }

        if (candidates.Count > 1 &&
            !string.Equals(candidates[0].Source, "dnsName", StringComparison.Ordinal) &&
            !string.Equals(candidates[0].Source, "hostName", StringComparison.Ordinal))
        {
            return RelaySettingsDiscoveryResult.Failure(
                "tailscale_ambiguous",
                "Tailscale returned more than one address and no preferred hostname.",
                "Set an explicit publicUrl or hostname.",
                candidates.Select(candidate => candidate.Host));
        }

        return RelaySettingsDiscoveryResult.Success(settings with { Hostname = candidates[0].Host }, "tailscale");
    }
}
