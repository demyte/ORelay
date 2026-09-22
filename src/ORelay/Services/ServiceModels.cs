namespace ORelay.Services;

/// <summary>The service manager that owns a service installation.</summary>
public enum ServicePlatform
{
    Windows,
    LinuxSystemd,
    Unsupported,
}

/// <summary>An operation exposed by the public <c>service</c> command.</summary>
public enum ServiceOperation
{
    Install,
    Start,
    Stop,
    Restart,
    Status,
    Uninstall,
}

/// <summary>The state reported by an operating system service manager.</summary>
public enum ServiceState
{
    Unsupported,
    NotInstalled,
    Stopped,
    StartPending,
    Running,
    StopPending,
    Paused,
    Failed,
    Unknown,
}

/// <summary>Stable error categories for service command output.</summary>
public enum ServiceErrorCode
{
    UnsupportedPlatform,
    InvalidPath,
    PermissionDenied,
    ManagerUnavailable,
    NotInstalled,
    Conflict,
    CommandFailed,
    InvalidState,
}

/// <summary>
/// Absolute paths and identity used for one service operation. The executable and
/// configuration paths are persisted into the service definition exactly as
/// represented by <see cref="ServiceCommandLine"/>.
/// </summary>
public sealed class ServiceRequest
{
    public ServiceRequest(
        string executablePath,
        string configurationPath,
        string? serviceName = null,
        string? unitFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The service executable path is required.", nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            throw new ArgumentException("The service configuration path is required.", nameof(configurationPath));
        }

        ExecutablePath = Path.GetFullPath(executablePath);
        ConfigurationPath = Path.GetFullPath(configurationPath);
        ServiceName = string.IsNullOrWhiteSpace(serviceName)
            ? ServiceIdentity.DefaultName
            : serviceName.Trim();
        if (!ServiceIdentity.IsValidName(ServiceName))
        {
            throw new ArgumentException(
                "The service name may contain only letters, numbers, '.', '-' and '_'.",
                nameof(serviceName));
        }

        UnitFilePath = string.IsNullOrWhiteSpace(unitFilePath)
            ? string.Equals(ServiceName, ServiceIdentity.DefaultName, StringComparison.OrdinalIgnoreCase)
                ? ServiceIdentity.DefaultSystemdUnitPath
                : Path.Combine(ServiceIdentity.DefaultSystemdUnitDirectory, $"{ServiceName}.service")
            : Path.GetFullPath(unitFilePath);
    }

    public string ExecutablePath { get; }

    public string ConfigurationPath { get; }

    public string ServiceName { get; }

    public string UnitFilePath { get; }

    public static ServiceRequest FromCurrentProcess(
        string configurationPath,
        string? serviceName = null,
        string? unitFilePath = null)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("The current executable path is unavailable.");
        }

        if (IsManagedHostProcess(executablePath))
        {
            throw new InvalidOperationException(
                "Service commands require a published ORelay executable; the current process is the dotnet host.");
        }

        return new ServiceRequest(executablePath, configurationPath, serviceName, unitFilePath);
    }

    /// <summary>Returns whether the current process is the managed dotnet host.</summary>
    public static bool IsManagedHostProcess() => IsManagedHostProcess(Environment.ProcessPath);

    private static bool IsManagedHostProcess(string? executablePath)
    {
        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        return string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A structured result that the CLI can render as text or JSON.</summary>
public sealed record ServiceOperationResult(
    ServicePlatform Platform,
    ServiceOperation Operation,
    string ServiceName,
    ServiceState State,
    bool Succeeded,
    bool Changed,
    bool Owned,
    string Message,
    ServiceErrorCode? ErrorCode = null,
    int? NativeErrorCode = null)
{
    public static ServiceOperationResult Success(
        ServicePlatform platform,
        ServiceOperation operation,
        string serviceName,
        ServiceState state,
        bool changed,
        bool owned,
        string message) =>
        new(platform, operation, serviceName, state, true, changed, owned, message);

    public static ServiceOperationResult Failure(
        ServicePlatform platform,
        ServiceOperation operation,
        string serviceName,
        ServiceState state,
        ServiceErrorCode errorCode,
        string message,
        int? nativeErrorCode = null,
        bool owned = false) =>
        new(platform, operation, serviceName, state, false, false, owned, message, errorCode, nativeErrorCode);
}

/// <summary>The platform-specific service implementation used by the CLI.</summary>
public interface IPlatformServiceManager
{
    ServicePlatform Platform { get; }

    ServiceOperationResult Execute(ServiceOperation operation, ServiceRequest request);
}

/// <summary>Names and default locations shared by platform service managers.</summary>
public static class ServiceIdentity
{
    public const string DefaultName = "ORelay";
    public const string DefaultSystemdUnitName = "orelay.service";
    public const string OwnershipMarker = "# Managed by ORelay. Do not edit.";

    public static string DefaultSystemdUnitDirectory =>
        OperatingSystem.IsWindows()
            ? Path.GetTempPath()
            : Path.Combine(Path.DirectorySeparatorChar.ToString(), "etc", "systemd", "system");

    public static string DefaultSystemdUnitPath =>
        Path.Combine(DefaultSystemdUnitDirectory, DefaultSystemdUnitName);

    public static bool IsValidName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One result returned by a native service-manager process.</summary>
public sealed record ServiceProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs a native command without a shell, preserving argument boundaries.</summary>
public interface IServiceProcessRunner
{
    ServiceProcessResult Run(string fileName, IReadOnlyList<string> arguments);
}

/// <summary>Default process runner used by the systemd implementation.</summary>
public sealed class ServiceProcessRunner : IServiceProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout;

    public ServiceProcessRunner(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The native service command timeout must be positive.");
        }
    }

    public ServiceProcessResult Run(string fileName, IReadOnlyList<string> arguments)
        => RunAsync(fileName, arguments).GetAwaiter().GetResult();

    private async Task<ServiceProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return new ServiceProcessResult(-1, string.Empty, $"Could not start '{fileName}'.");
            }

            // Drain both pipes concurrently. Reading one synchronously before
            // the other can deadlock when a native manager writes enough output
            // to fill the pipe that is not currently being read.
            var standardOutputTask = process.StandardOutput.ReadToEndAsync();
            var standardErrorTask = process.StandardError.ReadToEndAsync();

            try
            {
                await process.WaitForExitAsync().WaitAsync(_timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // The timeout result remains actionable even when the
                    // platform cannot terminate a child process tree.
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Do not allow a stuck native process to hold the CLI.
                }

                var timedOutOutput = await ReadOutputAsync(standardOutputTask).ConfigureAwait(false);
                var timedOutError = await ReadOutputAsync(standardErrorTask).ConfigureAwait(false);
                var timeoutMessage = $"Native command '{fileName}' timed out after {_timeout.TotalSeconds:0.#} seconds.";
                return new ServiceProcessResult(-2, timedOutOutput, string.IsNullOrWhiteSpace(timedOutError) ? timeoutMessage : $"{timeoutMessage} {timedOutError.Trim()}");
            }

            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            return new ServiceProcessResult(process.ExitCode, standardOutputTask.Result, standardErrorTask.Result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new ServiceProcessResult(-1, string.Empty, ex.Message);
        }
    }

    private static async Task<string> ReadOutputAsync(Task<string> outputTask)
    {
        try
        {
            return await outputTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            return string.Empty;
        }
    }
}
