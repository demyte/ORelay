using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ORelay.Configuration;

namespace ORelay.Updating;

/// <summary>Schedules one independent update worker at a time while the service runs.</summary>
internal sealed partial class ServiceAutoUpdateService(
    string executablePath,
    string configurationPath,
    string serviceName,
    RelayConfigurationState state,
    ILogger<ServiceAutoUpdateService> logger,
    Func<string, string, string, CancellationToken, Task<int>>? launch = null,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : BackgroundService
{
    private readonly Func<string, string, string, CancellationToken, Task<int>> _launch =
        launch ?? ServiceAutoUpdateLauncher.RunAsync;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? ((interval, token) => Task.Delay(interval, timeProvider ?? TimeProvider.System, token));
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private DateTimeOffset _startedAt;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _startedAt = _timeProvider.GetUtcNow();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        state.Changed += OnSettingsChanged;
        try
        {
            // The first check is due one interval after this service starts.
            var lastAttempt = _startedAt;
            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = state.Current;
                if (!settings.AutoUpdate)
                {
                    await _changes.Reader.ReadAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var interval = TimeSpan.FromSeconds(settings.AutoUpdateIntervalSeconds);
                var remaining = lastAttempt + interval - _timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    try
                    {
                        await WaitForChangeOrDelayAsync(remaining, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested)
                    {
                        WorkerFailed(logger);
                        lastAttempt = _timeProvider.GetUtcNow();
                    }
                    continue;
                }

                // A setting may have changed as the delay expired. Only the current
                // schedule can authorize a new worker.
                var current = state.Current;
                if (!current.AutoUpdate || current.AutoUpdateIntervalSeconds != settings.AutoUpdateIntervalSeconds)
                    continue;

                try
                {
                    var exitCode = await _launch(executablePath, configurationPath, serviceName, stoppingToken)
                        .ConfigureAwait(false);
                    if (exitCode != 0)
                        AttemptFailed(logger, exitCode);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // Exception text can contain environment values, so keep it out of logs.
                    WorkerFailed(logger);
                }
                finally
                {
                    lastAttempt = _timeProvider.GetUtcNow();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the current wait.
        }
        finally
        {
            state.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged() => _changes.Writer.TryWrite(true);

    private async Task WaitForChangeOrDelayAsync(TimeSpan remaining, CancellationToken stoppingToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var delayTask = _delay(remaining, wait.Token);
        var changeTask = _changes.Reader.ReadAsync(wait.Token).AsTask();
        var first = await Task.WhenAny(delayTask, changeTask).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);

        // Observe both tasks so the canceled loser cannot outlive this wait.
        try
        {
            await first.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
        }

        try
        {
            await (first == delayTask ? changeTask : delayTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Automatic update attempt failed with exit code {ExitCode}. The next attempt is after the configured interval.")]
    private static partial void AttemptFailed(ILogger logger, int exitCode);

    [LoggerMessage(2, LogLevel.Warning, "Could not run the automatic update worker. The next attempt is after the configured interval.")]
    private static partial void WorkerFailed(ILogger logger);
}
