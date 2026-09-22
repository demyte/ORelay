using System.Net;
using Aspire.Hosting.ApplicationModel;
using ORelay.Discovery;

namespace ORelay.Aspire.Hosting;

/// <summary>Worktree-owned callback address selection. This does not change a listener binding.</summary>
public sealed record ORelayCallbackOptions
{
    public string? Hostname { get; init; }
    public CallbackDiscoveryMode AutoDiscovery { get; init; } = CallbackDiscoveryMode.Local;
    public ITailscaleStatusProvider? TailscaleProvider { get; init; }
}

internal static class CallbackDestination
{
    internal static async Task<Uri> ResolveAsync(EndpointReference? endpoint, string callbackPath,
        Uri? explicitUrl, ORelayCallbackOptions options, CancellationToken ct)
    {
        var allocation = endpoint?.EndpointAnnotation.AllocatedEndpoint;
        var loopbackOnly = allocation is { BindingMode: EndpointBindingMode.SingleAddress }
            && (allocation.Address.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(allocation.Address.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        CallbackDiscoveryResult result;
        try
        {
            result = await new CallbackAddressDiscovery(options.TailscaleProvider).ResolveAsync(new CallbackDiscoveryRequest
            {
                ExplicitUrl = explicitUrl?.AbsoluteUri,
                Hostname = options.Hostname,
                Mode = options.AutoDiscovery,
                CallbackPath = callbackPath,
                AspireEndpoint = endpoint is null ? null : new AspireApplicationEndpoint(endpoint.Url, loopbackOnly),
            }, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("ORelay callback discovery timed out. Check Tailscale or supply an explicit callback URL, then restart the resource.");
        }
        if (!result.Succeeded)
        {
            var candidates = result.Candidates.Length == 0 ? string.Empty : " Candidates: " + string.Join(", ", result.Candidates) + ".";
            throw new InvalidOperationException($"ORelay callback discovery failed ({result.ErrorCode}). {result.Message} {result.NextStep}{candidates}");
        }
        return new Uri(result.CallbackUrl!, UriKind.Absolute);
    }
}
