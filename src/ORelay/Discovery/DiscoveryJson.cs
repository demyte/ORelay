using System.Text.Json;
using System.Text.Json.Serialization;

namespace ORelay.Discovery;

/// <summary>Source-generated JSON for callers that return discovery results from a CLI or host.</summary>
public static class DiscoveryJson
{
    public static string Serialize(CallbackDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, DiscoveryJsonContext.Default.CallbackDiscoveryResult);
    }

    public static string Serialize(RelaySettingsDiscoveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return JsonSerializer.Serialize(result, DiscoveryJsonContext.Default.RelaySettingsDiscoveryResult);
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(CallbackDiscoveryResult))]
[JsonSerializable(typeof(RelaySettingsDiscoveryResult))]
internal sealed partial class DiscoveryJsonContext : JsonSerializerContext;
