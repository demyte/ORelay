namespace ORelay.Configuration;

/// <summary>Names of settings understood by the relay configuration file and CLI.</summary>
public enum RelaySettingKey
{
    Port,
    Bind,
    PublicUrl,
    Hostname,
    AutoDiscovery,
    LeaseSeconds,
    MaxRegistrations,
    AutoUpdate,
    AutoUpdateIntervalSeconds,
}

/// <summary>The built-in values used when a saved setting is absent.</summary>
public static class RelayConfigurationDefaults
{
    public const int Port = 12_987;
    public const string Bind = "127.0.0.1";
    public const string Hostname = "localhost";
    public const string AutoDiscovery = "none";
    public const int LeaseSeconds = 300;
    public const int MaxRegistrations = 1_000;
    public const bool AutoUpdate = false;
    public const int AutoUpdateIntervalSeconds = 86_400;

    public static RelaySettings Settings { get; } = new(
        Port,
        Bind,
        PublicUrl: null,
        Hostname,
        AutoDiscovery,
        LeaseSeconds,
        MaxRegistrations,
        AutoUpdate,
        AutoUpdateIntervalSeconds);
}

/// <summary>Effective settings after applying defaults, a saved file, and invocation overrides.</summary>
public sealed record RelaySettings(
    int Port,
    string Bind,
    string? PublicUrl,
    string? Hostname,
    string AutoDiscovery,
    int LeaseSeconds,
    int MaxRegistrations,
    bool AutoUpdate = RelayConfigurationDefaults.AutoUpdate,
    int AutoUpdateIntervalSeconds = RelayConfigurationDefaults.AutoUpdateIntervalSeconds)
{
    public RelaySettings()
        : this(
            RelayConfigurationDefaults.Port,
            RelayConfigurationDefaults.Bind,
            null,
            RelayConfigurationDefaults.Hostname,
            RelayConfigurationDefaults.AutoDiscovery,
            RelayConfigurationDefaults.LeaseSeconds,
            RelayConfigurationDefaults.MaxRegistrations,
            RelayConfigurationDefaults.AutoUpdate,
            RelayConfigurationDefaults.AutoUpdateIntervalSeconds)
    {
    }
}

/// <summary>
/// Optional values used to apply saved or command-line settings. A null value means that
/// the setting was not supplied by this patch.
/// </summary>
public sealed record RelaySettingsPatch(
    int? Port = null,
    string? Bind = null,
    string? PublicUrl = null,
    string? Hostname = null,
    string? AutoDiscovery = null,
    int? LeaseSeconds = null,
    int? MaxRegistrations = null,
    bool? AutoUpdate = null,
    int? AutoUpdateIntervalSeconds = null)
{
    public bool IsEmpty =>
        Port is null &&
        Bind is null &&
        PublicUrl is null &&
        Hostname is null &&
        AutoDiscovery is null &&
        LeaseSeconds is null &&
        MaxRegistrations is null &&
        AutoUpdate is null &&
        AutoUpdateIntervalSeconds is null;

    public RelaySettings ApplyTo(RelaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings with
        {
            Port = Port ?? settings.Port,
            Bind = Bind ?? settings.Bind,
            PublicUrl = PublicUrl ?? settings.PublicUrl,
            Hostname = Hostname ?? settings.Hostname,
            AutoDiscovery = AutoDiscovery ?? settings.AutoDiscovery,
            LeaseSeconds = LeaseSeconds ?? settings.LeaseSeconds,
            MaxRegistrations = MaxRegistrations ?? settings.MaxRegistrations,
            AutoUpdate = AutoUpdate ?? settings.AutoUpdate,
            AutoUpdateIntervalSeconds = AutoUpdateIntervalSeconds ?? settings.AutoUpdateIntervalSeconds,
        };
    }

}
