using System.Text.Json.Serialization;

namespace ORelay.Configuration;

/// <summary>The versioned, nullable-overrides representation stored in orelay.json.</summary>
public sealed class RelayConfigurationDocument
{
    public int SchemaVersion { get; set; } = RelayConfigurationStore.CurrentSchemaVersion;

    public int? Port { get; set; }

    public string? Bind { get; set; }

    public string? PublicUrl { get; set; }

    public string? Hostname { get; set; }

    public string? AutoDiscovery { get; set; }

    public int? LeaseSeconds { get; set; }

    public int? MaxRegistrations { get; set; }

    internal RelaySettingsPatch ToPatch() => new(
        Port,
        Bind,
        PublicUrl,
        Hostname,
        AutoDiscovery,
        LeaseSeconds,
        MaxRegistrations);

    internal static RelayConfigurationDocument FromSettings(RelaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new RelayConfigurationDocument
        {
            SchemaVersion = RelayConfigurationStore.CurrentSchemaVersion,
            Port = settings.Port,
            Bind = settings.Bind,
            PublicUrl = settings.PublicUrl,
            Hostname = settings.Hostname,
            AutoDiscovery = settings.AutoDiscovery,
            LeaseSeconds = settings.LeaseSeconds,
            MaxRegistrations = settings.MaxRegistrations,
        };
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RelayConfigurationDocument))]
internal sealed partial class RelaySettingsJsonContext : JsonSerializerContext
{
}
