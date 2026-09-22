using System.Globalization;
using System.Net;

namespace ORelay.Configuration;

public static class RelaySettingsValidator
{
    public const int MinimumPort = 1;
    public const int MaximumPort = 65_535;
    public const int MinimumLeaseSeconds = 1;
    public const int MaximumLeaseSeconds = 86_400;
    public const int MinimumMaxRegistrations = 1;
    public const int MaximumMaxRegistrations = 1_000_000;

    public static void Validate(RelaySettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ValidatePort(settings.Port, path);
        ValidateBind(settings.Bind, path);
        ValidatePublicUrl(settings.PublicUrl, path);
        ValidateHostname(settings.Hostname, path);
        ValidateAutoDiscovery(settings.AutoDiscovery, path);
        ValidateLeaseSeconds(settings.LeaseSeconds, path);
        ValidateMaxRegistrations(settings.MaxRegistrations, path);
    }

    internal static RelaySettings ValidateAndReturn(RelaySettings settings, string? path = null)
    {
        Validate(settings, path);
        return settings;
    }

    public static bool TryParseKey(string key, out RelaySettingKey setting)
    {
        switch (key)
        {
            case "port":
                setting = RelaySettingKey.Port;
                return true;
            case "bind":
                setting = RelaySettingKey.Bind;
                return true;
            case "publicUrl":
                setting = RelaySettingKey.PublicUrl;
                return true;
            case "hostname":
                setting = RelaySettingKey.Hostname;
                return true;
            case "autoDiscovery":
                setting = RelaySettingKey.AutoDiscovery;
                return true;
            case "leaseSeconds":
                setting = RelaySettingKey.LeaseSeconds;
                return true;
            case "maxRegistrations":
                setting = RelaySettingKey.MaxRegistrations;
                return true;
            default:
                setting = default;
                return false;
        }
    }

    public static string GetKeyName(RelaySettingKey setting) => setting switch
    {
        RelaySettingKey.Port => "port",
        RelaySettingKey.Bind => "bind",
        RelaySettingKey.PublicUrl => "publicUrl",
        RelaySettingKey.Hostname => "hostname",
        RelaySettingKey.AutoDiscovery => "autoDiscovery",
        RelaySettingKey.LeaseSeconds => "leaseSeconds",
        RelaySettingKey.MaxRegistrations => "maxRegistrations",
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown relay setting."),
    };

    public static RelaySettingsPatch ParsePatch(IReadOnlyDictionary<string, string?> values, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        int? port = null;
        string? bind = null;
        string? publicUrl = null;
        string? hostname = null;
        string? autoDiscovery = null;
        int? leaseSeconds = null;
        int? maxRegistrations = null;

        foreach (var pair in values)
        {
            if (!TryParseKey(pair.Key, out var key))
            {
                throw InvalidValue(RelayConfigurationErrorCode.UnknownSetting, pair.Key, pair.Value, path,
                    $"Unknown setting '{pair.Key}'. Supported settings are port, bind, publicUrl, hostname, autoDiscovery, leaseSeconds, and maxRegistrations.");
            }

            switch (key)
            {
                case RelaySettingKey.Port:
                    port = ParseInt(pair.Key, pair.Value, path);
                    ValidatePort(port.Value, path, pair.Key);
                    break;
                case RelaySettingKey.Bind:
                    bind = RequireText(pair.Key, pair.Value, path);
                    ValidateBind(bind, path, pair.Key);
                    break;
                case RelaySettingKey.PublicUrl:
                    publicUrl = ParseNullableText(pair.Key, pair.Value, path);
                    ValidatePublicUrl(publicUrl, path, pair.Key);
                    break;
                case RelaySettingKey.Hostname:
                    hostname = ParseNullableText(pair.Key, pair.Value, path);
                    ValidateHostname(hostname, path, pair.Key);
                    break;
                case RelaySettingKey.AutoDiscovery:
                    autoDiscovery = RequireText(pair.Key, pair.Value, path).ToLowerInvariant();
                    ValidateAutoDiscovery(autoDiscovery, path, pair.Key);
                    break;
                case RelaySettingKey.LeaseSeconds:
                    leaseSeconds = ParseInt(pair.Key, pair.Value, path);
                    ValidateLeaseSeconds(leaseSeconds.Value, path, pair.Key);
                    break;
                case RelaySettingKey.MaxRegistrations:
                    maxRegistrations = ParseInt(pair.Key, pair.Value, path);
                    ValidateMaxRegistrations(maxRegistrations.Value, path, pair.Key);
                    break;
            }
        }

        return new RelaySettingsPatch(port, bind, publicUrl, hostname, autoDiscovery, leaseSeconds, maxRegistrations);
    }

