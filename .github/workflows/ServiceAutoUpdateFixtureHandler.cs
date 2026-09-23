using System.Net;
using System.Net.Http;

namespace ORelay.Updating;

// Copied only into disposable CI fixture sources. Never compile into the product.
internal sealed class ServiceAutoUpdateFixtureHandler : HttpMessageHandler
{
    private const string ReleaseUrl = "https://api.github.com/repos/demyte/ORelay/releases/latest";
    private const string AssetUrl = "https://api.github.com/repos/demyte/ORelay/releases/assets/";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = AppContext.BaseDirectory;
        var versionPath = Path.Combine(root, "auto-update-fixture-version.txt");
        var archivePath = Path.Combine(root, "auto-update-fixture-archive");
        var checksumPath = Path.Combine(root, "auto-update-fixture-checksum");
        var uri = request.RequestUri?.AbsoluteUri;
        if (request.Method != HttpMethod.Get || uri is null ||
            !File.Exists(versionPath) || !File.Exists(archivePath) || !File.Exists(checksumPath))
            throw new HttpRequestException("The disposable update fixture is incomplete or the request is unexpected.");

        byte[] body;
        string contentType;
        string requestName;
        if (uri == ReleaseUrl)
        {
            var version = File.ReadAllText(versionPath).Trim();
            if (!SemVersion.TryParse(version, out var parsed) || parsed!.Value.Prerelease is not null)
                throw new HttpRequestException("The disposable update fixture version is invalid.");
            var rid = new NativeUpdateRuntime().RuntimeIdentifier ??
                throw new HttpRequestException("The disposable update fixture platform is unsupported.");
            var extension = rid.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz";
            var name = $"orelay-{version}-{rid}{extension}";
            var archiveSize = new FileInfo(archivePath).Length;
            var checksumSize = new FileInfo(checksumPath).Length;
            var json = $$"""
                {"tag_name":"v{{version}}","draft":false,"prerelease":false,"assets":[{"name":"{{name}}","id":1,"size":{{archiveSize}}},{"name":"{{name}}.sha256","id":2,"size":{{checksumSize}}}]}
                """;
            body = System.Text.Encoding.UTF8.GetBytes(json);
            contentType = "application/vnd.github+json";
            requestName = "release";
        }
        else if (uri == AssetUrl + "1")
        {
            body = File.ReadAllBytes(archivePath);
            contentType = "application/octet-stream";
            requestName = "archive";
        }
        else if (uri == AssetUrl + "2")
        {
            body = File.ReadAllBytes(checksumPath);
            contentType = "application/octet-stream";
            requestName = "checksum";
        }
        else
        {
            throw new HttpRequestException("The disposable update fixture refused an unexpected URL.");
        }

        File.AppendAllText(Path.Combine(root, "auto-update-fixture-requests.txt"),
            requestName + Environment.NewLine);

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new(contentType) } }
        });
    }
}
