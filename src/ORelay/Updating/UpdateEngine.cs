using System.Security.Cryptography;
using ORelay.Configuration;
using ORelay.Diagnostics;
using ORelay.Services;

namespace ORelay.Updating;

public sealed class UpdateEngine
{
    private readonly IUpdateRuntime _runtime;
    private readonly Lazy<ReleaseClient> _releases;
    private readonly IPlatformServiceManager _services;
    private readonly IRelayHealthProbe _health;

    public UpdateEngine(HttpClient? http = null, IUpdateRuntime? runtime = null,
        IPlatformServiceManager? services = null, string? token = null, IRelayHealthProbe? health = null)
    {
        _runtime = runtime ?? new NativeUpdateRuntime();
        _releases = new Lazy<ReleaseClient>(() => new ReleaseClient(http, token));
        _services = services ?? ServiceManagerFactory.Create();
        _health = health ?? new HttpRelayHealthProbe();
    }

    public async Task<UpdateResult> CheckAsync(UpdateRequest request, CancellationToken cancellationToken = default)
    {
        var current = _runtime.CurrentVersion;
        var target = _runtime.ProcessPath;
        try
        {
            var rid = RequireNative();
            var local = ParseVersion(current);
            var release = await _releases.Value.GetLatestAsync(rid, cancellationToken);
            var latest = ParseVersion(release.Version);
            if (latest.CompareTo(local) < 0)
                return new UpdateResult(true, false, current, release.Version, target,
                    "This executable is newer than the latest stable release. No update is available.");
            var newer = latest.CompareTo(local) > 0;
            return new UpdateResult(true, false, current, release.Version, target,
                newer ? $"ORelay {release.Version} is available." : "ORelay is up to date.",
                UpdateAvailable: newer);
        }
        catch (UpdateException ex)
        {
            return UpdateResult.Failure(ex.Code, current, null, target, ex.Message);
        }
    }

