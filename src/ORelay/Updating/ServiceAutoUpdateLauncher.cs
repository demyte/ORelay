using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ORelay.Services;

namespace ORelay.Updating;

internal static class ServiceAutoUpdateLauncher
{
    private const string WorkerCommand = "__auto-update";

    internal static async Task<int> RunAsync(string executablePath, string configurationPath,
        string serviceName, CancellationToken cancellationToken = default)
    {
        var start = CreateStartInfo(executablePath, configurationPath, serviceName);
        using var process = Process.Start(start);
        if (process is null) return 3;

        // Cancellation ends the host's wait only. An accepted Windows process or
        // transient systemd service must finish its update after the relay stops.
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string configurationPath,
        string serviceName, bool? windows = null)
    {
        var onWindows = windows ?? OperatingSystem.IsWindows();
        if (!onWindows && !(windows is false || OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException("Automatic service updates require Windows or Linux systemd.");
        var validPaths = onWindows ?
            Path.IsPathFullyQualified(executablePath) && Path.IsPathFullyQualified(configurationPath) :
            executablePath.Length > 0 && executablePath[0] == '/' &&
            configurationPath.Length > 0 && configurationPath[0] == '/';
        if (!validPaths || !ServiceIdentity.IsValidName(serviceName))
            throw new ArgumentException("The auto-update target, configuration, or service name is invalid.");

        var executable = onWindows ? Path.GetFullPath(executablePath) : executablePath;
        var config = onWindows ? Path.GetFullPath(configurationPath) : configurationPath;

        ProcessStartInfo start;
        if (onWindows)
        {
            start = new ProcessStartInfo(executable);
        }
        else
        {
            // A transient service has its own systemd cgroup. A child in the
            // relay's cgroup would be killed when UpdateEngine stops the relay.
            start = new ProcessStartInfo("systemd-run");
            start.ArgumentList.Add("--unit=" + UnitName(config, serviceName));
            start.ArgumentList.Add("--wait");
            start.ArgumentList.Add("--collect");
            start.ArgumentList.Add("--service-type=exec");
            start.ArgumentList.Add("--expand-environment=no");
            start.ArgumentList.Add("--quiet");
            start.ArgumentList.Add("--property=RuntimeMaxSec=900s");
            start.ArgumentList.Add(executable);
        }

        start.ArgumentList.Add(WorkerCommand);
        start.ArgumentList.Add(config);
        start.ArgumentList.Add(serviceName);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WindowStyle = ProcessWindowStyle.Hidden;
        start.RedirectStandardInput = false;
        start.RedirectStandardOutput = false;
        start.RedirectStandardError = false;
        return start;
    }

    private static string UnitName(string config, string serviceName)
    {
        var identity = serviceName + "\n" + config;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "orelay-auto-update-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
