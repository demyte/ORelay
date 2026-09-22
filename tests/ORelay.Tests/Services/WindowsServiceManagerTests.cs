using ORelay.Services;

namespace ORelay.Tests.Services;

public sealed class WindowsServiceManagerTests
{
    [Fact]
    public void InstallDoesNotOverwriteAConflictingService()
    {
        using var fixture = new ServiceFixture();
        var backend = new FakeWindowsServiceBackend
        {
            Definition = new WindowsServiceDefinition(
                @"""C:\Other\orelay.exe"" ""server"" ""--config-file"" ""C:\Other\orelay.json""",
                ServiceState.Stopped),
        };
        var manager = new WindowsServiceManager(backend);

        var result = manager.Execute(ServiceOperation.Install, fixture.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrorCode.Conflict, result.ErrorCode);
        Assert.Equal(0, backend.CreateCalls);
    }

    [Fact]
    public void ReinstallOfOwnedServiceIsIdempotent()
    {
        using var fixture = new ServiceFixture();
        var backend = new FakeWindowsServiceBackend
        {
            Definition = new WindowsServiceDefinition(ServiceCommandLine.BuildWindows(fixture.Request), ServiceState.Stopped),
        };
        var manager = new WindowsServiceManager(backend);

        var result = manager.Execute(ServiceOperation.Install, fixture.Request);

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.True(result.Owned);
        Assert.Equal(0, backend.CreateCalls);
    }

    [Fact]
    public void LifecycleCommandsOperateOnlyTheOwnedService()
    {
        using var fixture = new ServiceFixture();
        var backend = new FakeWindowsServiceBackend
        {
            Definition = new WindowsServiceDefinition(ServiceCommandLine.BuildWindows(fixture.Request), ServiceState.Stopped),
        };
        var manager = new WindowsServiceManager(backend, TimeSpan.FromSeconds(1));

        var start = manager.Execute(ServiceOperation.Start, fixture.Request);
        var status = manager.Execute(ServiceOperation.Status, fixture.Request);
        var stop = manager.Execute(ServiceOperation.Stop, fixture.Request);

        Assert.True(start.Succeeded);
        Assert.Equal(ServiceState.Running, status.State);
        Assert.True(stop.Succeeded);
        Assert.Equal(1, backend.StartCalls);
        Assert.Equal(1, backend.StopCalls);
    }

    [Fact]
    public void NeverStartedServiceIsReportedAsStopped()
    {
        using var fixture = new ServiceFixture();
        var backend = new FakeWindowsServiceBackend
        {
            Definition = new WindowsServiceDefinition(
                ServiceCommandLine.BuildWindows(fixture.Request),
                ServiceState.Stopped,
                1077),
        };
        var manager = new WindowsServiceManager(backend);

        var result = manager.Execute(ServiceOperation.Status, fixture.Request);

        Assert.True(result.Succeeded);
        Assert.Equal(ServiceState.Stopped, result.State);
        Assert.True(result.Owned);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void UninstallLeavesTheConfigurationFileInPlace()
    {
        using var fixture = new ServiceFixture();
        File.WriteAllText(fixture.Request.ConfigurationPath, "{\"schemaVersion\":1}");
        var backend = new FakeWindowsServiceBackend
        {
            Definition = new WindowsServiceDefinition(ServiceCommandLine.BuildWindows(fixture.Request), ServiceState.Stopped),
        };
        var manager = new WindowsServiceManager(backend);

        var result = manager.Execute(ServiceOperation.Uninstall, fixture.Request);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(fixture.Request.ExecutablePath));
        Assert.True(File.Exists(fixture.Request.ConfigurationPath));
        Assert.Equal(1, backend.DeleteCalls);
    }

    [Fact]
    public void InstallRejectsAConfigurationDirectory()
    {
        using var fixture = new ServiceFixture();
        Directory.CreateDirectory(fixture.Request.ConfigurationPath);
        var backend = new FakeWindowsServiceBackend();
        var manager = new WindowsServiceManager(backend);

        var result = manager.Execute(ServiceOperation.Install, fixture.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrorCode.InvalidPath, result.ErrorCode);
        Assert.Contains("directory", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.CreateCalls);
    }

    [Fact]
    public void InstallRejectsMalformedExistingConfiguration()
    {
        using var fixture = new ServiceFixture();
        File.WriteAllText(fixture.Request.ConfigurationPath, "not-json");
        var backend = new FakeWindowsServiceBackend();
        var manager = new WindowsServiceManager(backend);

        var result = manager.Execute(ServiceOperation.Install, fixture.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrorCode.InvalidPath, result.ErrorCode);
        Assert.Contains("invalid", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.CreateCalls);
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orelay-service-tests", Guid.NewGuid().ToString("N"));

        public ServiceFixture()
        {
            Directory.CreateDirectory(_directory);
            var executable = Path.Combine(_directory, "ORelay Test.exe");
            var config = Path.Combine(_directory, "ORelay State", "orelay.json");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            File.WriteAllText(executable, "test");
            Request = new ServiceRequest(executable, config, "ORelay-Test");
        }

        public ServiceRequest Request { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class FakeWindowsServiceBackend : IWindowsServiceBackend
    {
        public WindowsServiceDefinition? Definition { get; set; }

        public int CreateCalls { get; private set; }

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public WindowsServiceQueryResult Query(string serviceName) =>
            Definition is null
                ? WindowsServiceQueryResult.NotFound()
                : WindowsServiceQueryResult.FoundService(Definition);

        public WindowsServiceActionResult Create(string serviceName, string displayName, string binaryPathName)
        {
            CreateCalls++;
            Definition = new WindowsServiceDefinition(binaryPathName, ServiceState.Stopped);
            return WindowsServiceActionResult.Success();
        }

        public WindowsServiceActionResult Start(string serviceName)
        {
            StartCalls++;
            Definition = Definition! with { State = ServiceState.Running, Win32ExitCode = 0 };
            return WindowsServiceActionResult.Success();
        }

        public WindowsServiceActionResult StopService(string serviceName)
        {
            StopCalls++;
            Definition = Definition! with { State = ServiceState.Stopped, Win32ExitCode = 0 };
            return WindowsServiceActionResult.Success();
        }

        public WindowsServiceActionResult Delete(string serviceName)
        {
            DeleteCalls++;
            Definition = null;
            return WindowsServiceActionResult.Success();
        }
    }
}
