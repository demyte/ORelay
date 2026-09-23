using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ORelay.Cli;
using ORelay.Configuration;
using ORelay.Diagnostics;
using ORelay.Discovery;
using ORelay.Server;
using ORelay.Services;
using ORelay.Setup;
using ORelay.Updating;

namespace ORelay;

internal static partial class ApplicationCommands
{
    public static async Task<int> ExecuteAsync(CliOptions options, TextWriter output, TextWriter error)
    {
        var operation = MutationName(options);
        if (operation is null)
            return await ExecuteCoreAsync(options, output, error, null).ConfigureAwait(false);

        using var logging = CreateLogging(options.ConfigFile, error);
        if (logging is null)
            return await ExecuteCoreAsync(options, output, error, null).ConfigureAwait(false);

        var logger = logging.CreateLogger("ORelay.Cli");
        var elapsed = Stopwatch.StartNew();
        CommandStarted(logger, operation);
        try
        {
            var exitCode = await ExecuteCoreAsync(options, output, error, logging).ConfigureAwait(false);
            if (exitCode == 0) CommandCompleted(logger, operation, exitCode, elapsed.ElapsedMilliseconds);
            else CommandFailed(logger, operation, exitCode, elapsed.ElapsedMilliseconds);
            return exitCode;
        }
        catch (Exception)
        {
            CommandInterrupted(logger, operation, elapsed.ElapsedMilliseconds);
            throw;
        }
    }

    // Only fixed operation names enter the log. Arguments, output, exception
    // messages, and configuration values may contain secrets.
    private static string? MutationName(CliOptions options) => options.Command switch
    {
        CliCommand.Init => "init",
        CliCommand.Setup => "setup",
        CliCommand.Install => "install",
        CliCommand.Update when options.Update is { Check: false } => "update",
        CliCommand.Config when options.Config is { Action: ConfigAction.Set } => "config set",
        CliCommand.Config when options.Config is { Action: ConfigAction.Clear } => "config clear",
        CliCommand.Doctor when options.Doctor is { Fix: true } => "doctor --fix",
        CliCommand.Service => options.Service?.Action switch
        {
            ServiceAction.Install => "service install",
            ServiceAction.Start => "service start",
            ServiceAction.Stop => "service stop",
            ServiceAction.Restart => "service restart",
            ServiceAction.Uninstall => "service uninstall",
            ServiceAction.Enable => "service enable",
            ServiceAction.Disable => "service disable",
            _ => null,
        },
        _ => null,
    };

    private static ILoggerFactory? CreateLogging(string? configFile, TextWriter error)
    {
        try
        {
            var path = RelayConfigurationPath.Resolve(configFile);
            return LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddProvider(new RotatingFileLoggerProvider(path)));
        }
        catch (Exception)
        {
            // Logging must not prevent the requested command from running.
            error.WriteLine("ORelay could not initialize command file logging.");
            return null;
        }
    }

    [LoggerMessage(1, LogLevel.Information, "CLI command started: {Operation}.")]
    private static partial void CommandStarted(ILogger logger, string operation);

    [LoggerMessage(2, LogLevel.Information, "CLI command completed: {Operation}; exit={ExitCode}, elapsed={ElapsedMilliseconds}ms.")]
    private static partial void CommandCompleted(ILogger logger, string operation, int exitCode, long elapsedMilliseconds);

    [LoggerMessage(3, LogLevel.Warning, "CLI command failed: {Operation}; exit={ExitCode}, elapsed={ElapsedMilliseconds}ms. See command output for details.")]
    private static partial void CommandFailed(ILogger logger, string operation, int exitCode, long elapsedMilliseconds);

    [LoggerMessage(4, LogLevel.Warning, "CLI command interrupted: {Operation}; elapsed={ElapsedMilliseconds}ms. No exit result was returned.")]
    private static partial void CommandInterrupted(ILogger logger, string operation, long elapsedMilliseconds);

    private static async Task<int> ExecuteCoreAsync(CliOptions options, TextWriter output, TextWriter error,
        ILoggerFactory? logging)
    {
        try
        {
            switch (options.Command)
            {
                case CliCommand.Init:
                case CliCommand.Config:
                    return await ConfigurationCommand.ExecuteAsync(options, output, error).ConfigureAwait(false);
                case CliCommand.Setup:
                    return await SetupCommand.ExecuteAsync(options, output, error).ConfigureAwait(false);
                case CliCommand.Server:
                    var store = new RelayConfigurationStore(options.ConfigFile);
                    store.Init(options.SettingsPatch);
                    var settings = store.Read(options.SettingsPatch);
                    var discovery = await RelaySettingsDiscovery.ResolveAsync(settings).ConfigureAwait(false);
                    if (!discovery.Succeeded || discovery.Settings is null)
                    {
                        throw new InvalidOperationException($"{discovery.Message} {discovery.NextStep}");
                    }

                    settings = discovery.Settings;
                    return await RelayServerHost.RunAsync(settings, null, null, options.IsJson, options.Server?.ServiceName,
                        registrationDatabasePath: Path.ChangeExtension(store.FilePath, "registrations.db"),
                        configurationPath: store.FilePath, invocationOverrides: options.SettingsPatch).ConfigureAwait(false);
                case CliCommand.Doctor:
                    return await DoctorCommand.ExecuteAsync(options, output, error, new DoctorRuntime
                    {
                        ServiceCheck = new ServiceDoctorCheck(options.ConfigFile, options.Doctor?.Name),
                    }).ConfigureAwait(false);
                case CliCommand.Service:
                    return await ServiceCommand.ExecuteAsync(options, output, error, options.Service?.Name, null).ConfigureAwait(false);
                case CliCommand.Update:
                case CliCommand.Install:
                    return await UpdateCommand.ExecuteAsync(options, output, error, logging).ConfigureAwait(false);
                default:
                    await error.WriteLineAsync("This command has not been integrated into this build yet.").ConfigureAwait(false);
                    return CliExitCodes.CommandUnavailable;
            }
        }
        catch (Exception ex) when (ex is RelayConfigurationException or ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var failure = new CommandFailure("command_failed", ex.Message);
            if (options.IsJson)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(failure, CommandJsonContext.Default.CommandFailure)).ConfigureAwait(false);
            }

            await error.WriteLineAsync($"error: {failure.Message}").ConfigureAwait(false);
            return 3;
        }
    }
}

internal sealed record CommandFailure(string Code, string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandFailure))]
internal sealed partial class CommandJsonContext : JsonSerializerContext;
