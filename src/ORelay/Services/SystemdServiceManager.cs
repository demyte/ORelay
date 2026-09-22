using System.Text;

namespace ORelay.Services;

/// <summary>Renders the owned systemd unit written by <c>service install</c>.</summary>
public static class SystemdUnitRenderer
{
    public static string Render(ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder();
        builder.AppendLine(ServiceIdentity.OwnershipMarker);
        builder.AppendLine("[Unit]");
        builder.AppendLine("Description=ORelay OAuth callback relay");
        builder.AppendLine("After=network-online.target");
        builder.AppendLine("Wants=network-online.target");
        builder.AppendLine();
        builder.AppendLine("[Service]");
        builder.AppendLine("Type=simple");
        builder.AppendLine("User=root");
        builder.Append("ExecStart=");
        builder.AppendLine(ServiceCommandLine.BuildSystemdExecStart(request));
        builder.AppendLine("Restart=on-failure");
        builder.AppendLine("RestartSec=2s");
        builder.AppendLine("KillSignal=SIGTERM");
        builder.AppendLine("TimeoutStopSec=15s");
        builder.AppendLine();
        builder.AppendLine("[Install]");
        builder.AppendLine("WantedBy=multi-user.target");
        return builder.ToString();
    }

    public static bool IsOwned(string unitContent) =>
        !string.IsNullOrEmpty(unitContent) &&
        unitContent.Contains(ServiceIdentity.OwnershipMarker, StringComparison.Ordinal);
}

