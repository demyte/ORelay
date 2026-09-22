using System.Reflection;
using System.Text;
using ORelay.Configuration;

namespace ORelay.Cli;

public static class CliApplication
{
    public static string Version { get; } = typeof(CliApplication).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string>? args,
        TextWriter? output = null,
        TextWriter? error = null,
        Func<CliOptions, TextWriter, TextWriter, Task<int>>? commandHandler = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        var result = CliParser.Parse(args);
        if (!result.IsSuccess)
        {
            await error.WriteLineAsync($"error: {result.Error}").ConfigureAwait(false);
            await error.WriteLineAsync("Run 'orelay --help' for usage.").ConfigureAwait(false);
            return CliExitCodes.UsageError;
        }

        var options = result.Options!;
        if (options.Help is not null)
        {
            await output.WriteLineAsync(GetCommandHelp(options.Help)).ConfigureAwait(false);
            return CliExitCodes.Success;
        }

        switch (options.Command)
        {
            case CliCommand.Help:
                await output.WriteLineAsync(GetHelpText()).ConfigureAwait(false);
                return CliExitCodes.Success;
            case CliCommand.Version:
                if (options.IsJson)
                {
                    await output.WriteLineAsync($"{{\"version\":\"{Version}\"}}").ConfigureAwait(false);
                }
                else
                {
                    await output.WriteLineAsync($"orelay {Version}").ConfigureAwait(false);
                }

                return CliExitCodes.Success;
            default:
                if (commandHandler is not null)
                {
                    return await commandHandler(options, output, error).ConfigureAwait(false);
                }

                await error.WriteLineAsync($"error: the '{GetCommandName(options.Command)}' command is not available in this build").ConfigureAwait(false);
                await error.WriteLineAsync("Run 'orelay --help' to see the commands that are available.").ConfigureAwait(false);
                return CliExitCodes.CommandUnavailable;
        }
    }

    public static string GetHelpText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("ORelay command-line interface");
        builder.AppendLine();
        builder.AppendLine("Usage:");
        builder.AppendLine("  orelay [--help]");
        builder.AppendLine("  orelay --version");
        builder.AppendLine();
        builder.AppendLine("Options:");
        builder.AppendLine("  -h, --help       Show this help and exit with code 0.");
        builder.AppendLine("  -v, --version    Show the version and exit with code 0.");
        builder.AppendLine("      --json       Write command results as JSON where supported.");
        builder.AppendLine("      --config-file <path>");
        builder.AppendLine("                   Select the configuration file for this command.");
        builder.AppendLine();
        builder.AppendLine("Commands:");
        builder.AppendLine("  init             Create the selected configuration file.");
        builder.AppendLine("  config           Read or update saved configuration values.");
        builder.AppendLine("  server           Run the relay in the foreground.");
        builder.AppendLine("  doctor           Check configuration, health, port, and discovery state.");
        builder.AppendLine("  service          Install or control the platform service.");
        builder.AppendLine();
        builder.AppendLine("Exit codes:");
        builder.AppendLine("  0                Command completed successfully.");
        builder.AppendLine("  1                Doctor found a failed check.");
        builder.AppendLine("  3                Command could not complete.");
        builder.AppendLine("  64               Invalid command-line input.");
        builder.AppendLine("  69               Command is unavailable on this build or platform.");
        return builder.ToString().TrimEnd();
    }

    private static string GetCommandHelp(CliHelpRequest request)
    {
        var builder = new StringBuilder();
        switch (request.Command)
        {
            case CliCommand.Init:
                AppendSettingsHelp(builder, "orelay init", "Create orelay.json if it does not exist. Existing valid content is preserved.");
                builder.AppendLine("Example:");
                builder.AppendLine("  orelay --config-file .run/orelay.json init --port 12987");
                break;
            case CliCommand.Config:
                AppendConfigHelp(builder, request.Topic);
                break;
            case CliCommand.Server:
                AppendSettingsHelp(builder, "orelay server", "Run the relay in the foreground using the effective configuration.");
                builder.AppendLine("Logs go to stderr with timestamps and coloured levels in a terminal.");
                builder.AppendLine("Set NO_COLOR=1 to disable colour. Redirected logs are plain text.");
                builder.AppendLine("--json writes startup JSON to stdout and JSON logs to stderr.");
                builder.AppendLine();
                builder.AppendLine("Example:");
                builder.AppendLine("  orelay server --bind 127.0.0.1 --port 12987");
                break;
            case CliCommand.Doctor:
                builder.AppendLine("Usage:");
                builder.AppendLine("  orelay doctor [--fix] [--name <service>] [--config-file <path>] [--json]");
                builder.AppendLine();
                builder.AppendLine("Check configuration validity, listener binding, health, port occupancy,");
                builder.AppendLine("and discovery prerequisites. --fix creates a missing configuration file.");
                builder.AppendLine("Exit code 0 means all checks passed; exit code 1 means a check failed.");
                break;
            case CliCommand.Service:
                builder.AppendLine("Usage:");
                builder.AppendLine("  orelay service <install|start|stop|restart|status|uninstall>");
                builder.AppendLine("    [--name <service>] [--config-file <path>] [--json]");
                builder.AppendLine();
                builder.AppendLine("Install or control the service owned by this executable.");
                break;
            default:
                builder.Append(GetHelpText());
                break;
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSettingsHelp(StringBuilder builder, string command, string description)
    {
        builder.AppendLine("Usage:");
        builder.Append("  ").Append(command).AppendLine(" [--config-file <path>] [--json]");
        builder.AppendLine("    [--port <number>] [--bind <host>]");
        builder.AppendLine("    [--public-url <url>] [--hostname <host>]");
        builder.AppendLine("    [--auto-discovery <local|none|tailscale>]");
        builder.AppendLine("    [--lease-seconds <number>] [--max-registrations <number>]");
        builder.AppendLine();
        builder.AppendLine(description);
        builder.AppendLine();
        builder.AppendLine("Defaults:");
        builder.Append("  port=").Append(RelayConfigurationDefaults.Port)
            .Append(", bind=").Append(RelayConfigurationDefaults.Bind)
            .Append(", auto-discovery=").Append(RelayConfigurationDefaults.AutoDiscovery)
            .AppendLine();
        builder.Append("  lease-seconds=").Append(RelayConfigurationDefaults.LeaseSeconds)
            .Append(", max-registrations=").Append(RelayConfigurationDefaults.MaxRegistrations)
            .AppendLine();
    }

    private static void AppendConfigHelp(StringBuilder builder, string? topic)
    {
        switch (topic?.ToLowerInvariant())
        {
            case "get":
                builder.AppendLine("Usage:");
                builder.AppendLine("  orelay config get [key] [--config-file <path>] [--json]");
                builder.AppendLine();
                builder.AppendLine("Read effective settings without creating a file.");
                break;
            case "set":
                builder.AppendLine("Usage:");
                builder.AppendLine("  orelay config set <key> <value> [--config-file <path>] [--json]");
                builder.AppendLine();
                builder.AppendLine("Validate and save one setting while preserving unrelated values.");
                break;
            case "clear":
                builder.AppendLine("Usage:");
                builder.AppendLine("  orelay config clear <key> [--config-file <path>] [--json]");
                builder.AppendLine();
                builder.AppendLine("Remove one saved override so its built-in default applies.");
                break;
            default:
                builder.AppendLine("Usage:");
                builder.AppendLine("  orelay config get [key]");
                builder.AppendLine("  orelay config set <key> <value>");
                builder.AppendLine("  orelay config clear <key>");
                builder.AppendLine("    [--config-file <path>] [--json]");
                builder.AppendLine();
                builder.AppendLine("Keys: port, bind, publicUrl, hostname, autoDiscovery, leaseSeconds, maxRegistrations.");
                break;
        }
    }

    private static string GetCommandName(CliCommand command) => command switch
    {
        CliCommand.Init => "init",
        CliCommand.Config => "config",
        CliCommand.Server => "server",
        CliCommand.Doctor => "doctor",
        CliCommand.Service => "service",
        _ => command.ToString().ToLowerInvariant()
    };
}
