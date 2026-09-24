namespace ORelay.Updating;

internal static class AutoUpdatePolicy
{
    internal static bool Allows(string currentVersion, string latestVersion, string level)
    {
        if (!SemVersion.TryParse(currentVersion, out var current) ||
            !SemVersion.TryParse(latestVersion, out var latest))
            throw new UpdateException(UpdateErrorCode.InvalidVersion, "The update has an invalid version stamp.");

        return latest!.Value.CompareTo(current!.Value) > 0 && level.ToLowerInvariant() switch
        {
            "major" => true,
            "minor" => latest.Value.Major == current.Value.Major,
            "patch" => latest.Value.Major == current.Value.Major && latest.Value.Minor == current.Value.Minor,
            _ => false,
        };
    }

    internal static string SkipMessage(string level) =>
        $"The latest stable release is outside the configured autoUpdateLevel '{level}'. No update was installed.";
}
