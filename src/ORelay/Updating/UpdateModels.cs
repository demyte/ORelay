using System.Text.Json.Serialization;

namespace ORelay.Updating;

[JsonConverter(typeof(JsonStringEnumConverter<UpdateErrorCode>))]
public enum UpdateErrorCode
{
    UnsupportedRuntime,
    UnsupportedPlatform,
    InvalidVersion,
    ReleaseUnavailable,
    InvalidRelease,
    DownloadFailed,
    IntegrityFailure,
    InvalidArchive,
    VersionMismatch,
    Downgrade,
    ServiceConflict,
    ServiceFailure,
    InstallFailure,
    Busy,
}

public sealed record UpdateResult(
    bool Succeeded,
    bool Changed,
    string CurrentVersion,
    string? LatestVersion,
    string? TargetPath,
    string Message,
    UpdateErrorCode? ErrorCode = null,
    bool UpdateAvailable = false)
{
    public static UpdateResult Failure(UpdateErrorCode code, string currentVersion, string? latestVersion,
        string? targetPath, string message) =>
        new(false, false, currentVersion, latestVersion, targetPath, message, code);
}

public sealed record UpdateRequest(bool RestartService = false, string? ServiceName = null,
    string? ConfigurationPath = null, string AutoUpdateLevel = "major");

public sealed record InstallRequest(string InstallDirectory, bool RestartService = false,
    string? ServiceName = null, string? ConfigurationPath = null);

internal sealed class UpdateException(UpdateErrorCode code, string message) : Exception(message)
{
    public UpdateErrorCode Code { get; } = code;
}
