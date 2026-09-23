using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class ServiceAutoUpdateServiceTests
{
    [Fact]
    public async Task Timer_WaitsForInterval_AndDoesNotOverlapWorkers()
    {
        var delay = new ControlledDelay();
        var workerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerFinished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var service = Create(delay, (exe, config, name, token) =>
        {
            Assert.Equal("executable", exe);
            Assert.Equal("selected-config", config);
            Assert.Equal("custom-service", name);
            Interlocked.Increment(ref calls);
            workerStarted.SetResult();
            return workerFinished.Task.WaitAsync(token);
        });

        await service.StartAsync(CancellationToken.None);
        var firstTick = await delay.NextAsync();
        Assert.Equal(0, calls);
        firstTick.SetResult();
        await workerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.False(delay.HasPendingDelay);
        workerFinished.SetResult(0);
        await delay.NextAsync();
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWorker_RetriesOnlyAfterAnotherInterval(bool throws)
    {
        var delay = new ControlledDelay();
        var calls = 0;
        using var service = Create(delay, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return throws ? Task.FromException<int>(new IOException("synthetic failure")) : Task.FromResult(3);
        });
        await service.StartAsync(CancellationToken.None);
        (await delay.NextAsync()).SetResult();
        var next = await delay.NextAsync();
        Assert.Equal(1, calls);
        next.SetResult();
        await delay.NextAsync();
        Assert.Equal(2, calls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_CancelsWaitingWithoutStartingWorker()
    {
        var delay = new ControlledDelay();
        var calls = 0;
        using var service = Create(delay, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(0);
        });
        await service.StartAsync(CancellationToken.None);
        await delay.NextAsync();
        await service.StopAsync(CancellationToken.None);
        Assert.Equal(0, calls);
    }

    private static ServiceAutoUpdateService Create(ControlledDelay delay,
        Func<string, string, string, CancellationToken, Task<int>> launch) =>
        new("executable", "selected-config", "custom-service", TimeSpan.FromSeconds(123),
            NullLogger<ServiceAutoUpdateService>.Instance, launch, delay.WaitAsync);

    private sealed class ControlledDelay
    {
        private readonly Channel<TaskCompletionSource> _ticks = Channel.CreateUnbounded<TaskCompletionSource>();
        public bool HasPendingDelay => _ticks.Reader.TryPeek(out _);

        public Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            Assert.Equal(TimeSpan.FromSeconds(123), interval);
            var tick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_ticks.Writer.TryWrite(tick));
            return tick.Task.WaitAsync(cancellationToken);
        }

        public async Task<TaskCompletionSource> NextAsync() =>
            await _ticks.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
