using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ORelay.Services;

/// <summary>The state and command identity returned by the Windows SCM.</summary>
public sealed record WindowsServiceDefinition(
    string BinaryPathName,
    ServiceState State,
    int Win32ExitCode = 0);

public sealed record WindowsServiceQueryResult(
    bool Succeeded,
    bool Found,
    WindowsServiceDefinition? Definition = null,
    string? ErrorMessage = null,
    int? NativeErrorCode = null)
{
    public static WindowsServiceQueryResult NotFound() => new(true, false);

    public static WindowsServiceQueryResult Failure(string message, int? nativeErrorCode = null) =>
        new(false, false, null, message, nativeErrorCode);

    public static WindowsServiceQueryResult FoundService(WindowsServiceDefinition definition) =>
        new(true, true, definition);
}

public sealed record WindowsServiceActionResult(
    bool Succeeded,
    string? ErrorMessage = null,
    int? NativeErrorCode = null)
{
    public static WindowsServiceActionResult Success() => new(true);

    public static WindowsServiceActionResult Failure(string message, int? nativeErrorCode = null) =>
        new(false, message, nativeErrorCode);
}

/// <summary>
/// Narrow backend contract so lifecycle behavior can be tested without touching
/// a user's SCM database.
/// </summary>
public interface IWindowsServiceBackend
{
    WindowsServiceQueryResult Query(string serviceName);

    WindowsServiceActionResult Create(string serviceName, string displayName, string binaryPathName);

    WindowsServiceActionResult Start(string serviceName);

    WindowsServiceActionResult StopService(string serviceName);

    WindowsServiceActionResult Delete(string serviceName);
}

