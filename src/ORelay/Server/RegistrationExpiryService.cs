using Microsoft.Extensions.Hosting;

namespace ORelay.Server;

/// <summary>
/// Periodically removes expired entries so expiry does not depend on another
/// callback or management request. The store also checks the boundary on every
/// operation, so routing stops at the exact expiry instant.
/// </summary>
public sealed class RegistrationExpiryService(
    RegistrationStore registrations,
    TimeSpan? sweepInterval = null) : BackgroundService
{
    private readonly TimeSpan _sweepInterval = sweepInterval ?? TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_sweepInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("The registration expiry sweep interval must be greater than zero.");
        }

        using var timer = new PeriodicTimer(_sweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            registrations.RemoveExpired();
        }
    }
}
