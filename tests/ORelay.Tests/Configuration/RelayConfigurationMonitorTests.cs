using Microsoft.Extensions.Logging.Abstractions;
using ORelay.Configuration;
using ORelay.Discovery;

namespace ORelay.Tests.Configuration;

public sealed class RelayConfigurationMonitorTests
{
    [Fact]
    public async Task DirectWriteAndAtomicReplacementApplyLatestSettingsOnce()
    {
        await using var fixture = new MonitorFixture();
        fixture.Write(14001);
        await fixture.StartAsync();
        await fixture.WaitForPortAsync(14001);

        fixture.Write(14002);
        await fixture.WaitForPortAsync(14002);
        fixture.Replace(14003);
        await fixture.WaitForPortAsync(14003);
        await Task.Delay(200);

        Assert.Equal("14001,14002,14003", string.Join(',', fixture.AppliedPorts));
    }

    [Fact]
    public async Task InvalidFileAndMissingFileKeepLastGoodThenRecover()
    {
        await using var fixture = new MonitorFixture();
        fixture.Write(14001);
        await fixture.StartAsync();
        await fixture.WaitForPortAsync(14001);

        File.WriteAllText(fixture.Path, "{ invalid json");
        await Task.Delay(180);
        Assert.Equal("14001", string.Join(',', fixture.AppliedPorts));

        File.Delete(fixture.Path);
        await Task.Delay(180);
        Assert.False(File.Exists(fixture.Path));
        Assert.Equal("14001", string.Join(',', fixture.AppliedPorts));

        fixture.Replace(14002);
        await fixture.WaitForPortAsync(14002);
        Assert.Equal("14001,14002", string.Join(',', fixture.AppliedPorts));
    }

    [Fact]
    public async Task InvocationOverrideSurvivesReloadAndMonitorDoesNotWriteFile()
    {
        await using var fixture = new MonitorFixture(new RelaySettingsPatch(LeaseSeconds: 900));
        fixture.Write(14001, leaseSeconds: 100);
        var before = File.ReadAllText(fixture.Path);
        await fixture.StartAsync();
        await fixture.WaitForPortAsync(14001);
        Assert.Equal(900, fixture.AppliedSettings.Last().LeaseSeconds);
        Assert.Equal(before, File.ReadAllText(fixture.Path));

        fixture.Replace(14002, leaseSeconds: 200);
        var after = File.ReadAllText(fixture.Path);
        await fixture.WaitForPortAsync(14002);
        Assert.Equal(900, fixture.AppliedSettings.Last().LeaseSeconds);
        Assert.Equal(after, File.ReadAllText(fixture.Path));
        Assert.False(File.Exists(fixture.Path + ".lock"));
    }

    [Fact]
    public async Task RapidWritesAndDuplicateEventsCoalesceToFinalContent()
    {
        await using var fixture = new MonitorFixture(debounce: TimeSpan.FromMilliseconds(90));
        fixture.Write(14001);
        await fixture.StartAsync();
        await fixture.WaitForPortAsync(14001);

        fixture.Write(14002);
        fixture.Write(14003);
        fixture.Write(14004);
        await fixture.WaitForPortAsync(14004);
        await Task.Delay(250);

        Assert.Equal("14001,14004", string.Join(',', fixture.AppliedPorts));
    }

    [Fact]
    public async Task FailedApplyIsNotRetriedByPollingButLaterEditRecovers()
    {
        await using var fixture = new MonitorFixture();
        fixture.Write(14001);
        fixture.RejectPort = 14002;
        await fixture.StartAsync();
        await fixture.WaitForPortAsync(14001);

        fixture.Write(14002);
        await fixture.WaitForAttemptAsync(14002);
        await Task.Delay(230);
        Assert.Equal(1, fixture.Attempts.Count(port => port == 14002));

        fixture.Write(14003);
        await fixture.WaitForPortAsync(14003);
        Assert.Equal("14001,14003", string.Join(',', fixture.AppliedPorts));
    }

    [Fact]
    public async Task FailedDiscoveryKeepsLastGoodSettings()
    {
        await using var fixture = new MonitorFixture();
        fixture.Write(14001);
        fixture.RejectDiscoveryPort = 14002;
        await fixture.StartAsync();
        await fixture.WaitForPortAsync(14001);

        fixture.Write(14002);
        await Task.Delay(200);
        Assert.Equal("14001", string.Join(',', fixture.AppliedPorts));

        fixture.Write(14003);
        await fixture.WaitForPortAsync(14003);
        Assert.Equal("14001,14003", string.Join(',', fixture.AppliedPorts));
    }

    private sealed class MonitorFixture : IAsyncDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orelay-monitor-tests", Guid.NewGuid().ToString("N"));
        private readonly RelaySettingsPatch? _overrides;
        private readonly TimeSpan _debounce;
        private readonly object _gate = new();
        private RelayConfigurationMonitor? _monitor;
        private readonly List<RelaySettings> _applied = [];
        private readonly List<int> _attempts = [];

        public MonitorFixture(RelaySettingsPatch? overrides = null, TimeSpan? debounce = null)
        {
            _overrides = overrides;
            _debounce = debounce ?? TimeSpan.FromMilliseconds(35);
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "orelay.json");
        }

        public string Path { get; }
        public int? RejectPort { get; set; }
        public int? RejectDiscoveryPort { get; set; }
        public int[] AppliedPorts { get { lock (_gate) return _applied.Select(value => value.Port).ToArray(); } }
        public RelaySettings[] AppliedSettings { get { lock (_gate) return _applied.ToArray(); } }
        public int[] Attempts { get { lock (_gate) return _attempts.ToArray(); } }

        public void Write(int port, int leaseSeconds = 300) =>
            File.WriteAllText(Path, $"{{\"schemaVersion\":1,\"port\":{port},\"leaseSeconds\":{leaseSeconds}}}");

        public void Replace(int port, int leaseSeconds = 300)
        {
            var temporaryPath = System.IO.Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(temporaryPath, $"{{\"schemaVersion\":1,\"port\":{port},\"leaseSeconds\":{leaseSeconds}}}");
            File.Move(temporaryPath, Path, overwrite: true);
        }

        public async Task StartAsync()
        {
            _monitor = new RelayConfigurationMonitor(Path, _overrides, ApplyAsync,
                NullLogger<RelayConfigurationMonitor>.Instance,
                (settings, _) => Task.FromResult(settings.Port == RejectDiscoveryPort ?
                    RelaySettingsDiscoveryResult.Failure("test_failure", "synthetic", "synthetic") :
                    RelaySettingsDiscoveryResult.Success(settings, "test")),
                _debounce, TimeSpan.FromMilliseconds(55));
            await _monitor.StartAsync(CancellationToken.None);
        }

        private Task<bool> ApplyAsync(RelaySettings settings, CancellationToken token)
        {
            lock (_gate)
            {
                _attempts.Add(settings.Port);
                if (settings.Port == RejectPort) return Task.FromResult(false);
                _applied.Add(settings);
            }
            return Task.FromResult(true);
        }

        public Task WaitForPortAsync(int port) => WaitUntilAsync(() => AppliedPorts.Contains(port));
        public Task WaitForAttemptAsync(int port) => WaitUntilAsync(() => Attempts.Contains(port));

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition()) await Task.Delay(20, timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (_monitor is not null)
            {
                await _monitor.StopAsync(CancellationToken.None);
                _monitor.Dispose();
            }
            Directory.Delete(_directory, recursive: true);
        }
    }
}
