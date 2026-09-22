namespace ORelay.Configuration;

public enum RelayConfigurationErrorCode
{
    InvalidJson,
    InvalidValue,
    UnknownSetting,
    UnsupportedSchemaVersion,
    Conflict,
    FileAccess,
}

/// <summary>An actionable failure while reading or changing the selected config file.</summary>
public sealed class RelayConfigurationException : Exception
{
    public RelayConfigurationException(
        RelayConfigurationErrorCode code,
        string message,
        string? path = null,
        string? setting = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Path = path;
        Setting = setting;
    }

    public RelayConfigurationErrorCode Code { get; }

    public string? Path { get; }

    public string? Setting { get; }
}
