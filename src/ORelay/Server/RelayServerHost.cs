using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ORelay.Configuration;
using ORelay.Services;

namespace ORelay.Server;

/// <summary>
/// Small hosting adapter for the CLI. The command layer remains responsible
/// for loading and validating configuration, then calls this method with the
/// effective settings and transient server overrides.
/// </summary>
public static class RelayServerHost
{
    public static Task<int> RunAsync(
        RelaySettings settings,
        int? portOverride = null,
        string? bindOverride = null,
        bool jsonOutput = false,
        CancellationToken cancellationToken = default) =>
        RunAsync(settings, portOverride, bindOverride, jsonOutput, serviceName: null, cancellationToken: cancellationToken);

    public static async Task<int> RunAsync(
        RelaySettings settings,
        int? portOverride,
        string? bindOverride,
        bool jsonOutput,
        string? serviceName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var effectiveSettings = settings with
        {
            Port = portOverride ?? settings.Port,
            Bind = bindOverride ?? settings.Bind,
        };
        RelaySettingsValidator.Validate(effectiveSettings);
        var options = RelayServerOptions.FromSettings(effectiveSettings);
        options.Validate();

        var builder = WebApplication.CreateSlimBuilder(Array.Empty<string>());
        // The default hosting request logs can include the full request target.
        // Callback queries carry authorization codes and state, so keep the
        // framework below warning level for this service.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        if (jsonOutput)
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole();
        }
        builder.Host.UseORelayServiceLifetime(serviceName);
        builder.WebHost.UseUrls($"http://{FormatBind(effectiveSettings.Bind)}:{effectiveSettings.Port}");
        builder.Services.AddRelayServer(options);

        await using var app = builder.Build();
        app.MapRelayEndpoints(
            app.Services.GetRequiredService<RegistrationStore>(),
            options);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var ready = new RelayServerReadyResponse(
            "orelay",
            effectiveSettings.Bind,
            effectiveSettings.Port,
            options.RelayCallbackUrl);
        if (jsonOutput)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(ready, RelayJsonContext.Default.RelayServerReadyResponse));
        }
        else
        {
            Console.Error.WriteLine(
                $"orelay listening on {effectiveSettings.Bind}:{effectiveSettings.Port}; callback {options.RelayCallbackUrl}");
        }
        await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static string FormatBind(string bind)
    {
        var normalized = bind.Trim('[', ']');
        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            normalized is "0.0.0.0" or "::" or "*" or "+")
        {
            return normalized;
        }

        if (!IPAddress.TryParse(normalized, out var address))
        {
            throw new ArgumentException(
                "Bind must be localhost, an IP address, or an explicit wildcard address. Use Hostname or PublicUrl for the advertised name.",
                nameof(bind));
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }
}
