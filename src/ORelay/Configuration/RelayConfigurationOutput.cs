using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ORelay.Configuration;

public static class RelayConfigurationExitCodes
{
    public const int Success = 0;
    public const int DiagnosticFailure = 1;
    public const int UsageError = 2;
    public const int ConfigurationError = 3;
}

internal static class RelayConfigurationOutput
{
    internal static string? GetValue(RelaySettings settings, RelaySettingKey key) => key switch
    {
        RelaySettingKey.Port => settings.Port.ToString(CultureInfo.InvariantCulture),
        RelaySettingKey.Bind => settings.Bind,
        RelaySettingKey.PublicUrl => settings.PublicUrl,
        RelaySettingKey.Hostname => settings.Hostname,
        RelaySettingKey.AutoDiscovery => settings.AutoDiscovery,
        RelaySettingKey.LeaseSeconds => settings.LeaseSeconds.ToString(CultureInfo.InvariantCulture),
        RelaySettingKey.MaxRegistrations => settings.MaxRegistrations.ToString(CultureInfo.InvariantCulture),
        RelaySettingKey.AutoUpdate => settings.AutoUpdate.ToString().ToLowerInvariant(),
        RelaySettingKey.AutoUpdateIntervalSeconds => settings.AutoUpdateIntervalSeconds.ToString(CultureInfo.InvariantCulture),
        RelaySettingKey.AutoUpdateLevel => settings.AutoUpdateLevel,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown relay setting."),
    };

    internal static JsonElement GetJsonValue(RelaySettings settings, RelaySettingKey key) => key switch
    {
        RelaySettingKey.Port => ParseJson(settings.Port.ToString(CultureInfo.InvariantCulture)),
        RelaySettingKey.Bind => ParseJsonString(settings.Bind),
        RelaySettingKey.PublicUrl => ParseJsonString(settings.PublicUrl),
        RelaySettingKey.Hostname => ParseJsonString(settings.Hostname),
        RelaySettingKey.AutoDiscovery => ParseJsonString(settings.AutoDiscovery),
        RelaySettingKey.LeaseSeconds => ParseJson(settings.LeaseSeconds.ToString(CultureInfo.InvariantCulture)),
        RelaySettingKey.MaxRegistrations => ParseJson(settings.MaxRegistrations.ToString(CultureInfo.InvariantCulture)),
        RelaySettingKey.AutoUpdate => ParseJson(settings.AutoUpdate ? "true" : "false"),
        RelaySettingKey.AutoUpdateIntervalSeconds => ParseJson(settings.AutoUpdateIntervalSeconds.ToString(CultureInfo.InvariantCulture)),
        RelaySettingKey.AutoUpdateLevel => ParseJsonString(settings.AutoUpdateLevel),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown relay setting."),
    };

    internal static string FormatSettings(RelaySettings settings)
    {
        var builder = new StringBuilder();
        foreach (var key in Enum.GetValues<RelaySettingKey>())
        {
            builder.Append(RelaySettingsValidator.GetKeyName(key));
            builder.Append('=');
            builder.AppendLine(GetValue(settings, key) ?? "null");
        }

        return builder.ToString().TrimEnd();
    }

    internal static string Serialize(object value) => value switch
    {
        ConfigurationValueOutput output => JsonSerializer.Serialize(output, RelayConfigurationJsonContext.Default.ConfigurationValueOutput),
        ConfigurationViewOutput output => JsonSerializer.Serialize(output, RelayConfigurationJsonContext.Default.ConfigurationViewOutput),
        ConfigurationActionOutput output => JsonSerializer.Serialize(output, RelayConfigurationJsonContext.Default.ConfigurationActionOutput),
        ConfigurationDoctorOutput output => JsonSerializer.Serialize(output, RelayConfigurationJsonContext.Default.ConfigurationDoctorOutput),
        ConfigurationErrorOutput output => JsonSerializer.Serialize(output, RelayConfigurationJsonContext.Default.ConfigurationErrorOutput),
        string text => text,
        _ => throw new InvalidOperationException($"Unsupported configuration result type '{value.GetType().Name}'."),
    };

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static JsonElement ParseJsonString(string? value)
    {
        if (value is null)
        {
            return ParseJson("null");
        }

        var escaped = JsonEncodedText.Encode(value).ToString();
        return ParseJson($"\"{escaped}\"");
    }
}

public sealed record ConfigurationValueOutput(string ConfigFile, string Key, JsonElement Value);

public sealed record ConfigurationViewOutput(
    string ConfigFile,
    bool SavedFileExists,
    RelaySettings Defaults,
    RelaySettings Effective);

public sealed record ConfigurationActionOutput(
    string Action,
    string ConfigFile,
    bool SavedFileExists,
    RelaySettings Effective,
    string Message);

public sealed record ConfigurationDoctorOutput(
    string ConfigFile,
    bool Exists,
    bool Valid,
    string Message);

public sealed record ConfigurationErrorOutput(
    string Code,
    string Message,
    string? ConfigFile,
    string? Setting);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ConfigurationValueOutput))]
[JsonSerializable(typeof(ConfigurationViewOutput))]
[JsonSerializable(typeof(ConfigurationActionOutput))]
[JsonSerializable(typeof(ConfigurationDoctorOutput))]
[JsonSerializable(typeof(ConfigurationErrorOutput))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(RelaySettings))]
internal sealed partial class RelayConfigurationJsonContext : JsonSerializerContext
{
}
