using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ORelay.Updating;

/// <summary>Schedules one independent update worker at a time while the service runs.</summary>
internal sealed partial class ServiceAutoUpdateService(
    string executablePath,
    string configurationPath,
    string serviceName,
    TimeSpan interval,
    ILogger<ServiceAutoUpdateService> logger,
    Func<string, string, string, CancellationToken, Task<int>>? launch = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : BackgroundService
{
    private readonly Func<string, string, string, CancellationToken, Task<int>> _launch =
        launch ?? ServiceAutoUpdateLauncher.RunAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Waiting first prevents restart loops from repeatedly checking the feed.
                await _delay(interval, stoppingToken).ConfigureAwait(false);
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
                // Never let network, process, or filesystem failures stop callback routing.
                // Exception text can contain environment values, so keep it out of logs.
                WorkerFailed(logger);
            }
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Automatic update attempt failed with exit code {ExitCode}. The next attempt is after the configured interval.")]
    private static partial void AttemptFailed(ILogger logger, int exitCode);

    [LoggerMessage(2, LogLevel.Warning, "Could not run the automatic update worker. The next attempt is after the configured interval.")]
    private static partial void WorkerFailed(ILogger logger);
}
