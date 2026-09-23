using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ORelay.Configuration;

namespace ORelay.Server;

// Kestrel reloads configuration endpoints asynchronously. Confirm the address
// through this server's bound-address collection before publishing settings.
internal sealed class RelayListenerReload(IConfigurationRoot configuration, IServer server, ILogger logger)
{
    internal const string UrlKey = "Endpoints:Relay:Url";

    internal async Task<bool> ApplyAsync(RelaySettings previous, RelaySettings next, CancellationToken cancellationToken)
    {
        var oldAddress = Address(previous);
        var newAddress = Address(next);
        if (oldAddress == newAddress && IsBound(oldAddress)) return true;

        configuration[UrlKey] = newAddress;
        configuration.Reload();
        if (await WaitForBindingAsync(newAddress, cancellationToken).ConfigureAwait(false)) return true;

        RelayServerLog.ListenerRejected(logger);
        configuration[UrlKey] = oldAddress;
        configuration.Reload();
        if (!await WaitForBindingAsync(oldAddress, cancellationToken).ConfigureAwait(false))
            RelayServerLog.ListenerRestoreFailed(logger);
        return false;
    }

    private async Task<bool> WaitForBindingAsync(string address, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        // Kestrel gives existing requests five seconds to drain when rebinding.
        while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsBound(address)) return true;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private bool IsBound(string address) =>
        server.Features.Get<IServerAddressesFeature>()?.Addresses.Any(bound =>
            string.Equals(bound, address, StringComparison.OrdinalIgnoreCase)) == true;

    internal static string Address(RelaySettings settings)
    {
        var bind = settings.Bind.Trim('[', ']');
        if (bind is "*" or "+" or "::") bind = "[::]";
        else if (IPAddress.TryParse(bind, out var address))
            bind = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        else bind = bind.ToLowerInvariant();
        return $"http://{bind}:{settings.Port}";
    }
}