    internal static void ValidateDocument(RelayConfigurationDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.SchemaVersion != RelayConfigurationStore.CurrentSchemaVersion)
        {
            throw new RelayConfigurationException(
                RelayConfigurationErrorCode.UnsupportedSchemaVersion,
                $"Configuration file '{path}' uses unsupported schemaVersion {document.SchemaVersion}. Supported version is {RelayConfigurationStore.CurrentSchemaVersion}.",
                path,
                "schemaVersion");
        }

        var effective = document.ToPatch().ApplyTo(RelayConfigurationDefaults.Settings);
        Validate(effective, path);
    }

    private static int ParseInt(string key, string? value, string? path)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value, path,
                $"Setting '{key}' must be an integer.");
        }

        return result;
    }

    private static string RequireText(string key, string? value, string? path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value, path,
                $"Setting '{key}' must be a non-empty value.");
        }

        return value.Trim();
    }

    private static string? ParseNullableText(string key, string? value, string? path)
    {
        if (value is null || value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return RequireText(key, value, path);
    }

    private static void ValidatePort(int value, string? path, string key = "port")
    {
        if (value is < MinimumPort or > MaximumPort)
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value.ToString(CultureInfo.InvariantCulture), path,
                $"Setting '{key}' must be between {MinimumPort} and {MaximumPort}.");
        }
    }

    private static void ValidateBind(string value, string? path, string key = "bind")
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Contains('/'))
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value, path,
                $"Setting '{key}' must be an IP address or host name without a URL path.");
        }

        if (value is not ("*" or "+") && !IsAddressOrHostName(value))
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value, path,
                $"Setting '{key}' must be an IP address or host name.");
        }
    }

    private static void ValidatePublicUrl(string? value, string? path, string key = "publicUrl")
    {
        if (value is null)
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Host is "0.0.0.0" or "::" or "*" or "+" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.Query))
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value, path,
                $"Setting '{key}' must be an absolute HTTP or HTTPS URL without credentials, query, or fragment.");
        }
    }

    private static void ValidateHostname(string? value, string? path, string key = "hostname")
    {
        if (value is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Contains('/') ||
            !IsAddressOrHostName(value))
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value, path,
                $"Setting '{key}' must be an IP address or host name.");
        }
    }

    private static void ValidateAutoDiscovery(string value, string? path, string key = "autoDiscovery")
    {
        if (value is not ("local" or "none" or "tailscale"))
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value, path,
                $"Setting '{key}' must be one of: local, none, tailscale.");
        }
    }

    private static void ValidateLeaseSeconds(int value, string? path, string key = "leaseSeconds")
    {
        if (value is < MinimumLeaseSeconds or > MaximumLeaseSeconds)
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value.ToString(CultureInfo.InvariantCulture), path,
                $"Setting '{key}' must be between {MinimumLeaseSeconds} and {MaximumLeaseSeconds} seconds.");
        }
    }

    private static void ValidateMaxRegistrations(int value, string? path, string key = "maxRegistrations")
    {
        if (value is < MinimumMaxRegistrations or > MaximumMaxRegistrations)
        {
            throw InvalidValue(RelayConfigurationErrorCode.InvalidValue, key, value.ToString(CultureInfo.InvariantCulture), path,
                $"Setting '{key}' must be between {MinimumMaxRegistrations} and {MaximumMaxRegistrations}.");
        }
    }

    private static RelayConfigurationException InvalidValue(
        RelayConfigurationErrorCode code,
        string setting,
        string? value,
        string? path,
        string message) => new(code, $"{message} Selected file: '{path ?? "(default)"}'.", path, setting);

    private static bool IsAddressOrHostName(string value)
    {
        var candidate = value.Trim('[', ']');
        return IPAddress.TryParse(candidate, out _) || Uri.CheckHostName(candidate) != UriHostNameType.Unknown;
    }
}
