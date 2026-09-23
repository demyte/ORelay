using System.Text;
using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class InstalledExecutableMetadataTests
{
    private const string ReleaseVersion = "0.1.2+ca87ea771c3b3fd748bcc0ac141127a517cfbb50";

    [Theory]
    [InlineData("pe")]
    [InlineData("elf")]
    [InlineData("macho")]
    public void ReadsVersionFromNativeExecutableWithoutRunningIt(string format)
    {
        using var file = new TemporaryFile(CreateExecutable(format, ReleaseVersion));
        Assert.Equal(ReleaseVersion, InstalledExecutableMetadata.ReadVersion(file.Path));
    }

    [Fact]
    public void ReadsLongPrereleaseVersionWithTwoByteNativeLength()
    {
        var version = "0.1.3-dev." + new string('a', 100) +
            "+8edb440f5f86d0768545cda73c8de88f6f4d60d5";
        using var file = new TemporaryFile(CreateExecutable("elf", version, fileVersion: "0.1.3.0"));
        Assert.Equal(version, InstalledExecutableMetadata.ReadVersion(file.Path));
    }

    [Fact]
    public void FindsMetadataFrameAcrossScannerBufferBoundary()
    {
        using var file = new TemporaryFile(CreateExecutable("elf", ReleaseVersion,
            paddingLength: 64 * 1024 - 5));
        Assert.Equal(ReleaseVersion, InstalledExecutableMetadata.ReadVersion(file.Path));
    }

    [Fact]
    public void RejectsUnknownExecutableAndMissingMetadata()
    {
        using var unknown = new TemporaryFile(CreateExecutable("unknown", ReleaseVersion));
        using var missing = new TemporaryFile([0x7f, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0]);
        Assert.Null(InstalledExecutableMetadata.ReadVersion(unknown.Path));
        Assert.Null(InstalledExecutableMetadata.ReadVersion(missing.Path));
    }

    [Fact]
    public void RejectsAmbiguousIdentityAndCorruptVersion()
    {
        var executable = CreateExecutable("elf", ReleaseVersion);
        using var duplicate = new TemporaryFile([.. executable, .. executable]);
        using var mismatch = new TemporaryFile(CreateExecutable("elf", ReleaseVersion, fileVersion: "0.1.3.0"));
        using var unrelated = new TemporaryFile(CreateExecutable("elf", ReleaseVersion,
            repository: "https://github.com/other/ORelay"));
        Assert.Null(InstalledExecutableMetadata.ReadVersion(duplicate.Path));
        Assert.Null(InstalledExecutableMetadata.ReadVersion(mismatch.Path));
        Assert.Null(InstalledExecutableMetadata.ReadVersion(unrelated.Path));
    }

    [Fact]
    public void IgnoresIncompleteMarkerInExecutableCodeData()
    {
        var executable = CreateExecutable("elf", ReleaseVersion);
        using var file = new TemporaryFile([.. executable, 0, 0, 0x1e, .. "James Summerton"u8]);
        Assert.Equal(ReleaseVersion, InstalledExecutableMetadata.ReadVersion(file.Path));
    }

    [Fact]
    public void RejectsTruncatedNativeLength()
    {
        var executable = CreateExecutable("macho", ReleaseVersion);
        var versionLengthOffset = Array.IndexOf(executable, (byte)'0', 44) + "0.1.2.0".Length;
        executable[versionLengthOffset] = 0xff;
        using var file = new TemporaryFile(executable);
        Assert.Null(InstalledExecutableMetadata.ReadVersion(file.Path));
    }

    [Fact]
    public void RejectsOversizedFileBeforeScanning()
    {
        using var file = new TemporaryFile(CreateExecutable("elf", ReleaseVersion));
        using (var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Write))
            stream.SetLength(90L * 1024 * 1024 + 1);
        Assert.Null(InstalledExecutableMetadata.ReadVersion(file.Path));
    }

    private static byte[] CreateExecutable(string format, string version, string fileVersion = "0.1.2.0",
        string repository = "https://github.com/demyte/ORelay", int paddingLength = 40)
    {
        using var bytes = new MemoryStream();
        bytes.Write(format switch
        {
            "pe" => [0x4d, 0x5a, 0x90, 0],
            "elf" => [0x7f, 0x45, 0x4c, 0x46],
            "macho" => [0xcf, 0xfa, 0xed, 0xfe],
            _ => [0, 0, 0, 0],
        });
        bytes.Write(new byte[paddingLength]);
        bytes.Write([0, 0]);
        WriteNativeString(bytes, "James Summerton");
        WriteNativeString(bytes, fileVersion);
        WriteNativeString(bytes, version);
        WriteNativeString(bytes, repository);
        return bytes.ToArray();
    }

    private static void WriteNativeString(Stream output, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length < 128)
        {
            output.WriteByte((byte)(bytes.Length << 1));
        }
        else
        {
            var encoded = (bytes.Length << 2) | 1;
            output.WriteByte((byte)encoded);
            output.WriteByte((byte)(encoded >> 8));
        }
        output.Write(bytes);
    }

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "orelay-installed-metadata-" + Guid.NewGuid().ToString("N"));

        public TemporaryFile(byte[] bytes) => File.WriteAllBytes(Path, bytes);
        public void Dispose() => File.Delete(Path);
    }
}
