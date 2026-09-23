using ORelay.Services;

namespace ORelay.Tests.Services;

public sealed class SystemdServiceManagerTests
{
    [Fact]
    public void InstallWritesOwnedUnitWithAbsoluteQuotedPaths()
    {
        using var fixture = new ServiceFixture();
        var runner = new FakeSystemdRunner();
        var manager = new SystemdServiceManager(runner, "systemctl", TimeSpan.FromSeconds(1));

        var result = manager.Execute(ServiceOperation.Install, fixture.Request);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.True(File.Exists(fixture.Request.UnitFilePath));
        var unit = File.ReadAllText(fixture.Request.UnitFilePath);
        Assert.Contains(ServiceIdentity.OwnershipMarker, unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=:\"", unit, StringComparison.Ordinal);
        Assert.Contains("ORelay State", unit, StringComparison.Ordinal);
        Assert.Contains(runner.Commands, command => command.SequenceEqual(new[] { "daemon-reload" }));
    }

    [Fact]
    public void InstallRefusesToOverwriteAnUnownedUnit()
    {
        using var fixture = new ServiceFixture();
        File.WriteAllText(fixture.Request.UnitFilePath, "[Service]\nExecStart=/usr/bin/other\n");
        var runner = new FakeSystemdRunner();
        var manager = new SystemdServiceManager(runner, "systemctl", TimeSpan.FromSeconds(1));

        var result = manager.Execute(ServiceOperation.Install, fixture.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrorCode.Conflict, result.ErrorCode);
        Assert.DoesNotContain(runner.Commands, command => command.SequenceEqual(new[] { "daemon-reload" }));
        Assert.Contains("/usr/bin/other", File.ReadAllText(fixture.Request.UnitFilePath), StringComparison.Ordinal);
    }

    [Fact]
    public void OwnedLifecycleAndUninstallPreserveConfiguration()
    {
        using var fixture = new ServiceFixture();
        File.WriteAllText(fixture.Request.ConfigurationPath, "{\"schemaVersion\":1}");
        var runner = new FakeSystemdRunner();
        var manager = new SystemdServiceManager(runner, "systemctl", TimeSpan.FromSeconds(1));

        Assert.True(manager.Execute(ServiceOperation.Install, fixture.Request).Succeeded);
        Assert.True(manager.Execute(ServiceOperation.Start, fixture.Request).Succeeded);
        var status = manager.Execute(ServiceOperation.Status, fixture.Request);
        Assert.Equal(ServiceState.Running, status.State);
        Assert.True(manager.Execute(ServiceOperation.Restart, fixture.Request).Succeeded);
        Assert.True(manager.Execute(ServiceOperation.Stop, fixture.Request).Succeeded);
        Assert.True(manager.Execute(ServiceOperation.Uninstall, fixture.Request).Succeeded);

        Assert.True(File.Exists(fixture.Request.ConfigurationPath));
        Assert.False(File.Exists(fixture.Request.UnitFilePath));
    }