/// <summary>systemd lifecycle and unit ownership implementation.</summary>
public sealed class SystemdServiceManager : IPlatformServiceManager
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private readonly IServiceProcessRunner _processRunner;
    private readonly string _systemctlPath;
    private readonly TimeSpan _operationTimeout;

    public SystemdServiceManager(
        IServiceProcessRunner? processRunner = null,
        string? systemctlPath = null,
        TimeSpan? operationTimeout = null)
    {
        _processRunner = processRunner ?? new ServiceProcessRunner();
        _systemctlPath = string.IsNullOrWhiteSpace(systemctlPath) ? "systemctl" : systemctlPath;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;

        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "The service operation timeout must be positive.");
        }
    }

    public ServicePlatform Platform => ServicePlatform.LinuxSystemd;

    public ServiceOperationResult Execute(ServiceOperation operation, ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OperatingSystem.IsLinux() && _processRunner is ServiceProcessRunner)
        {
            return ServiceOperationResult.Failure(
                Platform,
                operation,
                request.ServiceName,
                ServiceState.Unsupported,
                ServiceErrorCode.UnsupportedPlatform,
                "systemd commands are available only on Linux.");
        }

        return operation switch
        {
            ServiceOperation.Install => Install(request),
            ServiceOperation.Start => Start(request),
            ServiceOperation.Stop => Stop(request),
            ServiceOperation.Restart => Restart(request),
            ServiceOperation.Status => Status(request),
            ServiceOperation.Uninstall => Uninstall(request),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown service operation."),
        };
    }

    private ServiceOperationResult Install(ServiceRequest request)
    {
        var validation = ServicePathValidator.ValidateForInstall(Platform, ServiceOperation.Install, request);
        if (validation is not null)
        {
            return validation;
        }

        var unitDirectory = Path.GetDirectoryName(request.UnitFilePath);
        if (string.IsNullOrWhiteSpace(unitDirectory) || !Directory.Exists(unitDirectory))
        {
            return Failure(ServiceOperation.Install, request, ServiceErrorCode.InvalidPath, $"The systemd unit directory does not exist: '{unitDirectory ?? request.UnitFilePath}'.");
        }

        if (!CanWriteUnitDirectory(unitDirectory))
        {
            return Failure(ServiceOperation.Install, request, ServiceErrorCode.PermissionDenied, $"The current account cannot write systemd unit '{request.UnitFilePath}'. Run this explicit command with the required privilege.");
        }

        var expected = SystemdUnitRenderer.Render(request);
        var existing = ReadUnit(request.UnitFilePath, ServiceOperation.Install, request);
        if (existing.Error is not null)
        {
            return existing.Error;
        }

        if (existing.Content is not null)
        {
            if (!SystemdUnitRenderer.IsOwned(existing.Content))
            {
                return Failure(ServiceOperation.Install, request, ServiceErrorCode.Conflict, $"Systemd unit '{request.UnitFilePath}' already exists and is not owned by ORelay.");
            }

            if (!string.Equals(existing.Content, expected, StringComparison.Ordinal))
            {
                return Failure(ServiceOperation.Install, request, ServiceErrorCode.Conflict, $"Systemd unit '{request.UnitFilePath}' is owned by ORelay but records a different executable or configuration path.");
            }

            var current = ReadManagerState(request, ServiceOperation.Install);
            if (current.Result is not null && current.Result.ErrorCode != ServiceErrorCode.NotInstalled)
            {
                return current.Result;
            }

            return ServiceOperationResult.Success(Platform, ServiceOperation.Install, request.ServiceName, current.State, false, true, $"Systemd unit '{request.ServiceName}' is already installed.");
        }

        var managerState = ReadManagerState(request, ServiceOperation.Install);
        if (managerState.Result is not null && managerState.Result.ErrorCode != ServiceErrorCode.NotInstalled)
        {
            return managerState.Result;
        }

        if (managerState.Result is null && managerState.State != ServiceState.NotInstalled)
        {
            return Failure(ServiceOperation.Install, request, ServiceErrorCode.Conflict, $"Systemd service '{request.ServiceName}' is already loaded from another unit path.");
        }

        try
        {
            WriteUnitAtomically(request.UnitFilePath, expected);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure(ServiceOperation.Install, request, ServiceErrorCode.PermissionDenied, $"Could not write systemd unit '{request.UnitFilePath}': {ex.Message}");
        }
        catch (IOException ex)
        {
            return Failure(ServiceOperation.Install, request, ServiceErrorCode.CommandFailed, $"Could not write systemd unit '{request.UnitFilePath}': {ex.Message}");
        }

        var reload = RunSystemctl(["daemon-reload"]);
        if (!reload.Succeeded)
        {
            TryDeleteUnitIfUnchanged(request.UnitFilePath, expected);
            return Failure(ServiceOperation.Install, request, ToErrorCode(reload), FormatProcessFailure("systemd daemon-reload", reload));
        }

        return ServiceOperationResult.Success(Platform, ServiceOperation.Install, request.ServiceName, ServiceState.Stopped, true, true, $"Systemd unit '{request.ServiceName}' was installed. Start it with 'orelay service start'.");
    }

    private ServiceOperationResult Start(ServiceRequest request)
    {
        var ownership = ReadOwned(request, ServiceOperation.Start);
        if (ownership.Result is not null)
        {
            return ownership.Result;
        }

        var state = ReadManagerState(request, ServiceOperation.Start);
        if (state.Result is not null && state.Result.ErrorCode != ServiceErrorCode.NotInstalled)
        {
            return state.Result;
        }

        if (state.State == ServiceState.Running)
        {
            return ServiceOperationResult.Success(Platform, ServiceOperation.Start, request.ServiceName, state.State, false, true, $"Systemd service '{request.ServiceName}' is already running.");
        }

        var start = RunSystemctl(["start", UnitName(request)]);
        if (!start.Succeeded)
        {
            return Failure(ServiceOperation.Start, request, ToErrorCode(start), FormatProcessFailure($"start systemd service '{request.ServiceName}'", start), owned: true, state: state.State);
        }

        return WaitForState(request, ServiceOperation.Start, ServiceState.Running);
    }

    private ServiceOperationResult Stop(ServiceRequest request)
    {
        var ownership = ReadOwned(request, ServiceOperation.Stop);
        if (ownership.Result is not null)
        {
            return ownership.Result;
        }

        var state = ReadManagerState(request, ServiceOperation.Stop);
        if (state.Result is not null && state.Result.ErrorCode != ServiceErrorCode.NotInstalled)
        {
            return state.Result;
        }

        if (state.State is ServiceState.Stopped or ServiceState.Failed)
        {
            return ServiceOperationResult.Success(Platform, ServiceOperation.Stop, request.ServiceName, state.State, false, true, $"Systemd service '{request.ServiceName}' is already stopped.");
        }

        var stop = RunSystemctl(["stop", UnitName(request)]);
        if (!stop.Succeeded && !ContainsNotActive(stop))
        {
            return Failure(ServiceOperation.Stop, request, ToErrorCode(stop), FormatProcessFailure($"stop systemd service '{request.ServiceName}'", stop), owned: true, state: state.State);
        }

        return WaitForState(request, ServiceOperation.Stop, ServiceState.Stopped);
    }

    private ServiceOperationResult Restart(ServiceRequest request)
    {
        var ownership = ReadOwned(request, ServiceOperation.Restart);
        if (ownership.Result is not null)
        {
            return ownership.Result;
        }

        var restart = RunSystemctl(["restart", UnitName(request)]);
        if (!restart.Succeeded)
        {
            return Failure(ServiceOperation.Restart, request, ToErrorCode(restart), FormatProcessFailure($"restart systemd service '{request.ServiceName}'", restart), owned: true);
        }

        return WaitForState(request, ServiceOperation.Restart, ServiceState.Running);
    }

    private ServiceOperationResult Status(ServiceRequest request)
    {
        var unit = ReadUnit(request.UnitFilePath, ServiceOperation.Status, request);
        if (unit.Error is not null)
        {
            return unit.Error;
        }

        if (unit.Content is null)
        {
            var managerState = ReadManagerState(request, ServiceOperation.Status);
            if (managerState.Result is not null && managerState.Result.ErrorCode != ServiceErrorCode.NotInstalled)
            {
                return managerState.Result;
            }

            if (managerState.Result is null && managerState.State != ServiceState.NotInstalled)
            {
                return ServiceOperationResult.Failure(Platform, ServiceOperation.Status, request.ServiceName, managerState.State, ServiceErrorCode.Conflict, $"Systemd service '{request.ServiceName}' is loaded from another unit path.");
            }

            return ServiceOperationResult.Success(Platform, ServiceOperation.Status, request.ServiceName, ServiceState.NotInstalled, false, false, $"Systemd service '{request.ServiceName}' is not installed.");
        }

        if (!SystemdUnitRenderer.IsOwned(unit.Content))
        {
            var current = ReadManagerState(request, ServiceOperation.Status);
            return ServiceOperationResult.Failure(Platform, ServiceOperation.Status, request.ServiceName, current.State, ServiceErrorCode.Conflict, $"Systemd unit '{request.UnitFilePath}' exists but is not owned by ORelay.");
        }

        var expected = SystemdUnitRenderer.Render(request);
        if (!string.Equals(unit.Content, expected, StringComparison.Ordinal))
        {
            var current = ReadManagerState(request, ServiceOperation.Status);
            return ServiceOperationResult.Failure(Platform, ServiceOperation.Status, request.ServiceName, current.State, ServiceErrorCode.Conflict, $"Systemd unit '{request.UnitFilePath}' is owned by ORelay but records a different executable or configuration path.");
        }

        var state = ReadManagerState(request, ServiceOperation.Status);
        if (state.Result is not null && state.Result.ErrorCode != ServiceErrorCode.NotInstalled)
        {
            return state.Result;
        }

        if (state.Result?.ErrorCode == ServiceErrorCode.NotInstalled)
        {
            return Failure(ServiceOperation.Status, request, ServiceErrorCode.ManagerUnavailable, $"Systemd unit '{request.UnitFilePath}' exists but systemd has not loaded '{request.ServiceName}'. Run 'systemctl daemon-reload' and check the unit permissions.", owned: true, state: ServiceState.Unknown);
        }

        return ServiceOperationResult.Success(Platform, ServiceOperation.Status, request.ServiceName, state.State, false, true, $"Systemd service '{request.ServiceName}' is {state.State.ToString().ToLowerInvariant()}.");
    }

    private ServiceOperationResult Uninstall(ServiceRequest request)
    {
        var ownership = ReadOwned(request, ServiceOperation.Uninstall);
        if (ownership.Result is not null)
        {
            if (ownership.Result.ErrorCode == ServiceErrorCode.NotInstalled)
            {
                return ownership.Result with { Succeeded = true, ErrorCode = null, Message = $"Systemd service '{request.ServiceName}' is already uninstalled." };
            }

            return ownership.Result;
        }

        var state = ReadManagerState(request, ServiceOperation.Uninstall);
        if (state.Result is not null && state.Result.ErrorCode != ServiceErrorCode.NotInstalled)
        {
            return state.Result;
        }

        if (state.State is ServiceState.Running or ServiceState.StartPending or ServiceState.StopPending)
        {
            var stop = Stop(request);
            if (!stop.Succeeded)
            {
                return stop with { Operation = ServiceOperation.Uninstall };
            }
        }

        var disable = RunSystemctl(["disable", UnitName(request)]);
        if (!disable.Succeeded && !ContainsNotEnabled(disable))
        {
            return Failure(ServiceOperation.Uninstall, request, ToErrorCode(disable), FormatProcessFailure($"disable systemd service '{request.ServiceName}'", disable), owned: true, state: ServiceState.Stopped);
        }

        var oldContent = ReadUnit(request.UnitFilePath, ServiceOperation.Uninstall, request).Content;
        try
        {
            File.Delete(request.UnitFilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure(ServiceOperation.Uninstall, request, ServiceErrorCode.PermissionDenied, $"Could not remove systemd unit '{request.UnitFilePath}': {ex.Message}", owned: true, state: ServiceState.Stopped);
        }
        catch (IOException ex)
        {
            return Failure(ServiceOperation.Uninstall, request, ServiceErrorCode.CommandFailed, $"Could not remove systemd unit '{request.UnitFilePath}': {ex.Message}", owned: true, state: ServiceState.Stopped);
        }

        var reload = RunSystemctl(["daemon-reload"]);
        if (!reload.Succeeded)
        {
            if (oldContent is not null)
            {
                TryWriteUnit(request.UnitFilePath, oldContent);
            }

            return Failure(ServiceOperation.Uninstall, request, ToErrorCode(reload), FormatProcessFailure("systemd daemon-reload", reload), owned: true, state: ServiceState.Stopped);
        }

        return ServiceOperationResult.Success(Platform, ServiceOperation.Uninstall, request.ServiceName, ServiceState.NotInstalled, true, true, $"Systemd service '{request.ServiceName}' was uninstalled. Configuration '{request.ConfigurationPath}' was preserved.");
    }

    private (string? Content, ServiceOperationResult? Error) ReadUnit(string path, ServiceOperation operation, ServiceRequest request)
    {
        try
        {
            return (File.ReadAllText(path), null);
        }
        catch (FileNotFoundException)
        {
            return (null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return (null, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return (null, Failure(operation, request, ServiceErrorCode.PermissionDenied, $"Could not read systemd unit '{path}': {ex.Message}"));
        }
        catch (IOException ex)
        {
            return (null, Failure(operation, request, ServiceErrorCode.CommandFailed, $"Could not read systemd unit '{path}': {ex.Message}"));
        }
    }

    private (ServiceState State, ServiceOperationResult? Result) ReadManagerState(ServiceRequest request, ServiceOperation operation)
    {
        var show = RunSystemctl([
            "show",
            UnitName(request),
            "--no-page",
            "--property=LoadState,ActiveState,SubState,Result,ExecMainStatus",
        ]);

        if (!show.Succeeded)
        {
            if (ContainsNotFound(show))
            {
                return (ServiceState.NotInstalled, ServiceOperationResult.Failure(Platform, operation, request.ServiceName, ServiceState.NotInstalled, ServiceErrorCode.NotInstalled, $"Systemd service '{request.ServiceName}' is not installed."));
            }

            return (ServiceState.Unknown, Failure(operation, request, ToErrorCode(show), FormatProcessFailure($"inspect systemd service '{request.ServiceName}'", show)));
        }

        var properties = ParseProperties(show.StandardOutput);
        if (properties.TryGetValue("LoadState", out var loadState) && loadState.Equals("not-found", StringComparison.OrdinalIgnoreCase))
        {
            return (ServiceState.NotInstalled, ServiceOperationResult.Failure(Platform, operation, request.ServiceName, ServiceState.NotInstalled, ServiceErrorCode.NotInstalled, $"Systemd service '{request.ServiceName}' is not installed."));
        }

        var activeState = properties.GetValueOrDefault("ActiveState", "unknown");
        var result = properties.GetValueOrDefault("Result", "success");
        var state = activeState.ToLowerInvariant() switch
        {
            "active" => ServiceState.Running,
            "activating" => ServiceState.StartPending,
            "deactivating" => ServiceState.StopPending,
            "inactive" => ServiceState.Stopped,
            "failed" => ServiceState.Failed,
            _ => ServiceState.Unknown,
        };

        if (state == ServiceState.Stopped && !result.Equals("success", StringComparison.OrdinalIgnoreCase))
        {
            state = ServiceState.Failed;
        }

        return (state, null);
    }

    private (ServiceOperationResult? Result, string? Content) ReadOwned(ServiceRequest request, ServiceOperation operation)
    {
        var unit = ReadUnit(request.UnitFilePath, operation, request);
        if (unit.Error is not null)
        {
            return (unit.Error, null);
        }

        if (unit.Content is null)
        {
            return (ServiceOperationResult.Failure(Platform, operation, request.ServiceName, ServiceState.NotInstalled, ServiceErrorCode.NotInstalled, $"Systemd service '{request.ServiceName}' is not installed."), null);
        }

        if (!SystemdUnitRenderer.IsOwned(unit.Content))
        {
            return (Failure(operation, request, ServiceErrorCode.Conflict, $"Systemd unit '{request.UnitFilePath}' exists but is not owned by ORelay."), null);
        }

        if (!string.Equals(unit.Content, SystemdUnitRenderer.Render(request), StringComparison.Ordinal))
        {
            return (Failure(operation, request, ServiceErrorCode.Conflict, $"Systemd unit '{request.UnitFilePath}' is owned by ORelay but records a different executable or configuration path."), null);
        }

        return (null, unit.Content);
    }

    private ServiceOperationResult WaitForState(ServiceRequest request, ServiceOperation operation, ServiceState expected)
    {
        var deadline = DateTime.UtcNow + _operationTimeout;
        var latest = ServiceState.Unknown;
        while (DateTime.UtcNow <= deadline)
        {
            var state = ReadManagerState(request, operation);
            latest = state.State;
            if (state.Result is not null && state.Result.ErrorCode != ServiceErrorCode.NotInstalled)
            {
                return state.Result;
            }

            if (latest == expected)
            {
                return ServiceOperationResult.Success(Platform, operation, request.ServiceName, expected, true, true, $"Systemd service '{request.ServiceName}' reached state '{expected}'.");
            }

            if (latest == ServiceState.Failed)
            {
                return Failure(operation, request, ServiceErrorCode.InvalidState, $"Systemd service '{request.ServiceName}' entered a failed state while waiting for '{expected}'.", owned: true, state: ServiceState.Failed);
            }

            Thread.Sleep(100);
        }

        return Failure(operation, request, ServiceErrorCode.InvalidState, $"Systemd service '{request.ServiceName}' did not reach state '{expected}' before the {_operationTimeout.TotalSeconds:0.#}-second timeout. Current state: '{latest}'.", owned: true, state: latest);
    }

    private ServiceProcessResult RunSystemctl(IReadOnlyList<string> arguments) =>
        _processRunner.Run(_systemctlPath, arguments);

    private static string UnitName(ServiceRequest request)
    {
        var unitName = Path.GetFileName(request.UnitFilePath);
        return string.IsNullOrWhiteSpace(unitName) ? request.ServiceName : unitName;
    }

    private static Dictionary<string, string> ParseProperties(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                result[line[..separator]] = line[(separator + 1)..];
            }
        }

        return result;
    }

    private ServiceOperationResult Failure(
        ServiceOperation operation,
        ServiceRequest request,
        ServiceErrorCode errorCode,
        string message,
        bool owned = false,
        ServiceState state = ServiceState.Unknown) =>
        ServiceOperationResult.Failure(Platform, operation, request.ServiceName, state, errorCode, message, owned: owned);

    private static ServiceErrorCode ToErrorCode(ServiceProcessResult result) =>
        result.ExitCode < 0
            ? ServiceErrorCode.ManagerUnavailable
            : result.StandardError.Contains("permission", StringComparison.OrdinalIgnoreCase)
                ? ServiceErrorCode.PermissionDenied
                : ServiceErrorCode.CommandFailed;

    private static string FormatProcessFailure(string operation, ServiceProcessResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return string.IsNullOrWhiteSpace(detail)
            ? $"Could not {operation}; systemctl exited with code {result.ExitCode}."
            : $"Could not {operation}; systemctl exited with code {result.ExitCode}: {detail.Trim()}";
    }

    private static bool ContainsNotFound(ServiceProcessResult result) =>
        result.StandardError.Contains("not-found", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("not loaded", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        result.StandardOutput.Contains("LoadState=not-found", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNotActive(ServiceProcessResult result) =>
        result.StandardError.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("not active", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsNotEnabled(ServiceProcessResult result) =>
        result.StandardError.Contains("not enabled", StringComparison.OrdinalIgnoreCase) ||
        result.StandardError.Contains("not loaded", StringComparison.OrdinalIgnoreCase);

    private static bool CanWriteUnitDirectory(string directory)
    {
        var probe = Path.Combine(directory, $".orelay-unit-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch
            {
            }

            return false;
        }
    }

    private static void WriteUnitAtomically(string path, string content)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            // Install only creates a missing unit. Existing files were checked
            // for ownership before this point; refusing a race here prevents a
            // newly-created unrelated unit from being replaced.
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void TryWriteUnit(string path, string content)
    {
        try
        {
            WriteUnitAtomically(path, content);
        }
        catch
        {
        }
    }

    private static void TryDeleteUnitIfUnchanged(string path, string expectedContent)
    {
        try
        {
            // A manager reload can fail after another actor has replaced the
            // path. Remove only the exact unit content written by this install;
            // never delete a concurrent unit merely because the path matches.
            if (string.Equals(File.ReadAllText(path), expectedContent, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