/// <summary>Windows service lifecycle and ownership implementation.</summary>
public sealed class WindowsServiceManager : IPlatformServiceManager
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(30);
    private readonly IWindowsServiceBackend _backend;
    private readonly TimeSpan _operationTimeout;

    public WindowsServiceManager(
        IWindowsServiceBackend? backend = null,
        TimeSpan? operationTimeout = null)
    {
        _backend = backend ?? new Win32WindowsServiceBackend();
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;

        if (_operationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "The service operation timeout must be positive.");
        }
    }

    public ServicePlatform Platform => ServicePlatform.Windows;

    public ServiceOperationResult Execute(ServiceOperation operation, ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OperatingSystem.IsWindows() && _backend is Win32WindowsServiceBackend)
        {
            return ServiceOperationResult.Failure(
                Platform,
                operation,
                request.ServiceName,
                ServiceState.Unsupported,
                ServiceErrorCode.UnsupportedPlatform,
                "Windows Service commands are available only on Windows.");
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

        var query = _backend.Query(request.ServiceName);
        if (!query.Succeeded)
        {
            return Failure(ServiceOperation.Install, request, ToErrorCode(query), query.ErrorMessage!, query.NativeErrorCode);
        }

        var binaryPathName = ServiceCommandLine.BuildWindows(request);
        if (query.Found)
        {
            var definition = query.Definition!;
            if (!Owns(definition, binaryPathName))
            {
                return ServiceOperationResult.Failure(
                    Platform,
                    ServiceOperation.Install,
                    request.ServiceName,
                    definition.State,
                    ServiceErrorCode.Conflict,
                    $"Windows service '{request.ServiceName}' already exists with a different executable or configuration path.",
                    owned: false);
            }

            return ServiceOperationResult.Success(
                Platform,
                ServiceOperation.Install,
                request.ServiceName,
                EffectiveState(definition),
                changed: false,
                owned: true,
                $"Windows service '{request.ServiceName}' is already installed.");
        }

        var create = _backend.Create(request.ServiceName, request.ServiceName, binaryPathName);
        if (!create.Succeeded)
        {
            return Failure(ServiceOperation.Install, request, ToErrorCode(create), create.ErrorMessage!, create.NativeErrorCode);
        }

        return ServiceOperationResult.Success(
            Platform,
            ServiceOperation.Install,
            request.ServiceName,
            ServiceState.Stopped,
            changed: true,
            owned: true,
            $"Windows service '{request.ServiceName}' was installed. Start it with 'orelay service start'.");
    }

    private ServiceOperationResult Start(ServiceRequest request)
    {
        var ownership = QueryOwned(ServiceOperation.Start, request);
        if (ownership.Result is not null)
        {
            return ownership.Result;
        }

        var definition = ownership.Definition!;
        var currentState = EffectiveState(definition);
        if (currentState == ServiceState.Running)
        {
            return ServiceOperationResult.Success(Platform, ServiceOperation.Start, request.ServiceName, currentState, false, true, $"Windows service '{request.ServiceName}' is already running.");
        }

        var start = _backend.Start(request.ServiceName);
        if (!start.Succeeded && start.NativeErrorCode != 1056)
        {
            return Failure(ServiceOperation.Start, request, ToErrorCode(start), start.ErrorMessage!, start.NativeErrorCode, owned: true, state: currentState);
        }

        var wait = WaitForState(request, ServiceOperation.Start, ServiceState.Running);
        if (!wait.Succeeded)
        {
            return wait;
        }

        return ServiceOperationResult.Success(Platform, ServiceOperation.Start, request.ServiceName, ServiceState.Running, true, true, $"Windows service '{request.ServiceName}' is running.");
    }

    private ServiceOperationResult Stop(ServiceRequest request)
    {
        var ownership = QueryOwned(ServiceOperation.Stop, request);
        if (ownership.Result is not null)
        {
            return ownership.Result;
        }

        var definition = ownership.Definition!;
        var currentState = EffectiveState(definition);
        if (currentState == ServiceState.Stopped || currentState == ServiceState.Failed)
        {
            return ServiceOperationResult.Success(Platform, ServiceOperation.Stop, request.ServiceName, currentState, false, true, $"Windows service '{request.ServiceName}' is already stopped.");
        }

        var stop = _backend.StopService(request.ServiceName);
        if (!stop.Succeeded && stop.NativeErrorCode != 1062)
        {
            return Failure(ServiceOperation.Stop, request, ToErrorCode(stop), stop.ErrorMessage!, stop.NativeErrorCode, owned: true, state: currentState);
        }

        var wait = WaitForState(request, ServiceOperation.Stop, ServiceState.Stopped);
        if (!wait.Succeeded)
        {
            return wait;
        }

        return ServiceOperationResult.Success(Platform, ServiceOperation.Stop, request.ServiceName, ServiceState.Stopped, true, true, $"Windows service '{request.ServiceName}' is stopped.");
    }

    private ServiceOperationResult Restart(ServiceRequest request)
    {
        var ownership = QueryOwned(ServiceOperation.Restart, request);
        if (ownership.Result is not null)
        {
            return ownership.Result;
        }

        var currentState = EffectiveState(ownership.Definition!);
        if (currentState is ServiceState.Running or ServiceState.StartPending or ServiceState.StopPending)
        {
            var stop = Stop(request);
            if (!stop.Succeeded)
            {
                return stop with { Operation = ServiceOperation.Restart };
            }
        }

        var start = Start(request);
        return start with { Operation = ServiceOperation.Restart, Changed = start.Succeeded };
    }

    private ServiceOperationResult Status(ServiceRequest request)
    {
        var query = _backend.Query(request.ServiceName);
        if (!query.Succeeded)
        {
            return Failure(ServiceOperation.Status, request, ToErrorCode(query), query.ErrorMessage!, query.NativeErrorCode);
        }

        if (!query.Found)
        {
            return ServiceOperationResult.Success(Platform, ServiceOperation.Status, request.ServiceName, ServiceState.NotInstalled, false, false, $"Windows service '{request.ServiceName}' is not installed.");
        }

        var definition = query.Definition!;
        var owned = Owns(definition, ServiceCommandLine.BuildWindows(request));
        var state = EffectiveState(definition);
        if (!owned)
        {
            return ServiceOperationResult.Failure(Platform, ServiceOperation.Status, request.ServiceName, state, ServiceErrorCode.Conflict, $"Windows service '{request.ServiceName}' exists but is not owned by this executable and configuration path.", owned: false);
        }

        return ServiceOperationResult.Success(Platform, ServiceOperation.Status, request.ServiceName, state, false, true, $"Windows service '{request.ServiceName}' is {state.ToString().ToLowerInvariant()}.");
    }

    private ServiceOperationResult Uninstall(ServiceRequest request)
    {
        var ownership = QueryOwned(ServiceOperation.Uninstall, request);
        if (ownership.Result is not null)
        {
            if (ownership.Result.ErrorCode == ServiceErrorCode.NotInstalled)
            {
                return ownership.Result with { Succeeded = true, ErrorCode = null, Message = $"Windows service '{request.ServiceName}' is already uninstalled." };
            }

            return ownership.Result;
        }

        var state = EffectiveState(ownership.Definition!);
        if (state is ServiceState.Running or ServiceState.StartPending or ServiceState.StopPending)
        {
            var stop = Stop(request);
            if (!stop.Succeeded)
            {
                return stop with { Operation = ServiceOperation.Uninstall };
            }
        }

        var delete = _backend.Delete(request.ServiceName);
        if (!delete.Succeeded && delete.NativeErrorCode != 1072)
        {
            return Failure(ServiceOperation.Uninstall, request, ToErrorCode(delete), delete.ErrorMessage!, delete.NativeErrorCode, owned: true, state: ServiceState.Stopped);
        }

        return ServiceOperationResult.Success(Platform, ServiceOperation.Uninstall, request.ServiceName, ServiceState.NotInstalled, true, true, $"Windows service '{request.ServiceName}' was uninstalled. Configuration '{request.ConfigurationPath}' was preserved.");
    }

    private (WindowsServiceDefinition? Definition, ServiceOperationResult? Result) QueryOwned(
        ServiceOperation operation,
        ServiceRequest request)
    {
        var query = _backend.Query(request.ServiceName);
        if (!query.Succeeded)
        {
            return (null, Failure(operation, request, ToErrorCode(query), query.ErrorMessage!, query.NativeErrorCode));
        }

        if (!query.Found)
        {
            return (null, ServiceOperationResult.Failure(Platform, operation, request.ServiceName, ServiceState.NotInstalled, ServiceErrorCode.NotInstalled, $"Windows service '{request.ServiceName}' is not installed."));
        }

        var definition = query.Definition!;
        if (!Owns(definition, ServiceCommandLine.BuildWindows(request)))
        {
            return (definition, ServiceOperationResult.Failure(Platform, operation, request.ServiceName, EffectiveState(definition), ServiceErrorCode.Conflict, $"Windows service '{request.ServiceName}' exists but is not owned by this executable and configuration path."));
        }

        return (definition, null);
    }

    private ServiceOperationResult WaitForState(ServiceRequest request, ServiceOperation operation, ServiceState expected)
    {
        var deadline = DateTime.UtcNow + _operationTimeout;
        WindowsServiceDefinition? latest = null;
        while (DateTime.UtcNow <= deadline)
        {
            var query = _backend.Query(request.ServiceName);
            if (!query.Succeeded)
            {
                return Failure(operation, request, ToErrorCode(query), query.ErrorMessage!, query.NativeErrorCode, owned: true);
            }

            if (!query.Found)
            {
                return Failure(operation, request, ServiceErrorCode.NotInstalled, $"Windows service '{request.ServiceName}' disappeared while changing state.", null, owned: true);
            }

            latest = query.Definition!;
            if (EffectiveState(latest) == expected)
            {
                return ServiceOperationResult.Success(Platform, operation, request.ServiceName, expected, true, true, $"Windows service '{request.ServiceName}' reached state '{expected}'.");
            }

            if (EffectiveState(latest) == ServiceState.Failed)
            {
                return Failure(operation, request, ServiceErrorCode.InvalidState, $"Windows service '{request.ServiceName}' entered a failed state while waiting for '{expected}'.", latest.Win32ExitCode, owned: true, state: ServiceState.Failed);
            }

            Thread.Sleep(100);
        }

        return Failure(operation, request, ServiceErrorCode.InvalidState, $"Windows service '{request.ServiceName}' did not reach state '{expected}' before the {_operationTimeout.TotalSeconds:0.#}-second timeout. Current state: '{EffectiveState(latest!)}'.", null, owned: true, state: EffectiveState(latest!));
    }

    private static bool Owns(WindowsServiceDefinition definition, string expectedBinaryPath) =>
        string.Equals(definition.BinaryPathName.Trim(), expectedBinaryPath.Trim(), StringComparison.OrdinalIgnoreCase);

    private static ServiceState EffectiveState(WindowsServiceDefinition definition) =>
        definition.State == ServiceState.Stopped && definition.Win32ExitCode != 0
            ? ServiceState.Failed
            : definition.State;

    private ServiceOperationResult Failure(
        ServiceOperation operation,
        ServiceRequest request,
        ServiceErrorCode errorCode,
        string message,
        int? nativeErrorCode,
        bool owned = false,
        ServiceState state = ServiceState.Unknown) =>
        ServiceOperationResult.Failure(Platform, operation, request.ServiceName, state, errorCode, message, nativeErrorCode, owned);

    private static ServiceErrorCode ToErrorCode(WindowsServiceActionResult result) =>
        result.NativeErrorCode is 5 or 1300
            ? ServiceErrorCode.PermissionDenied
            : result.NativeErrorCode is 1073
                ? ServiceErrorCode.Conflict
                : ServiceErrorCode.CommandFailed;

    private static ServiceErrorCode ToErrorCode(WindowsServiceQueryResult result) =>
        result.NativeErrorCode is 5 or 1300
            ? ServiceErrorCode.PermissionDenied
            : ServiceErrorCode.ManagerUnavailable;
}

