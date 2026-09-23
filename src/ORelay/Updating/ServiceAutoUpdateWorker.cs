using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ORelay.Configuration;
using ORelay.Diagnostics;
using ORelay.Services;

namespace ORelay.Updating;

internal sealed record ServiceAutoUpdateResult(
    DateTimeOffset CheckedAtUtc,
    string Status,
    string Message,
    string? CurrentVersion = null,
    string? LatestVersion = null,
    UpdateErrorCode? ErrorCode = null);

internal sealed partial class ServiceAutoUpdateWorker
{
    private readonly IUpdateRuntime _runtime;
    private readonly IPlatformServiceManager _services;
    private readonly Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? _check;
    private readonly Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? _update;

    internal ServiceAutoUpdateWorker(
        IUpdateRuntime? runtime = null,
        IPlatformServiceManager? services = null,
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? check = null,
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? update = null)
    {
        _runtime = runtime ?? new NativeUpdateRuntime();
        _services = services ?? ServiceManagerFactory.Create();
        _check = check;
        _update = update;
    }

    internal static string ResultPath(string configurationPath) =>
        Path.ChangeExtension(configurationPath, "auto-update.json");

    internal async Task<int> RunAsync(string configurationPath, string serviceName,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(configurationPath) || !ServiceIdentity.IsValidName(serviceName))
            return 3;

