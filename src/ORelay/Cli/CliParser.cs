using ORelay.Configuration;

namespace ORelay.Cli;

public static class CliParser
{
    public static CliParseResult Parse(IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return CliParseResult.Failure("arguments cannot be null");
        }

        var commandArguments = new List<string>(args.Count);
        var settingValues = new Dictionary<string, string?>(StringComparer.Ordinal);
        var isJson = false;
        string? configFile = null;
        var requestedHelp = false;
        var requestedVersion = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index] ?? string.Empty;

            if (argument.Equals("--json", StringComparison.Ordinal))
            {
                isJson = true;
                continue;
            }

            if (argument.Equals("--help", StringComparison.Ordinal) || argument.Equals("-h", StringComparison.Ordinal))
            {
                requestedHelp = true;
                continue;
            }

            if (argument.Equals("--version", StringComparison.Ordinal) || argument.Equals("-v", StringComparison.Ordinal))
            {
                requestedVersion = true;
                continue;
            }

            if (argument.Equals("--config-file", StringComparison.Ordinal))
            {
                if (configFile is not null)
                {
                    return CliParseResult.Failure("--config-file may be specified only once");
                }

                if (!TryReadOptionValue(args, ref index, out configFile))
                {
                    return CliParseResult.Failure("--config-file requires a path");
                }

                continue;
            }

            if (argument.StartsWith("--config-file=", StringComparison.Ordinal))
            {
                if (configFile is not null)
                {
                    return CliParseResult.Failure("--config-file may be specified only once");
                }

                configFile = argument["--config-file=".Length..];
                if (string.IsNullOrWhiteSpace(configFile))
                {
                    return CliParseResult.Failure("--config-file requires a path");
                }

                continue;
            }

            if (TryGetSettingOption(argument, out var settingKey, out var inlineValue))
            {
                if (settingValues.ContainsKey(settingKey))
                {
                    return CliParseResult.Failure($"--{GetOptionName(settingKey)} may be specified only once");
                }

                string? value = inlineValue;
                if (value is null && !TryReadOptionValue(args, ref index, out value))
                {
                    return CliParseResult.Failure($"--{GetOptionName(settingKey)} requires a value");
                }

                settingValues.Add(settingKey, value);
                continue;
            }

            commandArguments.Add(argument);
        }

        if (requestedHelp && requestedVersion)
        {
            return CliParseResult.Failure("--help and --version cannot be used together");
        }

        var settings = ParseSettings(settingValues, configFile);
        if (!settings.IsSuccess)
        {
            return settings;
        }

        if (commandArguments.Count == 0)
        {
            if (requestedHelp)
            {
                return CliParseResult.Success(new CliOptions(CliCommand.Help, isJson, configFile, SettingsPatch: settings.Options?.SettingsPatch));
            }

            if (requestedVersion)
            {
                return CliParseResult.Success(new CliOptions(CliCommand.Version, isJson, configFile, SettingsPatch: settings.Options?.SettingsPatch));
            }

            if (isJson || configFile is not null || settings.Options?.SettingsPatch is not null)
            {
                return CliParseResult.Failure("global options require a command, --help, or --version");
            }

            return CliParseResult.Success(new CliOptions(CliCommand.Help, false, null));
        }

        if (requestedVersion)
        {
            return CliParseResult.Failure("--version applies to the top-level command only");
        }

        var parsed = ParseCommand(commandArguments, isJson, configFile, settings.Options?.SettingsPatch, requestedHelp);
        if (!parsed.IsSuccess || !requestedHelp)
        {
            return parsed;
        }

        var options = parsed.Options!;
        return CliParseResult.Success(options with
        {
            Help = new CliHelpRequest(options.Command, GetHelpTopic(commandArguments))
        });
    }

    private static CliParseResult ParseCommand(
        List<string> arguments,
        bool isJson,
        string? configFile,
        RelaySettingsPatch? settings,
        bool requestedHelp)
    {
        var command = arguments[0];
        return command.ToLowerInvariant() switch
        {
            "init" => ParseInit(arguments, isJson, configFile, settings),
            "setup" => ParseSetup(arguments, isJson, configFile, settings),
            "config" => ParseConfig(arguments, isJson, configFile, settings, requestedHelp),
            "server" => ParseServer(arguments, isJson, configFile, settings),
            "doctor" => ParseDoctor(arguments, isJson, configFile, settings),
            "service" => ParseService(arguments, isJson, configFile, settings, requestedHelp),
            "update" => ParseInstallation(arguments, isJson, configFile, settings, install: false),
            "install" => ParseInstallation(arguments, isJson, configFile, settings, install: true),
            _ => CliParseResult.Failure($"unknown command '{command}'")
        };
    }

    private static CliParseResult ParseInit(
        List<string> arguments,
        bool isJson,
        string? configFile,
        RelaySettingsPatch? settings)
    {
        return arguments.Count == 1
            ? CliParseResult.Success(new CliOptions(CliCommand.Init, isJson, configFile, SettingsPatch: settings))
            : CliParseResult.Failure("init does not accept positional arguments or command options");
    }

    private static CliParseResult ParseSetup(
        List<string> arguments, bool isJson, string? configFile, RelaySettingsPatch? settings)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "--defaults" or "--yes" or "--if-needed" or "--start" or "--enable-startup" or "--add-to-path" or "--skip-path")
            {
                if (!flags.Add(argument)) return CliParseResult.Failure($"{argument} may be specified only once");
                continue;
            }

            var matched = false;
            foreach (var name in new[] { "--access", "--mode", "--name" })
            {
                if (TryReadNamedOption(arguments, ref index, name, out var value, out var error))
                {
                    if (!values.TryAdd(name, value!)) return CliParseResult.Failure($"{name} may be specified only once");
                    matched = true;
                    break;
                }
                if (error is not null) return CliParseResult.Failure(error);
            }
            if (!matched) return CliParseResult.Failure($"unknown setup argument '{argument}'");
        }

        values.TryGetValue("--access", out var access);
        values.TryGetValue("--mode", out var mode);
        values.TryGetValue("--name", out var serviceName);
        if (access is not (null or "local" or "lan" or "tailscale"))
            return CliParseResult.Failure("--access must be local, lan, or tailscale");
        if (mode is not (null or "foreground" or "service"))
            return CliParseResult.Failure("--mode must be foreground or service");
        if (flags.Contains("--defaults") && (settings is not null || access is not null || mode is not null || flags.Contains("--start") || flags.Contains("--enable-startup")))
            return CliParseResult.Failure("--defaults cannot be combined with custom settings or service actions");
        if (flags.Contains("--yes") && mode != "service" && (flags.Contains("--start") || flags.Contains("--enable-startup")))
            return CliParseResult.Failure("--start and --enable-startup require --mode service");
        if (flags.Contains("--add-to-path") && flags.Contains("--skip-path"))
            return CliParseResult.Failure("--add-to-path and --skip-path cannot be combined");
        return CliParseResult.Success(new CliOptions(CliCommand.Setup, isJson, configFile, SettingsPatch: settings,
            Setup: new SetupCommandOptions(flags.Contains("--defaults"), flags.Contains("--yes"), flags.Contains("--if-needed"),
                access, mode, serviceName, flags.Contains("--start"), flags.Contains("--enable-startup"),
                flags.Contains("--add-to-path"), flags.Contains("--skip-path"))));
    }

    private static CliParseResult ParseConfig(
        List<string> arguments,
        bool isJson,
        string? configFile,
        RelaySettingsPatch? settings,
        bool requestedHelp)
    {
        if (settings is not null)
        {
            return CliParseResult.Failure("setting options can only be used with init, setup, or server");
        }

        if (arguments.Count < 2)
        {
            return requestedHelp
                ? CliParseResult.Success(new CliOptions(CliCommand.Config, isJson, configFile))
                : CliParseResult.Failure("config requires get, set, or clear");
        }

        var action = arguments[1].ToLowerInvariant();
        var options = action switch
        {
            "get" => arguments.Count switch
            {
                2 => new ConfigCommandOptions(ConfigAction.Get, null, null),
                3 => new ConfigCommandOptions(ConfigAction.Get, arguments[2], null),
                _ => null
            },
            "set" => arguments.Count == 4
                ? new ConfigCommandOptions(ConfigAction.Set, arguments[2], arguments[3])
                : null,
            "clear" => arguments.Count == 3
                ? new ConfigCommandOptions(ConfigAction.Clear, arguments[2], null)
                : null,
            _ => null
        };

        if (options is null)
        {
            return action switch
            {
                "get" => CliParseResult.Failure("config get accepts at most one key"),
                "set" => CliParseResult.Failure("config set requires a key and value"),
                "clear" => CliParseResult.Failure("config clear requires a key"),
                _ => CliParseResult.Failure($"unknown config action '{arguments[1]}'")
            };
        }

        return CliParseResult.Success(new CliOptions(CliCommand.Config, isJson, configFile, Config: options));
    }

    private static CliParseResult ParseServer(
        List<string> arguments,
        bool isJson,
        string? configFile,
        RelaySettingsPatch? settings)
    {
        string? serviceName = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (TryReadNamedOption(arguments, ref index, "--service-name", out var requestedName, out var nameError))
            {
                if (serviceName is not null)
                {
                    return CliParseResult.Failure("--service-name may be specified only once");
                }

                serviceName = requestedName;
                continue;
            }

            if (nameError is not null)
            {
                return CliParseResult.Failure(nameError);
            }

            return CliParseResult.Failure($"unknown server argument '{arguments[index]}'");
        }

        return CliParseResult.Success(new CliOptions(
            CliCommand.Server,
            isJson,
            configFile,
            Server: new ServerCommandOptions(settings?.Port, settings?.Bind, serviceName),
            SettingsPatch: settings));
    }

    private static CliParseResult ParseDoctor(
        List<string> arguments,
        bool isJson,
        string? configFile,
        RelaySettingsPatch? settings)
    {
        if (settings is not null)
        {
            return CliParseResult.Failure("setting options can only be used with init, setup, or server");
        }

        var fix = false;
        string? serviceName = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            if (arguments[index].Equals("--fix", StringComparison.Ordinal))
            {
                if (fix)
                {
                    return CliParseResult.Failure("--fix may be specified only once");
                }

                fix = true;
                continue;
            }

            if (TryReadNamedOption(arguments, ref index, "--name", out var requestedName, out var nameError))
            {
                if (serviceName is not null)
                {
                    return CliParseResult.Failure("--name may be specified only once");
                }

                serviceName = requestedName;
                continue;
            }

            if (nameError is not null)
            {
                return CliParseResult.Failure(nameError);
            }

            return CliParseResult.Failure($"unknown doctor argument '{arguments[index]}'");
        }

        return CliParseResult.Success(new CliOptions(
            CliCommand.Doctor,
            isJson,
            configFile,
            Doctor: new DoctorCommandOptions(fix, serviceName)));
    }

    private static CliParseResult ParseService(
        List<string> arguments,
        bool isJson,
        string? configFile,
        RelaySettingsPatch? settings,
        bool requestedHelp)
    {
        if (settings is not null)
        {
            return CliParseResult.Failure("setting options can only be used with init, setup, or server");
        }

        if (arguments.Count < 2)
        {
            return requestedHelp && arguments.Count == 1
                ? CliParseResult.Success(new CliOptions(CliCommand.Service, isJson, configFile))
                : CliParseResult.Failure("service requires install, start, stop, restart, status, uninstall, enable, or disable");
        }

        if (!Enum.TryParse<ServiceAction>(arguments[1], ignoreCase: true, out var action) || !Enum.IsDefined(action) || int.TryParse(arguments[1], out _))
        {
            return CliParseResult.Failure($"unknown service action '{arguments[1]}'");
        }

        string? serviceName = null;
        for (var index = 2; index < arguments.Count; index++)
        {
            if (TryReadNamedOption(arguments, ref index, "--name", out var requestedName, out var nameError))
            {
                if (serviceName is not null)
                {
                    return CliParseResult.Failure("--name may be specified only once");
                }

                serviceName = requestedName;
                continue;
            }

            if (nameError is not null)
            {
                return CliParseResult.Failure(nameError);
            }

            return CliParseResult.Failure($"unknown service argument '{arguments[index]}'");
        }

        return CliParseResult.Success(new CliOptions(
            CliCommand.Service,
            isJson,
            configFile,
            Service: new ServiceCommandOptions(action, serviceName)));
    }

    private static CliParseResult ParseInstallation(
        List<string> arguments,
        bool isJson,
        string? configFile,
        RelaySettingsPatch? settings,
        bool install)
    {
        if (settings is not null)
        {
            return CliParseResult.Failure("setting options can only be used with init, setup, or server");
        }

        var check = false;
        var restartService = false;
        string? name = null;
        string? directory = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!install && argument == "--check")
            {
                if (check) return CliParseResult.Failure("--check may be specified only once");
                check = true;
                continue;
            }

            if (argument == "--restart-service")
            {
                if (restartService) return CliParseResult.Failure("--restart-service may be specified only once");
                restartService = true;
                continue;
            }

            if (TryReadNamedOption(arguments, ref index, "--name", out var value, out var error))
            {
                if (name is not null) return CliParseResult.Failure("--name may be specified only once");
                name = value;
                continue;
            }
            if (error is not null) return CliParseResult.Failure(error);

            if (install)
            {
                if (TryReadNamedOption(arguments, ref index, "--install-dir", out value, out error))
                {
                    if (directory is not null) return CliParseResult.Failure("--install-dir may be specified only once");
                    directory = value;
                    continue;
                }
                if (error is not null) return CliParseResult.Failure(error);
            }

            return CliParseResult.Failure($"unknown {(install ? "install" : "update")} argument '{argument}'");
        }

        if (check && restartService)
            return CliParseResult.Failure("--check cannot be combined with --restart-service");

        return install
            ? CliParseResult.Success(new CliOptions(CliCommand.Install, isJson, configFile,
                Install: new InstallCommandOptions(directory, restartService, name)))
            : CliParseResult.Success(new CliOptions(CliCommand.Update, isJson, configFile,
                Update: new UpdateCommandOptions(check, restartService, name)));
    }

    private static bool TryReadNamedOption(
        List<string> arguments,
        ref int index,
        string option,
        out string? value,
        out string? error)
    {
        value = null;
        error = null;
        var argument = arguments[index];
        if (argument.Equals(option, StringComparison.Ordinal))
        {
            if (!TryReadOptionValue(arguments, ref index, out var optionValue))
            {
                error = $"{option} requires a value";
                return false;
            }

            value = optionValue;
            return true;
        }

        var prefix = option + "=";
        if (!argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var inlineValue = argument[prefix.Length..];
        if (string.IsNullOrWhiteSpace(inlineValue))
        {
            error = $"{option} requires a value";
            return false;
        }

        value = inlineValue;
        return true;
    }

    private static CliParseResult ParseSettings(
        Dictionary<string, string?> values,
        string? configFile)
    {
        if (values.Count == 0)
        {
            return CliParseResult.Success(new CliOptions(CliCommand.Help, false, null));
        }

        try
        {
            return CliParseResult.Success(new CliOptions(
                CliCommand.Help,
                false,
                configFile,
                SettingsPatch: RelaySettingsValidator.ParsePatch(values, configFile)));
        }
        catch (RelayConfigurationException exception)
        {
            return CliParseResult.Failure(exception.Message);
        }
    }

    private static string? GetHelpTopic(List<string> arguments) =>
        arguments.Count > 1 ? arguments[1] : null;

    private static bool TryGetSettingOption(string argument, out string settingKey, out string? inlineValue)
    {
        inlineValue = null;
        foreach (var pair in SettingOptions)
        {
            if (argument.Equals(pair.Option, StringComparison.Ordinal))
            {
                settingKey = pair.Key;
                return true;
            }

            var prefix = pair.Option + "=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                settingKey = pair.Key;
                inlineValue = argument[prefix.Length..];
                if (inlineValue.Length == 0)
                {
                    inlineValue = null;
                }

                return true;
            }
        }

        settingKey = string.Empty;
        return false;
    }

    private static bool TryReadOptionValue(
        IReadOnlyList<string> arguments,
        ref int index,
        out string value)
    {
        if (index + 1 < arguments.Count &&
            !string.IsNullOrWhiteSpace(arguments[index + 1]) &&
            !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = arguments[++index];
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string GetOptionName(string key) =>
        SettingOptions.First(pair => pair.Key.Equals(key, StringComparison.Ordinal)).Option[2..];

    private static readonly SettingOption[] SettingOptions =
    [
        new("port", "--port"),
        new("bind", "--bind"),
        new("publicUrl", "--public-url"),
        new("hostname", "--hostname"),
        new("autoDiscovery", "--auto-discovery"),
        new("leaseSeconds", "--lease-seconds"),
        new("maxRegistrations", "--max-registrations"),
    ];

    private sealed record SettingOption(string Key, string Option);
}