/// <summary>Win32 SCM backend used by the real executable on Windows.</summary>
public sealed class Win32WindowsServiceBackend : IWindowsServiceBackend
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ScManagerConnect = 0x0001;
    private const int ScManagerCreateService = 0x0002;
    private const int ServiceQueryConfig = 0x0001;
    private const int ServiceQueryStatus = 0x0004;
    private const int ServiceStart = 0x0010;
    private const int ServiceStop = 0x0020;
    private const int ServiceDelete = 0x00010000;
    private const int ServiceAllAccess = 0x000F01FF;
    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceDemandStart = 0x00000003;
    private const int ServiceErrorNormal = 0x00000001;
    private const int ServiceControlStop = 0x00000001;
    private const int ScStatusProcessInfo = 0;

    public WindowsServiceQueryResult Query(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsServiceQueryResult.Failure("The Windows Service Control Manager is available only on Windows.");
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return QueryFailure(
                $"Could not open the Windows Service Control Manager: {ErrorText(error)}",
                error);
        }

        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryConfig | ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return error == ErrorServiceDoesNotExist
                    ? WindowsServiceQueryResult.NotFound()
                    : QueryFailure($"Could not open Windows service '{serviceName}': {ErrorText(error)}", error);
            }

            try
            {
                var config = ReadConfiguration(service);
                if (!config.Succeeded)
                {
                    return config;
                }

                var status = ReadStatus(service);
                if (!status.Succeeded)
                {
                    return status;
                }

                return WindowsServiceQueryResult.FoundService(
                    new WindowsServiceDefinition(config.Definition!.BinaryPathName, status.Definition!.State, status.Definition.Win32ExitCode));
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public WindowsServiceActionResult Create(string serviceName, string displayName, string binaryPathName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsServiceActionResult.Failure("The Windows Service Control Manager is available only on Windows.");
        }

        var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
        if (manager == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return ActionFailure("Could not open the Windows Service Control Manager.", error);
        }

        try
        {
            var service = CreateService(
                manager,
                serviceName,
                displayName,
                ServiceAllAccess,
                ServiceWin32OwnProcess,
                ServiceDemandStart,
                ServiceErrorNormal,
                binaryPathName,
                null,
                IntPtr.Zero,
                null,
                "LocalSystem",
                null);

            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return ActionFailure($"Could not install Windows service '{serviceName}': {ErrorText(error)}", error);
            }

            CloseServiceHandle(service);
            return WindowsServiceActionResult.Success();
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    public WindowsServiceActionResult Start(string serviceName) => Invoke(serviceName, ServiceStart, static service => StartService(service, 0, IntPtr.Zero), ErrorServiceAlreadyRunning, "start");

    public WindowsServiceActionResult StopService(string serviceName) => Invoke(serviceName, ServiceStop | ServiceQueryStatus, static service =>
    {
        var status = new NativeServiceStatus();
        return ControlService(service, ServiceControlStop, ref status);
    }, ErrorServiceNotActive, "stop");

    public WindowsServiceActionResult Delete(string serviceName) => Invoke(serviceName, ServiceDelete, DeleteService, 1072, "remove");

    private static WindowsServiceActionResult Invoke(
        string serviceName,
        int access,
        Func<IntPtr, bool> action,
        int alreadyCompleteError,
        string actionName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return WindowsServiceActionResult.Failure("The Windows Service Control Manager is available only on Windows.");
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return ActionFailure("Could not open the Windows Service Control Manager.", error);
        }

        try
        {
            var service = OpenService(manager, serviceName, access);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return ActionFailure($"Could not open Windows service '{serviceName}' for {actionName}: {ErrorText(error)}", error);
            }

            try
            {
                if (action(service))
                {
                    return WindowsServiceActionResult.Success();
                }

                var error = Marshal.GetLastWin32Error();
                if (error == alreadyCompleteError)
                {
                    return WindowsServiceActionResult.Success();
                }

                return ActionFailure($"Could not {actionName} Windows service '{serviceName}': {ErrorText(error)}", error);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static WindowsServiceQueryResult ReadConfiguration(IntPtr service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var bytesNeeded);
        var error = Marshal.GetLastWin32Error();
        if (bytesNeeded <= 0 || error != 122)
        {
            return QueryFailure("Could not read the Windows service command line.", error);
        }

        var buffer = Marshal.AllocHGlobal(bytesNeeded);
        try
        {
            if (!QueryServiceConfig(service, buffer, bytesNeeded, out _))
            {
                error = Marshal.GetLastWin32Error();
                return QueryFailure("Could not read the Windows service command line.", error);
            }

            var native = Marshal.PtrToStructure<NativeQueryServiceConfig>(buffer);
            var binaryPathName = Marshal.PtrToStringUni(native.BinaryPathName);
            return string.IsNullOrWhiteSpace(binaryPathName)
                ? QueryFailure("The Windows service has no executable command line.")
                : WindowsServiceQueryResult.FoundService(new WindowsServiceDefinition(binaryPathName, ServiceState.Unknown));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static WindowsServiceQueryResult ReadStatus(IntPtr service)
    {
        var size = Marshal.SizeOf<NativeServiceStatusProcess>();
        var status = new NativeServiceStatusProcess();
        if (!QueryServiceStatusEx(service, ScStatusProcessInfo, ref status, size, out _))
        {
            var error = Marshal.GetLastWin32Error();
            return QueryFailure("Could not read the Windows service status.", error);
        }

        return WindowsServiceQueryResult.FoundService(new WindowsServiceDefinition(string.Empty, MapState(status.CurrentState), status.Win32ExitCode));
    }

    private static ServiceState MapState(int state) => state switch
    {
        1 => ServiceState.Stopped,
        2 => ServiceState.StartPending,
        3 => ServiceState.StopPending,
        4 => ServiceState.Running,
        5 or 6 => ServiceState.StartPending,
        7 => ServiceState.Paused,
        _ => ServiceState.Unknown,
    };

    private static WindowsServiceQueryResult QueryFailure(string message, int? nativeErrorCode = null) =>
        WindowsServiceQueryResult.Failure(message, nativeErrorCode);

    private static WindowsServiceActionResult ActionFailure(string message, int? nativeErrorCode = null) =>
        WindowsServiceActionResult.Failure(message, nativeErrorCode);

    private static string ErrorText(int errorCode) => new Win32Exception(errorCode).Message;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeQueryServiceConfig
    {
        public int ServiceType;
        public int StartType;
        public int ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public IntPtr TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceStatusProcess
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
        public int ProcessId;
        public int ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName, int desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateService(
        IntPtr serviceManager,
        string serviceName,
        string displayName,
        int desiredAccess,
        int serviceType,
        int startType,
        int errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr queryConfig, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, ref NativeServiceStatusProcess status, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr service, int argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, int control, ref NativeServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);
}
