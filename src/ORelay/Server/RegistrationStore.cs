using System.Security.Cryptography;

namespace ORelay.Server;

/// <summary>
/// Registry for live callback destinations. Registry IDs are opaque
/// routing identifiers. This version has no authentication or per-registration
/// management secret, so a later access policy can be added at the HTTP edge.
/// </summary>
public sealed class RegistrationStore : IDisposable
{
    public const int RegistrationIdBytes = 32;
    public const int MaxStateLength = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _leaseDuration;
    private readonly int _maxRegistrations;
    private readonly SqliteRegistrationDatabase? _database;
    private readonly bool _allowNonLoopbackDestinations;

    public RegistrationStore(
        TimeProvider? timeProvider = null,
        TimeSpan? leaseDuration = null,
        int maxRegistrations = RelayServerOptions.DefaultMaxRegistrations,
        string? databasePath = null,
        bool allowNonLoopbackDestinations = false)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(RelayServerOptions.DefaultLeaseSeconds);
        _maxRegistrations = maxRegistrations;
        _allowNonLoopbackDestinations = allowNonLoopbackDestinations;

        if (_leaseDuration <= TimeSpan.Zero || _leaseDuration > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "The lease duration must be greater than zero and no more than one day.");
        }

        if (_maxRegistrations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRegistrations), "The registry capacity must be greater than zero.");
        }

        if (databasePath is not null)
        {
            _database = new SqliteRegistrationDatabase(databasePath);
            try
            {
                lock (_gate)
                {
                    _database.InTransaction(() =>
                    {
                        _database.DeleteExpired(_timeProvider.GetUtcNow().UtcTicks);
                        foreach (var persisted in _database.ReadAll())
                        {
                            if (!IsValidRegistrationId(persisted.Id) ||
                                !CallbackDestination.TryValidate(persisted.CallbackUrl, _allowNonLoopbackDestinations, out _, out _))
                            {
                                _database.Delete(persisted.Id);
                            }
                        }
                    });
                }
            }
            catch
            {
                _database.Dispose();
                throw;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                RemoveExpiredLocked(now);
                return _database?.Count(now.UtcTicks) ?? _entries.Count;
            }
        }
    }

    public TimeSpan LeaseDuration => _leaseDuration;

    public RegistrationOperationResult Register(
        string callbackUrl,
        bool allowNonLoopback,
        string relayCallbackUrl = "")
    {
        if (!CallbackDestination.TryValidate(callbackUrl, allowNonLoopback, out _, out var errorCode))
        {
            return RegistrationOperationResult.Invalid(errorCode);
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            RemoveExpiredLocked(now);
            if (_database is null && _entries.Count >= _maxRegistrations)
            {
                return RegistrationOperationResult.Invalid("capacity_exceeded");
            }

            var id = NewId();
            var entry = new Entry(id, callbackUrl, now + _leaseDuration);
            if (_database is null)
            {
                _entries.Add(id, entry);
            }
            else
            {
                if (!_database.TryInsert(id, callbackUrl, now.UtcTicks, entry.ExpiresAt.UtcTicks, _maxRegistrations))
                {
                    return RegistrationOperationResult.Invalid("capacity_exceeded");
                }
            }
            return RegistrationOperationResult.Success(ToResponse(entry, relayCallbackUrl));
        }
    }

    public RegistrationOperationResult Renew(string id, string relayCallbackUrl)
    {
        if (!IsValidRegistrationId(id))
        {
            return RegistrationOperationResult.NotFound();
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            RemoveExpiredLocked(now);
            if (_database is not null)
            {
                var persisted = _database.Get(id, now.UtcTicks);
                if (persisted is null ||
                    !CallbackDestination.TryValidate(persisted.CallbackUrl, _allowNonLoopbackDestinations, out _, out _))
                {
                    return RegistrationOperationResult.NotFound();
                }

                var expiry = now + _leaseDuration;
                if (!_database.Renew(id, now.UtcTicks, expiry.UtcTicks))
                {
                    return RegistrationOperationResult.NotFound();
                }
                return RegistrationOperationResult.Success(ToResponse(new Entry(id, persisted.CallbackUrl, expiry), relayCallbackUrl));
            }

            if (!_entries.TryGetValue(id, out var entry))
            {
                return RegistrationOperationResult.NotFound();
            }

            entry.ExpiresAt = now + _leaseDuration;
            return RegistrationOperationResult.Success(ToResponse(entry, relayCallbackUrl));
        }
    }

    /// <summary>Deletes the entry if it is live. Unknown IDs are an idempotent success.</summary>
    public bool Delete(string id)
    {
        if (!IsValidRegistrationId(id))
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpiredLocked(_timeProvider.GetUtcNow());
            return _database?.Delete(id) ?? _entries.Remove(id);
        }
    }

    public bool TryGet(string id, out RegistrationSnapshot registration)
    {
        registration = default!;
        if (!IsValidRegistrationId(id))
        {
            return false;
        }

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            RemoveExpiredLocked(now);
            if (_database is not null)
            {
                var persisted = _database.Get(id, now.UtcTicks);
                if (persisted is null ||
                    !CallbackDestination.TryValidate(persisted.CallbackUrl, _allowNonLoopbackDestinations, out _, out _))
                {
                    return false;
                }
                registration = persisted;
                return true;
            }

            if (!_entries.TryGetValue(id, out var entry))
            {
                return false;
            }

            registration = new RegistrationSnapshot(entry.Id, entry.CallbackUrl, entry.ExpiresAt);
            return true;
        }
    }

    /// <summary>Removes entries at or beyond their exact expiry boundary.</summary>
    public int RemoveExpired()
    {
        lock (_gate)
        {
            return RemoveExpiredLocked(_timeProvider.GetUtcNow());
        }
    }

    public void Dispose() => _database?.Dispose();

    public static bool IsValidRegistrationId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length is >= 32 and <= 128 &&
        id.All(static character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    /// <summary>
    /// Parses the state envelope <c>&lt;registration-id&gt;.&lt;opaque-worktree-state&gt;</c>.
    /// The first dot is the only delimiter. IDs are 32 to 128 URL-safe
    /// characters, the complete state is capped at 4096 characters, and the
    /// opaque suffix must contain at least one character and may contain dots.
    /// </summary>
    public static bool TryExtractRegistrationId(string? state, out string id)
    {
        id = string.Empty;
        if (string.IsNullOrEmpty(state) || state.Length > MaxStateLength)
        {
            return false;
        }

        var separator = state.IndexOf('.');
        if (separator <= 0 || separator == state.Length - 1)
        {
            return false;
        }

        var candidate = state[..separator];
        if (!IsValidRegistrationId(candidate))
        {
            return false;
        }

        id = candidate;
        return true;
    }

    private RegistrationResponse ToResponse(Entry entry, string relayCallbackUrl) =>
        new(entry.Id, entry.CallbackUrl, entry.ExpiresAt, (int)_leaseDuration.TotalSeconds, relayCallbackUrl);

    private int RemoveExpiredLocked(DateTimeOffset now)
    {
        if (_database is not null)
        {
            return _database.DeleteExpired(now.UtcTicks);
        }

        var removed = 0;
        foreach (var pair in _entries.ToArray())
        {
            if (now >= pair.Value.ExpiresAt && _entries.Remove(pair.Key))
            {
                removed++;
            }
        }

        return removed;
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[RegistrationIdBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed class Entry(string id, string callbackUrl, DateTimeOffset expiresAt)
    {
        public string Id { get; } = id;
        public string CallbackUrl { get; } = callbackUrl;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
    }
}

public sealed record RegistrationSnapshot(string Id, string CallbackUrl, DateTimeOffset ExpiresAt);

public sealed record RegistrationOperationResult(
    bool IsSuccess,
    RegistrationResponse? Response,
    string? ErrorCode)
{
    public static RegistrationOperationResult Success(RegistrationResponse response) => new(true, response, null);

    public static RegistrationOperationResult Invalid(string code) => new(false, null, code);

    public static RegistrationOperationResult NotFound() => new(false, null, "registration_not_found");
}
