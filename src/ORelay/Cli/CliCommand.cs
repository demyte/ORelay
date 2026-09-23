using ORelay.Configuration;

namespace ORelay.Cli;

public enum CliCommand
{
    Help,
    Version,
    Init,
    Config,
    Server,
    Doctor,
    Service,
    Update,
    Install,
    Setup
}

public enum ConfigAction
{
    Get,
    Set,
    Clear
}

public enum ServiceAction
{
    Install,
    Start,
    Stop,
    Restart,
    Status,
    Uninstall,
    Enable,
    Disable
}

public sealed record ConfigCommandOptions(ConfigAction Action, string? Key, string? Value);

public sealed record ServerCommandOptions(int? Port, string? Bind, string? ServiceName = null);

public sealed record DoctorCommandOptions(bool Fix, string? Name = null);

public sealed record ServiceCommandOptions(ServiceAction Action, string? Name = null);

public sealed record UpdateCommandOptions(bool Check, bool RestartService, string? Name = null);

public sealed record InstallCommandOptions(string? InstallDirectory, bool RestartService, string? Name = null);

public sealed record SetupCommandOptions(
    bool Defaults, bool Yes, bool IfNeeded, string? Access = null, string? Mode = null,
    string? Name = null, bool Start = false, bool EnableStartup = false);

public sealed record CliHelpRequest(CliCommand Command, string? Topic);

public sealed record CliOptions(
    CliCommand Command,
    bool IsJson,
    string? ConfigFile,
    ConfigCommandOptions? Config = null,
    ServerCommandOptions? Server = null,
    DoctorCommandOptions? Doctor = null,
    ServiceCommandOptions? Service = null,
    CliHelpRequest? Help = null,
    RelaySettingsPatch? SettingsPatch = null,
    UpdateCommandOptions? Update = null,
    InstallCommandOptions? Install = null,
    SetupCommandOptions? Setup = null);

public sealed record CliParseResult(CliOptions? Options, string? Error)
{
    public bool IsSuccess => Options is not null;

    public static CliParseResult Success(CliOptions options) => new(options, null);

    public static CliParseResult Failure(string error) => new(null, error);
}

public static class CliExitCodes
{
    public const int Success = 0;
    public const int UsageError = 64;
    public const int CommandUnavailable = 69;
}
