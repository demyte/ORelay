using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
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
    public static async Task<int> RunAsync(
        RelaySettings settings,
        int? portOverride,
        string? bindOverride,
        bool jsonOutput,
        string? serviceName,
        string registrationDatabasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(registrationDatabasePath))
        {
            throw new ArgumentException("A registration database path is required when starting the relay.", nameof(registrationDatabasePath));
        }

        var effectiveSettings = settings with
        {
            Port = portOverride ?? settings.Port,
            Bind = bindOverride ?? settings.Bind,
        };
        RelaySettingsValidator.Validate(effectiveSettings);
        var options = RelayServerOptions.FromSettings(effectiveSettings, registrationDatabasePath);
        options.Validate();

        // Services can start in the filesystem root. Keep configuration watchers
        // scoped to the executable directory instead of the working directory.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        // Framework request logs can include OAuth values. Enable informational
        // output only for our own messages, which never include callback queries.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter(RelayServerLog.Category, LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Services.Configure<ConsoleLoggerOptions>(console =>
            console.LogToStandardErrorThreshold = LogLevel.Trace);
        if (jsonOutput)
        {
            builder.Logging.AddJsonConsole(console => console.TimestampFormat = "yyyy-MM-ddTHH:mm:sszzz");
        }
        else
        {
            builder.Logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
                console.ColorBehavior = !Console.IsErrorRedirected &&
                    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) &&
                    !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)
                        ? LoggerColorBehavior.Enabled
                        : LoggerColorBehavior.Disabled;
            });
        }
        builder.Host.UseORelayServiceLifetime(serviceName);
        builder.WebHost.UseUrls($"http://{FormatBind(effectiveSettings.Bind)}:{effectiveSettings.Port}");
        builder.Services.AddRelayServer(options);

        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(RelayServerLog.Category);
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
        RelayServerLog.Listening(logger,
            $"http://{FormatBind(effectiveSettings.Bind)}:{effectiveSettings.Port}", options.RelayCallbackUrl);
        using var stoppingRegistration = app.Lifetime.ApplicationStopping.Register(() => RelayServerLog.Stopping(logger));
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
