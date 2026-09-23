using System.Text.Json;
using Microsoft.Extensions.Logging;
using ORelay.Cli;
using ORelay.Diagnostics;
using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class UpdateCommandTests
{
    [Fact]
    public async Task Install_LogsSanitizedFailureAndKeepsJsonOutputValidOnManagedRuntime()
    {
        var secretDirectory = "install-path-secret-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetTempPath(), secretDirectory);
        var configurationPath = Path.Combine(root, "orelay.json");
        var installDirectory = Path.Combine(root, "install");
        try
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddProvider(new RotatingFileLoggerProvider(configurationPath)));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var options = new CliOptions(CliCommand.Install, true, configurationPath,
                Install: new InstallCommandOptions(installDirectory, false));

            var exitCode = await UpdateCommand.ExecuteAsync(options, output, error, loggerFactory);

            Assert.Equal(CliExitCodes.CommandUnavailable, exitCode);
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal("UnsupportedRuntime", document.RootElement.GetProperty("errorCode").GetString());
            Assert.Contains("Native AOT", document.RootElement.GetProperty("message").GetString());
            Assert.Contains("Native AOT", error.ToString());

            var logPath = Path.Combine(root, "logs", "orelay.log");
            var log = File.ReadAllText(logPath);
            Assert.Contains("[Warning] ORelay.Updating.UpdateCommand", log);
            Assert.Contains("Manual install failed", log);
            Assert.Contains("changed=False", log);
            Assert.Contains("error=UnsupportedRuntime", log);
            Assert.DoesNotContain(secretDirectory, log);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Check_DoesNotWriteManualOutcomeLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "orelay-update-check-test-" + Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(root, "orelay.json");
        try
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
                builder.AddProvider(new RotatingFileLoggerProvider(configurationPath)));
            using var output = new StringWriter();
            using var error = new StringWriter();
            var options = new CliOptions(CliCommand.Update, true, configurationPath,
                Update: new UpdateCommandOptions(true, false));

            await UpdateCommand.ExecuteAsync(options, output, error, loggerFactory);

            Assert.False(Directory.Exists(Path.Combine(root, "logs")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
