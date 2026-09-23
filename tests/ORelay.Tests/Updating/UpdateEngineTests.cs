using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ORelay.Diagnostics;
using ORelay.Services;
using ORelay.Updating;

namespace ORelay.Tests.Updating;

public sealed class UpdateEngineTests
{
    [Fact]
    public async Task Check_ReportsAvailabilityWithoutChangingExecutable()
    {
        using var fixture = new Fixture();

        var result = await fixture.Engine.CheckAsync(new UpdateRequest());

        Assert.True(result.Succeeded);
        Assert.True(result.UpdateAvailable);
        Assert.False(result.Changed);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task Check_OlderStableReleaseIsReadOnlySuccess()
    {
        using var fixture = new Fixture();
        fixture.Runtime.CurrentVersion = "1.2.0-dev.1+test";

        var result = await fixture.Engine.CheckAsync(new UpdateRequest());

        Assert.True(result.Succeeded);
        Assert.False(result.UpdateAvailable);
        Assert.False(result.Changed);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task Update_ReplacesExecutableAndPreservesNeighbouringState()
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Directory, "orelay.json");
        var database = config + ".registrations.db";
        File.WriteAllText(config, "saved config");
        File.WriteAllText(database, "saved registrations");

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal("new executable", File.ReadAllText(fixture.Target));
        Assert.Equal("saved config", File.ReadAllText(config));
        Assert.Equal("saved registrations", File.ReadAllText(database));
        Assert.False(fixture.Runtime.ExecutedOriginalTarget);
    }

