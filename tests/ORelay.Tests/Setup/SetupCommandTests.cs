using System.Text.Json;
using ORelay.Cli;
using ORelay.Configuration;
using ORelay.Discovery;
using ORelay.Services;
using ORelay.Setup;

namespace ORelay.Tests.Setup;

public sealed class SetupCommandTests
{
    [Fact]
    public async Task InteractiveDefaultsExplainSettingsAndWaitForApproval()
    {
        using var run = new TestRun();
        var result = await run.Execute("1\nn\n", "setup");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Defaults:", result.Output);
        Assert.Contains("Callback: http://localhost:12987/callback", result.Output);
        Assert.Contains("Settings to apply:", result.Output);
        Assert.Contains("Setup cancelled", result.Output);
        Assert.False(File.Exists(run.ConfigFile));
    }

    [Fact]
    public async Task InteractiveCustomLanCanBeReviewedAndApplied()
    {
        using var run = new TestRun();
        var result = await run.Execute("2\nlan\n13871\n\nrelay.test\nforeground\ny\n", "setup");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Shared access permits remote callbacks", result.Output);
        Assert.Contains("Callback: http://relay.test:13871/callback", result.Output);
        var settings = new RelayConfigurationStore(run.ConfigFile).Read();
        Assert.Equal("0.0.0.0", settings.Bind);
        Assert.Equal("relay.test", settings.Hostname);
        Assert.Equal(13871, settings.Port);
    }

    [Fact]
    public async Task InvalidInteractiveAnswerThenEofLeavesNoConfiguration()
    {
        using var run = new TestRun();
        var result = await run.Execute("bad\n", "setup");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Choose 1, 2", result.Output);
        Assert.Contains("Input ended", result.Error);
        Assert.False(File.Exists(run.ConfigFile));
    }

    [Fact]
    public async Task RedirectedSetupRequiresYesAndLeavesNoConfiguration()
    {
        using var run = new TestRun(interactive: false);
        var result = await run.Execute("", "setup", "--defaults");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("interactive terminal", result.Error);
        Assert.False(File.Exists(run.ConfigFile));
    }

    [Fact]
    public async Task UnattendedDefaultsAndIfNeededPreserveExistingBytes()
    {
        using var run = new TestRun(interactive: false);
        var created = await run.Execute("", "setup", "--defaults", "--yes", "--json");
        Assert.Equal(0, created.ExitCode);
        Assert.True(File.Exists(run.ConfigFile));
        using var createdJson = JsonDocument.Parse(created.Output);
        Assert.Equal("http://localhost:12987/callback", createdJson.RootElement.GetProperty("callbackUrl").GetString());

        var store = new RelayConfigurationStore(run.ConfigFile);
        store.Set("port", "13872");
        var before = File.ReadAllBytes(run.ConfigFile);
        var needed = await run.Execute("", "setup", "--if-needed", "--yes", "--port", "13999", "--json");
        var defaults = await run.Execute("", "setup", "--defaults", "--yes", "--json");
        Assert.Equal(0, needed.ExitCode);
        Assert.Equal(0, defaults.ExitCode);
        Assert.True(JsonDocument.Parse(needed.Output).RootElement.GetProperty("skipped").GetBoolean());
        Assert.True(JsonDocument.Parse(defaults.Output).RootElement.GetProperty("skipped").GetBoolean());
        Assert.Equal(before, File.ReadAllBytes(run.ConfigFile));
    }

    [Fact]
    public async Task UnattendedLanAppliesExplicitSettingsAndRejectsInvalidInput()
    {
        using var run = new TestRun(interactive: false);
        var invalid = await run.Execute("", "setup", "--yes", "--access", "lan", "--port", "0");
        Assert.NotEqual(0, invalid.ExitCode);
        Assert.False(File.Exists(run.ConfigFile));

        var valid = await run.Execute("", "setup", "--yes", "--access", "lan", "--hostname", "relay.test", "--port", "13873");
        Assert.Equal(0, valid.ExitCode);
        var settings = new RelayConfigurationStore(run.ConfigFile).Read();
        Assert.Equal("0.0.0.0", settings.Bind);
        Assert.Equal("relay.test", settings.Hostname);
        Assert.Equal(13873, settings.Port);
    }

    [Fact]
    public async Task TailscaleNeedsConnectedDiscoveryBeforeSaving()
    {
        using var run = new TestRun(interactive: false, tailscale: TailscaleStatusSnapshot.Unavailable("not connected"));
        var result = await run.Execute("", "setup", "--yes", "--access", "tailscale");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("not connected", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(run.ConfigFile));
    }

