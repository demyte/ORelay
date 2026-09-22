using ORelay.Cli;

namespace ORelay.Configuration;

/// <summary>Executes parsed init and config commands directly against the selected store.</summary>
public static class ConfigurationCommand
{
    public static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Command is not (CliCommand.Init or CliCommand.Config))
        {
            await error.WriteLineAsync("error: configuration command received an unsupported CLI command.").ConfigureAwait(false);
            return CliExitCodes.UsageError;
        }

        try
        {
            var store = new RelayConfigurationStore(options.ConfigFile);
            if (options.Command == CliCommand.Init)
            {
                return await ExecuteInitAsync(store, options, output, cancellationToken).ConfigureAwait(false);
            }

            return await ExecuteConfigAsync(store, options, output, cancellationToken).ConfigureAwait(false);
        }
        catch (RelayConfigurationException exception)
        {
            if (options.IsJson)
            {
                await output.WriteLineAsync(RelayConfigurationOutput.Serialize(new ConfigurationErrorOutput(
                    exception.Code.ToString(),
                    exception.Message,
                    exception.Path,
                    exception.Setting))).ConfigureAwait(false);
            }

            await error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return exception.Code == RelayConfigurationErrorCode.UnknownSetting
                ? CliExitCodes.UsageError
                : RelayConfigurationExitCodes.ConfigurationError;
        }
        catch (ArgumentException exception)
        {
            if (options.IsJson)
            {
                await output.WriteLineAsync(RelayConfigurationOutput.Serialize(new ConfigurationErrorOutput(
                    "usage_error",
                    exception.Message,
                    options.ConfigFile,
                    null))).ConfigureAwait(false);
            }

            await error.WriteLineAsync($"error: {exception.Message}").ConfigureAwait(false);
            return CliExitCodes.UsageError;
        }
    }

    private static async Task<int> ExecuteInitAsync(
        RelayConfigurationStore store,
        CliOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var settings = store.Init(options.SettingsPatch);
        var result = new ConfigurationActionOutput(
            "init",
            store.FilePath,
            store.Exists,
            settings,
            "Configuration initialized or already valid.");

        await WriteOutputAsync(
            options.IsJson ? RelayConfigurationOutput.Serialize(result) : result.Message,
            output,
            cancellationToken).ConfigureAwait(false);
        return CliExitCodes.Success;
    }

    private static async Task<int> ExecuteConfigAsync(
        RelayConfigurationStore store,
        CliOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var config = options.Config ?? throw new ArgumentException("Config options are required.", nameof(options));
        switch (config.Action)
        {
            case ConfigAction.Get:
                return await ExecuteGetAsync(store, options.IsJson, config.Key, output, cancellationToken).ConfigureAwait(false);
            case ConfigAction.Set:
                {
                    var key = NormalizeKey(config.Key ?? throw new ArgumentException("config set requires a key."));
                    var keyName = RelaySettingsValidator.GetKeyName(key);
                    var settings = store.Set(keyName, config.Value ?? throw new ArgumentException("config set requires a value."));
                    var result = new ConfigurationActionOutput(
                        "set",
                        store.FilePath,
                        store.Exists,
                        settings,
                        $"Saved {keyName} in '{store.FilePath}'.");
                    await WriteOutputAsync(
                        options.IsJson ? RelayConfigurationOutput.Serialize(result) : result.Message,
                        output,
                        cancellationToken).ConfigureAwait(false);
                    return CliExitCodes.Success;
                }
            case ConfigAction.Clear:
                {
                    var key = NormalizeKey(config.Key ?? throw new ArgumentException("config clear requires a key."));
                    var keyName = RelaySettingsValidator.GetKeyName(key);
                    var settings = store.Clear(keyName);
                    var result = new ConfigurationActionOutput(
                        "clear",
                        store.FilePath,
                        store.Exists,
                        settings,
                        $"Cleared {keyName}; the built-in default now applies.");
                    await WriteOutputAsync(
                        options.IsJson ? RelayConfigurationOutput.Serialize(result) : result.Message,
                        output,
                        cancellationToken).ConfigureAwait(false);
                    return CliExitCodes.Success;
                }
            default:
                throw new ArgumentException($"Unknown config action '{config.Action}'.", nameof(options));
        }
    }

    private static async Task<int> ExecuteGetAsync(
        RelayConfigurationStore store,
        bool json,
        string? requestedKey,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var settings = store.Read();
        if (requestedKey is not null)
        {
            var key = NormalizeKey(requestedKey);
            var value = RelayConfigurationOutput.GetValue(settings, key);
            var structured = new ConfigurationValueOutput(
                store.FilePath,
                RelaySettingsValidator.GetKeyName(key),
                RelayConfigurationOutput.GetJsonValue(settings, key));
            await WriteOutputAsync(
                json ? RelayConfigurationOutput.Serialize(structured) : value ?? "null",
                output,
                cancellationToken).ConfigureAwait(false);
            return CliExitCodes.Success;
        }

        var view = new ConfigurationViewOutput(
            store.FilePath,
            store.Exists,
            RelayConfigurationDefaults.Settings,
            settings);
        await WriteOutputAsync(
            json ? RelayConfigurationOutput.Serialize(view) : RelayConfigurationOutput.FormatSettings(settings),
            output,
            cancellationToken).ConfigureAwait(false);
        return CliExitCodes.Success;
    }

    private static async Task WriteOutputAsync(
        string value,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync(value).ConfigureAwait(false);
    }

    private static RelaySettingKey NormalizeKey(string key)
    {
        var normalized = key.TrimStart('-') switch
        {
            "public-url" => "publicUrl",
            "auto-discovery" => "autoDiscovery",
            "lease-seconds" => "leaseSeconds",
            "max-registrations" => "maxRegistrations",
            var value => value,
        };

        if (RelaySettingsValidator.TryParseKey(normalized, out var parsed))
        {
            return parsed;
        }

        throw new RelayConfigurationException(
            RelayConfigurationErrorCode.UnknownSetting,
            $"Unknown setting '{key}'. Supported settings are port, bind, publicUrl, hostname, autoDiscovery, leaseSeconds, and maxRegistrations.",
            setting: key);
    }
}