    public async Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
    {
        var current = _runtime.CurrentVersion;
        var target = _runtime.ProcessPath;
        string? latestVersion = null;
        try
        {
            var rid = RequireNative();
            var local = ParseVersion(current);
            var release = await _releases.Value.GetLatestAsync(rid, cancellationToken);
            latestVersion = release.Version;
            var latest = ParseVersion(latestVersion);
            if (latest.CompareTo(local) < 0)
                throw new UpdateException(UpdateErrorCode.Downgrade, "The latest stable release is older than this executable. No downgrade was made.");
            using var updateLock = AcquireLock(target!);
            var installedStamp = SafeReadInstalledVersion(target!);
            if (installedStamp is null)
                throw new UpdateException(UpdateErrorCode.InvalidVersion, "The installed executable has no recognized ORelay version metadata. It was not executed or replaced.");
            var installedVersion = ParseVersion(installedStamp);
            if (latest.CompareTo(installedVersion) < 0)
                throw new UpdateException(UpdateErrorCode.Downgrade, "The installed executable is newer. No downgrade was made.");
            if (latest.CompareTo(installedVersion) == 0)
                return new UpdateResult(true, false, current, latestVersion, target, "ORelay is already installed at this version.");
            var service = CheckService(target!, request.ConfigurationPath, request.ServiceName, request.RestartService);
            var archive = await _releases.Value.DownloadVerifiedArchiveAsync(release, cancellationToken);
            var candidate = UpdateArchive.ExtractExecutable(archive, rid);
            return await ReplaceAsync(candidate, target!, current, latestVersion, service,
                request.RestartService, cancellationToken);
        }
        catch (UpdateException ex)
        {
            return UpdateResult.Failure(ex.Code, current, latestVersion, target, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return UpdateResult.Failure(UpdateErrorCode.InstallFailure, current, latestVersion, target,
                "Could not replace the executable. Check path access and retry.");
        }
    }

    public async Task<UpdateResult> InstallSelfAsync(InstallRequest request, CancellationToken cancellationToken = default)
    {
        var current = _runtime.CurrentVersion;
        string? target = null;
        try
        {
            var rid = RequireNative();
            var source = _runtime.ProcessPath!;
            var version = ParseVersion(current);
            if (string.IsNullOrWhiteSpace(request.InstallDirectory))
                throw new UpdateException(UpdateErrorCode.InstallFailure, "An installation directory is required.");
            var directory = Path.GetFullPath(request.InstallDirectory);
            target = Path.Combine(directory, rid.StartsWith("win-", StringComparison.Ordinal) ? "orelay.exe" : "orelay");
            if (string.Equals(source, target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return new UpdateResult(true, false, current, current, target, "ORelay is already installed at this path.");

            RejectLinkedPath(target);
            Directory.CreateDirectory(directory);
            using var updateLock = AcquireLock(target);
            var sameVersion = false;
            if (File.Exists(target))
            {
                var installedVersion = SafeReadInstalledVersion(target);
                if (installedVersion is null || !SemVersion.TryParse(installedVersion, out var installed))
                    throw new UpdateException(UpdateErrorCode.InstallFailure, "The target has no recognized ORelay version metadata. It was not executed or replaced. Choose an empty installation directory.");
                if (installed!.Value.CompareTo(version) > 0)
                    throw new UpdateException(UpdateErrorCode.Downgrade, "The installed executable is newer. No downgrade was made.");
                sameVersion = installed.Value.CompareTo(version) == 0;
            }

            var bytes = await File.ReadAllBytesAsync(source, cancellationToken);
            // The bootstrap checks the published archive. Also verify the bytes
            // copied from this running executable before launching the candidate.
            var sourceHash = SHA256.HashData(bytes);
            if (sameVersion)
            {
                await using var installedStream = File.OpenRead(target);
                var installedHash = await SHA256.HashDataAsync(installedStream, cancellationToken);
                if (CryptographicOperations.FixedTimeEquals(sourceHash, installedHash))
                    return new UpdateResult(true, false, current, current, target, "This executable is already installed at the destination.");
            }

            var service = CheckService(target, request.ConfigurationPath, request.ServiceName, request.RestartService);
            var result = await ReplaceAsync(bytes, target, current, current, service,
                request.RestartService, cancellationToken, sourceHash);
            return result with
            {
                Message = result.Succeeded ?
                $"ORelay {current} installed at '{target}'. Add this directory to PATH if needed." : result.Message
            };
        }
        catch (UpdateException ex)
        {
            return UpdateResult.Failure(ex.Code, current, current, target, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return UpdateResult.Failure(UpdateErrorCode.InstallFailure, current, current, target,
                "Could not install the executable. Check the directory and permissions.");
        }
    }

    private string RequireNative()
    {
        if (!_runtime.IsNative || string.IsNullOrWhiteSpace(_runtime.ProcessPath) || !File.Exists(_runtime.ProcessPath))
            throw new UpdateException(UpdateErrorCode.UnsupportedRuntime,
                "Install and update require a published Native AOT ORelay executable.");
        RejectLinkedPath(_runtime.ProcessPath);
        return _runtime.RuntimeIdentifier ?? throw new UpdateException(UpdateErrorCode.UnsupportedPlatform,
            "This operating system or architecture has no ORelay native release.");
    }

    private static SemVersion ParseVersion(string value) =>
        SemVersion.TryParse(value, out var version) ? version!.Value :
            throw new UpdateException(UpdateErrorCode.InvalidVersion, "The executable has an invalid version stamp.");

    private sealed record ServiceContext(ServiceRequest? Request, ServiceState State);

    private ServiceContext CheckService(string target, string? configurationPath, string? name, bool restart)
    {
        if (_services.Platform == ServicePlatform.Unsupported)
        {
            if (restart) throw new UpdateException(UpdateErrorCode.UnsupportedPlatform,
                "Service restart is unavailable on this platform.");
            return new ServiceContext(null, ServiceState.NotInstalled);
        }

        var config = string.IsNullOrWhiteSpace(configurationPath) ?
            Path.Combine(Path.GetDirectoryName(target)!, "orelay.json") : Path.GetFullPath(configurationPath);
        ServiceRequest request;
        try { request = new ServiceRequest(target, config, name); }
        catch (ArgumentException) { throw new UpdateException(UpdateErrorCode.ServiceConflict, "Service name or configuration path is invalid."); }
        var status = _services.Execute(ServiceOperation.Status, request);
        if (!status.Succeeded)
            throw new UpdateException(status.ErrorCode == ServiceErrorCode.Conflict ? UpdateErrorCode.ServiceConflict : UpdateErrorCode.ServiceFailure,
                status.ErrorCode == ServiceErrorCode.Conflict ?
                    "The selected service records a different executable or configuration path." :
                    "The selected service could not be inspected before installation.");
        if (restart && status.State == ServiceState.NotInstalled)
            throw new UpdateException(UpdateErrorCode.ServiceFailure, "The selected service is not installed.");
        if (status.State is ServiceState.StartPending or ServiceState.StopPending or ServiceState.Paused or ServiceState.Failed or ServiceState.Unknown)
            throw new UpdateException(UpdateErrorCode.ServiceFailure, "The selected service is not in a stable running or stopped state.");
        return new ServiceContext(request, status.State);
    }

    private async Task<UpdateResult> ReplaceAsync(byte[] bytes, string target, string previousVersion,
        string nextVersion, ServiceContext service, bool restart, CancellationToken cancellationToken,
        byte[]? expectedSourceHash = null)
    {
        var stage = target + ".stage-" + Guid.NewGuid().ToString("N");
        var backup = target + ".backup-" + Guid.NewGuid().ToString("N");
        var hadTarget = File.Exists(target);
        var replaced = false;
        var restoreRunningService = false;
        try
        {
            await File.WriteAllBytesAsync(stage, bytes, cancellationToken);
            if (expectedSourceHash is not null && !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(await File.ReadAllBytesAsync(stage, cancellationToken)), expectedSourceHash))
                throw new UpdateException(UpdateErrorCode.IntegrityFailure, "The staged executable differs from the source.");
            if (!OperatingSystem.IsWindows())
            {
                var mode = hadTarget ? File.GetUnixFileMode(target) :
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(stage, mode);
            }

            var version = await SafeReadVersionAsync(stage, cancellationToken);
            if (!string.Equals(NormalizeVersion(version), NormalizeVersion(nextVersion), StringComparison.Ordinal))
                throw new UpdateException(UpdateErrorCode.VersionMismatch, "The candidate executable reports a different version.");
            if (restart && service.Request is not null && service.State == ServiceState.Running)
            {
                // The stop can take effect even when its confirmation fails.
                restoreRunningService = true;
                var stopped = _services.Execute(ServiceOperation.Stop, service.Request);
                if (!stopped.Succeeded || _services.Execute(ServiceOperation.Status, service.Request) is not
                    { Succeeded: true, State: ServiceState.Stopped })
                    throw new UpdateException(UpdateErrorCode.ServiceFailure,
                        "The service stop could not be confirmed. The executable was not changed.");
            }
            if (hadTarget) File.Move(target, backup);
            try
            {
                File.Move(stage, target);
                replaced = true;
            }
            catch
            {
                if (hadTarget) File.Move(backup, target);
                throw;
            }

            var installedVersion = await SafeReadVersionAsync(target, cancellationToken);
            if (!string.Equals(NormalizeVersion(installedVersion), NormalizeVersion(nextVersion), StringComparison.Ordinal))
                throw new UpdateException(UpdateErrorCode.VersionMismatch,
                    "The installed executable reports a different version. The previous executable will be restored.");

            if (restoreRunningService && service.Request is not null)
            {
                var started = _services.Execute(ServiceOperation.Start, service.Request);
                var healthy = started.Succeeded && _services.Execute(ServiceOperation.Status, service.Request) is
                { Succeeded: true, State: ServiceState.Running } &&
                    await VerifyHealthAsync(service.Request.ConfigurationPath, cancellationToken);
                if (!healthy)
                    throw new UpdateException(UpdateErrorCode.ServiceFailure,
                        "The service did not start healthy. The previous executable will be restored.");
                restoreRunningService = false;
            }

            var message = $"ORelay {nextVersion} installed at '{target}'.";
            if (service.State == ServiceState.Running && !restart)
                message += " Restart the service when ready to run the new version.";
            if (hadTarget)
            {
                try { File.Delete(backup); }
                catch (IOException) { message += $" Previous executable retained at '{backup}' until it can be removed."; }
                catch (UnauthorizedAccessException) { message += $" Previous executable retained at '{backup}' until it can be removed."; }
            }
            return new UpdateResult(true, true, previousVersion, nextVersion, target, message);
        }
        catch
        {
            if (replaced)
            {
                try
                {
                    if (restart && service.Request is not null && service.State == ServiceState.Running)
                    {
                        var status = _services.Execute(ServiceOperation.Status, service.Request);
                        if (!status.Succeeded) throw new IOException("Could not inspect the failed service before rollback.");
                        if (status.State == ServiceState.Running)
                        {
                            var stop = _services.Execute(ServiceOperation.Stop, service.Request);
                            if (!stop.Succeeded || _services.Execute(ServiceOperation.Status, service.Request) is not
                                { Succeeded: true, State: ServiceState.Stopped })
                                throw new IOException("Could not stop the failed service before rollback.");
                        }
                        restoreRunningService = true;
                    }
                    File.Move(target, stage);
                    if (hadTarget) File.Move(backup, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new UpdateException(UpdateErrorCode.InstallFailure,
                        $"Replacement failed and automatic rollback could not complete. Previous executable: '{backup}'.");
                }
            }
            // A rejected stop may have left the original service running.
            if (!replaced && restoreRunningService && service.Request is not null &&
                _services.Execute(ServiceOperation.Status, service.Request) is
                { Succeeded: true, State: ServiceState.Running })
                restoreRunningService = false;
            if (restoreRunningService && service.Request is not null)
            {
                var restored = _services.Execute(ServiceOperation.Start, service.Request);
                if (!restored.Succeeded || _services.Execute(ServiceOperation.Status, service.Request) is not
                    { Succeeded: true, State: ServiceState.Running } ||
                    !await VerifyHealthAsync(service.Request.ConfigurationPath, cancellationToken))
                    throw new UpdateException(UpdateErrorCode.ServiceFailure,
                        "The previous executable is in place, but the service could not be restarted healthy.");
            }
            throw;
        }
        finally
        {
            try { if (File.Exists(stage)) File.Delete(stage); }
            catch (IOException) { /* An executing image may remain until process exit. */ }
            catch (UnauthorizedAccessException) { /* Preserve it for manual cleanup. */ }
        }
    }

    private string? SafeReadInstalledVersion(string path)
    {
        try { return _runtime.ReadInstalledVersion(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return null; }
    }

    private async Task<string?> SafeReadVersionAsync(string path, CancellationToken cancellationToken)
    {
        try { return await _runtime.ReadExecutableVersionAsync(path, cancellationToken); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return null; }
    }

    private static string? NormalizeVersion(string? value) => value?.Split('+')[0];

    private async Task<bool> VerifyHealthAsync(string configurationPath, CancellationToken cancellationToken)
    {
        Uri url;
        try
        {
            var settings = new RelayConfigurationStore(configurationPath).Read();
            var host = settings.Bind switch
            {
                "::" or "[::]" => "::1",
                "0.0.0.0" or "+" or "*" => "127.0.0.1",
                _ => settings.Bind.Trim('[', ']'),
            };
            url = new UriBuilder("http", host, settings.Port, "/health").Uri;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or RelayConfigurationException)
        {
            return false;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            RelayHealthProbeResult health;
            try { health = await _health.CheckAsync(url, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            { health = RelayHealthProbeResult.Unreachable("Health request failed."); }
            if (health.Reachable && health.IsRelay && health.HttpStatusCode == 200 &&
                string.Equals(health.Identity, "orelay", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase)) return true;
            if (attempt < 19) await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    private static void RejectLinkedPath(string target)
    {
        var current = Path.GetFullPath(target);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UpdateException(UpdateErrorCode.InstallFailure,
                        "The executable path contains a symbolic link or junction. Use a direct path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            current = Path.GetDirectoryName(current);
        }
    }

    private static FileStream AcquireLock(string target)
    {
        RejectLinkedPath(target);
        RejectLinkedPath(target + ".update.lock");
        try
        {
            return new FileStream(target + ".update.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new UpdateException(UpdateErrorCode.Busy, "Another install or update is using this destination.");
        }
    }
}