    [Fact]
    public void UnitRendererIsStableForPathsContainingPercentAndSpaces()
    {
        using var fixture = new ServiceFixture();

        var content = SystemdUnitRenderer.Render(fixture.Request);

        Assert.True(SystemdUnitRenderer.IsOwned(content));
        Assert.Contains("%%", content, StringComparison.Ordinal);
        Assert.Contains("ORelay State", content, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultWindowsStyleServiceNameUsesTheLowercaseUnitFileForSystemctl()
    {
        using var fixture = new ServiceFixture(serviceName: ServiceIdentity.DefaultName);
        var runner = new FakeSystemdRunner();
        var manager = new SystemdServiceManager(runner, "systemctl", TimeSpan.FromSeconds(1));

        Assert.True(manager.Execute(ServiceOperation.Install, fixture.Request).Succeeded);
        Assert.True(manager.Execute(ServiceOperation.Start, fixture.Request).Succeeded);

        Assert.Contains(
            runner.Commands,
            command => command.SequenceEqual(new[] { "start", "orelay.service" }));
        Assert.Contains(
            runner.Commands,
            command => command.Length > 1 && command[0] == "show" && command[1] == "orelay.service");
    }

    [Fact]
    public void ExecStartPreservesDollarPathsWithoutEnvironmentExpansion()
    {
        using var fixture = new ServiceFixture(serviceName: ServiceIdentity.DefaultName, includeDollar: true);

        var content = SystemdUnitRenderer.Render(fixture.Request);

        Assert.Contains("ExecStart=:\"", content, StringComparison.Ordinal);
        Assert.Contains("ORelay$Data", content, StringComparison.Ordinal);
        Assert.DoesNotContain("$$", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BootStartupChangesOnlyForOwnedUnit()
    {
        using var fixture = new ServiceFixture();
        var runner = new FakeSystemdRunner();
        var manager = new SystemdServiceManager(runner, "systemctl", TimeSpan.FromSeconds(1));
        Assert.True(manager.Execute(ServiceOperation.Install, fixture.Request).Succeeded);

        var enable = manager.Execute(ServiceOperation.Enable, fixture.Request);
        var enableAgain = manager.Execute(ServiceOperation.Enable, fixture.Request);
        var disable = manager.Execute(ServiceOperation.Disable, fixture.Request);

        Assert.True(enable.Succeeded);
        Assert.True(enable.Changed);
        Assert.True(enable.Owned);
        Assert.False(enableAgain.Changed);
        Assert.True(disable.Succeeded);
        Assert.True(disable.Changed);
        Assert.Contains(runner.Commands, command => command.SequenceEqual(new[] { "enable", "orelay-test.service" }));
        Assert.Contains(runner.Commands, command => command.SequenceEqual(new[] { "disable", "orelay-test.service" }));

        File.WriteAllText(fixture.Request.UnitFilePath, "[Service]\nExecStart=/usr/bin/other\n");
        var conflict = manager.Execute(ServiceOperation.Enable, fixture.Request);
        Assert.Equal(ServiceErrorCode.Conflict, conflict.ErrorCode);
        Assert.Equal(1, runner.Commands.Count(command => command[0] == "enable"));
    }

    [Fact]
    public void BootStartupReportsSystemctlPermissionFailure()
    {
        using var fixture = new ServiceFixture();
        var runner = new FakeSystemdRunner();
        var manager = new SystemdServiceManager(runner, "systemctl", TimeSpan.FromSeconds(1));
        Assert.True(manager.Execute(ServiceOperation.Install, fixture.Request).Succeeded);
        runner.EnableError = new ServiceProcessResult(1, string.Empty, "Permission denied");

        var result = manager.Execute(ServiceOperation.Enable, fixture.Request);

        Assert.False(result.Succeeded);
        Assert.Equal(ServiceErrorCode.PermissionDenied, result.ErrorCode);
        Assert.True(result.Owned);
        Assert.Equal(ServiceState.Stopped, result.State);
    }

    private sealed class ServiceFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orelay-systemd-tests", Guid.NewGuid().ToString("N"));

        public ServiceFixture(string? serviceName = null, bool includeDollar = false)
        {
            Directory.CreateDirectory(_directory);
            var executableDirectory = includeDollar ? Path.Combine(_directory, "ORelay$Data") : _directory;
            Directory.CreateDirectory(executableDirectory);
            var executable = Path.Combine(executableDirectory, "ORelay Test");
            var config = Path.Combine(_directory, "ORelay State", "orelay 100%.json");
            var unit = Path.Combine(_directory, "units", "orelay-test.service");
            Directory.CreateDirectory(Path.GetDirectoryName(config)!);
            Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
            File.WriteAllText(executable, "test");
            if (string.Equals(serviceName, ServiceIdentity.DefaultName, StringComparison.Ordinal))
            {
                unit = Path.Combine(Path.GetDirectoryName(unit)!, "orelay.service");
            }

            Request = new ServiceRequest(executable, config, serviceName ?? "orelay-test.service", unit);
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

    private sealed class FakeSystemdRunner : IServiceProcessRunner
    {
        private bool _loaded;
        private bool _running;

        private bool _enabled;

        public ServiceProcessResult? EnableError { get; set; }

        public List<string[]> Commands { get; } = [];

        public ServiceProcessResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            var command = arguments.ToArray();
            Commands.Add(command);
            if (command.Length == 0)
            {
                return new ServiceProcessResult(0, string.Empty, string.Empty);
            }

            switch (command[0])
            {
                case "daemon-reload":
                    _loaded = true;
                    return new ServiceProcessResult(0, string.Empty, string.Empty);
                case "disable":
                    _enabled = false;
                    return new ServiceProcessResult(0, string.Empty, string.Empty);
                case "enable":
                    if (EnableError is not null) return EnableError;
                    _enabled = true;
                    return new ServiceProcessResult(0, string.Empty, string.Empty);
                case "is-enabled":
                    return _enabled
                        ? new ServiceProcessResult(0, "enabled", string.Empty)
                        : new ServiceProcessResult(1, "disabled", string.Empty);
                case "start":
                case "restart":
                    _running = true;
                    return new ServiceProcessResult(0, string.Empty, string.Empty);
                case "stop":
                    _running = false;
                    return new ServiceProcessResult(0, string.Empty, string.Empty);
                case "show":
                    if (!_loaded)
                    {
                        return new ServiceProcessResult(1, string.Empty, "Unit orelay-test.service not-found");
                    }

                    return new ServiceProcessResult(
                        0,
                        $"LoadState=loaded{Environment.NewLine}ActiveState={(_running ? "active" : "inactive")}{Environment.NewLine}SubState={(_running ? "running" : "dead")}{Environment.NewLine}Result=success{Environment.NewLine}",
                        string.Empty);
                default:
                    return new ServiceProcessResult(0, string.Empty, string.Empty);
            }
        }
    }
}
