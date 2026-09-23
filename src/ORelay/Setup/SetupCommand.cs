using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ORelay.Cli;
using ORelay.Configuration;
using ORelay.Discovery;
using ORelay.Server;
using ORelay.Services;

namespace ORelay.Setup;

internal sealed class SetupRuntime
{
    public TextReader Input { get; init; } = Console.In;
    public bool Interactive { get; init; } = !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public bool ServicesSupported { get; init; } = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
    public ITailscaleStatusProvider Tailscale { get; init; } = new TailscaleStatusProcessProvider();
    public IUserPathManager UserPath { get; init; } = new UserPathManager();
    public string InstallationDirectory { get; init; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public Func<ServiceOperation, string, string, ServiceOperationResult> Service { get; init; } =
        (operation, path, name) => ServiceCommandExecutor.Execute(operation, path, name);
}

internal sealed record SetupResult(bool Succeeded, bool Changed, bool Skipped, string ConfigFile,
    string Message, string? CallbackUrl = null, string? Mode = null, string? ServiceName = null, bool PathChanged = false);

internal static class SetupCommand
{
    public static async Task<int> ExecuteAsync(CliOptions options, TextWriter output, TextWriter error, SetupRuntime? runtime = null)
    {
        runtime ??= new SetupRuntime();
        var setup = options.Setup!;
        var store = new RelayConfigurationStore(options.ConfigFile);
        try
        {
            var existing = store.Exists ? store.Read() : null;
            if (existing is not null && (setup.IfNeeded || setup.Defaults))
            {
                var preserved = new SetupResult(true, false, true, store.FilePath,
                    "Existing configuration preserved. Run 'orelay setup' to review or change it.");
                var existingPathPlan = await SelectPathPlan(options, runtime, output).ConfigureAwait(false);
                if (existingPathPlan is null)
                    return await Finish(preserved, options, output, error).ConfigureAwait(false);
                if (!setup.Yes && !await YesNo(runtime, output, "Apply the PATH change?", true).ConfigureAwait(false))
                    return await Finish(preserved with { Message = "Setup cancelled. Nothing was changed." }, options, output, error).ConfigureAwait(false);
                return await Finish(ApplyPath(preserved, existingPathPlan, runtime), options, output, error).ConfigureAwait(false);
            }

            if (!setup.Yes && (!runtime.Interactive || options.IsJson))
                throw new InvalidOperationException("Setup needs an interactive terminal. Use 'setup --defaults --yes' for defaults, or 'setup --yes' with explicit options for unattended setup.");

            var settings = existing ?? RelayConfigurationDefaults.Settings;
            var mode = setup.Mode ?? "foreground";
            var name = setup.Name ?? ServiceIdentity.DefaultName;
            var start = setup.Start;
            var enableStartup = setup.EnableStartup;
            var useDefaults = setup.Defaults;
            string? selectedAccess = null;
            if (!setup.Yes && !setup.Defaults && options.SettingsPatch is null && setup.Access is null && setup.Mode is null && !start && !enableStartup)
            {
                await output.WriteLineAsync("ORelay setup").ConfigureAwait(false);
                await Describe(output, settings, "foreground", name, false, false, store.FilePath,
                    existing is null ? "Defaults" : "Current settings").ConfigureAwait(false);
                var choice = await Choose(runtime, output,
                    existing is null ? "1) Go with defaults  2) Customize" : "1) Keep current settings  2) Customize", "1", "1", "2").ConfigureAwait(false);
                useDefaults = choice == "1";
            }

            if (!useDefaults)
            {
                var access = setup.Access;
                if (!setup.Yes)
                    access = await Choose(runtime, output, "Access: local, lan, or tailscale", access ?? GuessAccess(settings), "local", "lan", "tailscale").ConfigureAwait(false);
                if (access is not null) settings = WithAccess(settings, access);
                selectedAccess = access;
                settings = options.SettingsPatch?.ApplyTo(settings) ?? settings;
                if (options.SettingsPatch?.AutoDiscovery == "tailscale" && options.SettingsPatch.Hostname is null && options.SettingsPatch.PublicUrl is null)
                    settings = settings with { Hostname = null, PublicUrl = null };

                if (!setup.Yes)
                {
                    settings = await ReadSetting(runtime, output, settings, "port", "Port", settings.Port.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                    settings = await ReadSetting(runtime, output, settings, "bind", "Bind address", settings.Bind).ConfigureAwait(false);
                    if (settings.AutoDiscovery != "tailscale")
                        settings = await ReadSetting(runtime, output, settings, "hostname", "Advertised hostname or IP", settings.Hostname ?? "localhost").ConfigureAwait(false);
                    else
                        await output.WriteLineAsync("Tailscale must already be installed and connected. Discovery selects the advertised hostname; binding controls which interfaces accept connections.").ConfigureAwait(false);
                    if (runtime.ServicesSupported)
                        mode = await Choose(runtime, output, "Run in foreground or as a service", mode, "foreground", "service").ConfigureAwait(false);
                    else
                        await output.WriteLineAsync("This platform supports foreground operation only.").ConfigureAwait(false);
                    if (mode == "service")
                    {
                        await output.WriteLineAsync("Service installation requires an elevated terminal on Windows or root on Linux. Setup does not elevate automatically.").ConfigureAwait(false);
                        name = await ReadServiceName(runtime, output, name).ConfigureAwait(false);
                        start = await YesNo(runtime, output, "Start or restart the selected service now?", start).ConfigureAwait(false);
                        enableStartup = await YesNo(runtime, output, "Start the service at boot?", enableStartup).ConfigureAwait(false);
                    }
                }
                if (mode != "service" && (start || enableStartup))
                    throw new InvalidOperationException("Starting now and boot startup require service mode.");
            }

            RelaySettingsValidator.Validate(settings, store.FilePath);
            var discovery = await RelaySettingsDiscovery.ResolveAsync(settings, runtime.Tailscale).ConfigureAwait(false);
            if (!discovery.Succeeded || discovery.Settings is null)
                throw new InvalidOperationException($"{discovery.Message} {discovery.NextStep}");
            var server = RelayServerOptions.FromSettings(discovery.Settings);
            server.Validate();
            var callback = server.RelayCallbackUrl;
            if (selectedAccess == "local" && !RelayServerOptions.IsLoopbackBind(settings.Bind))
                throw new InvalidOperationException("Local access requires a loopback bind address.");
            if (selectedAccess is "lan" or "tailscale" && RelayServerOptions.IsLoopbackBind(settings.Bind))
                throw new InvalidOperationException("LAN and Tailscale access require a reachable non-loopback bind address.");

            var pathPlan = await SelectPathPlan(options, runtime, output).ConfigureAwait(false);
            if (!options.IsJson)
            {
                await Describe(output, settings, mode, name, start, enableStartup, store.FilePath, "Settings to apply", callback).ConfigureAwait(false);
                await output.WriteLineAsync(pathPlan is null ? "  User PATH: unchanged" : $"  User PATH: {pathPlan.Directory}\n  {pathPlan.Description}").ConfigureAwait(false);
                if (!RelayServerOptions.IsLoopbackBind(settings.Bind))
                    await output.WriteLineAsync("Shared access permits remote callbacks. Anyone who can reach the management API can manage registrations. Setup does not change firewall rules; restrict access to trusted computers.").ConfigureAwait(false);
                if (mode == "foreground")
                    await output.WriteLineAsync("Existing services are left in place. Foreground setup saves settings and prints the command to run.").ConfigureAwait(false);
            }
            if (!setup.Yes && !await YesNo(runtime, output, "Apply these settings?", true).ConfigureAwait(false))
                return await Finish(new(true, false, true, store.FilePath, "Setup cancelled. Nothing was changed."), options, output, error).ConfigureAwait(false);

            ServiceOperationResult? serviceStatus = null;
            if (mode == "service")
            {
                if (!runtime.ServicesSupported) throw new InvalidOperationException("Services are supported on Windows and Linux systemd hosts only.");
                if (!ServiceIdentity.IsValidName(name)) throw new ArgumentException("The service name may contain only letters, numbers, '.', '-' and '_'.");
                serviceStatus = runtime.Service(ServiceOperation.Status, store.FilePath, name);
                RequireServiceSuccess(serviceStatus);
                if (serviceStatus.State != ServiceState.NotInstalled && !serviceStatus.Owned)
                    throw new InvalidOperationException("The selected service belongs to a different executable or configuration. Choose another service name.");
                if (serviceStatus.State == ServiceState.Running && existing != settings && !start)
                    throw new InvalidOperationException("The selected service is running. Choose start/restart now to apply changed settings, or stop it before setup.");
            }

            store.SaveSetup(settings, existing);
            var changed = existing != settings;
            if (mode == "service")
            {
                try
                {
                    changed |= ApplyService(ServiceOperation.Install);
                    changed |= ApplyService(enableStartup ? ServiceOperation.Enable : ServiceOperation.Disable);
                    if (start)
                        changed |= ApplyService(serviceStatus!.State == ServiceState.Running ? ServiceOperation.Restart : ServiceOperation.Start);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return await Finish(new(false, changed, false, store.FilePath,
                        $"Configuration saved, but service setup did not complete: {ex.Message} Correct the reported issue and rerun setup with the same configuration and service name. Completed service actions remain applied.",
                        callback, mode, name), options, output, error).ConfigureAwait(false);
                }
            }
            var completed = new SetupResult(true, changed, false, store.FilePath,
                mode == "service" ? "Configuration and service setup completed." : "Configuration saved. Start the relay with the command below.",
                callback, mode, mode == "service" ? name : null);
            return await Finish(ApplyPath(completed, pathPlan, runtime), options, output, error).ConfigureAwait(false);

            bool ApplyService(ServiceOperation operation)
            {
                var result = runtime.Service(operation, store.FilePath, name);
                RequireServiceSuccess(result);
                return result.Changed;
            }
        }
        catch (EndOfStreamException)
        {
            return await Finish(new(false, false, false, store.FilePath, "Input ended. Setup cancelled without applying changes."), options, output, error).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RelayConfigurationException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return await Finish(new(false, false, false, store.FilePath, ex.Message), options, output, error).ConfigureAwait(false);
        }
    }

    private static async Task<UserPathPlan?> SelectPathPlan(CliOptions options, SetupRuntime runtime, TextWriter output)
    {
        var setup = options.Setup!;
        if (setup.SkipPath || (!setup.AddToPath && (setup.Yes || !runtime.Interactive || options.IsJson))) return null;
        if (!setup.Yes && (!runtime.Interactive || options.IsJson))
            throw new InvalidOperationException("Adding to PATH without an interactive terminal requires --yes.");
        UserPathPlan plan;
        try { plan = runtime.UserPath.Inspect(runtime.InstallationDirectory); }
        catch (Exception ex) when (!setup.AddToPath && ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"PATH setup unavailable: {ex.Message}").ConfigureAwait(false);
            return null;
        }
        if (!options.IsJson)
            await output.WriteLineAsync($"Installation directory: {plan.Directory}\n{plan.Description}").ConfigureAwait(false);
        if (plan.AlreadyConfigured) return null;
        return setup.AddToPath || await YesNo(runtime, output, "Add this directory to your user PATH?", true).ConfigureAwait(false) ? plan : null;
    }

