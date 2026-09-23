using System.Collections.Immutable;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ORelay.Aspire.Hosting;

internal sealed class RelayDashboard(ExternalServiceResource resource) : IDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim publishGate = new(1, 1);
    private readonly Dictionary<string, RegistrationSession> sessions = new(StringComparer.Ordinal);
    private ResourceNotificationService? notifications;
    private ILogger logger = NullLogger.Instance;

    internal ExternalServiceResource Resource => resource;

    internal HealthCheckResult Health
    {
        get
        {
            lock (sync)
            {
                if (sessions.Count == 0) return HealthCheckResult.Degraded("No API registrations configured.");
                var inactive = sessions.Where(pair => pair.Value.View.Status != "Active")
                    .Select(pair => $"{pair.Key}: {pair.Value.View.Status}").ToArray();
                return inactive.Length == 0
                    ? HealthCheckResult.Healthy($"{sessions.Count} active API registration(s).")
                    : HealthCheckResult.Degraded(string.Join("; ", inactive)
                        + (sessions.Values.Any(session => session.View.Status == "Expired or lost")
                            ? ". Restart the affected resource or AppHost before another authorization flow."
                            : ""));
            }
        }
    }

    internal void Add(string name, RegistrationSession session)
    {
        lock (sync)
        {
            sessions.Add(name, session);
            session.Changed = Publish;
        }
    }

    internal void Connect(IServiceProvider services)
    {
        lock (sync)
        {
            notifications ??= services.GetRequiredService<ResourceNotificationService>();
            logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(resource);
        }
        Publish();
    }

    private void Publish()
    {
        ResourceNotificationService? service;
        lock (sync) service = notifications;
        if (service is not null) _ = PublishAsync(service);
    }

    private async Task PublishAsync(ResourceNotificationService service)
    {
        await publishGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (properties, relationships) = Details;
            await service.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                Properties = properties,
                Relationships = relationships
            }).ConfigureAwait(false);
        }
        catch (Exception) { SessionLog.DashboardFailed(logger); }
        finally { publishGate.Release(); }
    }

    internal (ImmutableArray<ResourcePropertySnapshot> Properties, ImmutableArray<RelationshipSnapshot> Relationships) Details
    {
        get
        {
            lock (sync)
            {
                var rows = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();
                var links = ImmutableArray.CreateBuilder<RelationshipSnapshot>();
                foreach (var (name, session) in sessions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var view = session.View;
                    links.Add(new RelationshipSnapshot(name, "Reference"));
                    rows.Add(new ResourcePropertySnapshot($"{name}.status", view.Status) { DisplayName = $"{name} status", IsHighlighted = true });
                    rows.Add(new ResourcePropertySnapshot($"{name}.publicCallback", view.PublicCallback) { DisplayName = $"{name} public callback", IsHighlighted = true });
                    rows.Add(new ResourcePropertySnapshot($"{name}.destination", view.Destination) { DisplayName = $"{name} destination", IsHighlighted = true });
                    rows.Add(new ResourcePropertySnapshot($"{name}.lastRenewal", view.LastRenewal?.ToString("O")) { DisplayName = $"{name} last successful renewal", IsHighlighted = true });
                    rows.Add(new ResourcePropertySnapshot($"{name}.leaseExpiry", view.LeaseExpiry?.ToString("O")) { DisplayName = $"{name} lease expiry", IsHighlighted = true });
                    rows.Add(new ResourcePropertySnapshot($"{name}.renewalInterval", view.RenewalInterval?.ToString()) { DisplayName = $"{name} renewal interval", IsHighlighted = true });
                }
                return (rows.ToImmutable(), links.ToImmutable());
            }
        }
    }

    public void Dispose() => publishGate.Dispose();
}
