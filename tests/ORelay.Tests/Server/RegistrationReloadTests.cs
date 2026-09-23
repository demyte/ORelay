using ORelay.Server;

namespace ORelay.Tests.Server;

public sealed class RegistrationReloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyConfiguration_PreservesLiveExpiryAndAppliesNewLeaseAndCapacity(bool persistent)
    {
        using var fixture = new StoreFixture(persistent);
        var clock = new ControlledTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        using var store = fixture.Open(clock, TimeSpan.FromSeconds(30), maxRegistrations: 2);
        var first = store.Register("http://127.0.0.1:4101/callback", false).Response!;
        var second = store.Register("http://127.0.0.1:4102/callback", false).Response!;
        clock.Advance(TimeSpan.FromSeconds(10));

        store.ApplyConfiguration(TimeSpan.FromSeconds(20), maxRegistrations: 1, allowNonLoopbackDestinations: false);

        Assert.Equal(TimeSpan.FromSeconds(20), store.LeaseDuration);
        Assert.True(store.TryGet(first.Id, out var unchanged));
        Assert.Equal(first.ExpiresAt, unchanged.ExpiresAt);
        Assert.True(store.TryGet(second.Id, out var unchangedSecond));
        Assert.Equal(second.ExpiresAt, unchangedSecond.ExpiresAt);
        Assert.Equal(2, store.Count);
        Assert.Equal("capacity_exceeded", store.Register("http://127.0.0.1:4103/callback", false).ErrorCode);

        var renewed = store.Renew(second.Id, "http://127.0.0.1:12987/callback");
        Assert.True(renewed.IsSuccess);
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(20), renewed.Response!.ExpiresAt);
        Assert.Equal(20, renewed.Response.LeaseSeconds);

        Assert.True(store.Delete(first.Id));
        Assert.True(store.Delete(second.Id));
        var added = store.Register("http://127.0.0.1:4103/callback", false).Response!;
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(20), added.ExpiresAt);
        Assert.Equal(20, added.LeaseSeconds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyConfiguration_CanRestrictAndRestoreRemoteDestinationPolicy(bool persistent)
    {
        using var fixture = new StoreFixture(persistent);
        var clock = new ControlledTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        using var store = fixture.Open(clock, TimeSpan.FromSeconds(60), maxRegistrations: 4, allowNonLoopback: true);
        var remote = store.Register("https://worktree.example.test/callback", true).Response!;

        store.ApplyConfiguration(TimeSpan.FromSeconds(60), maxRegistrations: 4, allowNonLoopbackDestinations: false);

        Assert.False(store.TryGet(remote.Id, out _));
        Assert.Equal("registration_not_found", store.Renew(remote.Id, "http://127.0.0.1:12987/callback").ErrorCode);
        Assert.Equal("callback_must_be_loopback", store.Register("https://another.example.test/callback", true).ErrorCode);
        Assert.Equal(1, store.Count);

        store.ApplyConfiguration(TimeSpan.FromSeconds(60), maxRegistrations: 4, allowNonLoopbackDestinations: true);

        Assert.True(store.TryGet(remote.Id, out var restored));
        Assert.Equal(remote.ExpiresAt, restored.ExpiresAt);
        Assert.True(store.Renew(remote.Id, "http://127.0.0.1:12987/callback").IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyConfiguration_InvalidValuesLeaveEveryPolicyUnchanged(bool persistent)
    {
        using var fixture = new StoreFixture(persistent);
        var clock = new ControlledTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        using var store = fixture.Open(clock, TimeSpan.FromSeconds(60), maxRegistrations: 2, allowNonLoopback: true);
        var remote = store.Register("https://worktree.example.test/callback", true).Response!;
        var local = store.Register("http://127.0.0.1:4102/callback", false).Response!;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ApplyConfiguration(TimeSpan.Zero, maxRegistrations: 1, allowNonLoopbackDestinations: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ApplyConfiguration(TimeSpan.FromSeconds(10), maxRegistrations: 0, allowNonLoopbackDestinations: false));

        Assert.Equal(TimeSpan.FromSeconds(60), store.LeaseDuration);
        Assert.True(store.TryGet(remote.Id, out _));
        Assert.True(store.TryGet(local.Id, out _));
        Assert.Equal("capacity_exceeded", store.Register("http://127.0.0.1:4103/callback", false).ErrorCode);
    }

    private sealed class StoreFixture(bool persistent) : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orelay-registration-reload-test-" + Guid.NewGuid().ToString("N"));

        public RegistrationStore Open(TimeProvider clock, TimeSpan leaseDuration, int maxRegistrations, bool allowNonLoopback = false)
        {
            var databasePath = persistent ? System.IO.Path.Combine(directory, "registrations.db") : null;
            return new RegistrationStore(clock, leaseDuration, maxRegistrations,
                databasePath: databasePath, allowNonLoopbackDestinations: allowNonLoopback);
        }

        public void Dispose()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class ControlledTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private long ticks = initial.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }
}
