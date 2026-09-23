using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using ORelay.Configuration;
using ORelay.Diagnostics;
using ORelay.Services;
using ORelay.Updating;

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
        string? configurationPath = null,
        RelaySettingsPatch? invocationOverrides = null,
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
        builder.Logging.AddProvider(new RotatingFileLoggerProvider(configurationPath ??
            Path.ChangeExtension(registrationDatabasePath, "json")));
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
        var state = new RelayConfigurationState(effectiveSettings);
        using var listenerConfiguration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [RelayListenerReload.UrlKey] = RelayListenerReload.Address(effectiveSettings) }).Build();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Configure(listenerConfiguration, reloadOnChange: true));
        builder.Services.AddRelayServer(options);
        builder.Services.AddTransient(_ => RelayServerOptions.FromSettings(state.Current, registrationDatabasePath));
        var isService = OperatingSystem.IsWindows() ? WindowsServiceHelpers.IsWindowsService() :
            OperatingSystem.IsLinux() && SystemdHelpers.IsSystemdService();
        var runtime = new NativeUpdateRuntime();
        if (isService && runtime.IsNative)
        {
            if (string.IsNullOrWhiteSpace(configurationPath))
                throw new ArgumentException("Automatic updates require the selected configuration path.", nameof(configurationPath));
            builder.Services.AddHostedService(provider => new ServiceAutoUpdateService(
                runtime.ProcessPath!, configurationPath, serviceName ?? ServiceIdentity.DefaultName,
                state,
                provider.GetRequiredService<ILogger<ServiceAutoUpdateService>>()));
        }

        await using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(RelayServerLog.Category);
        app.MapRelayEndpoints(
            app.Services.GetRequiredService<RegistrationStore>(),
            options, () => RelayServerOptions.FromSettings(state.Current, registrationDatabasePath));

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        RelayServerLog.Started(logger, runtime.CurrentVersion, isService ? "service" : "foreground",
            isService && runtime.IsNative ? "available" : "unavailable outside a native service");
        var listener = new RelayListenerReload(listenerConfiguration, app.Services.GetRequiredService<IServer>(), logger);
        using var monitor = configurationPath is null ? null : new RelayConfigurationMonitor(
            configurationPath,
            (invocationOverrides ?? new RelaySettingsPatch()) with
            {
                Port = portOverride ?? invocationOverrides?.Port,
                Bind = bindOverride ?? invocationOverrides?.Bind,
            },
            async (candidate, token) =>
            {
                if (candidate == state.Current) return true;
                var nextOptions = RelayServerOptions.FromSettings(candidate, registrationDatabasePath);
                nextOptions.Validate();
                if (!await listener.ApplyAsync(state.Current, candidate, token).ConfigureAwait(false)) return false;
                app.Services.GetRequiredService<RegistrationStore>().ApplyConfiguration(
                    nextOptions.LeaseDuration, nextOptions.MaxRegistrations, nextOptions.AllowsNonLoopbackDestinations);
                state.Publish(candidate);
                RelayServerLog.ConfigurationApplied(logger);
                return true;
            }, app.Services.GetRequiredService<ILogger<RelayConfigurationMonitor>>());
        if (monitor is not null) await monitor.StartAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);
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
            RelayListenerReload.Address(effectiveSettings), options.RelayCallbackUrl);
        using var stoppingRegistration = app.Lifetime.ApplicationStopping.Register(() => RelayServerLog.Stopping(logger));
        try
        {
            await app.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (monitor is not null) await monitor.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return 0;
    }

}
