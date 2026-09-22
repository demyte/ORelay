using System.Diagnostics;
using System.Text.Json;

namespace ORelay.Discovery;

/// <summary>
/// Reads <c>tailscale status --json</c> on the host where the worktree runs.
/// The provider is deliberately separate from callback resolution so an
/// Aspire host can inject its own process boundary in tests or in a package.
/// </summary>
public sealed class TailscaleStatusProcessProvider : ITailscaleStatusProvider
{
    public TailscaleStatusProcessProvider(string executable = "tailscale")
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new ArgumentException("The Tailscale executable name must not be empty.", nameof(executable));
        }

        Executable = executable;
    }

    public string Executable { get; }

    public async Task<TailscaleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("status");
        process.StartInfo.ArgumentList.Add("--json");

        try
        {
            if (!process.Start())
            {
                return TailscaleStatusSnapshot.Unavailable("The Tailscale status process could not be started.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return TailscaleStatusSnapshot.Unavailable(
                "The Tailscale CLI is unavailable on this host.");
        }

        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var child = (Process)state!;
            try
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }, process);

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await standardOutputTask.ConfigureAwait(false);
            _ = await standardErrorTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                return TailscaleStatusSnapshot.Unavailable(
                    $"Tailscale status exited with code {process.ExitCode}.");
            }

            TailscaleStatusDocument? document;
            try
            {
                document = JsonSerializer.Deserialize(output, TailscaleJsonContext.Default.TailscaleStatusDocument);
            }
            catch (JsonException)
            {
                return TailscaleStatusSnapshot.Unavailable("Tailscale returned invalid status JSON.");
            }

            if (document?.Self is null)
            {
                return TailscaleStatusSnapshot.Unavailable("Tailscale status did not contain local machine details.");
            }

            if (!string.Equals(document.BackendState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return TailscaleStatusSnapshot.Unavailable(
                    $"Tailscale is not running (backend state: {document.BackendState ?? "unknown"}).");
            }

            return new TailscaleStatusSnapshot(
                Available: true,
                BackendState: document.BackendState,
                DnsName: document.Self.DnsName,
                HostName: document.Self.HostName,
                Addresses: document.Self.TailscaleIps ?? Array.Empty<string>());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return TailscaleStatusSnapshot.Unavailable("Tailscale status could not be read.");
        }
    }
}
