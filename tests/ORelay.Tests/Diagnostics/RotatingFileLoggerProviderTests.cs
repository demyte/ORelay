using System.Text;
using Microsoft.Extensions.Logging;
using ORelay.Diagnostics;

namespace ORelay.Tests.Diagnostics;

public sealed class RotatingFileLoggerProviderTests
{
    [Fact]
    public void ReopensAndAppendsWithoutLosingExistingRecords()
    {
        using var fixture = new LogFixture();
        fixture.Write("first");
        Assert.Contains("first", File.ReadAllText(fixture.Current));

        fixture.Write("second");

        var lines = File.ReadAllLines(fixture.Current);
        Assert.Equal(2, lines.Length);
        Assert.Contains("first", lines[0]);
        Assert.Contains("second", lines[1]);
    }

    [Fact]
    public void RotatesBeforeTwoMegabytesAndRetainsOnlyTheNewestThreeFiles()
    {
        using var fixture = new LogFixture();
        var payload = new string('x', 8_000);
        using var provider = new RotatingFileLoggerProvider(fixture.ConfigurationPath);
        var logger = provider.CreateLogger("ORelay.Rotation");

        for (var i = 0; i < 800; i++)
            Emit(logger, $"sequence-{i:D4} {payload}");

        var files = Directory.GetFiles(fixture.LogDirectory, "orelay*.log");
        Assert.Equal(3, files.Length);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 2 * 1024 * 1024));

        var retained = string.Join("", files.Select(File.ReadAllText));
        Assert.Contains("sequence-0799", retained);
        Assert.DoesNotContain("sequence-0000", retained);
        Assert.True(File.GetLastWriteTimeUtc(fixture.Current) >= File.GetLastWriteTimeUtc(
            Path.Combine(fixture.LogDirectory, "orelay.2.log")));
    }

    [Fact]
    public void OversizedUnicodeAndControlCharactersStayWithinOneUtf8Line()
    {
        using var fixture = new LogFixture();
        fixture.Write("start\r\nsecret-line\t" + string.Concat(Enumerable.Repeat("😀", 10_000)));

        var bytes = File.ReadAllBytes(fixture.Current);
        var decoded = new UTF8Encoding(false, true).GetString(bytes);
        Assert.Single(File.ReadAllLines(fixture.Current));
        Assert.DoesNotContain('\r', decoded);
        Assert.DoesNotContain('\t', decoded);
        Assert.DoesNotContain('\uFFFD', decoded);
        Assert.InRange(bytes.Length, 1, 2 * 1024 * 1024);
    }

    [Fact]
    public async Task SeparateProvidersWritingTogetherRetainEveryRecord()
    {
        using var fixture = new LogFixture();
        using var first = new RotatingFileLoggerProvider(fixture.ConfigurationPath);
        using var second = new RotatingFileLoggerProvider(fixture.ConfigurationPath);
        var firstLogger = first.CreateLogger("ORelay.Concurrent");
        var secondLogger = second.CreateLogger("ORelay.Concurrent");

        var tasks = new[]
        {
            Task.Run(() => WriteMany(firstLogger, "first")),
            Task.Run(() => WriteMany(secondLogger, "second")),
        };
        await Task.WhenAll(tasks);

        var lines = File.ReadAllLines(fixture.Current);
        Assert.Equal(400, lines.Length);
        Assert.Equal(400, lines.Distinct().Count());
        for (var i = 0; i < 200; i++)
        {
            Assert.Contains(lines, line => line.EndsWith($"first-{i:D3}", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.EndsWith($"second-{i:D3}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ExistingFileLockBlocksWritingUntilReleased()
    {
        using var fixture = new LogFixture();
        Directory.CreateDirectory(fixture.LogDirectory);
        using var fileLock = new FileStream(Path.Combine(fixture.LogDirectory, "orelay.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var provider = new RotatingFileLoggerProvider(fixture.ConfigurationPath);
        var logger = provider.CreateLogger("ORelay.Concurrent");
        using var started = new ManualResetEventSlim();

        var write = Task.Run(() =>
        {
            started.Set();
            Emit(logger, "after-lock");
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotSame(write, await Task.WhenAny(write, Task.Delay(200)));
        Assert.False(File.Exists(fixture.Current));

        fileLock.Dispose();
        await write;
        Assert.Contains("after-lock", File.ReadAllText(fixture.Current));
    }

    [Fact]
    public void FrameworkCategoriesAndExceptionDetailsAreExcluded()
    {
        using var fixture = new LogFixture();
        using var provider = new RotatingFileLoggerProvider(fixture.ConfigurationPath);
        var framework = provider.CreateLogger("Microsoft.AspNetCore.Hosting");
        framework.Log(LogLevel.Error, new EventId(1), "framework-message",
            new InvalidOperationException("framework-secret"), static (state, _) => state);
        Assert.False(Directory.Exists(fixture.LogDirectory));

        var app = provider.CreateLogger("ORelay.Security");
        app.Log(LogLevel.Error, new EventId(1), "safe-message",
            new InvalidOperationException("exception-secret"),
            static (state, exception) => state + (exception is null ? "" : exception.Message));
        using (app.BeginScope("scope-secret"))
            Emit(app, "inside-scope");

        var text = File.ReadAllText(fixture.Current);
        Assert.Contains("safe-message", text);
        Assert.Contains("inside-scope", text);
        Assert.DoesNotContain("framework-message", text);
        Assert.DoesNotContain("framework-secret", text);
        Assert.DoesNotContain("exception-secret", text);
        Assert.DoesNotContain("scope-secret", text);
    }

    [Fact]
    public void FileFailureDoesNotThrowAndNextRecordIsRetried()
    {
        using var fixture = new LogFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.LogDirectory)!);
        File.WriteAllText(fixture.LogDirectory, "blocked");
        using var provider = new RotatingFileLoggerProvider(fixture.ConfigurationPath);
        var logger = provider.CreateLogger("ORelay.Recovery");

        Emit(logger, "first-failed");
        File.Delete(fixture.LogDirectory);
        Emit(logger, "second-saved");

        var text = File.ReadAllText(fixture.Current);
        Assert.Contains("second-saved", text);
        Assert.DoesNotContain("first-failed", text);
    }

    private static void WriteMany(ILogger logger, string prefix)
    {
        for (var i = 0; i < 200; i++)
            Emit(logger, $"{prefix}-{i:D3}");
    }

    private static void Emit(ILogger logger, string message) =>
        logger.Log(LogLevel.Information, new EventId(1), message, null,
            static (state, _) => state);

    private sealed class LogFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "orelay-file-log-tests", Guid.NewGuid().ToString("N"));

        internal string ConfigurationPath => Path.Combine(_root, "orelay.json");
        internal string LogDirectory => Path.Combine(_root, "logs");
        internal string Current => Path.Combine(LogDirectory, "orelay.log");

        internal void Write(string message)
        {
            using var provider = new RotatingFileLoggerProvider(ConfigurationPath);
            Emit(provider.CreateLogger("ORelay.Tests"), message);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
