using System.Text;
using ORelay.Setup;

namespace ORelay.Tests.Setup;

public sealed class UserPathManagerTests
{
    [Fact]
    public void WindowsPrependsOnlyTheUserPathAndPreservesExistingEntries()
    {
        var userPath = @"C:\Tools;C:\Other";
        var writes = 0;
        var manager = new UserPathManager(isWindows: true,
            readUserPath: () => userPath,
            writeUserPath: value => { userPath = value; writes++; });

        var plan = manager.Inspect(@"C:\Program Files\ORelay");
        Assert.False(plan.AlreadyConfigured);
        Assert.Contains("Windows user PATH", plan.Description);
        Assert.Equal(0, writes);

        Assert.True(manager.Apply(plan));
        Assert.Equal(@"C:\Program Files\ORelay;C:\Tools;C:\Other", userPath);
        Assert.False(manager.Apply(plan));
        Assert.Equal(1, writes);
        Assert.True(manager.Inspect(@"C:\Program Files\ORelay\").AlreadyConfigured);
    }

    [Fact]
    public void ExistingWindowsEntryIsRecognizedWithoutChangingUserPath()
    {
        var userPath = @"C:\Tools;C:\PROGRAM FILES\ORELAY\;C:\Other";
        var manager = new UserPathManager(isWindows: true,
            readUserPath: () => userPath,
            writeUserPath: _ => throw new InvalidOperationException("Unexpected user PATH write"));

        var plan = manager.Inspect(@"C:\Program Files\ORelay");
        Assert.True(plan.AlreadyConfigured);
        Assert.False(manager.Apply(plan));
    }

    [Fact]
    public void ShProfileKeepsOriginalBytesAndQuotesLiteralDirectory()
    {
        using var run = new TempHome();
        var profile = Path.Combine(run.Home, ".profile");
        var original = Encoding.UTF8.GetBytes("# keep this\r\nexport EDITOR=vi");
        File.WriteAllBytes(profile, original);
        var manager = run.ForShell("sh");
        var directory = "/opt/Relay's $HOME bin";

        var plan = manager.Inspect(directory);
        Assert.False(plan.AlreadyConfigured);
        Assert.Contains(profile, plan.Description);
        Assert.Equal(original, File.ReadAllBytes(profile));
        Assert.True(manager.Apply(plan));
        var result = File.ReadAllBytes(profile);
        Assert.Equal(original, result[..original.Length]);
        var added = Encoding.UTF8.GetString(result[original.Length..]);
        Assert.StartsWith("\n# ORelay user PATH", added);
        Assert.Contains("'/opt/Relay'\\''s $HOME bin'", added);
        Assert.False(manager.Apply(plan));
        Assert.Equal(result, File.ReadAllBytes(profile));
        Assert.True(manager.Inspect(directory).AlreadyConfigured);
    }

    [Fact]
    public void BashUpdatesRcAndFirstExistingLoginProfile()
    {
        using var run = new TempHome();
        var login = Path.Combine(run.Home, ".bash_login");
        File.WriteAllText(login, "# login\n");
        File.WriteAllText(Path.Combine(run.Home, ".profile"), "# fallback\n");
        var manager = run.ForShell("bash");

        Assert.True(manager.Apply(manager.Inspect("/opt/orelay bin")));
        Assert.Contains("# ORelay user PATH", File.ReadAllText(Path.Combine(run.Home, ".bashrc")));
        Assert.Contains("# ORelay user PATH", File.ReadAllText(login));
        Assert.Equal("# fallback\n", File.ReadAllText(Path.Combine(run.Home, ".profile")));
        Assert.True(manager.Inspect("/opt/orelay bin").AlreadyConfigured);
    }

    [Fact]
    public void ZshUsesZdotdirAndFishUsesXdgConfigHome()
    {
        using var run = new TempHome();
        var zdotdir = Path.Combine(run.Home, "custom zsh");
        var xdg = Path.Combine(run.Home, "custom config");
        var zsh = run.ForShell("zsh", zdotdir: zdotdir);
        var fish = run.ForShell("fish", xdgConfigHome: xdg);

        Assert.True(zsh.Apply(zsh.Inspect("/opt/Relay's $HOME")));
        Assert.True(File.Exists(Path.Combine(zdotdir, ".zshrc")));
        Assert.True(fish.Apply(fish.Inspect("/opt/Relay's $HOME")));
        var fishFile = Path.Combine(xdg, "fish", "conf.d", "orelay.fish");
        var script = File.ReadAllText(fishFile);
        Assert.Contains("if not contains -- '/opt/Relay\\'s $HOME' $PATH", script);
        Assert.Contains("set -gx PATH '/opt/Relay\\'s $HOME' $PATH", script);
        Assert.False(fish.Apply(fish.Inspect("/opt/Relay's $HOME")));
    }

    [Fact]
    public void UnsupportedShellAndUnsafeDirectoriesFailBeforeWriting()
    {
        using var run = new TempHome();
        var unsupported = run.ForShell("tcsh");
        Assert.Contains("not supported", Assert.Throws<InvalidOperationException>(() => unsupported.Inspect("/opt/orelay")).Message);

        var sh = run.ForShell("sh");
        Assert.Throws<ArgumentException>(() => sh.Inspect("/opt/bad\npath"));
        Assert.Throws<ArgumentException>(() => sh.Inspect("/opt/bad:path"));
        Assert.False(File.Exists(Path.Combine(run.Home, ".profile")));
    }

    private sealed class TempHome : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "orelay-user-path-tests", Guid.NewGuid().ToString("N"));

        public TempHome() => System.IO.Directory.CreateDirectory(Home);

        public UserPathManager ForShell(string shell, string? zdotdir = null, string? xdgConfigHome = null)
            => new(isWindows: false, homeDirectory: Home, shell: "/bin/" + shell,
                zdotdir: zdotdir, xdgConfigHome: xdgConfigHome,
                readUserPath: () => throw new InvalidOperationException("Unexpected Windows PATH read"),
                writeUserPath: _ => throw new InvalidOperationException("Unexpected Windows PATH write"));

        public void Dispose() => System.IO.Directory.Delete(Home, recursive: true);
    }
}
