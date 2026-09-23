using System.Text.Json;
using System.Text.Json.Serialization;
using ORelay.Cli;
using ORelay.Configuration;
using ORelay.Diagnostics;
using ORelay.Discovery;
using ORelay.Server;
using ORelay.Services;
using ORelay.Setup;
using ORelay.Updating;

namespace ORelay;

internal static class ApplicationCommands
{
    public static async Task<int> ExecuteAsync(CliOptions options, TextWriter output, TextWriter error)
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
                    return await UpdateCommand.ExecuteAsync(options, output, error).ConfigureAwait(false);
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