        var config = Path.GetFullPath(configurationPath);
        var resultPath = ResultPath(config);
        var parent = Path.GetDirectoryName(config)!;
        if (!Directory.Exists(parent)) return 3;
        using var logging = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new RotatingFileLoggerProvider(config)));
        var logger = logging.CreateLogger<ServiceAutoUpdateWorker>();
        var engine = new UpdateEngine(runtime: _runtime, services: _services,
            logger: logging.CreateLogger<UpdateEngine>());
        var checkRelease = _check ?? engine.CheckAsync;
        var installUpdate = _update ?? engine.UpdateAsync;
        var elapsed = Stopwatch.StartNew();
        var phase = "preflight";
        var reason = "unexpected-error";
        ServiceAutoUpdateResult result;
        FileStream? checkLock = null;
        try
        {
            // The update engine locks its target only after fetching a release. This
            // sibling lock also prevents overlapping checks of the same service.
            checkLock = new FileStream(config + ".auto-update.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // A previous worker owns this check. Do not replace its result file.
            CheckSkipped(logger);
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return await WriteResultAsync(resultPath,
                new(DateTimeOffset.UtcNow, "failed", "Could not lock the auto-update check."), logger,
                serviceName, "check-lock-access-denied", elapsed.ElapsedMilliseconds);
        }

        using (checkLock)
        {
            CheckStarted(logger, serviceName);
            try
            {
                var store = new RelayConfigurationStore(config);
                if (!store.Exists)
                {
                    reason = "configuration-missing";
                    result = new(DateTimeOffset.UtcNow, "failed", "The selected configuration file is missing.");
                }
                else if (!store.Read().AutoUpdate)
                {
                    reason = "disabled-in-configuration";
                    result = new(DateTimeOffset.UtcNow, "disabled", "Automatic updates are disabled.");
                }
                else if (!IsNativeTargetCurrent())
                {
                    reason = "native-target-mismatch";
                    result = new(DateTimeOffset.UtcNow, "failed", "Automatic updates require the current installed native executable.");
                }
                else if (!IsOwnedRunningService(config, serviceName))
                {
                    reason = "service-not-owned-or-running";
                    result = new(DateTimeOffset.UtcNow, "failed", "The selected service is not an owned, running ORelay service at this executable and configuration path.");
                }
                else
                {
                    var request = new UpdateRequest(true, serviceName, config);
                    phase = "release-check";
                    CheckingRelease(logger);
                    var check = await checkRelease(request, cancellationToken);
                    if (!check.Succeeded)
                    {
                        reason = "release-check-failed";
                        result = new(DateTimeOffset.UtcNow, "failed", check.Message,
                            check.CurrentVersion, check.LatestVersion, check.ErrorCode);
                    }
                    else if (!check.UpdateAvailable)
                    {
                        reason = "no-newer-stable-release";
                        result = new(DateTimeOffset.UtcNow, "up-to-date", "No update is available.",
                            check.CurrentVersion, check.LatestVersion);
                    }
                    else if (!store.Exists || !store.Read().AutoUpdate)
                    {
                        reason = "disabled-during-check";
                        result = new(DateTimeOffset.UtcNow, "disabled", "Automatic updates were disabled during the release check.",
                            check.CurrentVersion, check.LatestVersion);
                    }
                    else if (!IsNativeTargetCurrent() || !IsOwnedRunningService(config, serviceName))
                    {
                        reason = "target-changed-during-check";
                        result = new(DateTimeOffset.UtcNow, "failed", "The selected service changed during the release check.",
                            check.CurrentVersion, check.LatestVersion);
                    }
                    else
                    {
                        phase = "installation";
                        InstallingUpdate(logger, check.CurrentVersion, check.LatestVersion);
                        var updated = await installUpdate(request, cancellationToken);
                        reason = updated.Succeeded ? updated.Changed ? "installed" : "already-installed" : "installation-failed";
                        result = new(DateTimeOffset.UtcNow, updated.Succeeded ?
                            updated.Changed ? "updated" : "up-to-date" : "failed",
                            updated.Succeeded ? updated.Changed ? "Update installed." : "No update was needed." :
                                updated.Message,
                            updated.CurrentVersion, updated.LatestVersion, updated.ErrorCode);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                reason = "cancelled";
                result = new(DateTimeOffset.UtcNow, "failed", "The auto-update check was cancelled.");
            }
            catch (RelayConfigurationException ex)
            {
                reason = "configuration-" + ex.Code;
                result = new(DateTimeOffset.UtcNow, "failed", "The selected configuration could not be read or validated.");
            }
            catch (Exception)
            {
                reason = "unexpected-" + phase + "-failure";
                result = new(DateTimeOffset.UtcNow, "failed", "The auto-update check failed. Inspect the service and configuration.");
            }

            return await WriteResultAsync(resultPath, result, logger, serviceName, reason, elapsed.ElapsedMilliseconds);
        }
    }

    private bool IsNativeTargetCurrent()
    {
        var target = _runtime.ProcessPath;
        if (!_runtime.IsNative || string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target) ||
            !File.Exists(target) || _runtime.RuntimeIdentifier is null)
            return false;

        var installed = _runtime.ReadInstalledVersion(target);
        return installed is not null && SemVersion.TryParse(installed, out var installedVersion) &&
            SemVersion.TryParse(_runtime.CurrentVersion, out var currentVersion) &&
            installedVersion!.Value.CompareTo(currentVersion!.Value) == 0;
    }

    private bool IsOwnedRunningService(string config, string serviceName)
    {
        if (_services.Platform == ServicePlatform.Unsupported) return false;
        var request = new ServiceRequest(_runtime.ProcessPath!, config, serviceName);
        var status = _services.Execute(ServiceOperation.Status, request);
        return status.Succeeded && status.Owned && status.State == ServiceState.Running;
    }

    private static async Task<int> WriteResultAsync(string path, ServiceAutoUpdateResult result, ILogger logger,
        string serviceName, string reason, long elapsedMilliseconds)
    {
        if (result.Status == "failed")
            CheckFailed(logger, reason, result.ErrorCode?.ToString() ?? "none", serviceName,
                result.CurrentVersion, result.LatestVersion, elapsedMilliseconds);
        else
            CheckCompleted(logger, result.Status, reason, serviceName,
                result.CurrentVersion, result.LatestVersion, elapsedMilliseconds);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, result,
                    ServiceAutoUpdateJsonContext.Default.ServiceAutoUpdateResult);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            return result.Status == "failed" ? 3 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ResultWriteFailed(logger);
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return 3;
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Automatic update check started for {ServiceName}.")]
    private static partial void CheckStarted(ILogger logger, string serviceName);

    [LoggerMessage(2, LogLevel.Information, "Checking the stable release feed.")]
    private static partial void CheckingRelease(ILogger logger);

    [LoggerMessage(3, LogLevel.Information, "Installing automatic update from {CurrentVersion} to {LatestVersion}.")]
    private static partial void InstallingUpdate(ILogger logger, string? currentVersion, string? latestVersion);

    [LoggerMessage(4, LogLevel.Information, "Automatic update completed: {Status}; reason={Reason}, service={ServiceName}, current={CurrentVersion}, latest={LatestVersion}, elapsed={ElapsedMilliseconds}ms.")]
    private static partial void CheckCompleted(ILogger logger, string status, string reason, string serviceName,
        string? currentVersion, string? latestVersion, long elapsedMilliseconds);

    [LoggerMessage(5, LogLevel.Warning, "Automatic update failed: {Reason}; error={ErrorCode}, service={ServiceName}, current={CurrentVersion}, latest={LatestVersion}, elapsed={ElapsedMilliseconds}ms. See the latest auto-update result for details.")]
    private static partial void CheckFailed(ILogger logger, string reason, string errorCode, string serviceName,
        string? currentVersion, string? latestVersion, long elapsedMilliseconds);

    [LoggerMessage(6, LogLevel.Information, "Automatic update check skipped; the check lock could not be acquired.")]
    private static partial void CheckSkipped(ILogger logger);

    [LoggerMessage(7, LogLevel.Warning, "Could not save the latest automatic update result.")]
    private static partial void ResultWriteFailed(ILogger logger);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServiceAutoUpdateResult))]
internal sealed partial class ServiceAutoUpdateJsonContext : JsonSerializerContext;
