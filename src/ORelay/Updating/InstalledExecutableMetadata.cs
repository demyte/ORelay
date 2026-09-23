using System.Globalization;
using System.Text;

namespace ORelay.Updating;

/// <summary>Reads the version of an existing Native AOT executable without starting it.</summary>
internal static class InstalledExecutableMetadata
{
    private const long MaxExecutableBytes = 90 * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private const int MaxFrameBytes = 512;
    private static readonly byte[] CompanyMarker = [0, 0, 0x1e, .. "James Summerton"u8];
    private const string Repository = "https://github.com/demyte/ORelay";

    public static string? ReadVersion(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 4 or > MaxExecutableBytes || !HasNativeHeader(stream)) return null;

            var buffer = new byte[BufferSize + CompanyMarker.Length - 1];
            var carry = 0;
            var filePosition = 0L;
            string? version = null;
            var markers = 0;
            while (true)
            {
                var read = RandomAccess.Read(stream.SafeFileHandle,
                    buffer.AsSpan(carry, BufferSize), filePosition);
                if (read == 0) break;
                var count = carry + read;
                var windowStart = filePosition - carry;
                for (var index = 0; index <= count - CompanyMarker.Length; index++)
                {
                    if (!buffer.AsSpan(index, CompanyMarker.Length).SequenceEqual(CompanyMarker)) continue;
                    var candidate = ReadFrame(stream, windowStart + index);
                    // The scanner's own marker can also occur in native read-only data.
                    // Only complete identity/version frames count as metadata.
                    if (candidate is null) continue;
                    if (++markers != 1) return null;
                    version = candidate;
                }

                carry = Math.Min(count, CompanyMarker.Length - 1);
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
                filePosition += read;
            }

            return markers == 1 ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool HasNativeHeader(FileStream stream)
    {
        Span<byte> header = stackalloc byte[4];
        if (RandomAccess.Read(stream.SafeFileHandle, header, 0) != header.Length) return false;
        return header.StartsWith("MZ"u8) ||
            (header[0] == 0x7f && header[1] == (byte)'E' &&
                header[2] == (byte)'L' && header[3] == (byte)'F') ||
            (header[0] == 0xcf && header[1] == 0xfa &&
                header[2] == 0xed && header[3] == 0xfe);
    }

    private static string? ReadFrame(FileStream stream, long offset)
    {
        Span<byte> frame = stackalloc byte[MaxFrameBytes];
        var count = RandomAccess.Read(stream.SafeFileHandle, frame, offset);
        var data = frame[..count];
        if (!data.StartsWith(CompanyMarker)) return null;
        var cursor = CompanyMarker.Length;
        if (!TryReadString(data, ref cursor, 32, out var fileVersion) ||
            !TryReadString(data, ref cursor, 160, out var productVersion) ||
            !TryReadString(data, ref cursor, Repository.Length, out var repository) ||
            repository != Repository ||
            !SemVersion.TryParse(productVersion, out var parsed)) return null;

        var semanticVersion = parsed!.Value;
        var expectedFileVersion = string.Create(CultureInfo.InvariantCulture,
            $"{semanticVersion.Major}.{semanticVersion.Minor}.{semanticVersion.Patch}.0");
        return fileVersion == expectedFileVersion ? productVersion : null;
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, ref int cursor, int maxLength, out string value)
    {
        value = string.Empty;
        if (!TryReadUnsigned(data, ref cursor, out var length) || length == 0 || length > maxLength ||
            length > data.Length - cursor) return false;
        var bytes = data.Slice(cursor, (int)length);
        if (bytes.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7e) >= 0) return false;
        value = Encoding.ASCII.GetString(bytes);
        cursor += (int)length;
        return true;
    }

    // Native Format unsigned integers use low-bit tags to select 1-5 bytes.
    // See dotnet/runtime docs/design/coreclr/botr/readytorun-format.md, "Integer encoding".
    private static bool TryReadUnsigned(ReadOnlySpan<byte> data, ref int cursor, out uint value)
    {
        value = 0;
        if (cursor >= data.Length) return false;
        var first = data[cursor++];
        if ((first & 1) == 0) { value = (uint)first >> 1; return true; }
        var following = (first & 3) == 1 ? 1 : (first & 7) == 3 ? 2 :
            (first & 15) == 7 ? 3 : first == 15 ? 4 : -1;
        if (following < 0 || following > data.Length - cursor) return false;
        if (following == 4)
        {
            value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor, 4));
        }
        else
        {
            uint packed = first;
            for (var index = 0; index < following; index++)
                packed |= (uint)data[cursor + index] << (8 * (index + 1));
            value = packed >> (following + 1);
        }
        cursor += following;
        return true;
    }
}
