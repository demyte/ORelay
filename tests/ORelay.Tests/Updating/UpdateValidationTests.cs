using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class UpdateValidationTests
{
    [Theory]
    [InlineData("1.0.0-999999999999999999999", "1.0.0-1000000000000000000000")]
    [InlineData("1.0.0-999999999999999999999", "1.0.0-alpha")]
    [InlineData("1.0.0-1000000000000000000000", "1.0.0-1000000000000000000001")]
    public void PrereleaseComparison_HandlesNumericIdentifiersBeyondLongRange(string lower, string higher)
    {
        Assert.True(SemVersion.TryParse(lower, out var left));
        Assert.True(SemVersion.TryParse(higher, out var right));
        Assert.True(left!.Value.CompareTo(right!.Value) < 0);
        Assert.True(right.Value.CompareTo(left.Value) > 0);
    }

    [Theory]
    [InlineData("LICENSE")]
    [InlineData("orelay.pdb")]
    public void TarArchive_RejectsOversizedAllowedSidecarBeforeReadingItsBody(string entryName)
    {
        using var tarBytes = new MemoryStream();
        using (var writer = new TarWriter(tarBytes, TarEntryFormat.Ustar, leaveOpen: true))
        {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream([1]),
            });
        }

        var bytes = tarBytes.ToArray();
        var size = Encoding.ASCII.GetBytes(Convert.ToString(90L * 1024 * 1024 + 1, 8)!.PadLeft(11, '0') + "\0");
        size.CopyTo(bytes, 124);
        Array.Fill(bytes, (byte)' ', 148, 8);
        var checksum = bytes.Take(512).Sum(value => (int)value);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8)!.PadLeft(6, '0') + "\0 ").CopyTo(bytes, 148);

        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(bytes);

        var error = Assert.Throws<UpdateException>(() =>
            UpdateArchive.ExtractExecutable(archive.ToArray(), "linux-x64"));
        Assert.Equal(UpdateErrorCode.InvalidArchive, error.Code);
        Assert.Contains("oversized entry", error.Message, StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task VersionProbe_ReadsValidVersion()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var directory = Path.Combine(Path.GetTempPath(), "orelay-version-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var script = Path.Combine(directory, "version-probe");
            File.WriteAllText(script, "#!/bin/sh\nprintf '{\"version\":\"1.2.3\"}'\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Assert.Equal("1.2.3", await new NativeUpdateRuntime().ReadExecutableVersionAsync(script, CancellationToken.None));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [UnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedVersionProbeOutput_StopsItsChildProcess(bool stderr)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var directory = Path.Combine(Path.GetTempPath(), "orelay-version-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidFile = Path.Combine(directory, "pid");
        Task<string?>? probe = null;
        try
        {
            var script = Path.Combine(directory, "version-probe");
            var redirect = stderr ? " >&2" : string.Empty;
            File.WriteAllText(script,
                $"#!/bin/sh\nprintf '%s' \"$$\" > '{pidFile}'\ni=0\nwhile [ $i -lt 200 ]; do printf '1234567890'{redirect}; i=$((i + 1)); done\nexec sleep 60\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            probe = new NativeUpdateRuntime().ReadExecutableVersionAsync(script, CancellationToken.None);
            var pid = await WaitForPidAsync(pidFile);
            Assert.Null(await probe.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile),
                    System.Globalization.CultureInfo.InvariantCulture, out var processId))
            {
                try
                {
                    using var child = Process.GetProcessById(processId);
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
                catch (ArgumentException) { } // The child has already exited.
            }

            Directory.Delete(directory, recursive: true);
        }
    }

    [UnixFact]
    public async Task CancelledVersionProbe_StopsItsChildProcess()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var directory = Path.Combine(Path.GetTempPath(), "orelay-version-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidFile = Path.Combine(directory, "pid");
        using var cancellation = new CancellationTokenSource();
        Task<string?>? probe = null;
        try
        {
            var script = Path.Combine(directory, "version-probe");
            File.WriteAllText(script, $"#!/bin/sh\nprintf '%s' \"$$\" > '{pidFile}'\nexec sleep 60\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            probe = new NativeUpdateRuntime().ReadExecutableVersionAsync(script, cancellation.Token);
            var pid = await WaitForPidAsync(pidFile);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally
        {
            try
            {
                cancellation.Cancel();
                if (probe is not null)
                {
                    try { await probe.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                    catch (TimeoutException) { }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile),
                            System.Globalization.CultureInfo.InvariantCulture, out var processId))
                    {
                        try
                        {
                            using var child = Process.GetProcessById(processId);
                            if (!child.HasExited)
                            {
                                child.Kill(entireProcessTree: true);
                                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                            }
                        }
                        catch (ArgumentException) { } // The child has already exited.
                    }
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
        }
    }

    private static async Task<int> WaitForPidAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path),
                    System.Globalization.CultureInfo.InvariantCulture, out var pid)) return pid;
            await Task.Delay(20, timeout.Token);
        }
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix executable script.";
    }
}

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Requires a Unix executable script.";
    }
}