    private static SetupResult ApplyPath(SetupResult result, UserPathPlan? plan, SetupRuntime runtime)
    {
        if (plan is null) return result;
        try
        {
            var changed = runtime.UserPath.Apply(plan);
            return result with
            {
                Changed = result.Changed || changed,
                Skipped = result.Skipped && !changed,
                PathChanged = changed,
                Message = result.Message + " User PATH is configured. Open a new terminal to run orelay commands."
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return result with
            {
                Succeeded = false,
                Skipped = false,
                Message = result.Message + $" PATH setup did not complete: {ex.Message} Completed changes remain applied. Rerun setup to finish PATH configuration."
            };
        }
    }

    private static string GuessAccess(RelaySettings settings) => settings.AutoDiscovery == "tailscale" ? "tailscale" : RelayServerOptions.IsLoopbackBind(settings.Bind) ? "local" : "lan";

    private static RelaySettings WithAccess(RelaySettings settings, string access)
    {
        if (access == GuessAccess(settings))
            return access == "tailscale" ? settings with { Hostname = null, PublicUrl = null } : settings;
        return access switch
        {
            "local" => settings with { Bind = "127.0.0.1", Hostname = "localhost", PublicUrl = null, AutoDiscovery = "none" },
            "lan" => settings with { Bind = "0.0.0.0", Hostname = Environment.MachineName, PublicUrl = null, AutoDiscovery = "none" },
            "tailscale" => settings with { Bind = "0.0.0.0", Hostname = null, PublicUrl = null, AutoDiscovery = "tailscale" },
            _ => throw new ArgumentException("Access must be local, lan, or tailscale.")
        };
    }

    private static async Task<RelaySettings> ReadSetting(SetupRuntime runtime, TextWriter output, RelaySettings settings, string key, string label, string defaultValue)
    {
        while (true)
        {
            var value = await Read(runtime, output, label, defaultValue).ConfigureAwait(false);
            try
            {
                var patch = RelaySettingsValidator.ParsePatch(new Dictionary<string, string?> { [key] = value });
                var candidate = patch.ApplyTo(settings);
                if (key == "bind" && !RelayServerOptions.IsWildcardBind(candidate.Bind) && !RelayServerOptions.IsLoopbackBind(candidate.Bind) && !System.Net.IPAddress.TryParse(candidate.Bind.Trim('[', ']'), out _))
                    throw new ArgumentException("Bind must be an IP address, localhost, or a wildcard. Use the advertised hostname for a DNS name.");
                return candidate;
            }
            catch (Exception ex) when (ex is RelayConfigurationException or ArgumentException)
            {
                await output.WriteLineAsync(ex.Message).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ReadServiceName(SetupRuntime runtime, TextWriter output, string defaultValue)
    {
        while (true)
        {
            var value = await Read(runtime, output, "Service name", defaultValue).ConfigureAwait(false);
            if (ServiceIdentity.IsValidName(value)) return value;
            await output.WriteLineAsync("Use only letters, numbers, '.', '-' and '_'.").ConfigureAwait(false);
        }
    }

    private static async Task<bool> YesNo(SetupRuntime runtime, TextWriter output, string label, bool defaultValue)
        => (await Choose(runtime, output, label + " y/n", defaultValue ? "y" : "n", "y", "n", "yes", "no").ConfigureAwait(false)) is "y" or "yes";

    private static async Task<string> Choose(SetupRuntime runtime, TextWriter output, string label, string defaultValue, params string[] choices)
    {
        while (true)
        {
            var value = (await Read(runtime, output, label, defaultValue).ConfigureAwait(false)).ToLowerInvariant();
            if (choices.Contains(value, StringComparer.Ordinal)) return value;
            await output.WriteLineAsync("Choose " + string.Join(", ", choices) + ".").ConfigureAwait(false);
        }
    }

    private static async Task<string> Read(SetupRuntime runtime, TextWriter output, string label, string defaultValue)
    {
        await output.WriteAsync($"{label} [{defaultValue}]: ").ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        var line = await runtime.Input.ReadLineAsync().ConfigureAwait(false) ?? throw new EndOfStreamException();
        return string.IsNullOrWhiteSpace(line) ? defaultValue : line.Trim();
    }

    private static async Task Describe(TextWriter output, RelaySettings settings, string mode, string name, bool start, bool boot, string path, string title, string? callback = null)
    {
        callback ??= settings.AutoDiscovery == "tailscale" && settings.Hostname is null && settings.PublicUrl is null
            ? "Discovered from Tailscale" : RelayServerOptions.FromSettings(settings).RelayCallbackUrl;
        await output.WriteLineAsync($"{title}:\n  Config: {path}\n  Bind: {settings.Bind}\n  Port: {settings.Port}\n  Callback: {callback}\n  Discovery: {settings.AutoDiscovery}\n  Mode: {mode}\n  Service: {(mode == "service" ? name : "none")}\n  Start now: {(start ? "yes" : "no")}\n  Start at boot: {(boot ? "yes" : "no")}\n  Lease: {settings.LeaseSeconds} seconds; capacity: {settings.MaxRegistrations}").ConfigureAwait(false);
    }

    private static void RequireServiceSuccess(ServiceOperationResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException(result.Message);
    }

    private static async Task<int> Finish(SetupResult result, CliOptions options, TextWriter output, TextWriter error)
    {
        if (options.IsJson)
            await output.WriteLineAsync(JsonSerializer.Serialize(result, SetupJsonContext.Default.SetupResult)).ConfigureAwait(false);
        else
        {
            await output.WriteLineAsync(result.Message).ConfigureAwait(false);
            if (result.Succeeded && !result.Skipped && result.Mode == "foreground")
                await output.WriteLineAsync($"orelay --config-file \"{result.ConfigFile}\" server").ConfigureAwait(false);
        }
        if (!result.Succeeded) await error.WriteLineAsync($"error: {result.Message}").ConfigureAwait(false);
        return result.Succeeded ? 0 : 3;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SetupResult))]
internal sealed partial class SetupJsonContext : JsonSerializerContext;
