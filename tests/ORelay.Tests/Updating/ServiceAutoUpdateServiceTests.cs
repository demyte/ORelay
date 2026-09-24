using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ORelay.Configuration;
using ORelay.Diagnostics;
using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class ServiceAutoUpdateServiceTests
{
    [Fact]
    public async Task LogsDisabledAndEachActualScheduleChange()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(enabled: false, interval: 100);
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerFinished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new LogFixture();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(new RotatingFileLoggerProvider(fixture.ConfigurationPath)));
        using var service = Create(state, clock, delay, (_, _, _, _) =>
        {
            workerStarted.SetResult();
            return workerFinished.Task;
        }, loggerFactory.CreateLogger<ServiceAutoUpdateService>());

        await service.StartAsync(CancellationToken.None);
        await WaitForLogAsync(fixture.Current,
            "Automatic updates disabled for custom-service; no check scheduled.");
        state.Publish(state.Current with { AutoUpdate = true });
        var initial = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(100), initial.Interval);
        var initialDue = clock.GetUtcNow().AddSeconds(100);
        var log = File.ReadAllText(fixture.Current);
        Assert.Contains("Automatic updates disabled for custom-service; no check scheduled.", log);
        Assert.Contains($"interval=100s, next check at {initialDue:O}", log);

        clock.Advance(TimeSpan.FromSeconds(10));
        state.Publish(state.Current with { AutoUpdateIntervalSeconds = 200 });
        await initial.Canceled.WaitAsync(TestTimeout);
        var extended = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(190), extended.Interval);
        Assert.Contains($"interval=200s, next check at {clock.GetUtcNow().AddSeconds(190):O}",
            File.ReadAllText(fixture.Current));

        clock.Advance(TimeSpan.FromSeconds(10));
        state.Publish(state.Current with { Port = state.Current.Port + 1 });
        await extended.Canceled.WaitAsync(TestTimeout);
        var unchanged = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(180), unchanged.Interval);
        log = File.ReadAllText(fixture.Current);
        Assert.Equal(2, Count(log, "Automatic updates enabled for custom-service:"));

        clock.Advance(TimeSpan.FromSeconds(180));
        unchanged.Complete();
        await workerStarted.Task.WaitAsync(TestTimeout);
        clock.Advance(TimeSpan.FromSeconds(25));
        workerFinished.SetResult(0);
        var afterAttempt = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(200), afterAttempt.Interval);
        var completionDue = clock.GetUtcNow().AddSeconds(200);
        log = File.ReadAllText(fixture.Current);
        Assert.Contains($"interval=200s, next check at {completionDue:O}", log);
        Assert.Equal(3, Count(log, "Automatic updates enabled for custom-service:"));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Timer_WaitsForInterval_AndDoesNotOverlapWorkers()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(interval: 123);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = Create(state, clock, delay, (exe, config, name, _) =>
        {
            Assert.Equal("executable", exe);
            Assert.Equal("selected-config", config);
            Assert.Equal("custom-service", name);
            Interlocked.Increment(ref calls);
            started.SetResult();
            return finished.Task;
        });

        await service.StartAsync(CancellationToken.None);
        var first = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(123), first.Interval);
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(123));
        first.Complete();
        await started.Task.WaitAsync(TestTimeout);
        Assert.Equal(1, calls);
        Assert.False(delay.HasPendingRequest);

        clock.Advance(TimeSpan.FromSeconds(500));
        state.Publish(state.Current with { Port = state.Current.Port + 1 });
        Assert.False(delay.HasPendingRequest);
        Assert.Equal(1, calls);
        finished.SetResult(0);
        var next = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(123), next.Interval);
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisabledService_CanEnableDisableAndEnableAgainWithoutRestart()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(enabled: false, interval: 123);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = Create(state, clock, delay, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            launched.SetResult();
            return Task.FromResult(0);
        });

        await service.StartAsync(CancellationToken.None);
        Assert.False(delay.HasPendingRequest);
        clock.Advance(TimeSpan.FromSeconds(100));
        state.Publish(state.Current with { AutoUpdate = true });
        var pending = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(23), pending.Interval);
        state.Publish(state.Current with { AutoUpdate = false });
        await pending.Canceled.WaitAsync(TestTimeout);
        Assert.Equal(0, calls);

        clock.Advance(TimeSpan.FromSeconds(30));
        state.Publish(state.Current with { AutoUpdate = true });
        await launched.Task.WaitAsync(TestTimeout);
        Assert.Equal(1, calls);
        var next = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(123), next.Interval);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShorteningInterval_LaunchesOverdueAttemptImmediately()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(interval: 200);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(state, clock, delay, (_, _, _, _) =>
        {
            launched.SetResult();
            return Task.FromResult(0);
        });

        await service.StartAsync(CancellationToken.None);
        var old = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(200), old.Interval);
        clock.Advance(TimeSpan.FromSeconds(150));
        state.Publish(state.Current with { AutoUpdateIntervalSeconds = 100 });
        await old.Canceled.WaitAsync(TestTimeout);
        await launched.Task.WaitAsync(TestTimeout);
        var next = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(100), next.Interval);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExtendingInterval_PostponesAttemptFromOriginalAnchor()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(interval: 100);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(state, clock, delay, (_, _, _, _) =>
        {
            launched.SetResult();
            return Task.FromResult(0);
        });

        await service.StartAsync(CancellationToken.None);
        var old = await delay.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(90));
        state.Publish(state.Current with { AutoUpdateIntervalSeconds = 200 });
        await old.Canceled.WaitAsync(TestTimeout);
        var extended = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(110), extended.Interval);
        clock.Advance(TimeSpan.FromSeconds(110));
        extended.Complete();
        await launched.Task.WaitAsync(TestTimeout);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task UnrelatedSettingChange_DoesNotResetTimer()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(interval: 100);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(state, clock, delay, (_, _, _, _) =>
        {
            launched.SetResult();
            return Task.FromResult(0);
        });

        await service.StartAsync(CancellationToken.None);
        var old = await delay.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        state.Publish(state.Current with { Port = state.Current.Port + 1 });
        await old.Canceled.WaitAsync(TestTimeout);
        var remaining = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(70), remaining.Interval);
        clock.Advance(TimeSpan.FromSeconds(70));
        remaining.Complete();
        await launched.Task.WaitAsync(TestTimeout);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OptingOut_DoesNotCancelAnAcceptedWorker()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(interval: 100);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = Create(state, clock, delay, (_, _, _, token) =>
        {
            started.SetResult(token);
            return finished.Task;
        });

        await service.StartAsync(CancellationToken.None);
        var first = await delay.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(100));
        first.Complete();
        var workerToken = await started.Task.WaitAsync(TestTimeout);
        state.Publish(state.Current with { AutoUpdate = false });
        Assert.False(workerToken.IsCancellationRequested);
        Assert.False(delay.HasPendingRequest);
        finished.SetResult(0);
        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWorker_RetriesOnlyAfterAnotherInterval(bool throws)
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(interval: 123);
        var calls = 0;
        using var service = Create(state, clock, delay, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return throws ? Task.FromException<int>(new IOException("synthetic failure")) : Task.FromResult(3);
        });

        await service.StartAsync(CancellationToken.None);
        var first = await delay.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(123));
        first.Complete();
        var second = await delay.NextAsync();
        Assert.Equal(1, calls);
        Assert.Equal(TimeSpan.FromSeconds(123), second.Interval);
        clock.Advance(TimeSpan.FromSeconds(123));
        second.Complete();
        await delay.NextAsync();
        Assert.Equal(2, calls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_CancelsWaitingWithoutStartingWorker()
    {
        var clock = new ManualClock();
        var delay = new ControlledDelay();
        var state = CreateState(interval: 123);
        var calls = 0;
        using var service = Create(state, clock, delay, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(0);
        });

        await service.StartAsync(CancellationToken.None);
        var pending = await delay.NextAsync();
        await service.StopAsync(CancellationToken.None);
        await pending.Canceled.WaitAsync(TestTimeout);
        Assert.Equal(0, calls);
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static RelayConfigurationState CreateState(bool enabled = true, int interval = 123) =>
        new(RelayConfigurationDefaults.Settings with
        {
            AutoUpdate = enabled,
            AutoUpdateIntervalSeconds = interval,
        });

    private static ServiceAutoUpdateService Create(RelayConfigurationState state, ManualClock clock,
        ControlledDelay delay, Func<string, string, string, CancellationToken, Task<int>> launch,
        ILogger<ServiceAutoUpdateService>? logger = null) =>
        new("executable", "selected-config", "custom-service", state,
            logger ?? NullLogger<ServiceAutoUpdateService>.Instance, launch, clock, delay.WaitAsync);

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static async Task WaitForLogAsync(string path, string value)
    {
        var timeout = DateTime.UtcNow + TestTimeout;
        while (DateTime.UtcNow < timeout)
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                if ((await reader.ReadToEndAsync()).Contains(value, StringComparison.Ordinal))
                    return;
            }
            await Task.Delay(10);
        }

        Assert.Fail($"Timed out waiting for log entry: {value}");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class ControlledDelay
    {
        private readonly Channel<DelayRequest> _requests = Channel.CreateUnbounded<DelayRequest>();

        public bool HasPendingRequest => _requests.Reader.TryPeek(out _);

        public Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            var request = new DelayRequest(interval, cancellationToken);
            Assert.True(_requests.Writer.TryWrite(request));
            return request.WaitAsync();
        }

        public async Task<DelayRequest> NextAsync() =>
            await _requests.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
    }

    private sealed class DelayRequest(TimeSpan interval, CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Interval => interval;
        public Task Canceled => _canceled.Task;

        public Task WaitAsync()
        {
            cancellationToken.Register(() => _canceled.TrySetResult());
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete() => _completion.SetResult();
    }

    private sealed class LogFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "orelay-auto-update-log-tests",
            Guid.NewGuid().ToString("N"));

        internal string ConfigurationPath => Path.Combine(_root, "orelay.json");
        internal string Current => Path.Combine(_root, "logs", "orelay.log");

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
