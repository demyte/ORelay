using System.Text.Json;
using System.Text.Json.Serialization;
using ORelay.Configuration;
using ORelay.Services;

namespace ORelay.Updating;

internal sealed record ServiceAutoUpdateResult(
    DateTimeOffset CheckedAtUtc,
    string Status,
    string Message,
    string? CurrentVersion = null,
    string? LatestVersion = null,
    UpdateErrorCode? ErrorCode = null);

internal sealed class ServiceAutoUpdateWorker
{
    private readonly IUpdateRuntime _runtime;
    private readonly IPlatformServiceManager _services;
    private readonly Func<UpdateRequest, CancellationToken, Task<UpdateResult>> _check;
    private readonly Func<UpdateRequest, CancellationToken, Task<UpdateResult>> _update;

    internal ServiceAutoUpdateWorker(
        IUpdateRuntime? runtime = null,
        IPlatformServiceManager? services = null,
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? check = null,
        Func<UpdateRequest, CancellationToken, Task<UpdateResult>>? update = null)
    {
        _runtime = runtime ?? new NativeUpdateRuntime();
        _services = services ?? ServiceManagerFactory.Create();
        var engine = new UpdateEngine(runtime: _runtime, services: _services);
        _check = check ?? engine.CheckAsync;
        _update = update ?? engine.UpdateAsync;
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
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return await WriteResultAsync(resultPath,
                new(DateTimeOffset.UtcNow, "failed", "Could not lock the auto-update check."));
        }

        using (checkLock)
        {
            try
            {
                var store = new RelayConfigurationStore(config);
                if (!store.Exists)
                {
                    result = new(DateTimeOffset.UtcNow, "failed", "The selected configuration file is missing.");
                }
                else if (!store.Read().AutoUpdate)
                {
                    result = new(DateTimeOffset.UtcNow, "disabled", "Automatic updates are disabled.");
                }
                else if (!IsNativeTargetCurrent())
                {
                    result = new(DateTimeOffset.UtcNow, "failed", "Automatic updates require the current installed native executable.");
                }
                else if (!IsOwnedRunningService(config, serviceName))
                {
                    result = new(DateTimeOffset.UtcNow, "failed", "The selected service is not an owned, running ORelay service at this executable and configuration path.");
                }
                else
                {
                    var request = new UpdateRequest(true, serviceName, config);
                    var check = await _check(request, cancellationToken);
                    if (!check.Succeeded)
                    {
                        result = new(DateTimeOffset.UtcNow, "failed", check.Message,
                            check.CurrentVersion, check.LatestVersion, check.ErrorCode);
                    }
                    else if (!check.UpdateAvailable)
                    {
                        result = new(DateTimeOffset.UtcNow, "up-to-date", "No update is available.",
                            check.CurrentVersion, check.LatestVersion);
                    }
                    else if (!store.Exists || !store.Read().AutoUpdate)
                    {
                        result = new(DateTimeOffset.UtcNow, "disabled", "Automatic updates were disabled during the release check.",
                            check.CurrentVersion, check.LatestVersion);
                    }
                    else if (!IsNativeTargetCurrent() || !IsOwnedRunningService(config, serviceName))
                    {
                        result = new(DateTimeOffset.UtcNow, "failed", "The selected service changed during the release check.",
                            check.CurrentVersion, check.LatestVersion);
                    }
                    else
                    {
                        var updated = await _update(request, cancellationToken);
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
                result = new(DateTimeOffset.UtcNow, "failed", "The auto-update check was cancelled.");
            }
            catch (Exception)
            {
                result = new(DateTimeOffset.UtcNow, "failed", "The auto-update check failed. Inspect the service and configuration.");
            }

            return await WriteResultAsync(resultPath, result);
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

    private static async Task<int> WriteResultAsync(string path, ServiceAutoUpdateResult result)
    {
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
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return 3;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServiceAutoUpdateResult))]
internal sealed partial class ServiceAutoUpdateJsonContext : JsonSerializerContext;
