namespace ORelay.Services;

/// <summary>Dependency hooks used by tests and disposable service-manager environments.</summary>
public sealed class ServiceManagerFactoryOptions
{
    public IWindowsServiceBackend? WindowsBackend { get; init; }

    public IServiceProcessRunner? SystemdProcessRunner { get; init; }

    public string? SystemctlPath { get; init; }

    public TimeSpan? OperationTimeout { get; init; }
}

/// <summary>Selects the service manager for the current operating system.</summary>
public static class ServiceManagerFactory
{
    public static IPlatformServiceManager Create(ServiceManagerFactoryOptions? options = null)
    {
        options ??= new ServiceManagerFactoryOptions();

        if (OperatingSystem.IsWindows())
        {
            return new WindowsServiceManager(options.WindowsBackend, options.OperationTimeout);
        }

        if (OperatingSystem.IsLinux())
        {
            return new SystemdServiceManager(options.SystemdProcessRunner, options.SystemctlPath, options.OperationTimeout);
        }

        return new UnsupportedServiceManager();
    }
}

/// <summary>Returns a clear result on platforms without an in-scope service manager.</summary>
public sealed class UnsupportedServiceManager : IPlatformServiceManager
{
    public ServicePlatform Platform => ServicePlatform.Unsupported;

    public ServiceOperationResult Execute(ServiceOperation operation, ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ServiceOperationResult.Failure(
            Platform,
            operation,
            request.ServiceName,
            ServiceState.Unsupported,
            ServiceErrorCode.UnsupportedPlatform,
            "ORelay service commands are supported on Windows and Linux systemd hosts only.");
    }
}

/// <summary>
/// Small command facade for the CLI. It deliberately does no elevation and never
/// asks for input; platform managers return actionable failures instead.
/// </summary>
public static class ServiceCommandExecutor
{
    public static ServiceOperationResult Execute(
        ServiceOperation operation,
        string configurationPath,
        string? serviceName = null,
        string? unitFilePath = null,
        ServiceManagerFactoryOptions? factoryOptions = null)
    {
        var request = ServiceRequest.FromCurrentProcess(configurationPath, serviceName, unitFilePath);
        return ServiceManagerFactory.Create(factoryOptions).Execute(operation, request);
    }
}
