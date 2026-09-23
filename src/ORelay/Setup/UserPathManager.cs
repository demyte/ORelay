using System.Text;

namespace ORelay.Setup;

internal interface IUserPathManager
{
    UserPathPlan Inspect(string installationDirectory);
    bool Apply(UserPathPlan plan);
}

internal sealed record UserPathPlan(string Directory, bool AlreadyConfigured, string Description);

internal sealed class UserPathManager : IUserPathManager
{
    private readonly bool _windows;
    private readonly string _home;
    private readonly string? _shell;
    private readonly string? _zdotdir;
    private readonly string? _xdgConfigHome;
    private readonly Func<string?> _readUserPath;
    private readonly Action<string> _writeUserPath;

    internal UserPathManager(bool? isWindows = null, string? homeDirectory = null, string? shell = null,
        string? zdotdir = null, string? xdgConfigHome = null,
        Func<string?>? readUserPath = null, Action<string>? writeUserPath = null)
    {
        _windows = isWindows ?? OperatingSystem.IsWindows();
        _home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _shell = shell ?? Environment.GetEnvironmentVariable("SHELL");
        _zdotdir = zdotdir ?? Environment.GetEnvironmentVariable("ZDOTDIR");
        _xdgConfigHome = xdgConfigHome ?? Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        _readUserPath = readUserPath ?? (() => Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
        _writeUserPath = writeUserPath ?? (value => Environment.SetEnvironmentVariable("PATH", value, EnvironmentVariableTarget.User));
    }

    public UserPathPlan Inspect(string installationDirectory)
    {
        ValidateDirectory(installationDirectory);
        if (_windows)
        {
            var windowsConfigured = ContainsWindowsPath(_readUserPath(), installationDirectory);
            return new UserPathPlan(installationDirectory, windowsConfigured,
                windowsConfigured
                    ? "The installation directory is already in your Windows user PATH. Open a new terminal to use it."
                    : "Prepend the installation directory to your Windows user PATH. Open a new terminal after setup.");
        }

        var files = ProfileFiles();
        var snippet = ShellSnippet(installationDirectory);
        var configuredFiles = files.Where(path => File.Exists(path) && File.ReadAllText(path).Contains(snippet, StringComparison.Ordinal)).ToArray();
        var configured = configuredFiles.Length == files.Count;
        var targets = configured ? configuredFiles : files.Except(configuredFiles, StringComparer.Ordinal).ToArray();
        var action = configured ? "Already configured in " : "Add the installation directory to ";
        return new UserPathPlan(installationDirectory, configured,
            action + string.Join(", ", targets) + ". Open a new terminal after setup.");
    }

    public bool Apply(UserPathPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateDirectory(plan.Directory);
        if (_windows)
        {
            var current = _readUserPath();
            if (ContainsWindowsPath(current, plan.Directory)) return false;
            _writeUserPath(plan.Directory + (string.IsNullOrEmpty(current) ? "" : ";" + current));
            return true;
        }

        var files = ProfileFiles();
        var snippet = ShellSnippet(plan.Directory);
        var changed = false;
        foreach (var file in files)
        {
            if (File.Exists(file) && File.ReadAllText(file).Contains(snippet, StringComparison.Ordinal)) continue;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var needsNewline = false;
            if (File.Exists(file))
            {
                using var existing = File.OpenRead(file);
                if (existing.Length > 0)
                {
                    existing.Seek(-1, SeekOrigin.End);
                    needsNewline = existing.ReadByte() != '\n';
                }
            }
            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.None);
            stream.Write(Encoding.UTF8.GetBytes((needsNewline ? "\n" : "") + snippet));
            changed = true;
        }
        return changed;
    }

    private void ValidateDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!(_windows ? IsAbsoluteWindowsDirectory(directory) : directory.StartsWith('/')) ||
            directory.IndexOfAny(['\0', '\r', '\n']) >= 0 || directory.Contains(_windows ? ';' : ':', StringComparison.Ordinal))
            throw new ArgumentException("The installation directory must be an absolute path without newlines or PATH separators.", nameof(directory));
    }

    private static bool IsAbsoluteWindowsDirectory(string directory)
        => directory.Length >= 3 && char.IsLetter(directory[0]) && directory[1] == ':' && directory[2] is '\\' or '/' ||
           directory.StartsWith(@"\\", StringComparison.Ordinal) && directory.Length > 2;

    private static bool ContainsWindowsPath(string? userPath, string directory)
    {
        if (string.IsNullOrEmpty(userPath)) return false;
        var expected = directory.TrimEnd('\\', '/');
        return userPath.Split(';').Any(part =>
            string.Equals(part.Trim().Trim('"').TrimEnd('\\', '/'), expected, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> ProfileFiles()
    {
        if (string.IsNullOrWhiteSpace(_home))
            throw new InvalidOperationException("Cannot find your home directory to update the shell profile.");
        var shell = Path.GetFileName(_shell?.TrimEnd('/', '\\'));
        return shell switch
        {
            "sh" => [Path.Combine(_home, ".profile")],
            "bash" => [Path.Combine(_home, ".bashrc"), BashLoginProfile()],
            "zsh" => [Path.Combine(string.IsNullOrWhiteSpace(_zdotdir) ? _home : _zdotdir, ".zshrc")],
            "fish" => [Path.Combine(string.IsNullOrWhiteSpace(_xdgConfigHome) ? Path.Combine(_home, ".config") : _xdgConfigHome,
                "fish", "conf.d", "orelay.fish")],
            _ => throw new InvalidOperationException($"The shell '{_shell ?? "unknown"}' is not supported for automatic PATH setup. Add the installation directory to your shell's user PATH manually.")
        };
    }

    private string BashLoginProfile()
    {
        foreach (var name in new[] { ".bash_profile", ".bash_login", ".profile" })
        {
            var file = Path.Combine(_home, name);
            if (File.Exists(file)) return file;
        }
        return Path.Combine(_home, ".profile");
    }

    private string ShellSnippet(string directory)
    {
        if (Path.GetFileName(_shell?.TrimEnd('/', '\\')) == "fish")
        {
            var quoted = "'" + directory.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
            return "# ORelay user PATH\nif not contains -- " + quoted + " $PATH\n    set -gx PATH " + quoted + " $PATH\nend\n";
        }
        var literal = "'" + directory.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return "# ORelay user PATH\norelay_user_path=" + literal +
            "\ncase \":$PATH:\" in\n    *:\"$orelay_user_path\":*) ;;\n    *) PATH=\"$orelay_user_path${PATH:+:$PATH}\"; export PATH ;;\nesac\n";
    }
}
