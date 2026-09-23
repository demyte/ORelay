using System.Text.Json;
using ORelay.Configuration;
using ORelay.Services;
using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class ServiceAutoUpdateWorkerTests
{
    [Fact]
    public async Task DisabledConfigurationMakesNoNetworkRequest()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(false);

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(0, fixture.CheckCalls);
        Assert.Equal(0, fixture.UpdateCalls);
        Assert.Equal("disabled", fixture.Status());
    }

    [Fact]
    public async Task NoReleaseAvailableDoesNotInstall()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);
        fixture.CheckResult = new(true, false, "1.0.0", "1.0.0", fixture.Executable,
            "Up to date.");

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(1, fixture.CheckCalls);
        Assert.Equal(0, fixture.UpdateCalls);
        Assert.Equal("up-to-date", fixture.Status());
    }

    [Fact]
    public async Task AvailableReleaseUsesExactServiceAndConfiguration()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(1, fixture.CheckCalls);
        Assert.Equal(1, fixture.UpdateCalls);
        Assert.Equal(new UpdateRequest(true, fixture.ServiceName, fixture.Config), fixture.LastUpdateRequest);
        Assert.Equal(fixture.Config, fixture.Services.LastRequest!.ConfigurationPath);
        Assert.Equal(fixture.Executable, fixture.Services.LastRequest.ExecutablePath);
        Assert.Equal("updated", fixture.Status());
        Assert.True(fixture.ResultTimestamp() <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task FailedReleaseCheckDoesNotInstall()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);
        fixture.CheckResult = UpdateResult.Failure(UpdateErrorCode.ReleaseUnavailable,
            "1.0.0", null, fixture.Executable, "The release is unavailable.");

        var exit = await fixture.RunAsync();

        Assert.Equal(3, exit);
        Assert.Equal(0, fixture.UpdateCalls);
        Assert.Equal("failed", fixture.Status());
        Assert.Contains("The release is unavailable.", File.ReadAllText(fixture.ResultFile));
        Assert.Contains("ReleaseUnavailable", File.ReadAllText(fixture.ResultFile));
    }

    [Fact]
    public async Task UnexpectedCheckExceptionIsSanitizedAndDoesNotInstall()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);
        fixture.OnCheck = () => throw new System.ComponentModel.Win32Exception("synthetic secret");

        var exit = await fixture.RunAsync();

        Assert.Equal(3, exit);
        Assert.Equal(0, fixture.UpdateCalls);
        Assert.DoesNotContain("synthetic secret", File.ReadAllText(fixture.ResultFile));
    }

    [Fact]
    public async Task FailedInstallRecordsRecoveryMessageAndCode()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);
        fixture.UpdateResult = UpdateResult.Failure(UpdateErrorCode.InstallFailure,
            "1.0.0", "2.0.0", fixture.Executable, "Rollback needs manual recovery at the saved backup path.");

        var exit = await fixture.RunAsync();

        Assert.Equal(3, exit);
        Assert.Equal("failed", fixture.Status());
        Assert.Contains("Rollback needs manual recovery", File.ReadAllText(fixture.ResultFile));
        Assert.Contains("InstallFailure", File.ReadAllText(fixture.ResultFile));
    }

    [Fact]
    public async Task DisablingDuringCheckCancelsInstall()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);
        fixture.OnCheck = () => fixture.SetEnabled(false);

        var exit = await fixture.RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal(0, fixture.UpdateCalls);
        Assert.Equal("disabled", fixture.Status());
    }

    [Fact]
    public async Task ConcurrentWorkerLeavesActiveCheckInCharge()
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnCheckAsync = async () =>
        {
            entered.SetResult();
            await release.Task;
        };

        var first = fixture.RunAsync();
        await entered.Task;
        var second = await fixture.RunAsync();
        release.SetResult();
        var firstExit = await first;

        Assert.Equal(0, second);
        Assert.Equal(0, firstExit);
        Assert.Equal(1, fixture.CheckCalls);
        Assert.Equal(1, fixture.UpdateCalls);
        Assert.Equal("updated", fixture.Status());
    }

    [Theory]
    [InlineData(false, ServiceState.Running)]
    [InlineData(true, ServiceState.Stopped)]
    public async Task ForeignOrStoppedServiceIsRejectedBeforeNetwork(bool owned, ServiceState state)
    {
        using var fixture = new Fixture();
        fixture.SetEnabled(true);
        fixture.Services.Owned = owned;
        fixture.Services.State = state;

        var exit = await fixture.RunAsync();

        Assert.Equal(3, exit);
        Assert.Equal(0, fixture.CheckCalls);
        Assert.Equal(0, fixture.UpdateCalls);
        Assert.Equal("failed", fixture.Status());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orelay-auto-update-" + Guid.NewGuid().ToString("N"));
        public readonly string ServiceName = "orelay-test";
        public string Executable => Path.Combine(_directory, "orelay.exe");
        public string Config => Path.Combine(_directory, "settings.json");
        public string ResultFile => ServiceAutoUpdateWorker.ResultPath(Config);
        public FakeService Services { get; } = new();
        public int CheckCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public UpdateRequest? LastUpdateRequest { get; private set; }
        public Action? OnCheck { get; set; }
        public Func<Task>? OnCheckAsync { get; set; }
        public UpdateResult? CheckResult { get; set; }
        public UpdateResult? UpdateResult { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Executable, "test executable");
        }

        public void SetEnabled(bool enabled)
        {
            var store = new RelayConfigurationStore(Config);
            store.Init();
            store.Set("autoUpdate", enabled ? "true" : "false");
        }

        public Task<int> RunAsync()
        {
            var runtime = new FakeRuntime(Executable);
            var worker = new ServiceAutoUpdateWorker(runtime, Services, Check, Update);
            return worker.RunAsync(Config, ServiceName);
        }

        private async Task<UpdateResult> Check(UpdateRequest request, CancellationToken cancellationToken)
        {
            CheckCalls++;
            OnCheck?.Invoke();
            if (OnCheckAsync is not null) await OnCheckAsync();
            return CheckResult ?? new UpdateResult(true, false, "1.0.0", "2.0.0",
                Executable, "Available.", UpdateAvailable: true);
        }

        private Task<UpdateResult> Update(UpdateRequest request, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            LastUpdateRequest = request;
            return Task.FromResult(UpdateResult ?? new UpdateResult(true, true, "1.0.0", "2.0.0", Executable,
                "Installed."));
        }

        public string Status()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ResultFile));
            return json.RootElement.GetProperty("status").GetString()!;
        }

        public DateTimeOffset ResultTimestamp()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(ResultFile));
            return json.RootElement.GetProperty("checkedAtUtc").GetDateTimeOffset();
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeRuntime(string executable) : IUpdateRuntime
    {
        public string? ProcessPath => executable;
        public bool IsNative => true;
        public string? RuntimeIdentifier => "win-x64";
        public string CurrentVersion => "1.0.0";
        public string? ReadInstalledVersion(string path) => "1.0.0";
        public Task<string?> ReadExecutableVersionAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("1.0.0");
    }

    private sealed class FakeService : IPlatformServiceManager
    {
        public ServicePlatform Platform => ServicePlatform.Windows;
        public bool Owned { get; set; } = true;
        public ServiceState State { get; set; } = ServiceState.Running;
        public ServiceRequest? LastRequest { get; private set; }

        public ServiceOperationResult Execute(ServiceOperation operation, ServiceRequest request)
        {
            LastRequest = request;
            return ServiceOperationResult.Success(Platform, operation, request.ServiceName,
                State, changed: false, owned: Owned, "Test service status.");
        }
    }
}
