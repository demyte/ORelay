using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ORelay.Aspire.Hosting;
using Xunit;

namespace ORelay.Aspire.Hosting.Tests;

public sealed class RegistrationLifecycleTests
{
    [Fact]
    public async Task ConcurrentStartupRegistersOnceAndStopThenRestartUsesNewDestinationAndId()
    {
        using var server = new RelayHandler();
        using var session = NewSession(server);
        var registrations = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default)));
        Assert.Single(registrations.Select(r => r.Id).Distinct());
        Assert.Equal(1, server.Posts);
        Assert.Equal("Active", session.View.Status);
        Assert.Equal("http://relay.test/callback", session.View.PublicCallback);
        Assert.Equal("http://localhost:12001/callback", session.View.Destination);
        Assert.Null(session.View.LastRenewal);
        Assert.Equal(TimeSpan.FromSeconds(1), session.View.RenewalInterval);
        await UntilAsync(() => session.View.LastRenewal is not null);
        Assert.True(session.View.LeaseExpiry > registrations[0].ExpiresAt);
        await session.StopRegistrationAsync();
        Assert.Equal("Stopped", session.View.Status);
        Assert.Empty(server.Live);
        var restarted = await session.EnsureRegisteredAsync(new Uri("http://localhost:12002/callback"), default);
        Assert.NotEqual(registrations[0].Id, restarted.Id);
        Assert.Equal("http://localhost:12002/callback", restarted.CallbackUrl);
        await session.StopAsync(default);
        Assert.Empty(server.Live);
    }

    [Fact]
    public async Task RegistryLossRequiresExplicitRestartAndDoesNotRegisterInBackground()
    {
        using var server = new RelayHandler();
        using var session = NewSession(server);
        await session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default);
        server.Live.Clear();
        await UntilAsync(() => session.Health.Description!.Contains("Restart", StringComparison.Ordinal));
        Assert.Equal(HealthStatus.Degraded, session.Health.Status);
        Assert.Equal("Expired or lost", session.View.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default));
        Assert.Equal(1, server.Posts);
        await session.StopRegistrationAsync();
        await session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default);
        Assert.Equal(2, server.Posts);
        await session.StopAsync(default);
    }

    [Fact]
    public async Task DisconnectedRenewalExpiresWithoutCreatingReplacement()
    {
        using var server = new RelayHandler { FailRenewals = true };
        using var session = NewSession(server);
        await session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default);
        await UntilAsync(() => session.View.Status == "Retrying");
        Assert.Equal(HealthStatus.Degraded, session.Health.Status);
        await UntilAsync(() => session.Health.Description!.Contains("Restart", StringComparison.Ordinal));
        Assert.Equal("Expired or lost", session.View.Status);
        Assert.Equal(1, server.Posts);
        await session.StopAsync(default);
    }

    [Fact]
    public async Task BlockedRenewalReachesLeaseDeadlineAndRequiresManualRestart()
    {
        using var server = new RelayHandler();
        using var session = NewSession(server);
        var registration = await session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default);
        server.BlockId = registration.Id;
        await server.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => session.Health.Description!.Contains("Restart", StringComparison.Ordinal));
        Assert.Equal(HealthStatus.Degraded, session.Health.Status);
        var requestsAtExpiry = server.Renewals;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default));
        await Task.Delay(1100);
        Assert.Equal(requestsAtExpiry, server.Renewals);
        Assert.Equal(1, server.Posts);
        await session.StopRegistrationAsync();
        var replacement = await session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default);
        Assert.NotEqual(registration.Id, replacement.Id);
        Assert.Equal(2, server.Posts);
        await session.StopAsync(default);
    }

    [Fact]
    public async Task StopCancelsBlockedRenewalAndDoesNotDeleteAnotherAppHostsRegistration()
    {
        using var server = new RelayHandler();
        using var first = NewSession(server);
        using var second = NewSession(server);
        var one = await first.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default);
        var two = await second.EnsureRegisteredAsync(new Uri("http://localhost:12002/callback"), default);
        server.BlockId = one.Id;
        await server.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(server.Live.ContainsKey(one.Id));
        Assert.True(server.Live.ContainsKey(two.Id));
        Assert.Equal(2, server.Posts);
        await second.StopAsync(default);
        Assert.Empty(server.Live);
    }

    [Fact]
    public async Task DelayedRegistrationResponseCannotStartApplicationWithExpiredLease()
    {
        using var server = new RelayHandler { DelayRegistration = true };
        using var session = NewSession(server);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default));
        Assert.Contains("expired", error.Message, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Degraded, session.Health.Status);
        await session.StopAsync(default);
        Assert.Empty(server.Live);
    }

    [Fact]
    public async Task InitialHttpFailureIsActionableAndNoRegistrationDataIsReturned()
    {
        using var server = new RelayHandler { FailRegistration = true };
        using var session = NewSession(server);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default));
        Assert.Contains("503", error.Message, StringComparison.Ordinal);
        Assert.Contains("restart", error.Message, StringComparison.Ordinal);
        Assert.Empty(server.Live);
        await session.StopAsync(default);
    }

    [Fact]
    public async Task RelayHealthReflectsEachRegistrationAndKeepsOtherApiActive()
    {
        using var server = new RelayHandler();
        using var first = NewSession(server);
        using var second = NewSession(server);
        using var dashboard = new RelayDashboard(new ExternalServiceResource("relay", new Uri("http://relay.test")));
        dashboard.Add("first-api", first);
        dashboard.Add("second-api", second);
        Assert.Equal(HealthStatus.Degraded, dashboard.Health.Status);
        await first.EnsureRegisteredAsync(new Uri("http://localhost:12001/callback"), default);
        await second.EnsureRegisteredAsync(new Uri("http://localhost:12002/callback"), default);
        Assert.Equal(HealthStatus.Healthy, dashboard.Health.Status);
        var details = dashboard.Details;
        Assert.Equal("http://localhost:12001/callback", details.Properties.Single(property => property.Name == "first-api.destination").Value);
        Assert.Equal("http://localhost:12002/callback", details.Properties.Single(property => property.Name == "second-api.destination").Value);
        Assert.All(details.Properties, property => Assert.True(property.IsHighlighted));
        Assert.Contains(details.Relationships, relation => relation.ResourceName == "first-api");
        Assert.Contains(details.Relationships, relation => relation.ResourceName == "second-api");
        await first.StopRegistrationAsync();
        Assert.Equal(HealthStatus.Degraded, dashboard.Health.Status);
        Assert.Contains("first-api: Stopped", dashboard.Health.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("second-api: Stopped", dashboard.Health.Description, StringComparison.Ordinal);
        server.Live.Clear();
        await UntilAsync(() => second.View.Status == "Expired or lost");
        Assert.Contains("Restart the affected resource", dashboard.Health.Description, StringComparison.Ordinal);
        await second.StopAsync(default);
    }

    private static RegistrationSession NewSession(RelayHandler server) => new(new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri("http://relay.test") });

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    private sealed class RelayHandler : HttpMessageHandler
    {
        public ConcurrentDictionary<string, Registration> Live { get; } = new();
        private int posts;
        private int renewals;
        public int Posts => Volatile.Read(ref posts);
        public int Renewals => Volatile.Read(ref renewals);
        public bool FailRenewals { get; init; }
        public bool FailRegistration { get; init; }
        public bool DelayRegistration { get; init; }
        public string? BlockId { get; set; }
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                if (FailRegistration) return new(HttpStatusCode.ServiceUnavailable);
                var body = await request.Content!.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken);
                var registration = new Registration(Interlocked.Increment(ref posts).ToString(System.Globalization.CultureInfo.InvariantCulture), body!["callbackUrl"], DateTimeOffset.UtcNow.AddSeconds(3), 3, "http://relay.test/callback");
                Live[registration.Id] = registration;
                if (DelayRegistration) await Task.Delay(TimeSpan.FromSeconds(3.1), cancellationToken);
                return new(HttpStatusCode.Created) { Content = JsonContent.Create(registration) };
            }
            var id = request.RequestUri!.Segments[2].TrimEnd('/');
            if (request.Method == HttpMethod.Delete)
            {
                Live.TryRemove(id, out _);
                return new(HttpStatusCode.NoContent);
            }
            Interlocked.Increment(ref renewals);
            if (id == BlockId)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (FailRenewals) throw new HttpRequestException("Disconnected.");
            return Live.TryGetValue(id, out var current)
                ? new(HttpStatusCode.OK) { Content = JsonContent.Create(current with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(3) }) }
                : new(HttpStatusCode.NotFound);
        }
    }
}
