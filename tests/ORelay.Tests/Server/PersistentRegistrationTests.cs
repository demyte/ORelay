using ORelay.Server;

namespace ORelay.Tests.Server;

public sealed class PersistentRegistrationTests
{
    [Fact]
    public void AcknowledgedRegistration_RoutesWithSameIdWhileAnotherStoreOpensTheFile()
    {
        using var fixture = new DatabaseFixture();
        var clock = new ControlledTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        using var first = fixture.Open(clock);
        var created = first.Register("http://127.0.0.1:4111/oauth/callback", false).Response!;

        // Open a second connection before the first closes. This proves the
        // acknowledged write is on disk, rather than deferred to Dispose.
        using var second = fixture.Open(clock);
        Assert.True(second.TryGet(created.Id, out var restored));
        Assert.Equal(created.CallbackUrl, restored.CallbackUrl);
        Assert.Equal(created.ExpiresAt, restored.ExpiresAt);
    }

    [Fact]
    public void RenewalAndDeletion_SurviveReopen_WithoutRestoringExpiredIds()
    {
        using var fixture = new DatabaseFixture();
        var clock = new ControlledTimeProvider(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        string renewedId;
        string deletedId;
        string expiredId;
        using (var first = fixture.Open(clock))
        {
            renewedId = first.Register("http://localhost:4112/renew", false).Response!.Id;
            deletedId = first.Register("http://localhost:4113/delete", false).Response!.Id;
            expiredId = first.Register("http://localhost:4114/expire", false).Response!.Id;
            Assert.True(first.Delete(deletedId));
            clock.Advance(TimeSpan.FromSeconds(20));
            Assert.True(first.Renew(renewedId, "http://127.0.0.1:12987/callback").IsSuccess);
        }

        clock.Advance(TimeSpan.FromSeconds(11));
        using var reopened = fixture.Open(clock);
        Assert.True(reopened.TryGet(renewedId, out _));
        Assert.False(reopened.TryGet(deletedId, out _));
        Assert.False(reopened.TryGet(expiredId, out _));
        Assert.False(reopened.Renew(expiredId, "http://127.0.0.1:12987/callback").IsSuccess);
        Assert.Equal(1, reopened.Count);
        using var anotherReader = fixture.Open(clock);
        Assert.False(anotherReader.TryGet(expiredId, out _));
    }

    [Fact]
    public void NarrowedDestinationPolicy_DoesNotRestoreOldRemoteCallback()
    {
        using var fixture = new DatabaseFixture();
        var clock = new ControlledTimeProvider(DateTimeOffset.UtcNow);
        string remoteId;
        using (var shared = fixture.Open(clock, allowNonLoopback: true))
        {
            remoteId = shared.Register("https://worktree.example.test/callback", true).Response!.Id;
        }

        using var local = fixture.Open(clock);
        Assert.False(local.TryGet(remoteId, out _));
        Assert.False(local.Renew(remoteId, "http://127.0.0.1:12987/callback").IsSuccess);
        Assert.Equal(0, local.Count);
    }

    [Fact]
    public void CorruptDatabase_FailsClosedWithoutLeakingFileContents()
    {
        using var fixture = new DatabaseFixture();
        File.WriteAllText(fixture.Path, "synthetic-secret-bad-database");
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Open(new ControlledTimeProvider(DateTimeOffset.UtcNow)));
        Assert.DoesNotContain("synthetic-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NewerSchemaVersion_FailsClosed()
    {
        using var fixture = new DatabaseFixture();
        var clock = new ControlledTimeProvider(DateTimeOffset.UtcNow);
        using (var store = fixture.Open(clock))
        {
            Assert.True(store.Register("http://127.0.0.1:4115/callback", false).IsSuccess);
        }

        // SQLite stores user_version as a big-endian int at header offset 60.
        using (var file = File.Open(fixture.Path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            file.Position = 60;
            file.Write([0, 0, 0, 2]);
        }

        Assert.Throws<InvalidOperationException>(() => fixture.Open(clock));
    }

    [Fact]
    public async Task ConcurrentStoreInstances_RespectSharedCapacity()
    {
        using var fixture = new DatabaseFixture();
        var clock = new ControlledTimeProvider(DateTimeOffset.UtcNow);
        using var first = fixture.Open(clock, capacity: 8);
        using var second = fixture.Open(clock, capacity: 8);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            (index % 2 == 0 ? first : second).Register($"http://127.0.0.1:{4500 + index}/callback", false))));

        Assert.Equal(8, outcomes.Count(static outcome => outcome.IsSuccess));
        Assert.Equal(8, first.Count);
        Assert.Equal(8, second.Count);
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orelay-registration-test-" + Guid.NewGuid().ToString("N"));

        public DatabaseFixture() => Directory.CreateDirectory(directory);

        public string Path => System.IO.Path.Combine(directory, "registrations.db");

        public RegistrationStore Open(TimeProvider clock, bool allowNonLoopback = false, int capacity = 1000) =>
            new(clock, TimeSpan.FromSeconds(30), maxRegistrations: capacity, databasePath: Path,
                allowNonLoopbackDestinations: allowNonLoopback);

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
