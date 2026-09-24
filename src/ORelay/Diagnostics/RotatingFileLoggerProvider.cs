using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ORelay.Diagnostics;

internal sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxMessageCharacters = 8 * 1024;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);
    private static int _warningWritten;

    private readonly string _directory;
    private readonly string _lockPath;

    internal RotatingFileLoggerProvider(string configurationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        _directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configurationPath))!, "logs");
        _lockPath = Path.Combine(_directory, "orelay.lock");
    }

    public ILogger CreateLogger(string categoryName) =>
        categoryName is "ORelay" || categoryName.StartsWith("ORelay.", StringComparison.Ordinal)
            ? new FileLogger(this, categoryName)
            : DisabledLogger.Instance;

    public void Dispose() { }

    private void Write(LogLevel level, string category, EventId eventId, string message)
    {
        try
        {
            var safeMessage = Sanitize(message, MaxMessageCharacters);
            var safeCategory = Sanitize(category, 256);

            // Messages are limited before encoding so a single record always fits in one file.
            var record = $"{DateTimeOffset.UtcNow:O} [pid {Environment.ProcessId}] [{level}] {safeCategory}[{eventId.Id}]: {safeMessage}\n";
            var bytes = Encoding.UTF8.GetBytes(record);
            Directory.CreateDirectory(_directory);
            // Keep this file in place so every process locks the same file, including after a restart.
            using var fileLock = AcquireLock();
            var current = Path.Combine(_directory, "orelay.log");
            var currentSize = File.Exists(current) ? new FileInfo(current).Length : 0;
            if (currentSize + bytes.Length > MaxFileBytes)
                Rotate(current);

            using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception)
        {
            // File paths and exception messages may contain secrets. Keep the warning fixed.
            if (Interlocked.Exchange(ref _warningWritten, 1) == 0)
            {
                try { Console.Error.WriteLine("ORelay could not write its log file."); }
                catch (Exception) { }
            }
        }
    }

    private FileStream AcquireLock()
    {
        var wait = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (wait.Elapsed < LockWait)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static string Sanitize(string input, int maxCharacters)
    {
        var safe = new StringBuilder(Math.Min(input.Length, maxCharacters));
        for (var i = 0; i < input.Length && i < maxCharacters; i++)
        {
            var character = input[i];
            if (char.IsHighSurrogate(character))
            {
                if (i + 1 < input.Length && i + 1 < maxCharacters &&
                    char.IsLowSurrogate(input[i + 1]))
                {
                    safe.Append(character).Append(input[++i]);
                }
                else
                {
                    safe.Append(' ');
                }
            }
            else
            {
                safe.Append(char.IsControl(character) || char.IsLowSurrogate(character) ||
                    character is '\u2028' or '\u2029' ? ' ' : character);
            }
        }
        return safe.ToString();
    }

    private void Rotate(string current)
    {
        var previous = Path.Combine(_directory, "orelay.1.log");
        var oldest = Path.Combine(_directory, "orelay.2.log");
        File.Delete(oldest);
        if (File.Exists(previous))
            File.Move(previous, oldest);
        if (File.Exists(current))
            File.Move(current, previous);
    }

    private sealed class FileLogger(RotatingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            try
            {
                // Exception objects and scopes are never written, including through the formatter.
                provider.Write(logLevel, category, eventId, formatter(state, null));
            }
            catch (Exception)
            {
                if (Interlocked.Exchange(ref _warningWritten, 1) == 0)
                {
                    try { Console.Error.WriteLine("ORelay could not write its log file."); }
                    catch (Exception) { }
                }
            }
        }
    }

    private sealed class DisabledLogger : ILogger
    {
        internal static readonly DisabledLogger Instance = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