    [Fact]
    public async Task ConnectedTailscaleSuppliesAdvertisedCallback()
    {
        using var run = new TestRun(interactive: false, tailscale: new TailscaleStatusSnapshot(
            true, "Running", "relay.tailnet.ts.net", "relay", ["100.64.1.2"]));
        var result = await run.Execute("", "setup", "--yes", "--access", "tailscale", "--json");

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("http://relay.tailnet.ts.net:12987/callback", json.RootElement.GetProperty("callbackUrl").GetString());
        var settings = new RelayConfigurationStore(run.ConfigFile).Read();
        Assert.Equal("tailscale", settings.AutoDiscovery);
        Assert.Null(settings.Hostname);
    }

    [Fact]
    public async Task ServiceModeUsesSelectedIdentityAndRequestedActions()
    {
        using var run = new TestRun(interactive: false);
        var names = new List<string>();
        run.Service = (operation, _, name) =>
        {
            names.Add(name);
            return ServiceOperationResult.Success(ServicePlatform.Windows, operation, name,
                ServiceState.NotInstalled, changed: true, owned: false, "completed");
        };
        var result = await run.Execute("", "setup", "--yes", "--mode", "service", "--name", "relay_test", "--start", "--enable-startup", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal([ServiceOperation.Status, ServiceOperation.Install, ServiceOperation.Enable, ServiceOperation.Start], run.ServiceOperations);
        Assert.All(names, name => Assert.Equal("relay_test", name));
        Assert.True(File.Exists(run.ConfigFile));
    }

    [Fact]
    public async Task ServiceIdentityConflictIsRejectedBeforeSaving()
    {
        using var run = new TestRun(interactive: false);
        run.Service = (operation, _, name) => ServiceOperationResult.Success(ServicePlatform.Windows, operation, name,
            ServiceState.Running, false, owned: false, "belongs elsewhere");
        var result = await run.Execute("", "setup", "--yes", "--mode", "service", "--name", "relay_test");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("different executable or configuration", result.Error);
        Assert.False(File.Exists(run.ConfigFile));
        Assert.Equal([ServiceOperation.Status], run.ServiceOperations);
    }

    [Fact]
    public async Task ConcurrentConfigChangeRejectsReviewedSave()
    {
        using var run = new TestRun(interactive: false);
        run.Service = (operation, path, name) =>
        {
            new RelayConfigurationStore(path).Set("port", "14000");
            return ServiceOperationResult.Success(ServicePlatform.Windows, operation, name,
                ServiceState.NotInstalled, false, owned: false, "available");
        };
        var result = await run.Execute("", "setup", "--yes", "--mode", "service", "--name", "relay_test");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Configuration changed during setup", result.Error);
        Assert.Equal(14000, new RelayConfigurationStore(run.ConfigFile).Read().Port);
        Assert.Equal([ServiceOperation.Status], run.ServiceOperations);
    }

    [Fact]
    public async Task ServiceFailureReportsSavedConfigurationAndIncompleteService()
    {
        using var run = new TestRun(interactive: false);
        run.Service = (operation, _, name) => operation == ServiceOperation.Install
            ? ServiceOperationResult.Failure(ServicePlatform.Windows, operation, name, ServiceState.NotInstalled,
                ServiceErrorCode.PermissionDenied, "administrator required")
            : ServiceOperationResult.Success(ServicePlatform.Windows, operation, name,
                ServiceState.NotInstalled, false, owned: false, "available");
        var result = await run.Execute("", "setup", "--yes", "--mode", "service", "--name", "relay_test", "--json");

        Assert.Equal(3, result.ExitCode);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.Contains("Configuration saved, but service setup did not complete", result.Error);
        Assert.True(File.Exists(run.ConfigFile));
        Assert.Equal([ServiceOperation.Status, ServiceOperation.Install], run.ServiceOperations);
    }

    [Fact]
    public async Task PathOfferIsReviewedAndCancellationAppliesNothing()
    {
        using var run = new TestRun();
        run.UserPath.Configured = false;
        var cancelled = await run.Execute("1\ny\nn\n", "setup");
        Assert.Equal(0, cancelled.ExitCode);
        Assert.Contains("Add this directory to your user PATH?", cancelled.Output);
        Assert.Contains("User PATH: ", cancelled.Output);
        Assert.Equal(0, run.UserPath.Writes);
        Assert.False(File.Exists(run.ConfigFile));

        var accepted = await run.Execute("1\ny\ny\n", "setup");
        Assert.Equal(0, accepted.ExitCode);
        Assert.Equal(1, run.UserPath.Writes);
        Assert.Contains("Open a new terminal", accepted.Output);
        Assert.True(File.Exists(run.ConfigFile));
    }

    [Theory]
    [InlineData("1\nn\ny\n", false)]
    [InlineData("1\ny\n", true)]
    public async Task DecliningOrSkippingPathStillSavesConfiguration(string answers, bool skipPath)
    {
        using var run = new TestRun();
        run.UserPath.Configured = false;
        var result = await run.Execute(answers, skipPath ? ["setup", "--skip-path"] : ["setup"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, run.UserPath.Writes);
        Assert.True(File.Exists(run.ConfigFile));
    }

    [Fact]
    public async Task UnattendedPathChangeRequiresExplicitFlagAndPreservesExistingConfig()
    {
        using var run = new TestRun(interactive: false);
        run.UserPath.Configured = false;
        Assert.Equal(0, (await run.Execute("", "setup", "--defaults", "--yes")).ExitCode);
        Assert.Equal(0, run.UserPath.Inspections);
        Assert.Equal(0, run.UserPath.Writes);
        var original = File.ReadAllBytes(run.ConfigFile);

        var result = await run.Execute("", "setup", "--if-needed", "--yes", "--add-to-path", "--json");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, run.UserPath.Writes);
        Assert.Equal(original, File.ReadAllBytes(run.ConfigFile));
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("pathChanged").GetBoolean());
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.False(json.RootElement.GetProperty("skipped").GetBoolean());
    }