    [Fact]
    public async Task Update_RejectsUnrecognizedInstalledMetadataWithoutExecutingIt()
    {
        using var fixture = new Fixture();
        fixture.Runtime.InstalledVersion = null;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.InvalidVersion, result.ErrorCode);
        Assert.False(fixture.Runtime.ExecutedOriginalTarget);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.Empty(fixture.Service.Operations);
    }

    [Fact]
    public async Task ConcurrentUpdate_LeavesExecutableUntouched()
    {
        using var fixture = new Fixture();
        using var held = new FileStream(fixture.Target + ".update.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.Busy, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task Update_RejectsWrongChecksumWithoutChangingInstalledExecutable()
    {
        using var fixture = new Fixture();
        fixture.Download.CorruptChecksum = true;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.IntegrityFailure, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task Update_DoesNotForwardTokenToAssetRedirectHost()
    {
        using var fixture = new Fixture();
        fixture.Download.RedirectChecksum = true;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.True(result.Succeeded);
        Assert.True(fixture.Download.SawAuthorizationOnApi);
        Assert.False(fixture.Download.SawAuthorizationOnRedirectHost);
    }

    [Fact]
    public async Task Update_RejectsArchiveTraversalWithoutChangingInstalledExecutable()
    {
        using var fixture = new Fixture();
        fixture.Download.SetExecutableEntryName("../orelay");

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.InvalidArchive, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task Update_RejectsCandidateWithWrongVersionBeforeReplacement()
    {
        using var fixture = new Fixture();
        fixture.Runtime.CandidateVersion = "1.2.0";

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.VersionMismatch, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task Update_RejectsServiceConflictBeforeDownloadingCandidate()
    {
        using var fixture = new Fixture();
        fixture.Service.Conflict = true;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.ServiceConflict, result.ErrorCode);
        Assert.Equal(0, fixture.Download.AssetRequests);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    [Fact]
    public async Task Update_RestoresExecutableWhenServiceRestartFails()
    {
        using var fixture = new Fixture();
        fixture.Service.Installed = true;
        fixture.Service.RestartFails = true;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest(RestartService: true));

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.ServiceFailure, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.False(fixture.Service.IsStopped);
        Assert.Equal(new[] { ServiceOperation.Stop, ServiceOperation.Start, ServiceOperation.Start },
            fixture.Service.Operations.Where(operation => operation != ServiceOperation.Status));
    }

    [Fact]
    public async Task Update_RunningServiceRestartsAfterReplacementAndChecksSelectedConfigHealth()
    {
        using var fixture = new Fixture();
        fixture.Service.Installed = true;
        var config = Path.Combine(fixture.Directory, "selected.json");
        File.WriteAllText(config, "{\"schemaVersion\":1,\"bind\":\"::\",\"port\":45124}");

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest(true, "updater-test", config));

        Assert.True(result.Succeeded);
        Assert.Equal("new executable", File.ReadAllText(fixture.Target));
        Assert.False(fixture.Service.IsStopped);
        Assert.Equal(new[] { ServiceOperation.Stop, ServiceOperation.Start },
            fixture.Service.Operations.Where(operation => operation != ServiceOperation.Status));
        string[] expectedContents = ["old executable", "new executable"];
        Assert.Equal(expectedContents, fixture.Service.LifecycleExecutableContents);
        Assert.All(fixture.Service.Requests, request =>
        {
            Assert.Equal(config, request.ConfigurationPath);
            Assert.Equal("updater-test", request.ServiceName);
            Assert.Equal(fixture.Target, request.ExecutablePath);
        });
        Assert.Equal("http://[::1]:45124/health", Assert.Single(fixture.Health.Urls).AbsoluteUri);
    }

    [Fact]
    public async Task Update_InitiallyStoppedServiceRemainsStopped()
    {
        using var fixture = new Fixture();
        fixture.Service.Installed = true;
        fixture.Service.IsStopped = true;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest(RestartService: true));

        Assert.True(result.Succeeded);
        Assert.Equal("new executable", File.ReadAllText(fixture.Target));
        Assert.True(fixture.Service.IsStopped);
        Assert.DoesNotContain(fixture.Service.Operations, operation => operation != ServiceOperation.Status);
        Assert.Empty(fixture.Health.Urls);
    }

    [Fact]
    public async Task Update_StopPermissionFailurePreservesRunningServiceAndExecutable()
    {
        using var fixture = new Fixture();
        fixture.Service.Installed = true;
        fixture.Service.StopFails = true;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest(RestartService: true));

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.ServiceFailure, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.False(fixture.Service.IsStopped);
        Assert.Equal(new[] { ServiceOperation.Stop },
            fixture.Service.Operations.Where(operation => operation != ServiceOperation.Status));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "orelay*.backup-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_StopStatusFailureRestoresRunningServiceAndPreservesExecutable(bool install)
    {
        using var fixture = new Fixture();
        fixture.Service.Installed = true;
        fixture.Service.StatusFailsAfterStop = true;
        if (install)
        {
            fixture.Runtime.ProcessPath = fixture.Source;
            fixture.Runtime.CurrentVersion = fixture.Runtime.CandidateVersion;
        }

        var result = install ?
            await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory, RestartService: true)) :
            await fixture.Engine.UpdateAsync(new UpdateRequest(RestartService: true));

        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Equal(UpdateErrorCode.ServiceFailure, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.False(fixture.Service.IsStopped);
        Assert.Equal(new[] { ServiceOperation.Stop, ServiceOperation.Start },
            fixture.Service.Operations.Where(operation => operation != ServiceOperation.Status));
        Assert.Single(fixture.Health.Urls);
        Assert.Empty(Directory.GetFiles(fixture.Directory, "orelay*.backup-*"));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "orelay*.stage-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_StopStatusFailureReportsFailedServiceRecovery(bool install)
    {
        using var fixture = new Fixture();
        fixture.Service.Installed = true;
        fixture.Service.StatusFailsAfterStop = true;
        fixture.Service.RestartFails = true;
        if (install)
        {
            fixture.Runtime.ProcessPath = fixture.Source;
            fixture.Runtime.CurrentVersion = fixture.Runtime.CandidateVersion;
        }

        var result = install ?
            await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory, RestartService: true)) :
            await fixture.Engine.UpdateAsync(new UpdateRequest(RestartService: true));

        Assert.False(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Equal(UpdateErrorCode.ServiceFailure, result.ErrorCode);
        Assert.Contains("service could not be restarted healthy", result.Message, StringComparison.Ordinal);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.True(fixture.Service.IsStopped);
        Assert.Equal(new[] { ServiceOperation.Stop, ServiceOperation.Start },
            fixture.Service.Operations.Where(operation => operation != ServiceOperation.Status));
        Assert.Empty(fixture.Health.Urls);
    }

    [Fact]
    public async Task Update_HealthFailureRestoresPreviousExecutableAndRunningService()
    {
        using var fixture = new Fixture();
        fixture.Service.Installed = true;
        fixture.Health.FailChecks = 20;

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest(RestartService: true));

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.ServiceFailure, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.False(fixture.Service.IsStopped);
        Assert.Equal(new[] { ServiceOperation.Stop, ServiceOperation.Start, ServiceOperation.Stop, ServiceOperation.Start },
            fixture.Service.Operations.Where(operation => operation != ServiceOperation.Status));
        string[] expectedContents = ["old executable", "new executable", "new executable", "old executable"];
        Assert.Equal(expectedContents, fixture.Service.LifecycleExecutableContents);
        Assert.Equal(21, fixture.Health.Urls.Count);
    }

    [Fact]
    public async Task InstallSelf_RejectsDowngradeAndPreservesTarget()
    {
        using var fixture = new Fixture();
        fixture.Runtime.ProcessPath = fixture.Source;
        fixture.Runtime.InstalledVersion = "2.0.0";

        var result = await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory));

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.Downgrade, result.ErrorCode);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.False(fixture.Runtime.ExecutedOriginalTarget);
    }

    [Fact]
    public async Task InstallSelf_RejectsUnrecognizedTargetWithoutExecutingIt()
    {
        using var fixture = new Fixture();
        fixture.Runtime.ProcessPath = fixture.Source;
        fixture.Runtime.InstalledVersion = null;

        var result = await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory));

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.InstallFailure, result.ErrorCode);
        Assert.False(fixture.Runtime.ExecutedOriginalTarget);
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
        Assert.Empty(fixture.Service.Operations);
    }

    [UnixFact]
    public async Task InstallSelf_DoesNotRunUnrelatedExecutableToDiscoverItsIdentity()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Runtime.ProcessPath = fixture.Source;
        fixture.Runtime.UseNativeVersionReaders = true;
        var marker = Path.Combine(fixture.Directory, "unexpected-execution");
        var payload = "#!/bin/sh\nprintf executed > '" + marker.Replace("'", "'\\''", StringComparison.Ordinal) + "'\n";
        File.WriteAllText(fixture.Target, payload);
        File.SetUnixFileMode(fixture.Target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var result = await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory));

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.InstallFailure, result.ErrorCode);
        Assert.False(File.Exists(marker));
        Assert.Equal(payload, File.ReadAllText(fixture.Target));
        Assert.Empty(fixture.Service.Operations);
    }

    [Theory]
    [InlineData("1.0.0+different-commit")]
    [InlineData("1.0.0+test")]
    public async Task InstallSelf_ReplacesDifferentBytesAtTheSameVersion(string sourceVersion)
    {
        using var fixture = new Fixture();
        fixture.Runtime.ProcessPath = fixture.Source;
        fixture.Runtime.CurrentVersion = sourceVersion;
        fixture.Runtime.CandidateVersion = sourceVersion;

        var result = await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory));

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal(File.ReadAllBytes(fixture.Source), File.ReadAllBytes(fixture.Target));
        Assert.Empty(Directory.GetFiles(fixture.Directory, "orelay*.backup-*"));
        Assert.False(fixture.Runtime.ExecutedOriginalTarget);
    }

    [Fact]
    public async Task InstallSelf_IdenticalArtifactDoesNotReplaceOrRestart()
    {
        using var fixture = new Fixture();
        fixture.Runtime.ProcessPath = fixture.Source;
        File.Copy(fixture.Target, fixture.Source, overwrite: true);

        var result = await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory));

        Assert.True(result.Succeeded);
        Assert.False(result.Changed);
        Assert.Empty(fixture.Service.Operations);
        Assert.Equal(File.ReadAllBytes(fixture.Source), File.ReadAllBytes(fixture.Target));
    }

    [UnixFact]
    public async Task InstallSelf_RejectsLinkedDirectoryBeforeCreatingSubdirectoryOrLock()
    {
        using var fixture = new Fixture();
        fixture.Runtime.ProcessPath = fixture.Source;
        var other = Directory.CreateDirectory(Path.Combine(fixture.Directory, "other")).FullName;
        var link = Path.Combine(fixture.Directory, "alias");
        Directory.CreateSymbolicLink(link, other);
        try
        {
            var result = await fixture.Engine.InstallSelfAsync(new InstallRequest(Path.Combine(link, "new-install")));

            Assert.False(result.Succeeded);
            Assert.Equal(UpdateErrorCode.InstallFailure, result.ErrorCode);
            Assert.Empty(Directory.GetFileSystemEntries(other));
        }
        finally { Directory.Delete(link); }
    }

    [UnixFact]
    public async Task InstallSelf_RejectsLinkedExecutableBeforeCreatingLock()
    {
        using var fixture = new Fixture();
        fixture.Runtime.ProcessPath = fixture.Source;
        File.Delete(fixture.Target);
        File.CreateSymbolicLink(fixture.Target, fixture.Source);

        var result = await fixture.Engine.InstallSelfAsync(new InstallRequest(fixture.Directory));

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.InstallFailure, result.ErrorCode);
        Assert.False(File.Exists(fixture.Target + ".update.lock"));
        Assert.Equal("source executable", File.ReadAllText(fixture.Source));
    }

    [UnixFact]
    public async Task Update_RejectsDanglingLockLinkWithoutCreatingItsTarget()
    {
        using var fixture = new Fixture();
        var other = Path.Combine(fixture.Directory, "unrelated-file");
        File.CreateSymbolicLink(fixture.Target + ".update.lock", other);

        var result = await fixture.Engine.UpdateAsync(new UpdateRequest());

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateErrorCode.InstallFailure, result.ErrorCode);
        Assert.False(File.Exists(other));
        Assert.Equal("old executable", File.ReadAllText(fixture.Target));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "orelay-update-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Target = Path.Combine(Directory, OperatingSystem.IsWindows() ? "orelay.exe" : "orelay");
            File.WriteAllText(Target, "old executable");
            Source = Path.Combine(Directory, "source.bin");
            File.WriteAllText(Source, "source executable");
            Runtime = new FakeRuntime(Target, Target);
            Download = new FakeDownload();
            Service = new FakeService();
            Health = new FakeHealth();
            Engine = new UpdateEngine(new HttpClient(Download), Runtime, Service, token: "test-token",
                health: Health);
        }

        public string Directory { get; }
        public string Target { get; }
        public string Source { get; }
        public FakeRuntime Runtime { get; }
        public FakeDownload Download { get; }
        public FakeService Service { get; }
        public FakeHealth Health { get; }
        public UpdateEngine Engine { get; }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class FakeRuntime(string processPath, string target) : IUpdateRuntime
    {
        public string? ProcessPath { get; set; } = processPath;
        public bool IsNative => true;
        public string? RuntimeIdentifier => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        public string CurrentVersion { get; set; } = "1.0.0+test";
        public string CandidateVersion { get; set; } = "1.1.0+test";
        public string? InstalledVersion { get; set; } = "1.0.0+test";
        public bool ExecutedOriginalTarget { get; private set; }
        public bool UseNativeVersionReaders { get; set; }
        public string? ReadInstalledVersion(string path) => UseNativeVersionReaders ? InstalledExecutableMetadata.ReadVersion(path) : InstalledVersion;

        public Task<string?> ReadExecutableVersionAsync(string path, CancellationToken cancellationToken)
        {
            if (UseNativeVersionReaders) return new NativeUpdateRuntime().ReadExecutableVersionAsync(path, cancellationToken);
            if (path == target && File.ReadAllText(path) == "old executable") ExecutedOriginalTarget = true;
            return Task.FromResult(path == target && File.ReadAllText(path) != "new executable" ?
                InstalledVersion : CandidateVersion);
        }
    }

    private sealed class FakeService : IPlatformServiceManager
    {
        public ServicePlatform Platform => ServicePlatform.Windows;
        public bool Conflict { get; set; }
        public bool Installed { get; set; }
        public bool RestartFails { get; set; }
        public bool StopFails { get; set; }
        public bool StatusFailsAfterStop { get; set; }
        public bool IsStopped { get => _stopped; set => _stopped = value; }
        public List<ServiceOperation> Operations { get; } = [];
        public List<ServiceRequest> Requests { get; } = [];
        public List<string> LifecycleExecutableContents { get; } = [];
        private bool _stopped;
        private bool _failedOnce;
        private bool _statusFailedAfterStop;

        public ServiceOperationResult Execute(ServiceOperation operation, ServiceRequest request)
        {
            Operations.Add(operation);
            Requests.Add(request);
            if (operation is ServiceOperation.Stop or ServiceOperation.Start)
                LifecycleExecutableContents.Add(File.ReadAllText(request.ExecutablePath));
            if (Conflict) return ServiceOperationResult.Failure(Platform, operation, request.ServiceName,
                ServiceState.Running, ServiceErrorCode.Conflict, "conflict");
            if (operation == ServiceOperation.Stop && StopFails)
                return ServiceOperationResult.Failure(Platform, operation, request.ServiceName,
                    ServiceState.Running, ServiceErrorCode.PermissionDenied, "access denied");
            if (operation == ServiceOperation.Status && _stopped && StatusFailsAfterStop && !_statusFailedAfterStop)
            {
                _statusFailedAfterStop = true;
                return ServiceOperationResult.Failure(Platform, operation, request.ServiceName,
                    ServiceState.Unknown, ServiceErrorCode.ManagerUnavailable, "transient status failure");
            }
            if (operation == ServiceOperation.Start && RestartFails && !_failedOnce)
            {
                _failedOnce = true;
                return ServiceOperationResult.Failure(Platform, operation, request.ServiceName,
                    ServiceState.Failed, ServiceErrorCode.CommandFailed, "restart failed");
            }
            if (operation == ServiceOperation.Stop) _stopped = true;
            if (operation == ServiceOperation.Start) _stopped = false;
            return ServiceOperationResult.Success(Platform, operation, request.ServiceName,
                Installed ? _stopped ? ServiceState.Stopped : ServiceState.Running : ServiceState.NotInstalled,
                false, Installed, "ok");
        }
    }

    private sealed class FakeHealth : IRelayHealthProbe
    {
        public int FailChecks { get; set; }
        public List<Uri> Urls { get; } = [];

        public Task<RelayHealthProbeResult> CheckAsync(Uri healthUrl, CancellationToken cancellationToken = default)
        {
            Urls.Add(healthUrl);
            return Task.FromResult(Urls.Count <= FailChecks ?
                RelayHealthProbeResult.Unreachable("not ready") :
                new RelayHealthProbeResult(true, true, "orelay", "ok", 200, null));
        }
    }

    private sealed class FakeDownload : HttpMessageHandler
    {
        private byte[] _archive;
        private readonly string _archiveName;
        public bool CorruptChecksum { get; set; }
        public bool RedirectChecksum { get; set; }
        public bool SawAuthorizationOnApi { get; private set; }
        public bool SawAuthorizationOnRedirectHost { get; private set; }
        public int AssetRequests { get; private set; }

        public FakeDownload()
        {
            var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            _archiveName = $"orelay-1.1.0-{rid}" + (OperatingSystem.IsWindows() ? ".zip" : ".tar.gz");
            _archive = BuildArchive(OperatingSystem.IsWindows() ? "orelay.exe" : "./orelay");
        }

        public void SetExecutableEntryName(string name) => _archive = BuildArchive(name);

        private static byte[] BuildArchive(string name)
        {
            if (OperatingSystem.IsWindows())
            {
                using var stream = new MemoryStream();
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write("new executable");
                }
                return stream.ToArray();
            }
            else
            {
                using var stream = new MemoryStream();
                using (var gzip = new GZipStream(stream, CompressionMode.Compress, leaveOpen: true))
                using (var tar = new System.Formats.Tar.TarWriter(gzip))
                {
                    using var content = new MemoryStream(Encoding.UTF8.GetBytes("new executable"));
                    tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = content });
                }
                return stream.ToArray();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "api.github.com" && request.Headers.Authorization is not null)
                SawAuthorizationOnApi = true;
            if (request.RequestUri.Host == "release-assets.githubusercontent.com" && request.Headers.Authorization is not null)
                SawAuthorizationOnRedirectHost = true;
            if (RedirectChecksum && path.EndsWith("/2", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://release-assets.githubusercontent.com/checksum") },
                });
            byte[] body;
            if (path.EndsWith("/latest", StringComparison.Ordinal))
            {
                body = Encoding.UTF8.GetBytes($$"""
                    {"tag_name":"v1.1.0","draft":false,"prerelease":false,"assets":[
                    {"name":"{{_archiveName}}","id":1,"size":{{_archive.Length}}},
                    {"name":"{{_archiveName}}.sha256","id":2,"size":100}]}
                    """);
            }
            else
            {
                AssetRequests++;
                if (path.EndsWith("/1", StringComparison.Ordinal)) body = _archive;
                else
                {
                    var hash = Convert.ToHexString(SHA256.HashData(_archive));
                    body = Encoding.ASCII.GetBytes($"{(CorruptChecksum ? new string('0', 64) : hash)}  {_archiveName}\n");
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}
