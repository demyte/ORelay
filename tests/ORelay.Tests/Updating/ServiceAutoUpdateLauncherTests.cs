using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class ServiceAutoUpdateLauncherTests
{
    [Fact]
    public void WindowsLaunchHasLiteralArgumentsAndNoInheritedRedirectedHandles()
    {
        var exe = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "orelay install", "orelay.exe"));
        var config = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "relay config $HOME", "settings.json"));

        var start = ServiceAutoUpdateLauncher.CreateStartInfo(exe, config, "relay_test", windows: true);

        Assert.Equal(exe, start.FileName);
        Assert.Equal(["__auto-update", config, "relay_test"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
    }

    [Fact]
    public void LinuxLaunchUsesStableIndependentTransientService()
    {
        var exe = "/opt/relay with space/orelay";
        var config = "/etc/relay with $HOME/settings.json";

        var first = ServiceAutoUpdateLauncher.CreateStartInfo(exe, config, "relay_test", windows: false);
        var second = ServiceAutoUpdateLauncher.CreateStartInfo(exe, config, "relay_test", windows: false);

        Assert.Equal("systemd-run", first.FileName);
        Assert.Equal(first.ArgumentList[0], second.ArgumentList[0]);
        Assert.StartsWith("--unit=orelay-auto-update-", first.ArgumentList[0]);
        Assert.Contains("--wait", first.ArgumentList);
        Assert.Contains("--collect", first.ArgumentList);
        Assert.Contains("--service-type=exec", first.ArgumentList);
        Assert.Contains("--expand-environment=no", first.ArgumentList);
        Assert.Contains("--property=RuntimeMaxSec=900s", first.ArgumentList);
        Assert.DoesNotContain("--scope", first.ArgumentList);
        Assert.Equal([exe, "__auto-update", config, "relay_test"], first.ArgumentList.TakeLast(4));
    }
}
