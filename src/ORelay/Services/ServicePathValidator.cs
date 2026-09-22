using ORelay.Configuration;

namespace ORelay.Services;

/// <summary>Read-only path and access checks shared by service installers.</summary>
public static class ServicePathValidator
{
    public static ServiceOperationResult? ValidateForInstall(
        ServicePlatform platform,
        ServiceOperation operation,
        ServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Path.IsPathFullyQualified(request.ExecutablePath))
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                ServiceErrorCode.InvalidPath,
                $"The service executable path must be absolute: '{request.ExecutablePath}'.");
        }

        var executableState = ProbeExistingFile(request.ExecutablePath, "service executable");
        if (executableState.Failure is not null)
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                executableState.Failure.Value.ErrorCode,
                executableState.Failure.Value.Message);
        }

        if (!executableState.Exists)
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                ServiceErrorCode.InvalidPath,
                $"The service executable does not exist: '{request.ExecutablePath}'.");
        }

        if (!Path.IsPathFullyQualified(request.ConfigurationPath))
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                ServiceErrorCode.InvalidPath,
                $"The service configuration path must be absolute: '{request.ConfigurationPath}'.");
        }

        var configurationDirectory = Path.GetDirectoryName(request.ConfigurationPath);
        if (string.IsNullOrWhiteSpace(configurationDirectory))
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                ServiceErrorCode.InvalidPath,
                $"The service configuration directory does not exist: '{configurationDirectory ?? request.ConfigurationPath}'.");
        }

        var directoryState = ProbeDirectory(configurationDirectory);
        if (directoryState.Failure is not null)
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                directoryState.Failure.Value.ErrorCode,
                directoryState.Failure.Value.Message);
        }

        if (!directoryState.Exists)
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                ServiceErrorCode.InvalidPath,
                $"The service configuration directory does not exist: '{configurationDirectory}'.");
        }

        var configurationState = ProbeExistingFile(request.ConfigurationPath, "configuration");
        if (configurationState.Failure is not null)
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                configurationState.Failure.Value.ErrorCode,
                configurationState.Failure.Value.Message);
        }

        if (configurationState.Exists)
        {
            try
            {
                // Installation must persist a configuration that the server can
                // actually consume. An open/read check alone would accept a
                // directory, malformed JSON, or an invalid settings document.
                _ = new RelayConfigurationStore(request.ConfigurationPath).Read();
            }
            catch (RelayConfigurationException ex) when (ex.Code == RelayConfigurationErrorCode.FileAccess)
            {
                return ServiceOperationResult.Failure(
                    platform,
                    operation,
                    request.ServiceName,
                    ServiceState.Unknown,
                    ServiceErrorCode.PermissionDenied,
                    $"The service cannot read configuration '{request.ConfigurationPath}': {ex.Message}");
            }
            catch (RelayConfigurationException ex)
            {
                return ServiceOperationResult.Failure(
                    platform,
                    operation,
                    request.ServiceName,
                    ServiceState.Unknown,
                    ServiceErrorCode.InvalidPath,
                    $"Configuration '{request.ConfigurationPath}' is invalid: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                return ServiceOperationResult.Failure(
                    platform,
                    operation,
                    request.ServiceName,
                    ServiceState.Unknown,
                    ServiceErrorCode.PermissionDenied,
                    $"The service cannot read configuration '{request.ConfigurationPath}': {ex.Message}");
            }
            catch (IOException ex)
            {
                return ServiceOperationResult.Failure(
                    platform,
                    operation,
                    request.ServiceName,
                    ServiceState.Unknown,
                    ServiceErrorCode.InvalidPath,
                    $"The service cannot open configuration '{request.ConfigurationPath}': {ex.Message}");
            }
        }
        else if (!CanWriteDirectory(configurationDirectory))
        {
            return ServiceOperationResult.Failure(
                platform,
                operation,
                request.ServiceName,
                ServiceState.Unknown,
                ServiceErrorCode.PermissionDenied,
                $"The service cannot create configuration '{request.ConfigurationPath}' in '{configurationDirectory}'.");
        }

        return null;
    }

    private static ConfigurationPathProbe ProbeExistingFile(string path, string description)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return ConfigurationPathProbe.Failed(
                    ServiceErrorCode.InvalidPath,
                    $"The selected {description} path is a directory, not a file: '{path}'.");
            }

            return ConfigurationPathProbe.Present();
        }
        catch (FileNotFoundException)
        {
            return ConfigurationPathProbe.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ConfigurationPathProbe.Missing();
        }
        catch (UnauthorizedAccessException ex)
        {
            return ConfigurationPathProbe.Failed(
                ServiceErrorCode.PermissionDenied,
                $"The service cannot inspect {description} '{path}': {ex.Message}");
        }
        catch (IOException ex)
        {
            return ConfigurationPathProbe.Failed(
                ServiceErrorCode.InvalidPath,
                $"The service cannot inspect {description} '{path}': {ex.Message}");
        }
    }

    private static ConfigurationPathProbe ProbeDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0
                ? ConfigurationPathProbe.Present()
                : ConfigurationPathProbe.Failed(
                    ServiceErrorCode.InvalidPath,
                    $"The service configuration parent is not a directory: '{path}'.");
        }
        catch (FileNotFoundException)
        {
            return ConfigurationPathProbe.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ConfigurationPathProbe.Missing();
        }
        catch (UnauthorizedAccessException ex)
        {
            return ConfigurationPathProbe.Failed(
                ServiceErrorCode.PermissionDenied,
                $"The service cannot inspect configuration directory '{path}': {ex.Message}");
        }
        catch (IOException ex)
        {
            return ConfigurationPathProbe.Failed(
                ServiceErrorCode.InvalidPath,
                $"The service cannot inspect configuration directory '{path}': {ex.Message}");
        }
    }

    private static bool CanWriteDirectory(string directory)
    {
        var probePath = Path.Combine(directory, $".orelay-service-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch
            {
                // Keep the actionable access failure from the original probe.
            }

            return false;
        }
    }

    private readonly record struct ConfigurationPathProbe(
        bool Exists,
        (ServiceErrorCode ErrorCode, string Message)? Failure)
    {
        public static ConfigurationPathProbe Present() => new(true, null);

        public static ConfigurationPathProbe Missing() => new(false, null);

        public static ConfigurationPathProbe Failed(ServiceErrorCode errorCode, string message) =>
            new(false, (errorCode, message));
    }
}
