using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ORelay.Discovery;

namespace ORelay.Configuration;

/// <summary>Applies valid changes to the selected configuration file while the relay runs.</summary>
internal sealed partial class RelayConfigurationMonitor : BackgroundService
{
    private readonly RelayConfigurationStore _store;
    private readonly RelaySettingsPatch? _invocationOverrides;
    private readonly Func<RelaySettings, CancellationToken, Task<bool>> _apply;
    private readonly Func<RelaySettings, CancellationToken, Task<RelaySettingsDiscoveryResult>> _resolveDiscovery;
    private readonly ILogger<RelayConfigurationMonitor> _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _fileEvent = new(0, 1);
    private string? _lastFingerprint;
    private bool _lastAttemptSucceeded;

    public RelayConfigurationMonitor(
        string configurationPath,
        RelaySettingsPatch? invocationOverrides,
        Func<RelaySettings, CancellationToken, Task<bool>> apply,
        ILogger<RelayConfigurationMonitor> logger,
        Func<RelaySettings, CancellationToken, Task<RelaySettingsDiscoveryResult>>? resolveDiscovery = null,
        TimeSpan? debounce = null,
        TimeSpan? pollInterval = null)
    {
        _store = new RelayConfigurationStore(configurationPath);
        _invocationOverrides = invocationOverrides;
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolveDiscovery = resolveDiscovery ?? ((settings, token) => RelaySettingsDiscovery.ResolveAsync(
            settings, cancellationToken: token));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        if (_debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = CreateWatcher();
            // Watch first, then read. The initial read closes the startup gap.
            await ReloadAsync(fileEvent: false, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                var changed = await _fileEvent.WaitAsync(_pollInterval, stoppingToken).ConfigureAwait(false);
                if (changed)
                {
                    await Task.Delay(_debounce, stoppingToken).ConfigureAwait(false);
                    while (_fileEvent.Wait(0, CancellationToken.None)) { }
                }

                watcher ??= CreateWatcher();
                await ReloadAsync(changed, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private FileSystemWatcher? CreateWatcher()
    {
        var directory = Path.GetDirectoryName(_store.FilePath)!;
        if (!Directory.Exists(directory)) return null;

        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Polling still notices a later file or directory change.
            return null;
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (IsSelectedFile(args.FullPath)) SignalFileEvent();
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (IsSelectedFile(args.FullPath) || IsSelectedFile(args.OldFullPath)) SignalFileEvent();
    }

    private void OnWatcherError(object sender, ErrorEventArgs args) => SignalFileEvent();

    private bool IsSelectedFile(string path) =>
        string.Equals(path, _store.FilePath, OperatingSystem.IsWindows() ?
            StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void SignalFileEvent()
    {
        try { _fileEvent.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task ReloadAsync(bool fileEvent, CancellationToken token)
    {
        string? fingerprint = null;
        try
        {
            // Read never creates a missing file. Missing is distinct from default settings.
            var json = ReadTextOrNull();
            if (json is null)
            {
                if (_lastFingerprint is not null || fileEvent)
                    MissingFile(_logger);
                _lastFingerprint = null;
                _lastAttemptSucceeded = false;
                return;
            }

            fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            if (fingerprint == _lastFingerprint && (_lastAttemptSucceeded || !fileEvent)) return;
            _lastAttemptSucceeded = false;

            var settings = _store.ReadCapturedText(json, _invocationOverrides);
            var discovery = await _resolveDiscovery(settings, token).ConfigureAwait(false);
            if (!discovery.Succeeded || discovery.Settings is null)
            {
                DiscoveryFailed(_logger);
                return;
            }

            if (!await _apply(discovery.Settings, token).ConfigureAwait(false))
            {
                ApplyFailed(_logger);
                return;
            }

            _lastAttemptSucceeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Configuration and discovery errors may contain sensitive values.
            ReloadFailed(_logger);
        }
        finally
        {
            if (fingerprint is not null) _lastFingerprint = fingerprint;
        }
    }

    private string? ReadTextOrNull()
    {
        try { return File.ReadAllText(_store.FilePath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    [LoggerMessage(1, LogLevel.Warning, "The selected configuration file is missing; keeping the current settings.")]
    private static partial void MissingFile(ILogger logger);

    [LoggerMessage(2, LogLevel.Warning, "Configuration discovery failed; keeping the current settings.")]
    private static partial void DiscoveryFailed(ILogger logger);

    [LoggerMessage(3, LogLevel.Warning, "Could not apply configuration; keeping the current settings.")]
    private static partial void ApplyFailed(ILogger logger);

    [LoggerMessage(4, LogLevel.Warning, "Could not reload configuration; keeping the current settings.")]
    private static partial void ReloadFailed(ILogger logger);
}
