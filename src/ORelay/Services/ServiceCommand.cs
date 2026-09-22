using System.Text.Json;
using System.Text.Json.Serialization;
using ORelay.Cli;
using ORelay.Configuration;

namespace ORelay.Services;

/// <summary>Executes the parsed service command and renders its public result.</summary>
public static class ServiceCommand
{
    public static Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error) =>
        ExecuteAsync(options, output, error, serviceName: null, unitFilePath: null, CancellationToken.None);

    public static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
        => await ExecuteAsync(options, output, error, serviceName: null, unitFilePath: null, cancellationToken).ConfigureAwait(false);

    public static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        string? serviceName,
        string? unitFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Command != CliCommand.Service || options.Service is null)
        {
            await error.WriteLineAsync("error: service command received an unsupported CLI command.").ConfigureAwait(false);
            return CliExitCodes.UsageError;
        }

        var operation = options.Service.Action switch
        {
            ServiceAction.Install => ServiceOperation.Install,
            ServiceAction.Start => ServiceOperation.Start,
            ServiceAction.Stop => ServiceOperation.Stop,
            ServiceAction.Restart => ServiceOperation.Restart,
            ServiceAction.Status => ServiceOperation.Status,
            ServiceAction.Uninstall => ServiceOperation.Uninstall,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Service.Action, "Unknown service action."),
        };

        ServiceOperationResult result;
        try
        {
            result = ServiceCommandExecutor.Execute(
                operation,
                RelayConfigurationPath.Resolve(options.ConfigFile),
                serviceName,
                unitFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = ServiceOperationResult.Failure(
                ServicePlatform.Unsupported,
                operation,
                ServiceIdentity.DefaultName,
                ServiceState.Unknown,
                ServiceErrorCode.InvalidPath,
                exception.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (options.IsJson)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(result, ServiceCommandJsonContext.Default.ServiceOperationResult)).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(result.Message).ConfigureAwait(false);
        }

        if (!result.Succeeded)
        {
            await error.WriteLineAsync($"error: {result.Message}").ConfigureAwait(false);
            return result.ErrorCode == ServiceErrorCode.UnsupportedPlatform
                ? CliExitCodes.CommandUnavailable
                : RelayConfigurationExitCodes.ConfigurationError;
        }

        return CliExitCodes.Success;
    }
}

/// <summary>Read-only doctor integration for the selected platform service.</summary>
public sealed class ServiceDoctorCheck : ORelay.Diagnostics.IDoctorServiceCheck
{
    private readonly string? _configurationPath;
    private readonly string? _serviceName;
    private readonly string? _unitFilePath;
    private readonly ServiceManagerFactoryOptions? _factoryOptions;

    public ServiceDoctorCheck(
        string? configurationPath = null,
        string? serviceName = null,
        string? unitFilePath = null,
        ServiceManagerFactoryOptions? factoryOptions = null)
    {
        _configurationPath = configurationPath;
        _serviceName = serviceName;
        _unitFilePath = unitFilePath;
        _factoryOptions = factoryOptions;
    }

    public Task<ORelay.Diagnostics.DoctorCheck?> CheckAsync(
        RelaySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        if (ServiceRequest.IsManagedHostProcess())
        {
            return Task.FromResult<ORelay.Diagnostics.DoctorCheck?>(
                ORelay.Diagnostics.DoctorCheck.Skipped(
                    "service",
                    "Service status is available from a published ORelay executable; the current process is the dotnet host."));
        }

        var configurationPath = _configurationPath ?? RelayConfigurationPath.Resolve(null);
        ServiceOperationResult result;
        try
        {
            var request = ServiceRequest.FromCurrentProcess(configurationPath, _serviceName, _unitFilePath);
            result = ServiceManagerFactory.Create(_factoryOptions).Execute(ServiceOperation.Status, request);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Task.FromResult<ORelay.Diagnostics.DoctorCheck?>(
                ORelay.Diagnostics.DoctorCheck.Failed("service", exception.Message, "Check the executable, selected configuration path, and service-manager permissions."));
        }

        ORelay.Diagnostics.DoctorCheck check = result.ErrorCode == ServiceErrorCode.UnsupportedPlatform
            ? ORelay.Diagnostics.DoctorCheck.Skipped("service", result.Message)
            : result.State == ServiceState.NotInstalled
                ? ORelay.Diagnostics.DoctorCheck.Skipped("service", "No ORelay service is installed for the selected platform.")
                : result.Succeeded && (result.State is ServiceState.Running or ServiceState.Stopped)
                    ? ORelay.Diagnostics.DoctorCheck.Passed("service", result.Message)
                    : ORelay.Diagnostics.DoctorCheck.Failed("service", result.Message, "Run the reported service command after checking ownership and privileges.");

        return Task.FromResult<ORelay.Diagnostics.DoctorCheck?>(check);
    }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ServiceOperationResult))]
internal sealed partial class ServiceCommandJsonContext : JsonSerializerContext;
