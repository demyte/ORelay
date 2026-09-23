using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ORelay.Cli;

namespace ORelay.Updating;

public static partial class UpdateCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, TextWriter output, TextWriter error,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var isReadOnlyCheck = options.Command == CliCommand.Update && options.Update?.Check == true;
        var engine = new UpdateEngine(logger: isReadOnlyCheck ? null : loggerFactory?.CreateLogger<UpdateEngine>());
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

        if (!isReadOnlyCheck && loggerFactory is not null)
        {
            var logger = loggerFactory.CreateLogger("ORelay.Updating.UpdateCommand");
            var operation = options.Command == CliCommand.Install ? "install" : "update";
            if (result.Succeeded)
            {
                UpdateCompleted(logger, operation, result.CurrentVersion, result.LatestVersion, result.Changed, result.ErrorCode);
            }
            else
            {
                UpdateFailed(logger, operation, result.CurrentVersion, result.LatestVersion, result.Changed, result.ErrorCode);
            }
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

    [LoggerMessage(1, LogLevel.Information,
        "Manual {Operation} completed: current={CurrentVersion}, latest={LatestVersion}, changed={Changed}, error={ErrorCode}.")]
    private static partial void UpdateCompleted(ILogger logger, string operation, string currentVersion, string? latestVersion,
        bool changed, UpdateErrorCode? errorCode);

    [LoggerMessage(2, LogLevel.Warning,
        "Manual {Operation} failed: current={CurrentVersion}, latest={LatestVersion}, changed={Changed}, error={ErrorCode}.")]
    private static partial void UpdateFailed(ILogger logger, string operation, string currentVersion, string? latestVersion,
        bool changed, UpdateErrorCode? errorCode);
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdateResult))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
