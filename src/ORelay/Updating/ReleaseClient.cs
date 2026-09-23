using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ORelay.Updating;

internal sealed record ReleaseAsset(string Name, long Id, long Size);
internal sealed record ReleaseInfo(string Version, ReleaseAsset Archive, ReleaseAsset Checksum);

internal sealed class ReleaseClient
{
    private const long MaxArchiveBytes = 100 * 1024 * 1024;
    private const long MaxChecksumBytes = 1024;
    private readonly HttpClient _http;
    private readonly string? _token;

    public ReleaseClient(HttpClient? http = null, string? token = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _token = token ?? Environment.GetEnvironmentVariable("GH_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? ReadGhToken();
    }

    public async Task<ReleaseInfo> GetLatestAsync(string rid, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await GetAsync(new Uri("https://api.github.com/repos/demyte/ORelay/releases/latest"),
            "application/vnd.github+json", timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(UpdateErrorCode.ReleaseUnavailable,
                $"GitHub release lookup failed with HTTP {(int)response.StatusCode}.");
        }

        byte[] json;
        try { json = await ReadBoundedAsync(response, 1024 * 1024, timeout.Token); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        { throw new UpdateException(UpdateErrorCode.ReleaseUnavailable, "GitHub release lookup failed or timed out."); }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            if (tag is null || !tag.StartsWith('v') || tag.Contains('+') || !SemVersion.TryParse(tag[1..], out var version) ||
                version!.Value.Prerelease is not null ||
                root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean() ||
                root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                throw new UpdateException(UpdateErrorCode.InvalidRelease, "The latest GitHub release has an invalid stable tag.");
            }

            var archiveName = $"orelay-{version!.Value.Original}-{rid}" + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
            var assets = root.GetProperty("assets").EnumerateArray().Select(ParseAsset).ToArray();
            var archive = SingleAsset(assets, archiveName);
            var checksum = SingleAsset(assets, archiveName + ".sha256");
            if (archive.Size <= 0 || archive.Size > MaxArchiveBytes || checksum.Size <= 0 || checksum.Size > MaxChecksumBytes)
            {
                throw new UpdateException(UpdateErrorCode.InvalidRelease, "Release asset size is invalid.");
            }

            return new ReleaseInfo(version.Value.Original, archive, checksum);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, "GitHub release metadata is malformed.");
        }
    }

    public async Task<byte[]> DownloadVerifiedArchiveAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        var checksum = await DownloadAssetAsync(release.Checksum, MaxChecksumBytes, cancellationToken);
        var checksumText = System.Text.Encoding.ASCII.GetString(checksum).Trim();
        var match = Regex.Match(checksumText, "^([0-9a-fA-F]{64})[ \\t]+([^\\r\\n]+)$", RegexOptions.CultureInvariant);
        if (!match.Success || !string.Equals(match.Groups[2].Value, release.Archive.Name, StringComparison.Ordinal))
        {
            throw new UpdateException(UpdateErrorCode.IntegrityFailure, "Release checksum has an invalid format or asset name.");
        }

        var archive = await DownloadAssetAsync(release.Archive, MaxArchiveBytes, cancellationToken);
        var actual = SHA256.HashData(archive);
        var expected = Convert.FromHexString(match.Groups[1].Value);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new UpdateException(UpdateErrorCode.IntegrityFailure, "Release archive checksum does not match.");
        }

        return archive;
    }

    private async Task<byte[]> DownloadAssetAsync(ReleaseAsset asset, long maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var uri = new Uri($"https://api.github.com/repos/demyte/ORelay/releases/assets/{asset.Id}");
        using var response = await GetAsync(uri, "application/octet-stream", timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(UpdateErrorCode.DownloadFailed,
                $"Release asset '{asset.Name}' download failed with HTTP {(int)response.StatusCode}.");
        }

        try { return await ReadBoundedAsync(response, maxBytes, timeout.Token); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        { throw new UpdateException(UpdateErrorCode.DownloadFailed, "Release asset download failed or timed out."); }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, string accept, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect < 5; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(uri.Host))
            {
                throw new UpdateException(UpdateErrorCode.DownloadFailed, "Release download redirected to an untrusted host.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            request.Headers.UserAgent.ParseAdd("ORelay-Updater");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new UpdateException(UpdateErrorCode.DownloadFailed, "GitHub release download failed or timed out.");
            }

            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther))
            {
                return response;
            }

            var next = response.Headers.Location;
            response.Dispose();
            if (next is null)
            {
                throw new UpdateException(UpdateErrorCode.DownloadFailed, "Release download redirect has no location.");
            }

            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
        }

        throw new UpdateException(UpdateErrorCode.DownloadFailed, "Release download redirected too many times.");
    }

    private static bool IsAllowedHost(string host) =>
        host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximum)
        {
            throw new UpdateException(UpdateErrorCode.DownloadFailed, "Release response exceeds the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximum)
            {
                throw new UpdateException(UpdateErrorCode.DownloadFailed, "Release response exceeds the size limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static ReleaseAsset ParseAsset(JsonElement element) =>
        new(element.GetProperty("name").GetString() ?? "", element.GetProperty("id").GetInt64(), element.GetProperty("size").GetInt64());

    private static ReleaseAsset SingleAsset(ReleaseAsset[] assets, string name)
    {
        var matches = assets.Where(asset => string.Equals(asset.Name, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].Id <= 0)
        {
            throw new UpdateException(UpdateErrorCode.InvalidRelease, $"Release asset '{name}' is missing or duplicated.");
        }

        return matches[0];
    }

    private static string? ReadGhToken()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "auth", "token", "--hostname", "github.com" },
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            _ = errors.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
