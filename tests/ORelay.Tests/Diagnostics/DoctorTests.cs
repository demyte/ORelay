using System.Text.Json;
using ORelay.Cli;
using ORelay.Diagnostics;
using ORelay.Discovery;

namespace ORelay.Tests.Diagnostics;

public sealed class DoctorTests
{
    [Fact]
    public async Task ReadOnlyDoctorDoesNotCreateMissingConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var config = Path.Combine(directory.Path, "missing.json");
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(
            Options(config, json: true, fix: false),
            output,
            error,
            HealthyRuntime());

        Assert.Equal(DoctorExitCodes.DiagnosticFailure, exitCode);
        Assert.False(File.Exists(config));
        Assert.Empty(error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("healthy").GetBoolean());
        Assert.Contains(
            document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == "configuration" &&
                     check.GetProperty("status").GetString() == "Failed");
    }

    [Fact]
    public async Task FixCreatesMissingConfigurationAndRepeatingItIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        var config = Path.Combine(directory.Path, "orelay.json");

        var firstOutput = new StringWriter();
        var firstCode = await DoctorCommand.ExecuteAsync(
            Options(config, json: true, fix: true),
            firstOutput,
            new StringWriter(),
            HealthyRuntime());
        var firstBytes = File.ReadAllBytes(config);

        var secondOutput = new StringWriter();
        var secondCode = await DoctorCommand.ExecuteAsync(
            Options(config, json: true, fix: true),
            secondOutput,
            new StringWriter(),
            HealthyRuntime());
        var secondBytes = File.ReadAllBytes(config);

        Assert.Equal(DoctorExitCodes.Success, firstCode);
        Assert.Equal(DoctorExitCodes.Success, secondCode);
        Assert.Equal(firstBytes, secondBytes);
        Assert.True(JsonDocument.Parse(firstOutput.ToString()).RootElement.GetProperty("changed").GetBoolean());
        Assert.False(JsonDocument.Parse(secondOutput.ToString()).RootElement.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task FixPreservesMalformedConfiguration()
    {
        using var directory = new TemporaryDirectory();
        var config = Path.Combine(directory.Path, "malformed.json");
        var malformed = "{ not valid json";
        await File.WriteAllTextAsync(config, malformed);

        var code = await DoctorCommand.ExecuteAsync(
            Options(config, json: true, fix: true),
            new StringWriter(),
            new StringWriter(),
            HealthyRuntime());

        Assert.Equal(DoctorExitCodes.DiagnosticFailure, code);
        Assert.Equal(malformed, await File.ReadAllTextAsync(config));
    }

    [Fact]
    public async Task DoctorDistinguishesForeignListenerFromRelayIdentity()
    {
        using var directory = new TemporaryDirectory();
        var config = Path.Combine(directory.Path, "orelay.json");
        new ORelay.Configuration.RelayConfigurationStore(config).Init();
        var runtime = new DoctorRuntime
        {
            HealthProbe = new FakeHealthProbe(new RelayHealthProbeResult(true, false, "other-service", "ok", 200, null)),
            PortProbe = new FakePortProbe(new PortOccupancyProbeResult(true, "foreign listener")),
            TailscaleProvider = new FakeTailscaleProvider(),
        };
        var output = new StringWriter();

        var code = await DoctorCommand.ExecuteAsync(Options(config, json: true, fix: false), output, new StringWriter(), runtime);

        Assert.Equal(DoctorExitCodes.DiagnosticFailure, code);
        var text = output.ToString();
        Assert.Contains("did not identify as a healthy ORelay", text, StringComparison.Ordinal);
        Assert.Contains("occupied, but the listener did not identify as ORelay", text, StringComparison.Ordinal);
    }

    private static CliOptions Options(string config, bool json, bool fix) =>
        new(CliCommand.Doctor, json, config, Doctor: new DoctorCommandOptions(fix));

    private static DoctorRuntime HealthyRuntime() => new()
    {
        HealthProbe = new FakeHealthProbe(new RelayHealthProbeResult(true, true, "orelay", "ok", 200, null)),
        PortProbe = new FakePortProbe(new PortOccupancyProbeResult(false, null)),
        TailscaleProvider = new FakeTailscaleProvider(),
    };

    private sealed class FakeHealthProbe(RelayHealthProbeResult result) : IRelayHealthProbe
    {
        public Task<RelayHealthProbeResult> CheckAsync(Uri healthUrl, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class FakePortProbe(PortOccupancyProbeResult result) : IPortOccupancyProbe
    {
        public Task<PortOccupancyProbeResult> CheckAsync(string bind, int port, CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class FakeTailscaleProvider : ITailscaleStatusProvider
    {
        public Task<TailscaleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TailscaleStatusSnapshot(true, "Running", "host.tailnet.test", null, []));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orelay-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
