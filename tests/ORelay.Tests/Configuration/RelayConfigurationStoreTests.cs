using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ORelay.Cli;
using ORelay.Configuration;
using ORelay.Discovery;
using ORelay.Server;

namespace ORelay.Tests.Configuration;

public sealed class RelayConfigurationStoreTests
{
    [Fact]
    public void DefaultInitializationWritesTheLocalhostTemplate()
    {
        using var fixture = new ConfigurationFixture();
        var store = fixture.Store;
        Assert.Equal("localhost", store.Read().Hostname);
        Assert.False(store.Exists);

        store.Init();

        using var expected = JsonDocument.Parse("""
            {
              "schemaVersion": 1,
              "port": 12987,
              "bind": "127.0.0.1",
              "hostname": "localhost",
              "autoDiscovery": "none",
              "leaseSeconds": 300,
              "maxRegistrations": 1000,
              "autoUpdate": false,
              "autoUpdateIntervalSeconds": 86400
            }
            """);
        using var actual = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.Equal("http://localhost:12987/callback", RelayServerOptions.FromSettings(store.Read()).RelayCallbackUrl);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TailscaleDiscoveryWorksWhenNoHostnameIsExplicit(bool initializeWithTailscale)
    {
        using var fixture = new ConfigurationFixture();
        var store = fixture.Store;
        if (initializeWithTailscale)
        {
            store.Init(new RelaySettingsPatch(AutoDiscovery: "tailscale"));
        }
        else
        {
            store.Init();
            store.Set("autoDiscovery", "tailscale");
            var explicitHost = await RelaySettingsDiscovery.ResolveAsync(store.Read(), new FixedTailscaleProvider());
            Assert.Equal("explicit-hostname", explicitHost.Source);
            Assert.Equal("localhost", explicitHost.Settings!.Hostname);
            Assert.Null(store.Clear("hostname").Hostname);
        }

        Assert.Null(store.ReadSavedDocument()!.Hostname);
        var discovered = await RelaySettingsDiscovery.ResolveAsync(store.Read(), new FixedTailscaleProvider());
        Assert.True(discovered.Succeeded);
        Assert.Equal("tailscale", discovered.Source);
        Assert.Equal("http://relay.tailnet.test:12987/callback", RelayServerOptions.FromSettings(discovered.Settings!).RelayCallbackUrl);
    }

    [Fact]
    public void InitWritesDefaultsAndKeepsExistingFileOnRepeat()
    {
        using var fixture = new ConfigurationFixture();
        var store = fixture.Store;

        var initialized = store.Init(new RelaySettingsPatch(Port: 14_001));
        var firstContents = File.ReadAllText(store.FilePath);

        Assert.Equal(14_001, initialized.Port);
        Assert.Contains("\"schemaVersion\": 1", firstContents, StringComparison.Ordinal);
        Assert.Contains("\"port\": 14001", firstContents, StringComparison.Ordinal);

        var transient = store.Read(new RelaySettingsPatch(Port: 14_002));
        var repeated = store.Init(new RelaySettingsPatch(Port: 14_003));

        Assert.Equal(14_002, transient.Port);
        Assert.Equal(14_001, repeated.Port);
        Assert.Equal(firstContents, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void SetAndClearPreserveUnrelatedSettings()
    {
        using var fixture = new ConfigurationFixture();
        var store = fixture.Store;

        store.Set("port", "14101");
        store.Set("bind", "0.0.0.0");
        store.Clear("port");

        var settings = store.Read();
        var saved = File.ReadAllText(store.FilePath);

        Assert.Equal(RelayConfigurationDefaults.Port, settings.Port);
        Assert.Equal("0.0.0.0", settings.Bind);
        Assert.DoesNotContain("\"port\"", saved, StringComparison.Ordinal);
        Assert.Contains("\"bind\": \"0.0.0.0\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoUpdateSettingsDefaultPersistReloadAndClear()
    {
        using var fixture = new ConfigurationFixture();
        var store = fixture.Store;

        var defaults = store.Read();
        Assert.False(defaults.AutoUpdate);
        Assert.Equal(86_400, defaults.AutoUpdateIntervalSeconds);

        store.Set("autoUpdate", "true");
        store.Set("autoUpdateIntervalSeconds", "3600");

        var reloaded = new RelayConfigurationStore(store.FilePath).Read();
        Assert.True(reloaded.AutoUpdate);
        Assert.Equal(3_600, reloaded.AutoUpdateIntervalSeconds);

        var cleared = store.Clear("autoUpdate");
        Assert.False(cleared.AutoUpdate);
        Assert.Equal(3_600, cleared.AutoUpdateIntervalSeconds);
        Assert.False(store.ReadSavedDocument()!.AutoUpdate.HasValue);

        store.Clear("autoUpdateIntervalSeconds");
        Assert.Equal(86_400, store.Read().AutoUpdateIntervalSeconds);
    }

    [Theory]
    [InlineData("autoUpdate", "sometimes")]
    [InlineData("autoUpdate", "null")]
    [InlineData("autoUpdateIntervalSeconds", "59")]
    [InlineData("autoUpdateIntervalSeconds", "2592001")]
    [InlineData("autoUpdateIntervalSeconds", "nope")]
    public void InvalidAutoUpdateValuesAreRejected(string key, string value)
    {
        using var fixture = new ConfigurationFixture();

        var exception = Assert.Throws<RelayConfigurationException>(() => fixture.Store.Set(key, value));

        Assert.Equal(RelayConfigurationErrorCode.InvalidValue, exception.Code);
        Assert.Equal(key, exception.Setting);
    }

    [Theory]
    [InlineData("autoUpdate")]
    [InlineData("autoUpdateIntervalSeconds")]
    public void NullAutoUpdateJsonValuesAreRejected(string key)
    {
        using var fixture = new ConfigurationFixture();
        File.WriteAllText(fixture.Store.FilePath, $"{{ \"schemaVersion\": 1, \"{key}\": null }}");

        var exception = Assert.Throws<RelayConfigurationException>(() => fixture.Store.Read());

        Assert.Equal(RelayConfigurationErrorCode.InvalidValue, exception.Code);
        Assert.Equal(key, exception.Setting);
    }

    [Fact]
    public async Task ConfigCommandsExposeAutoUpdateValuesAndPreserveThemOnOtherEdits()
    {
        using var fixture = new ConfigurationFixture();
        var output = new StringWriter();
        var error = new StringWriter();
        var configOptions = new CliOptions(CliCommand.Config, IsJson: true, fixture.Store.FilePath,
            Config: new ConfigCommandOptions(ConfigAction.Set, "autoUpdate", "true"));

        Assert.Equal(CliExitCodes.Success, await ConfigurationCommand.ExecuteAsync(configOptions, output, error));
        using (var setJson = JsonDocument.Parse(output.ToString()))
        {
            Assert.True(setJson.RootElement.GetProperty("effective").GetProperty("autoUpdate").GetBoolean());
        }

        output.GetStringBuilder().Clear();
        var getOptions = configOptions with
        {
            Config = new ConfigCommandOptions(ConfigAction.Get, "autoUpdate", null),
        };
        Assert.Equal(CliExitCodes.Success, await ConfigurationCommand.ExecuteAsync(getOptions, output, error));
        using (var getJson = JsonDocument.Parse(output.ToString()))
        {
            Assert.True(getJson.RootElement.GetProperty("value").GetBoolean());
        }

        fixture.Store.Set("port", "14001");
        Assert.True(fixture.Store.Read().AutoUpdate);

        output.GetStringBuilder().Clear();
        var clearOptions = configOptions with
        {
            Config = new ConfigCommandOptions(ConfigAction.Clear, "autoUpdate", null),
        };
        Assert.Equal(CliExitCodes.Success, await ConfigurationCommand.ExecuteAsync(clearOptions, output, error));
        using (var clearJson = JsonDocument.Parse(output.ToString()))
        {
            Assert.False(clearJson.RootElement.GetProperty("effective").GetProperty("autoUpdate").GetBoolean());
        }
        Assert.False(fixture.Store.Read().AutoUpdate);
        Assert.Equal(14_001, fixture.Store.Read().Port);
    }

    [Fact]
    public void InvalidJsonUnknownKeysAndVersionsFailWithoutReplacingTheFile()
    {
        using var fixture = new ConfigurationFixture();
        var store = fixture.Store;

        var cases = new[]
        {
            ("{ \"port\": 14001,", RelayConfigurationErrorCode.InvalidJson),
            ("{ \"schemaVersion\": 1, \"unexpected\": true }", RelayConfigurationErrorCode.InvalidJson),
            ("{ \"schemaVersion\": 2 }", RelayConfigurationErrorCode.UnsupportedSchemaVersion),
        };

        foreach (var (contents, expectedCode) in cases)
        {
            File.WriteAllText(store.FilePath, contents, Encoding.UTF8);
            var beforeHash = Hash(store.FilePath);

            var exception = Assert.Throws<RelayConfigurationException>(() => store.Read());

            Assert.Equal(expectedCode, exception.Code);
            Assert.Equal(beforeHash, Hash(store.FilePath));
        }
    }

    [Fact]
    public void DirectorySelectedAsConfigPathFailsInsteadOfReturningDefaults()
    {
        using var fixture = new ConfigurationFixture();
        var directoryPath = Path.Combine(fixture.DirectoryPath, "config-directory");
        Directory.CreateDirectory(directoryPath);
        var store = new RelayConfigurationStore(directoryPath);

        var exception = Assert.Throws<RelayConfigurationException>(() => store.Read());

        Assert.Equal(RelayConfigurationErrorCode.FileAccess, exception.Code);
        Assert.Contains(directoryPath, exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(directoryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directoryPath));
    }

    [Fact]
    public void InitCreatesASelectedFileWhenItsParentDoesNotExist()
    {
        using var fixture = new ConfigurationFixture();
        var store = new RelayConfigurationStore(Path.Combine(fixture.DirectoryPath, "new-parent", "orelay.json"));

        store.Init();

        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(RelayConfigurationDefaults.Port, store.Read().Port);
    }

    [Fact]
    public async Task ConcurrentSetOperationsDoNotLoseUnrelatedUpdates()
    {
        using var fixture = new ConfigurationFixture();
        var store = fixture.Store;
        store.Init();

        var operations = Enumerable.Range(0, 8)
            .Select(index => Task.Run(() => store.Set("port", (14_200 + index).ToString(CultureInfo.InvariantCulture))))
            .Concat(Enumerable.Range(0, 8)
                .Select(index => Task.Run(() => store.Set("maxRegistrations", (2_000 + index).ToString(CultureInfo.InvariantCulture)))))
            .ToArray();

        await Task.WhenAll(operations);

        var settings = store.Read();
        Assert.InRange(settings.Port, 14_200, 14_207);
        Assert.InRange(settings.MaxRegistrations, 2_000, 2_007);
    }

    [Fact]
    public async Task TypedCommandUsesSettingsPatchAndGeneratedJsonOutput()
    {
        using var fixture = new ConfigurationFixture();
        var output = new StringWriter();
        var error = new StringWriter();

        var initOptions = new CliOptions(
            CliCommand.Init,
            IsJson: false,
            fixture.Store.FilePath,
            SettingsPatch: new RelaySettingsPatch(Port: 14_321));
        var initExitCode = await ConfigurationCommand.ExecuteAsync(initOptions, output, error);

        Assert.Equal(CliExitCodes.Success, initExitCode);
        Assert.Empty(error.ToString());
        Assert.Equal(14_321, fixture.Store.Read().Port);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        var getOptions = new CliOptions(
            CliCommand.Config,
            IsJson: true,
            fixture.Store.FilePath,
            Config: new ConfigCommandOptions(ConfigAction.Get, "port", null));
        var getExitCode = await ConfigurationCommand.ExecuteAsync(getOptions, output, error);

        Assert.Equal(CliExitCodes.Success, getExitCode);
        Assert.Empty(error.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(14_321, json.RootElement.GetProperty("value").GetInt32());

        output.GetStringBuilder().Clear();
        var bindOptions = getOptions with
        {
            Config = new ConfigCommandOptions(ConfigAction.Get, "bind", null),
        };
        await ConfigurationCommand.ExecuteAsync(bindOptions, output, error);
        using var bindJson = JsonDocument.Parse(output.ToString());
        Assert.Equal("127.0.0.1", bindJson.RootElement.GetProperty("value").GetString());

        output.GetStringBuilder().Clear();
        var publicUrlOptions = getOptions with
        {
            Config = new ConfigCommandOptions(ConfigAction.Get, "publicUrl", null),
        };
        await ConfigurationCommand.ExecuteAsync(publicUrlOptions, output, error);
        using var publicUrlJson = JsonDocument.Parse(output.ToString());
        Assert.Equal(JsonValueKind.Null, publicUrlJson.RootElement.GetProperty("value").ValueKind);
    }

    private static string Hash(string path)
    {
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private sealed class FixedTailscaleProvider : ITailscaleStatusProvider
    {
        public Task<TailscaleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new TailscaleStatusSnapshot(true, "Running", "relay.tailnet.test", null, []));
    }

    private sealed class ConfigurationFixture : IDisposable
    {
        public ConfigurationFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "orelay-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            Store = new RelayConfigurationStore(Path.Combine(DirectoryPath, "orelay.json"));
        }

        public string DirectoryPath { get; }

        public RelayConfigurationStore Store { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
