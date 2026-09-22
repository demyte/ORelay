using System.Text;
using System.Text.Json;
using ORelay.Cli;

namespace ORelay.Tests.Cli;

public sealed class CliApplicationTests
{
    [Fact]
    public async Task VersionWritesPlainTextAndDoesNotCreateState()
    {
        var output = new StringWriter(new StringBuilder());
        var error = new StringWriter(new StringBuilder());

        var exitCode = await CliApplication.ExecuteAsync(VersionArguments, output, error);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Matches(@"\Aorelay [0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z", output.ToString().Trim());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task JsonVersionIsStableAndUnambiguous()
    {
        var output = new StringWriter(new StringBuilder());

        var exitCode = await CliApplication.ExecuteAsync(JsonVersionArguments, output);

        Assert.Equal(CliExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Single(document.RootElement.EnumerateObject());
        Assert.Matches(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z",
            document.RootElement.GetProperty("version").GetString()!);
    }

    [Fact]
    public async Task UnknownCommandReturnsNonzeroAndPointsToHelp()
    {
        var output = new StringWriter(new StringBuilder());
        var error = new StringWriter(new StringBuilder());

        var exitCode = await CliApplication.ExecuteAsync(UnknownCommandArguments, output, error);

        Assert.Equal(CliExitCodes.UsageError, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("unknown command 'wat'", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("orelay --help", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidFutureCommandIsNotReportedAsWorking()
    {
        var error = new StringWriter(new StringBuilder());

        var exitCode = await CliApplication.ExecuteAsync(ServerArguments, error: error);

        Assert.Equal(CliExitCodes.CommandUnavailable, exitCode);
        Assert.Contains("server", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("not available", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlerReceivesParsedFutureCommand()
    {
        CliOptions? received = null;
        var exitCode = await CliApplication.ExecuteAsync(
            DoctorArguments,
            commandHandler: (options, _, _) =>
            {
                received = options;
                return Task.FromResult(17);
            });

        Assert.Equal(17, exitCode);
        Assert.Equal(CliCommand.Doctor, received!.Command);
        Assert.True(received.IsJson);
        Assert.True(received.Doctor!.Fix);
    }

    [Fact]
    public async Task SubcommandHelpIsHandledBeforeCommandDispatch()
    {
        var output = new StringWriter(new StringBuilder());
        var error = new StringWriter(new StringBuilder());
        var handlerCalled = false;

        var exitCode = await CliApplication.ExecuteAsync(
            ServerHelpArguments,
            output,
            error,
            (_, _, _) =>
            {
                handlerCalled = true;
                return Task.FromResult(99);
            });

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(handlerCalled);
        Assert.Contains("orelay server", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--auto-discovery", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--service-name", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task ConfigActionHelpDescribesItsArguments()
    {
        var output = new StringWriter(new StringBuilder());

        var exitCode = await CliApplication.ExecuteAsync(ConfigGetHelpArguments, output);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("config get [key]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("without creating a file", output.ToString(), StringComparison.Ordinal);
    }

    private static readonly string[] VersionArguments = ["--version"];
    private static readonly string[] JsonVersionArguments = ["--json", "--version"];
    private static readonly string[] UnknownCommandArguments = ["wat"];
    private static readonly string[] ServerArguments = ["server"];
    private static readonly string[] DoctorArguments = ["doctor", "--fix", "--json"];
    private static readonly string[] ServerHelpArguments = ["server", "--help"];
    private static readonly string[] ConfigGetHelpArguments = ["config", "get", "--help"];
}
