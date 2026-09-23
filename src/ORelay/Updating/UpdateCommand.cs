using System.Text.Json;
using System.Text.Json.Serialization;
using ORelay.Cli;

namespace ORelay.Updating;

public static class UpdateCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var engine = new UpdateEngine();
        UpdateResult result;
        if (options.Command == CliCommand.Update && options.Update is not null)
        {
            var request = new UpdateRequest(options.Update.RestartService, options.Update.Name, options.ConfigFile);
            result = options.Update.Check ? await engine.CheckAsync(request) : await engine.UpdateAsync(request);
        }
        else if (options.Command == CliCommand.Install && options.Install is not null)
        {
            var directory = options.Install.InstallDirectory ?? DefaultInstallDirectory();
            result = await engine.InstallSelfAsync(new InstallRequest(directory,
                options.Install.RestartService, options.Install.Name, options.ConfigFile));
        }
        else
        {
            await error.WriteLineAsync("error: unsupported install or update command");
            return CliExitCodes.UsageError;
        }

        if (options.IsJson)
            await output.WriteLineAsync(JsonSerializer.Serialize(result, UpdateJsonContext.Default.UpdateResult));
        else
            await output.WriteLineAsync(result.Message);

        if (!result.Succeeded)
        {
            await error.WriteLineAsync("error: " + result.Message);
            return result.ErrorCode is UpdateErrorCode.UnsupportedRuntime or UpdateErrorCode.UnsupportedPlatform ?
                CliExitCodes.CommandUnavailable : 3;
        }

        return CliExitCodes.Success;
    }

    public static string DefaultInstallDirectory() => OperatingSystem.IsWindows() ?
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ORelay") :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin");
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateResult))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
