using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ORelay.Aspire.Hosting;

internal sealed record Registration(string Id, string CallbackUrl, DateTimeOffset ExpiresAt, int LeaseSeconds, string RelayCallbackUrl);

// The gate serializes registration and deletion. Cancellation interrupts renewal before stop waits for the gate.
internal sealed class RegistrationSession(HttpClient client) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? renewalCancellation;
    private Task? renewal;
    private Registration? registration;
    private volatile bool restartRequired;
    private volatile string healthDescription = "Waiting for endpoint allocation.";
    private volatile bool healthy;
    internal ILogger Logger { get; set; } = NullLogger.Instance;
    internal HealthCheckResult Health => healthy ? HealthCheckResult.Healthy("ORelay registration is active.") : HealthCheckResult.Degraded(healthDescription);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal Task<Registration> EnsureRegisteredAsync(Uri destination, CancellationToken cancellationToken)
        => EnsureRegisteredAsync(_ => Task.FromResult(destination), cancellationToken);

    internal async Task<Registration> EnsureRegisteredAsync(Func<CancellationToken, Task<Uri>> destinationFactory, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (restartRequired)
                throw new InvalidOperationException("ORelay registration was lost. Explicitly restart the affected resource or AppHost before beginning another authorization flow.");
            if (registration is not null) return registration;
            var destination = await destinationFactory(linked.Token).ConfigureAwait(false);
            var requestedAt = Stopwatch.GetTimestamp();
            using var response = await client.PostAsJsonAsync("registrations", new { callbackUrl = destination.AbsoluteUri }, linked.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"ORelay registration failed with HTTP {(int)response.StatusCode}. Check the relay address and callback configuration, then restart the resource.");
            var created = await response.Content.ReadFromJsonAsync<Registration>(linked.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("ORelay returned an empty registration response.");
            if (string.IsNullOrWhiteSpace(created.Id) || created.LeaseSeconds <= 0 || !Uri.TryCreate(created.RelayCallbackUrl, UriKind.Absolute, out _))
                throw new InvalidOperationException("ORelay returned an invalid registration response.");
            registration = created;
            if (Stopwatch.GetElapsedTime(requestedAt).TotalSeconds >= created.LeaseSeconds)
            {
                MarkLost();
                throw new InvalidOperationException("ORelay registration response arrived after its lease expired. Restart the resource.");
            }
            healthy = true;
            SessionLog.Registered(Logger);
            renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            renewal = RenewAsync(created, requestedAt, renewalCancellation.Token);
            return created;
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("ORelay is unreachable. Start the relay or correct its server URL, then restart the resource.");
        }
        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
        {
            throw new InvalidOperationException("ORelay registration timed out. Check the relay, then restart the resource.");
        }
        finally { gate.Release(); }
    }

    private async Task RenewAsync(Registration current, long requestedAt, CancellationToken ct)
    {
        var deadline = requestedAt + (long)(current.LeaseSeconds * (double)Stopwatch.Frequency);
        var delay = TimeSpan.FromSeconds(Math.Min(60, current.LeaseSeconds / 3.0));
        try
        {
            while (true)
            {
                var remaining = TimeSpan.FromSeconds(Math.Max(0, (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency));
                await Task.Delay(delay < remaining ? delay : remaining, ct).ConfigureAwait(false);
                if (Stopwatch.GetTimestamp() >= deadline) { MarkLost(); return; }
                try
                {
                    var renewalRequestedAt = Stopwatch.GetTimestamp();
                    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    requestCancellation.CancelAfter(TimeSpan.FromSeconds(Math.Max(0, (deadline - renewalRequestedAt) / (double)Stopwatch.Frequency)));
                    using var response = await client.PutAsync($"registrations/{Uri.EscapeDataString(current.Id)}/lease", null, requestCancellation.Token).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { MarkLost(); return; }
                    response.EnsureSuccessStatusCode();
                    var renewed = await response.Content.ReadFromJsonAsync<Registration>(requestCancellation.Token).ConfigureAwait(false);
                    if (renewed is null || renewed.Id != current.Id || renewed.LeaseSeconds <= 0) { MarkLost(); return; }
                    deadline = renewalRequestedAt + (long)(renewed.LeaseSeconds * (double)Stopwatch.Frequency);
                    if (Stopwatch.GetTimestamp() >= deadline) { MarkLost(); return; }
                    healthy = true;
                    delay = TimeSpan.FromSeconds(Math.Min(60, renewed.LeaseSeconds / 3.0));
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    healthy = false;
                    healthDescription = "ORelay renewal disconnected. Retrying only until the current lease expires.";
                    SessionLog.Disconnected(Logger);
                    var remainingSeconds = (deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                    if (remainingSeconds <= 0) { MarkLost(); return; }
                    delay = TimeSpan.FromSeconds(Math.Min(5, remainingSeconds));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (System.Text.Json.JsonException) { MarkLost(); }
    }

    private void MarkLost()
    {
        healthy = false;
        restartRequired = true;
        healthDescription = "ORelay registration lost. Restart this resource or AppHost before another authorization flow.";
        SessionLog.Lost(Logger);
    }

    internal async Task StopRegistrationAsync()
    {
        renewalCancellation?.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Also cancels renewal created by an in-flight initial registration.
            renewalCancellation?.Cancel();
            if (renewal is not null) await renewal.ConfigureAwait(false);
            if (registration is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    using var response = await client.DeleteAsync($"registrations/{Uri.EscapeDataString(registration.Id)}", timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) SessionLog.CleanupFailed(Logger);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                { SessionLog.CleanupFailed(Logger); }
            }
            registration = null;
            renewal = null;
            renewalCancellation?.Dispose();
            renewalCancellation = null;
            restartRequired = false;
            healthy = false;
            healthDescription = "Resource stopped.";
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        await StopRegistrationAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        shutdown.Cancel();
        renewalCancellation?.Cancel();
        renewalCancellation?.Dispose();
        shutdown.Dispose();
        client.Dispose();
        gate.Dispose();
    }
}
