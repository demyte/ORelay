using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ORelay.Configuration;
using ORelay.Server;

namespace ORelay.Tests.Server;

public sealed class RelayListenerReloadTests
{
    [Fact]
    public async Task RebindsListenerAndRestoresPreviousAddressWhenPortIsOccupied()
    {
        var previous = new RelaySettings { Port = FreePort() };
        using var config = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [RelayListenerReload.UrlKey] = RelayListenerReload.Address(previous) }).Build();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Configure(config, reloadOnChange: true));
        await using var app = builder.Build();
        app.MapGet("/health", () => "healthy");
        await app.StartAsync();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var reload = new RelayListenerReload(config, app.Services.GetRequiredService<IServer>(), NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var next = previous with { Port = FreePort() };
            Assert.True(await reload.ApplyAsync(previous, next, timeout.Token));
            Assert.Equal("healthy", await client.GetStringAsync(RelayListenerReload.Address(next) + "/health", timeout.Token));
            var closedListener = await Record.ExceptionAsync(() => client.GetStringAsync(
                RelayListenerReload.Address(previous) + "/health", timeout.Token));
            Assert.True(closedListener is HttpRequestException or TaskCanceledException);

            using var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            var rejected = next with { Port = ((IPEndPoint)occupied.LocalEndpoint).Port };
            Assert.False(await reload.ApplyAsync(next, rejected, timeout.Token));
            Assert.Equal("healthy", await client.GetStringAsync(RelayListenerReload.Address(next) + "/health", timeout.Token));

            var corrected = next with { Port = FreePort() };
            Assert.True(await reload.ApplyAsync(next, corrected, timeout.Token));
            Assert.Equal("healthy", await client.GetStringAsync(RelayListenerReload.Address(corrected) + "/health", timeout.Token));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
