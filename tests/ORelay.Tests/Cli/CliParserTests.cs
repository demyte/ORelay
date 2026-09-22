using ORelay.Cli;

namespace ORelay.Tests.Cli;

public sealed class CliParserTests
{
    [Fact]
    public void EmptyArgumentsSelectHelp()
    {
        var result = CliParser.Parse(Array.Empty<string>());

        Assert.True(result.IsSuccess);
        Assert.Equal(CliCommand.Help, result.Options!.Command);
    }

    [Fact]
    public void ConfigSetParsesGlobalOptionsAndValue()
    {
        var result = CliParser.Parse(ConfigSetArguments);

        Assert.True(result.IsSuccess);
        Assert.Equal(CliCommand.Config, result.Options!.Command);
        Assert.True(result.Options.IsJson);
        Assert.Equal("state.json", result.Options.ConfigFile);
        Assert.Equal(ConfigAction.Set, result.Options.Config!.Action);
        Assert.Equal("listener.port", result.Options.Config.Key);
        Assert.Equal("12987", result.Options.Config.Value);
    }

    [Fact]
    public void ServerParsesPortAndBindOverrides()
    {
        var result = CliParser.Parse(ServerArguments);

        Assert.True(result.IsSuccess);
        Assert.Equal(CliCommand.Server, result.Options!.Command);
        Assert.Equal(12987, result.Options.Server!.Port);
        Assert.Equal("0.0.0.0", result.Options.Server.Bind);
        Assert.Equal("https://relay.example.test", result.Options.SettingsPatch!.PublicUrl);
        Assert.Equal("relay.example.test", result.Options.SettingsPatch.Hostname);
        Assert.Equal("tailscale", result.Options.SettingsPatch.AutoDiscovery);
        Assert.Equal(600, result.Options.SettingsPatch.LeaseSeconds);
        Assert.Equal(12, result.Options.SettingsPatch.MaxRegistrations);
    }

    [Fact]
    public void ServerParsesInternalServiceNameWithoutPersistingIt()
    {
        var result = CliParser.Parse(["server", "--service-name", "custom-orelay"]);

        Assert.True(result.IsSuccess);
        Assert.Equal("custom-orelay", result.Options!.Server!.ServiceName);
        Assert.Null(result.Options.SettingsPatch);
    }

    [Fact]
    public void InitAcceptsSettingsOverridesWithoutRequiringEnvironmentState()
    {
        var result = CliParser.Parse(InitArguments);

        Assert.True(result.IsSuccess);
        Assert.Equal(CliCommand.Init, result.Options!.Command);
        Assert.Equal("run.json", result.Options.ConfigFile);
        Assert.Equal(12000, result.Options.SettingsPatch!.Port);
        Assert.Equal("localhost", result.Options.SettingsPatch.Bind);
    }

    [Fact]
    public void DoctorAndServiceParseTheirActions()
    {
        var doctor = CliParser.Parse(DoctorArguments);
        var service = CliParser.Parse(ServiceArguments);

        Assert.True(doctor.IsSuccess);
        Assert.True(doctor.Options!.Doctor!.Fix);
        Assert.Equal("custom-orelay", doctor.Options.Doctor.Name);
        Assert.True(service.IsSuccess);
        Assert.Equal(ServiceAction.Restart, service.Options!.Service!.Action);
        Assert.Equal("custom-orelay", service.Options.Service.Name);
    }

    [Theory]
    [InlineData("unknown", "unknown command")]
    [InlineData("config", "config requires")]
    [InlineData("server --port 0", "Setting 'port' must be between")]
    [InlineData("service reboot", "unknown service action")]
    public void InvalidCommandsReturnActionableErrors(string commandLine, string expectedError)
    {
        var result = CliParser.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.False(result.IsSuccess);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SubcommandHelpCarriesCommandAndTopicMetadata()
    {
        var result = CliParser.Parse(ServerHelpArguments);

        Assert.True(result.IsSuccess);
        Assert.Equal(CliCommand.Server, result.Options!.Command);
        Assert.Equal(CliCommand.Server, result.Options.Help!.Command);
        Assert.Null(result.Options.Help.Topic);
    }

    [Theory]
    [InlineData(CliCommand.Config, "config")]
    [InlineData(CliCommand.Service, "service")]
    public void RootSubcommandHelpWorksWithoutARequiredAction(CliCommand command, string commandName)
    {
        var result = CliParser.Parse([commandName, "--help"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(command, result.Options!.Command);
        Assert.Equal(command, result.Options.Help!.Command);
        Assert.Null(result.Options.Help.Topic);
    }

    private static readonly string[] ConfigSetArguments =
    ["--json", "config", "set", "listener.port", "12987", "--config-file", "state.json"];

    private static readonly string[] ServerArguments =
    [
        "server",
        "--port=12987",
        "--bind", "0.0.0.0",
        "--public-url", "https://relay.example.test",
        "--hostname", "relay.example.test",
        "--auto-discovery", "tailscale",
        "--lease-seconds", "600",
        "--max-registrations", "12",
    ];

    private static readonly string[] InitArguments =
    ["--config-file", "run.json", "init", "--port", "12000", "--bind", "localhost"];

    private static readonly string[] DoctorArguments = ["doctor", "--fix", "--name", "custom-orelay"];
    private static readonly string[] ServiceArguments = ["service", "restart", "--name", "custom-orelay"];
    private static readonly string[] ServerHelpArguments = ["server", "--help"];
}
