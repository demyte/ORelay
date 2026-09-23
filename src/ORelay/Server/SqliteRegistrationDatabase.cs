using System.Runtime.InteropServices;
using System.Text;

namespace ORelay.Server;

// All SQL and errors stay inside this class. Native SQLite messages can include
// statement values and paths, so failures expose only a fixed, safe message.
internal sealed partial class SqliteRegistrationDatabase : IDisposable
{
    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private const int ReadWriteCreateFullMutex = 0x00010006;
    private IntPtr _connection;

    public SqliteRegistrationDatabase(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ":memory:")
        {
            throw new ArgumentException("A file path is required for persistent registrations.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        try
        {
            Check(Native.Open(fullPath, out _connection, ReadWriteCreateFullMutex, IntPtr.Zero));
            Check(Native.BusyTimeout(_connection, 5000));
            using (var journal = Prepare("PRAGMA journal_mode=DELETE"))
            {
                if (journal.Step() != Row || journal.Text(0) != "delete") throw Failure();
            }
            Execute("PRAGMA synchronous=FULL");
            using (var check = Prepare("PRAGMA quick_check"))
            {
                if (check.Step() != Row || check.Text(0) != "ok" || check.Step() != Done)
                {
                    throw Failure();
                }
            }

            var version = ScalarLong("PRAGMA user_version");
            if (version == 0)
            {
                if (ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'") != 0)
                {
                    throw Failure();
                }

                InTransaction(() =>
                {
                    Execute("CREATE TABLE registrations (id TEXT PRIMARY KEY NOT NULL, callback_url TEXT NOT NULL, expires_utc_ticks INTEGER NOT NULL)");
                    Execute("CREATE INDEX registrations_expiry ON registrations(expires_utc_ticks)");
                    Execute("PRAGMA user_version=1");
                });
            }
            else if (version != 1)
            {
                throw Failure();
            }

            using var schema = Prepare("SELECT id, callback_url, expires_utc_ticks FROM registrations LIMIT 0");
            _ = schema.Step();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Count(long nowTicks)
    {
        using var statement = Prepare("SELECT COUNT(*) FROM registrations WHERE expires_utc_ticks > ?1");
        statement.Long(1, nowTicks);
        if (statement.Step() != Row) throw Failure();
        return checked((int)statement.GetLong(0));
    }

    public void Insert(string id, string callbackUrl, long expiryTicks)
    {
        using var statement = Prepare("INSERT INTO registrations(id, callback_url, expires_utc_ticks) VALUES (?1, ?2, ?3)");
        statement.Text(1, id);
        statement.Text(2, callbackUrl);
        statement.Long(3, expiryTicks);
        if (statement.Step() != Done) throw Failure();
    }

    public bool TryInsert(string id, string callbackUrl, long nowTicks, long expiryTicks, int capacity)
    {
        var inserted = false;
        InTransaction(() =>
        {
            DeleteExpired(nowTicks);
            if (Count(nowTicks) >= capacity) return;
            Insert(id, callbackUrl, expiryTicks);
            inserted = true;
        });
        return inserted;
    }

    public RegistrationSnapshot? Get(string id, long nowTicks)
    {
        using var statement = Prepare("SELECT callback_url, expires_utc_ticks FROM registrations WHERE id=?1 AND expires_utc_ticks>?2");
        statement.Text(1, id);
        statement.Long(2, nowTicks);
        if (statement.Step() != Row) return null;
        var callback = statement.Text(0);
        var expiry = new DateTimeOffset(statement.GetLong(1), TimeSpan.Zero);
        return new RegistrationSnapshot(id, callback, expiry);
    }

    public bool Renew(string id, long nowTicks, long expiryTicks)
    {
        using var statement = Prepare("UPDATE registrations SET expires_utc_ticks=?1 WHERE id=?2 AND expires_utc_ticks>?3");
        statement.Long(1, expiryTicks);
        statement.Text(2, id);
        statement.Long(3, nowTicks);
        if (statement.Step() != Done) throw Failure();
        return Native.Changes(_connection) == 1;
    }

    public bool Delete(string id)
    {
        using var statement = Prepare("DELETE FROM registrations WHERE id=?1");
        statement.Text(1, id);
        if (statement.Step() != Done) throw Failure();
        return Native.Changes(_connection) == 1;
    }

    public int DeleteExpired(long nowTicks)
    {
        using var statement = Prepare("DELETE FROM registrations WHERE expires_utc_ticks<=?1");
        statement.Long(1, nowTicks);
        if (statement.Step() != Done) throw Failure();
        return Native.Changes(_connection);
    }

    public IReadOnlyList<RegistrationSnapshot> ReadAll()
    {
        var rows = new List<RegistrationSnapshot>();
        using var statement = Prepare("SELECT id, callback_url, expires_utc_ticks FROM registrations");
        while (statement.Step() == Row)
        {
            rows.Add(new RegistrationSnapshot(statement.Text(0), statement.Text(1),
                new DateTimeOffset(statement.GetLong(2), TimeSpan.Zero)));
        }
        return rows;
    }

    public void InTransaction(Action action)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            action();
            Execute("COMMIT");
        }
        catch
        {
            try { Execute("ROLLBACK"); } catch { /* Keep the original failure. */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (_connection != IntPtr.Zero)
        {
            _ = Native.Close(_connection);
            _connection = IntPtr.Zero;
        }
    }

    private long ScalarLong(string sql)
    {
        using var statement = Prepare(sql);
        if (statement.Step() != Row) throw Failure();
        return statement.GetLong(0);
    }

    private void Execute(string sql)
    {
        using var statement = Prepare(sql);
        if (statement.Step() != Done) throw Failure();
    }

    private Statement Prepare(string sql)
    {
        Check(Native.Prepare(_connection, sql, -1, out var handle, IntPtr.Zero));
        return new Statement(handle);
    }

    private static void Check(int code)
    {
        if (code != Ok) throw Failure();
    }

    private static InvalidOperationException Failure() => new("The registration database could not be read or updated.");

    private sealed class Statement(IntPtr handle) : IDisposable
    {
        public int Step()
        {
            var result = Native.Step(handle);
            if (result != Row && result != Done) throw Failure();
            return result;
        }

        public void Text(int index, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Check(Native.BindText(handle, index, bytes, bytes.Length, new IntPtr(-1)));
        }

        public void Long(int index, long value) => Check(Native.BindInt64(handle, index, value));

        public string Text(int index)
        {
            var pointer = Native.ColumnText(handle, index);
            var length = Native.ColumnBytes(handle, index);
            if (pointer == IntPtr.Zero || length < 0) throw Failure();
            return Marshal.PtrToStringUTF8(pointer, length) ?? throw Failure();
        }

        public long GetLong(int index) => Native.ColumnInt64(handle, index);

        public void Dispose() => _ = Native.Finalize(handle);
    }

    private static partial class Native
    {
        private const string Library = "e_sqlite3";

        [LibraryImport(Library, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Open(string path, out IntPtr db, int flags, IntPtr vfs);
        [LibraryImport(Library, EntryPoint = "sqlite3_close_v2")]
        internal static partial int Close(IntPtr db);
        [LibraryImport(Library, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Prepare(IntPtr db, string sql, int length, out IntPtr statement, IntPtr tail);
        [LibraryImport(Library, EntryPoint = "sqlite3_step")]
        internal static partial int Step(IntPtr statement);
        [LibraryImport(Library, EntryPoint = "sqlite3_finalize")]
        internal static partial int Finalize(IntPtr statement);
        [LibraryImport(Library, EntryPoint = "sqlite3_bind_text")]
        internal static partial int BindText(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);
        [LibraryImport(Library, EntryPoint = "sqlite3_bind_int64")]
        internal static partial int BindInt64(IntPtr statement, int index, long value);
        [LibraryImport(Library, EntryPoint = "sqlite3_column_text")]
        internal static partial IntPtr ColumnText(IntPtr statement, int index);
        [LibraryImport(Library, EntryPoint = "sqlite3_column_bytes")]
        internal static partial int ColumnBytes(IntPtr statement, int index);
        [LibraryImport(Library, EntryPoint = "sqlite3_column_int64")]
        internal static partial long ColumnInt64(IntPtr statement, int index);
        [LibraryImport(Library, EntryPoint = "sqlite3_changes")]
        internal static partial int Changes(IntPtr db);
        [LibraryImport(Library, EntryPoint = "sqlite3_busy_timeout")]
        internal static partial int BusyTimeout(IntPtr db, int milliseconds);
    }
}
