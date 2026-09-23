using System.Text.Json;
using ORelay.Cli;
using ORelay.Configuration;

namespace ORelay.Tests.Cli;

public sealed class CliLoggingTests
{
    [Fact]
    public async Task ConfigurationMutationsLogOutcomesWithoutValuesOrInvalidKeys()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, (await fixture.RunAsync("init")).Exit);
        Assert.Equal(0, (await fixture.RunAsync("config", "set", "hostname", "synthetic-secret.example")).Exit);
        var saved = File.ReadAllText(fixture.Config);
        Assert.Equal(3, (await fixture.RunAsync("config", "set", "port", "synthetic-secret-value")).Exit);
        Assert.Equal(64, (await fixture.RunAsync("config", "set", "synthetic-secret-key", "value")).Exit);
        Assert.Equal(saved, File.ReadAllText(fixture.Config));
        Assert.Equal(0, (await fixture.RunAsync("config", "clear", "hostname")).Exit);
        Assert.Equal("localhost", new RelayConfigurationStore(fixture.Config).Read().Hostname);

        var log = File.ReadAllText(fixture.Log);
        Assert.Contains("CLI command started: init.", log);
        Assert.Contains("CLI command completed: config set; exit=0, elapsed=", log);
        Assert.Contains("CLI command failed: config set; exit=3, elapsed=", log);
        Assert.Contains("CLI command failed: config set; exit=64, elapsed=", log);
        Assert.Contains("CLI command completed: config clear; exit=0", log);
        Assert.DoesNotContain("synthetic-secret", log);
        Assert.DoesNotContain(fixture.Config, log);
        Assert.Equal(10, File.ReadAllLines(fixture.Log).Length);
    }

    [Theory]
    [InlineData("config", "get")]
    [InlineData("doctor")]
    [InlineData("update", "--check")]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("not-a-command")]
    public async Task ReadOnlyAndUnparsedCommandsCreateNoLogDirectory(params string[] command)
    {
        using var fixture = new Fixture();
        await fixture.RunAsync(command);
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task SetupLogsCompletionWithoutClaimingSkippedSetupChangedState()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, (await fixture.RunAsync("setup", "--defaults", "--yes", "--skip-path")).Exit);
        var saved = File.ReadAllText(fixture.Config);
        var repeat = await fixture.RunAsync("setup", "--defaults", "--yes", "--skip-path");
        Assert.Equal(0, repeat.Exit);
        using var json = JsonDocument.Parse(repeat.Output);
        Assert.True(json.RootElement.GetProperty("skipped").GetBoolean());
        Assert.Equal(saved, File.ReadAllText(fixture.Config));
        Assert.Equal(2, File.ReadAllLines(fixture.Log).Count(line => line.Contains("CLI command completed: setup; exit=0", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FileLoggingFailureDoesNotPreventConfigurationMutation()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        File.WriteAllText(Path.Combine(fixture.Root, "logs"), "blocked log directory");

        var result = await fixture.RunAsync("init");

        Assert.Equal(0, result.Exit);
        Assert.True(File.Exists(fixture.Config));
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("init", json.RootElement.GetProperty("action").GetString());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "orelay-cli-logs-" + Guid.NewGuid().ToString("N"));
        public string Config => Path.Combine(Root, "orelay.json");
        public string Log => Path.Combine(Root, "logs", "orelay.log");

        public async Task<(int Exit, string Output)> RunAsync(params string[] command)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var args = new[] { "--config-file", Config, "--json" }.Concat(command).ToArray();
            var exit = await CliApplication.ExecuteAsync(args, output, error, ApplicationCommands.ExecuteAsync);
            return (exit, output.ToString());
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
