using System.Text;
using ORelay.Cli;
using ORelay.Configuration;
using ORelay.Diagnostics;
using ORelay.Services;

namespace ORelay.Tests.Services;

public sealed class ServiceCommandTests
{
    [Fact]
    public async Task UnsupportedCliCommandReturnsUsageErrorWithoutManagerAccess()
    {
        var output = new StringWriter(new StringBuilder());
        var error = new StringWriter(new StringBuilder());

        var exitCode = await ServiceCommand.ExecuteAsync(
            new CliOptions(CliCommand.Server, false, null),
            output,
            error);

        Assert.Equal(CliExitCodes.UsageError, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("unsupported CLI command", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoctorServiceCheckSkipsAnUninstalledOwnedPlatformService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backend = new FakeWindowsServiceBackend();
        var check = new ServiceDoctorCheck(
            configurationPath: Path.Combine(Path.GetTempPath(), "orelay-doctor", "orelay.json"),
            factoryOptions: new ServiceManagerFactoryOptions { WindowsBackend = backend });

        var result = await check.CheckAsync(RelayConfigurationDefaults.Settings);

        Assert.NotNull(result);
        Assert.Equal(DoctorCheckStatus.Skipped, result!.Status);
    }

    private sealed class FakeWindowsServiceBackend : IWindowsServiceBackend
    {
        public WindowsServiceQueryResult Query(string serviceName) => WindowsServiceQueryResult.NotFound();

        public WindowsServiceActionResult Create(string serviceName, string displayName, string binaryPathName) => WindowsServiceActionResult.Success();

        public WindowsServiceActionResult Start(string serviceName) => WindowsServiceActionResult.Success();

        public WindowsServiceActionResult StopService(string serviceName) => WindowsServiceActionResult.Success();

        public WindowsServiceActionResult Delete(string serviceName) => WindowsServiceActionResult.Success();
    }
}