    [Fact]
    public async Task ReinstallOffersPathWithoutRewritingConfiguration()
    {
        using var run = new TestRun();
        await run.Execute("", "setup", "--defaults", "--yes");
        var original = File.ReadAllBytes(run.ConfigFile);
        run.UserPath.Configured = false;
        var result = await run.Execute("y\ny\n", "setup", "--if-needed");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, run.UserPath.Writes);
        Assert.Equal(original, File.ReadAllBytes(run.ConfigFile));
        Assert.DoesNotContain("Go with defaults", result.Output);
    }

    [Fact]
    public async Task PathFailureReportsThatSavedConfigurationRemains()
    {
        using var run = new TestRun(interactive: false);
        run.UserPath.Configured = false;
        run.UserPath.FailWrite = true;
        var result = await run.Execute("", "setup", "--defaults", "--yes", "--add-to-path", "--json");
        Assert.Equal(3, result.ExitCode);
        Assert.True(File.Exists(run.ConfigFile));
        Assert.Contains("PATH setup did not complete", result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("changed").GetBoolean());
    }

    [Theory]
    [InlineData("--add-to-path", "--skip-path")]
    [InlineData("--add-to-path", "--add-to-path")]
    public async Task ConflictingPathOptionsFailBeforeWriting(string first, string second)
    {
        using var run = new TestRun();
        var result = await run.Execute("", "setup", "--defaults", "--yes", first, second);
        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(run.ConfigFile));
        Assert.Equal(0, run.UserPath.Inspections);
    }

    private sealed class TestRun : IDisposable
    {
        private readonly bool _interactive;
        private readonly TailscaleStatusSnapshot _tailscale;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "orelay-setup-tests", Guid.NewGuid().ToString("N"));

        public TestRun(bool interactive = true, TailscaleStatusSnapshot? tailscale = null)
        {
            _interactive = interactive;
            _tailscale = tailscale ?? TailscaleStatusSnapshot.Unavailable("unused");
            Directory.CreateDirectory(_directory);
        }

        public string ConfigFile => Path.Combine(_directory, "orelay.json");
        public List<ServiceOperation> ServiceOperations { get; } = [];
        public Func<ServiceOperation, string, string, ServiceOperationResult>? Service { get; set; }
        public FakeUserPath UserPath { get; } = new();

        public async Task<(int ExitCode, string Output, string Error)> Execute(string input, params string[] args)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var allArgs = new[] { "--config-file", ConfigFile }.Concat(args).ToArray();
            var runtime = new SetupRuntime
            {
                Input = new StringReader(input),
                Interactive = _interactive,
                ServicesSupported = true,
                Tailscale = new FakeTailscale(_tailscale),
                UserPath = UserPath,
                Service = (operation, path, name) =>
                {
                    ServiceOperations.Add(operation);
                    return Service?.Invoke(operation, path, name) ?? ServiceOperationResult.Success(
                        ServicePlatform.Windows, operation, name, ServiceState.NotInstalled, false, owned: false, "available");
                }
            };
            var code = await CliApplication.ExecuteAsync(allArgs, output, error,
                (options, stdout, stderr) => SetupCommand.ExecuteAsync(options, stdout, stderr, runtime));
            return (code, output.ToString(), error.ToString());
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeUserPath : IUserPathManager
    {
        public bool Configured { get; set; } = true;
        public bool FailWrite { get; set; }
        public int Inspections { get; private set; }
        public int Writes { get; private set; }

        public UserPathPlan Inspect(string installationDirectory)
        {
            Inspections++;
            return new(installationDirectory, Configured, "Add to the test user's PATH. Open a new terminal after setup.");
        }

        public bool Apply(UserPathPlan plan)
        {
            if (FailWrite) throw new IOException("profile is read-only");
            Writes++;
            Configured = true;
            return true;
        }
    }

    private sealed class FakeTailscale(TailscaleStatusSnapshot snapshot) : ITailscaleStatusProvider
    {
        public Task<TailscaleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }
}
