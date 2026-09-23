using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ORelay.Updating;

internal readonly record struct SemVersion(int Major, int Minor, int Patch, string? Prerelease, string Original)
    : IComparable<SemVersion>
{
    private static readonly Regex Pattern = new(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);

    public static bool TryParse(string value, out SemVersion? version)
    {
        version = null;
        var match = Pattern.Match(value);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch)) return false;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre is not null && pre.Split('.').Any(part => part.Length == 0 ||
            part.All(char.IsDigit) && part.Length > 1 && part[0] == '0')) return false;
        version = new SemVersion(major, minor, patch, pre, value.Split('+')[0]);
        return true;
    }

    public int CompareTo(SemVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var lnum = left[index].All(char.IsDigit);
            var rnum = right[index].All(char.IsDigit);
            var comparison = lnum && rnum ?
                left[index].Length.CompareTo(right[index].Length) : lnum ? -1 : rnum ? 1 : 0;
            if (comparison == 0) comparison = string.CompareOrdinal(left[index], right[index]);
            if (comparison != 0) return comparison;
        }

        return left.Length.CompareTo(right.Length);
    }
}

public interface IUpdateRuntime
{
    string? ProcessPath { get; }
    bool IsNative { get; }
    string? RuntimeIdentifier { get; }
    string CurrentVersion { get; }
    string? ReadInstalledVersion(string path);
    Task<string?> ReadExecutableVersionAsync(string path, CancellationToken cancellationToken);
}

public sealed class NativeUpdateRuntime : IUpdateRuntime
{
    private const int MaxProbeOutputChars = 1024;

    public string? ProcessPath => Environment.ProcessPath;
    public bool IsNative => !RuntimeFeature.IsDynamicCodeSupported &&
        string.Equals(Path.GetFileNameWithoutExtension(ProcessPath), "orelay", StringComparison.OrdinalIgnoreCase);
    public string CurrentVersion => ORelay.Cli.CliApplication.Version;
    public string? ReadInstalledVersion(string path) => InstalledExecutableMetadata.ReadVersion(path);

    public string? RuntimeIdentifier
    {
        get
        {
            var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" :
                OperatingSystem.IsMacOS() ? "osx" : null;
            var architecture = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => null,
            };
            return os is null || architecture is null ? null : $"{os}-{architecture}";
        }
    }

    public async Task<string?> ReadExecutableVersionAsync(string path, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            ArgumentList = { "--version", "--json" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        });
        if (process is null) return null;
        var outputLimitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = ReadProbeOutputAsync(process.StandardOutput, capture: true, outputLimitReached, cancellationToken);
        var stderr = ReadProbeOutputAsync(process.StandardError, capture: false, outputLimitReached, cancellationToken);
        try
        {
            var completion = Task.WhenAll(
                process.WaitForExitAsync(cancellationToken), stdout, stderr)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (await Task.WhenAny(completion, outputLimitReached.Task) == outputLimitReached.Task)
            {
                await StopProbeAsync(process);
                return null;
            }

            await completion;
        }
        catch (TimeoutException)
        {
            await StopProbeAsync(process);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopProbeAsync(process);
            throw;
        }

        var output = await stdout;
        if (process.ExitCode != 0 || output is null || await stderr is null) return null;
        try
        {
            using var json = JsonDocument.Parse(output);
            return json.RootElement.GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadProbeOutputAsync(
        StreamReader reader, bool capture, TaskCompletionSource outputLimitReached,
        CancellationToken cancellationToken)
    {
        var buffer = new char[256];
        var output = capture ? new StringBuilder() : null;
        var length = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output?.ToString() ?? string.Empty;
            length += read;
            if (length > MaxProbeOutputChars)
            {
                outputLimitReached.TrySetResult();
                return null;
            }

            output?.Append(buffer, 0, read);
        }
    }

    private static async Task StopProbeAsync(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { return; } // The process exited before Kill.
        await process.WaitForExitAsync(CancellationToken.None);
    }
}

internal static class UpdateArchive
{
    private const long MaxExecutableBytes = 90 * 1024 * 1024;
    private const long MaxTarExpandedBytes = 3 * MaxExecutableBytes;

    public static byte[] ExtractExecutable(byte[] bytes, string rid)
    {
        var expected = rid.StartsWith("win-", StringComparison.Ordinal) ? "orelay.exe" : "orelay";
        byte[]? executable = null;
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            if (rid.StartsWith("win-", StringComparison.Ordinal))
            {
                using var zip = new ZipArchive(source, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    var name = ValidateName(entry.FullName, expected);
                    if (IsZipLink(entry) || entry.Length > MaxExecutableBytes)
                        throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive has an unsafe entry.");
                    if (name == expected)
                    {
                        if (executable is not null) throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive has duplicate executables.");
                        using var input = entry.Open();
                        executable = ReadLimited(input);
                    }
                }
            }
            else
            {
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                long expandedBytes = 0;
                TarEntry? entry;
                while ((entry = tar.GetNextEntry()) is not null)
                {
                    var name = ValidateName(entry.Name, expected);
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                        throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive has a link or unsafe entry.");
                    if (entry.Length > MaxExecutableBytes)
                        throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive has an oversized entry.");
                    if (entry.Length > MaxTarExpandedBytes - expandedBytes)
                        throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive exceeds the expanded size limit.");
                    expandedBytes += entry.Length;
                    if (name == expected)
                    {
                        if (executable is not null || entry.DataStream is null)
                            throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive has duplicate or empty executables.");
                        executable = ReadLimited(entry.DataStream);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive could not be read.");
        }

        return executable is { Length: > 0 } ? executable :
            throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive is missing its executable.");
    }

    private static string ValidateName(string name, string expected)
    {
        var normalized = name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;
        if (normalized is "" or "." or "./") return normalized;
        if (normalized.StartsWith('/') || normalized.Contains('\\') || normalized.Contains("../", StringComparison.Ordinal) ||
            normalized.Contains('/')) throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive contains a path outside its root.");
        if (normalized is not ("LICENSE" or "LICENSE/" or "orelay.pdb" or "orelay.exe.pdb") && normalized != expected)
            throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release archive contains an unexpected entry.");
        return normalized;
    }

    private static bool IsZipLink(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static byte[] ReadLimited(Stream input)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = input.Read(buffer);
            if (count == 0) return output.ToArray();
            if (output.Length + count > MaxExecutableBytes)
                throw new UpdateException(UpdateErrorCode.InvalidArchive, "Release executable exceeds the size limit.");
            output.Write(buffer, 0, count);
        }
    }
}
