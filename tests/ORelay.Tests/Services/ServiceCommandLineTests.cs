using ORelay.Services;

namespace ORelay.Tests.Services;

public sealed class ServiceCommandLineTests
{
    [Fact]
    public void WindowsCommandLinePreservesExecutableAndConfigPathsWithSpaces()
    {
        // These are native Windows paths. ServiceRequest normalizes paths
        // according to the test host, so a Linux host must not reinterpret
        // them as relative POSIX paths.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var request = new ServiceRequest(
            @"C:\Program Files\ORelay\orelay.exe",
            @"C:\ProgramData\ORelay State\orelay.json");

        var command = ServiceCommandLine.BuildWindows(request);

        Assert.Equal(
            @"""C:\Program Files\ORelay\orelay.exe"" ""server"" ""--config-file"" ""C:\ProgramData\ORelay State\orelay.json""",
            command);
    }

    [Fact]
    public void WindowsCommandLineEscapesTrailingBackslashesAndQuotes()
    {
        var trailing = ServiceCommandLine.QuoteWindowsArgument(@"C:\path\");
        var quoted = ServiceCommandLine.QuoteWindowsArgument("a\"b");

        Assert.StartsWith("\"C:\\path", trailing, StringComparison.Ordinal);
        Assert.EndsWith("\\\\\"", trailing, StringComparison.Ordinal);
        Assert.Equal("\"a\\\"b\"", quoted);
    }

    [Fact]
    public void SystemdCommandLineEscapesPercentAndBackslash()
    {
        var request = new ServiceRequest(
            Path.Combine(Path.GetTempPath(), "ORelay", "orelay"),
            Path.Combine(Path.GetTempPath(), "ORelay State", "100%", "cash$state", "orelay.json"));

        var command = ServiceCommandLine.BuildSystemdExecStart(request);

        Assert.Contains("100%%", command, StringComparison.Ordinal);
        Assert.Contains("cash$$state", command, StringComparison.Ordinal);
        Assert.Contains("server", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceRequestNormalizesRelativePathsToAbsolutePaths()
    {
        var request = new ServiceRequest("orelay", Path.Combine("state", "orelay.json"));

        Assert.True(Path.IsPathFullyQualified(request.ExecutablePath));
        Assert.True(Path.IsPathFullyQualified(request.ConfigurationPath));
    }

    [Fact]
    public void CustomServiceNameDerivesAnIsolatedSystemdUnitPath()
    {
        var request = new ServiceRequest("orelay", "state/orelay.json", "ownedtestservice");

        Assert.EndsWith("ownedtestservice.service", request.UnitFilePath, StringComparison.Ordinal);
        Assert.Contains("--service-name", ServiceCommandLine.BuildSystemdExecStart(request), StringComparison.Ordinal);
    }

    [Fact]
    public void CustomWindowsServiceNameIsPersistedForTheServerHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var request = new ServiceRequest(
            @"C:\Program Files\ORelay\orelay.exe",
            @"C:\ProgramData\ORelay State\orelay.json",
            "Owned-Test");

        var command = ServiceCommandLine.BuildWindows(request);

        Assert.Contains("\"--service-name\" \"Owned-Test\"", command, StringComparison.Ordinal);
    }
}
