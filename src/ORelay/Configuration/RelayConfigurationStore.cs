using System.Text.Json;

namespace ORelay.Configuration;

public static class RelayConfigurationPath
{
    public const string DefaultFileName = "orelay.json";

    public static string Resolve(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Path.Combine(AppContext.BaseDirectory, DefaultFileName);
        }

        return Path.GetFullPath(selectedPath);
    }
}

/// <summary>
/// Reads and updates the versioned relay configuration file.
///
/// A process or command should create one store for its selected path and use the
/// update methods for writes. Every update takes the sibling lock, reloads the file,
/// and replaces it atomically, so two cooperating writers do not discard each other's
/// settings.
/// </summary>
public sealed class RelayConfigurationStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly string[] NonNullableSettingNames =
    ["port", "bind", "autoDiscovery", "leaseSeconds", "maxRegistrations", "autoUpdate", "autoUpdateIntervalSeconds"];

    private readonly TimeSpan _lockTimeout;

    public RelayConfigurationStore(string? selectedPath = null, TimeSpan? lockTimeout = null)
    {
        FilePath = RelayConfigurationPath.Resolve(selectedPath);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);

        if (_lockTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), "The configuration lock timeout must be positive.");
        }
    }

    public string FilePath { get; }

    public string LockFilePath => FilePath + ".lock";

    public bool Exists => GetFileState() == ConfigurationFileState.Present;

    public RelaySettings Read(RelaySettingsPatch? invocationOverrides = null)
    {
        var document = ReadDocumentIfPresent() ?? new RelayConfigurationDocument();
        var effective = ResolveSettings(document.ToPatch(), invocationOverrides);

        return RelaySettingsValidator.ValidateAndReturn(effective, FilePath);
    }

    internal RelaySettings ReadCapturedText(string json, RelaySettingsPatch? invocationOverrides = null)
    {
        var document = ReadDocument(json);
        var effective = ResolveSettings(document.ToPatch(), invocationOverrides);
        return RelaySettingsValidator.ValidateAndReturn(effective, FilePath);
    }

    public RelayConfigurationDocument? ReadSavedDocument()
    {
        var document = ReadDocumentIfPresent();
        return document is null ? null : Clone(document);
    }

    /// <summary>Create the file once. Existing valid content is preserved.</summary>
    public RelaySettings Init(RelaySettingsPatch? invocationOverrides = null)
    {
        if (GetFileState() == ConfigurationFileState.Present)
        {
            return Read();
        }

        using var fileLock = AcquireLock();
        if (GetFileState() == ConfigurationFileState.Present)
        {
            return Read();
        }

        var effective = ResolveSettings(null, invocationOverrides);
        RelaySettingsValidator.Validate(effective, FilePath);
        WriteDocument(RelayConfigurationDocument.FromSettings(effective));
        return effective;
    }

    public RelaySettings Set(string key, string? value)
    {
        if (!RelaySettingsValidator.TryParseKey(key, out var setting))
        {
            throw new RelayConfigurationException(
                RelayConfigurationErrorCode.UnknownSetting,
                $"Unknown setting '{key}'. Selected file: '{FilePath}'.",
                FilePath,
                key);
        }

        var patch = RelaySettingsValidator.ParsePatch(
            new Dictionary<string, string?>(StringComparer.Ordinal) { [key] = value },
            FilePath);

        using var fileLock = AcquireLock();
        var document = ReadDocumentIfPresent() ?? new RelayConfigurationDocument();
        ApplyPatch(document, setting, patch);
        RelaySettingsValidator.ValidateDocument(document, FilePath);
        WriteDocument(document);
        return ResolveSettings(document.ToPatch());
    }

    public RelaySettings Clear(string key)
    {
        if (!RelaySettingsValidator.TryParseKey(key, out var setting))
        {
            throw new RelayConfigurationException(
                RelayConfigurationErrorCode.UnknownSetting,
                $"Unknown setting '{key}'. Selected file: '{FilePath}'.",
                FilePath,
                key);
        }

        if (GetFileState() == ConfigurationFileState.Missing)
        {
            return RelayConfigurationDefaults.Settings;
        }

        using var fileLock = AcquireLock();
        var document = ReadDocumentIfPresent();
        if (document is null)
        {
            return RelayConfigurationDefaults.Settings;
        }

        ClearProperty(document, setting);
        RelaySettingsValidator.ValidateDocument(document, FilePath);
        WriteDocument(document);
        return ResolveSettings(document.ToPatch());
    }

    public RelaySettings Load(RelaySettingsPatch? invocationOverrides = null) => Read(invocationOverrides);

    /// <summary>Save a reviewed setup in one write, refusing concurrent changes.</summary>
    public void SaveSetup(RelaySettings settings, RelaySettings? expectedSettings)
    {
        RelaySettingsValidator.Validate(settings, FilePath);
        using var fileLock = AcquireLock();
        var document = ReadDocumentIfPresent();
        var current = document is null ? null : ResolveSettings(document.ToPatch());
        if (current != expectedSettings)
        {
            throw new RelayConfigurationException(RelayConfigurationErrorCode.Conflict,
                $"Configuration changed during setup. Run setup again to review it. Selected file: '{FilePath}'.", FilePath);
        }

        if (current != settings) WriteDocument(RelayConfigurationDocument.FromSettings(settings));
    }

    public Task<RelaySettings> ReadAsync(
        RelaySettingsPatch? invocationOverrides = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(invocationOverrides));
    }

    public Task<RelaySettings> InitAsync(
        RelaySettingsPatch? invocationOverrides = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Init(invocationOverrides));
    }

    public Task<RelaySettings> SetAsync(
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Set(key, value));
    }

    public Task<RelaySettings> ClearAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Clear(key));
    }

    private static RelaySettings ResolveSettings(RelaySettingsPatch? saved, RelaySettingsPatch? invocationOverrides = null)
    {
        var effective = saved?.ApplyTo(RelayConfigurationDefaults.Settings) ?? RelayConfigurationDefaults.Settings;
        effective = invocationOverrides?.ApplyTo(effective) ?? effective;

        // A default hostname must not suppress requested discovery. A hostname
        // explicitly saved or supplied on the command line still takes precedence.
        if (saved?.Hostname is null && invocationOverrides?.Hostname is null &&
            string.Equals(effective.AutoDiscovery, "tailscale", StringComparison.OrdinalIgnoreCase))
        {
            effective = effective with { Hostname = null };
        }

        return effective;
    }

    private RelayConfigurationDocument? ReadDocumentIfPresent()
    {
        if (GetFileState() == ConfigurationFileState.Missing)
        {
            return null;
        }

        return ReadDocument();
    }

    private ConfigurationFileState GetFileState()
    {
        try
        {
            var attributes = File.GetAttributes(FilePath);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw FileFailure(
                    $"Selected configuration path '{FilePath}' is a directory, not a file.",
                    new IOException("The selected configuration path is a directory."));
            }

            // File.Exists hides access failures. Open the selected file so a read-only
            // command reports an inaccessible existing path instead of using defaults.
            using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                options: FileOptions.SequentialScan);
            return ConfigurationFileState.Present;
        }
        catch (FileNotFoundException)
        {
            return ConfigurationFileState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ConfigurationFileState.Missing;
        }
        catch (RelayConfigurationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileFailure($"Could not access selected configuration path '{FilePath}'.", ex);
        }
    }

    private RelayConfigurationDocument ReadDocument() => ReadDocument(ReadFileText());

    private string ReadFileText()
    {
        try
        {
            return File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileFailure($"Could not read configuration file '{FilePath}'.", ex);
        }
    }

    private RelayConfigurationDocument ReadDocument(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RelayConfigurationException(
                    RelayConfigurationErrorCode.InvalidJson,
                    $"Configuration file '{FilePath}' must contain a JSON object.",
                    FilePath);
            }

            foreach (var property in parsed.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null &&
                    Array.IndexOf(NonNullableSettingNames, property.Name) >= 0)
                {
                    throw new RelayConfigurationException(
                        RelayConfigurationErrorCode.InvalidValue,
                        $"Setting '{property.Name}' cannot be null in configuration file '{FilePath}'.",
                        FilePath,
                        property.Name);
                }
            }

            var document = JsonSerializer.Deserialize(
                json,
                RelaySettingsJsonContext.Default.RelayConfigurationDocument);

            if (document is null)
            {
                throw new RelayConfigurationException(
                    RelayConfigurationErrorCode.InvalidJson,
                    $"Configuration file '{FilePath}' is empty.",
                    FilePath);
            }

            RelaySettingsValidator.ValidateDocument(document, FilePath);
            return document;
        }
        catch (RelayConfigurationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new RelayConfigurationException(
                RelayConfigurationErrorCode.InvalidJson,
                $"Configuration file '{FilePath}' is invalid JSON or contains an unknown setting: {ex.Message}",
                FilePath,
                innerException: ex);
        }
    }

    private FileStream AcquireLock()
    {
        EnsureParentDirectory();

        var deadline = DateTime.UtcNow + _lockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    LockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.WriteThrough);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw FileFailure($"Could not lock configuration file '{FilePath}' for writing.", ex);
            }
            catch (IOException ex)
            {
                throw new RelayConfigurationException(
                    RelayConfigurationErrorCode.Conflict,
                    $"Another process is updating configuration file '{FilePath}'. Try again.",
                    FilePath,
                    innerException: ex);
            }
        }
    }

    private void WriteDocument(RelayConfigurationDocument document)
    {
        EnsureParentDirectory();
        var temporaryPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    document,
                    RelaySettingsJsonContext.Default.RelayConfigurationDocument);
                stream.Flush(flushToDisk: true);
            }

            // The temporary file is in the same directory, so replacement is a single
            // rename on the supported file systems. The old file remains untouched if
            // serialization or the replacement fails.
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (RelayConfigurationException)
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw FileFailure($"Could not write configuration file '{FilePath}'. The existing file was left intact.", ex);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    private void EnsureParentDirectory()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw FileFailure($"Could not access the configuration directory for '{FilePath}'.", ex);
        }
    }

    private static void ApplyPatch(
        RelayConfigurationDocument document,
        RelaySettingKey setting,
        RelaySettingsPatch patch)
    {
        switch (setting)
        {
            case RelaySettingKey.Port:
                document.Port = patch.Port;
                break;
            case RelaySettingKey.Bind:
                document.Bind = patch.Bind;
                break;
            case RelaySettingKey.PublicUrl:
                document.PublicUrl = patch.PublicUrl;
                break;
            case RelaySettingKey.Hostname:
                document.Hostname = patch.Hostname;
                break;
            case RelaySettingKey.AutoDiscovery:
                document.AutoDiscovery = patch.AutoDiscovery;
                break;
            case RelaySettingKey.LeaseSeconds:
                document.LeaseSeconds = patch.LeaseSeconds;
                break;
            case RelaySettingKey.MaxRegistrations:
                document.MaxRegistrations = patch.MaxRegistrations;
                break;
            case RelaySettingKey.AutoUpdate:
                document.AutoUpdate = patch.AutoUpdate;
                break;
            case RelaySettingKey.AutoUpdateIntervalSeconds:
                document.AutoUpdateIntervalSeconds = patch.AutoUpdateIntervalSeconds;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown relay setting.");
        }
    }

    private static void ClearProperty(RelayConfigurationDocument document, RelaySettingKey setting) =>
        ApplyPatch(document, setting, new RelaySettingsPatch());

    private static RelayConfigurationDocument Clone(RelayConfigurationDocument document) => new()
    {
        SchemaVersion = document.SchemaVersion,
        Port = document.Port,
        Bind = document.Bind,
        PublicUrl = document.PublicUrl,
        Hostname = document.Hostname,
        AutoDiscovery = document.AutoDiscovery,
        LeaseSeconds = document.LeaseSeconds,
        MaxRegistrations = document.MaxRegistrations,
        AutoUpdate = document.AutoUpdate,
        AutoUpdateIntervalSeconds = document.AutoUpdateIntervalSeconds,
    };

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Preserve the original write failure. A later cleanup can remove a
            // uniquely named temporary file if the platform still has it open.
        }
    }

    private RelayConfigurationException FileFailure(string message, Exception innerException) =>
        new(RelayConfigurationErrorCode.FileAccess, message, FilePath, innerException: innerException);

    private enum ConfigurationFileState
    {
        Missing,
        Present,
    }
}
