using ORelay.Server;

namespace ORelay.Tests.Server;

public sealed class RegistrationStoreTests
{
    [Fact]
    public void Register_UsesOpaqueRandomId_AndPreservesStateSuffixDelimiters()
    {
        var clock = new ControlledTimeProvider(DateTimeOffset.Parse("2026-09-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new RegistrationStore(clock, TimeSpan.FromMinutes(5), maxRegistrations: 4);

        var created = store.Register("http://127.0.0.1:4100/oauth/callback", allowNonLoopback: false);

        Assert.True(created.IsSuccess);
        Assert.NotNull(created.Response);
        Assert.True(RegistrationStore.IsValidRegistrationId(created.Response!.Id));
        Assert.DoesNotContain('.', created.Response.Id);
        Assert.True(RegistrationStore.TryExtractRegistrationId($"{created.Response.Id}.worktree.part", out var id));
        Assert.Equal(created.Response.Id, id);
    }

    [Fact]
    public void Expiry_IsExclusiveAtTheBoundary_AndCannotBeRenewedAfterRemoval()
    {
        var clock = new ControlledTimeProvider(DateTimeOffset.Parse("2026-09-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var store = new RegistrationStore(clock, TimeSpan.FromSeconds(30));
        var created = store.Register("https://localhost:4100/callback", allowNonLoopback: false);
        var id = created.Response!.Id;

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.False(store.TryGet(id, out _));
        Assert.Equal(0, store.Count);
        Assert.False(store.Renew(id, "http://127.0.0.1:12987/callback").IsSuccess);
    }

    [Fact]
    public void Delete_IsIdempotent_AndDoesNotAffectOtherRegistrations()
    {
        var store = new RegistrationStore(new ControlledTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5));
        var first = store.Register("http://127.0.0.1:4101/callback", allowNonLoopback: false).Response!;
        var second = store.Register("http://127.0.0.1:4102/callback", allowNonLoopback: false).Response!;

        Assert.True(store.Delete(first.Id));
        Assert.False(store.Delete(first.Id));
        Assert.False(store.TryGet(first.Id, out _));
        Assert.True(store.TryGet(second.Id, out var remaining));
        Assert.Equal(second.CallbackUrl, remaining.CallbackUrl);
    }

    [Fact]
    public async Task ConcurrentRegistration_RespectsCapacityAndUniqueIds()
    {
        var store = new RegistrationStore(new ControlledTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5), maxRegistrations: 16);
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => Task.Run(() =>
            store.Register($"http://127.0.0.1:{4200 + index}/callback", allowNonLoopback: false))));

        var successful = results.Where(static result => result.IsSuccess).Select(static result => result.Response!).ToArray();
        Assert.Equal(16, successful.Length);
        Assert.Equal(16, successful.Select(static result => result.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(16, store.Count);
    }

    private sealed class ControlledTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private long utcTicks = initial.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        public void Advance(TimeSpan amount) => Interlocked.Add(ref utcTicks, amount.Ticks);
    }
}
