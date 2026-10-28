using System.Text;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Tests.Hosting;

/// <summary>
/// The engine's recent log entries (1.4.x keeps this run's last 2,000 lines for its Logs page,
/// <c>Logging/InMemoryLogSink.cs</c>) and its settable log level.
/// </summary>
public sealed class RecentLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Serilog.ILogger LoggerOver(RecentLog recent) =>
        new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(recent).CreateLogger();

    // ---- the buffer ---------------------------------------------------------------------------------------------

    [Fact]
    public void Entries_HaveSequenceNumbersFrom1_TheirLevelSourceTimeAndRenderedMessage()
    {
        var recent = new RecentLog();
        var logger = LoggerOver(recent);
        Assert.Equal(0, recent.Latest);
        Assert.Empty(recent.After(0));

        logger.ForContext("SourceContext", "InfoPanel.Some.Thing").Information("Loaded {Name} in {Ms} ms", "Wide bar", 12);
        logger.Warning(new InvalidOperationException("boom"), "Something failed");
        logger.Debug("Detail");

        var entries = recent.After(0);
        Assert.Equal([1L, 2L, 3L], entries.Select(e => e.Seq));
        Assert.Equal(3, recent.Latest);
        Assert.Equal(("information", "InfoPanel.Some.Thing", "Loaded Wide bar in 12 ms", null),
            (entries[0].Level, entries[0].Source, entries[0].Message, entries[0].Exception));
        Assert.Equal(("warning", "", "Something failed"), (entries[1].Level, entries[1].Source, entries[1].Message));
        Assert.Contains("System.InvalidOperationException: boom", entries[1].Exception);
        Assert.Equal("debug", entries[2].Level);
        Assert.True(entries[0].Time <= entries[1].Time && entries[1].Time - entries[0].Time < TimeSpan.FromMinutes(1));
        Assert.Equal([3L], recent.After(2).Select(e => e.Seq));
        Assert.Empty(recent.After(3));
        Assert.Empty(recent.After(long.MaxValue));
    }

    [Fact]
    public void EveryLevel_HasItsName()
    {
        var recent = new RecentLog();
        var logger = LoggerOver(recent);

        foreach (var level in Enum.GetValues<LogEventLevel>())
            logger.Write(level, "x");

        Assert.Equal(["verbose", "debug", "information", "warning", "error", "fatal"], recent.After(0).Select(e => e.Level));
    }

    [Fact]
    public void TheNewest2000Entries_AreKept()
    {
        var recent = new RecentLog();
        var logger = LoggerOver(recent);

        for (var i = 1; i <= 2500; i++)
            logger.Information("Line {N}", i);

        var entries = recent.After(0);
        Assert.Equal(RecentLog.MaxEntries, entries.Count);
        Assert.Equal(2000, RecentLog.MaxEntries);
        Assert.Equal(501, entries[0].Seq);
        Assert.Equal("Line 501", entries[0].Message);
        Assert.Equal(2500, entries[^1].Seq);
        Assert.Equal(2500, recent.Latest);
        // A gap from what a reader had: entries 2–500 were dropped.
        Assert.Equal(501, recent.After(1)[0].Seq);
    }

    [Fact]
    public void LongMessagesAndExceptions_AreCut_AndTheBufferKeepsAtMost2MiB()
    {
        var recent = new RecentLog();
        var logger = LoggerOver(recent);
        var long1 = new string('m', 10_000);
        var long2 = new string('e', 20_000);

        logger.Error(new InvalidOperationException(long2), "{Text}", long1);

        var entry = Assert.Single(recent.After(0));
        Assert.Equal(RecentLog.MaxMessageLength, entry.Message.Length);
        Assert.Equal(4096, RecentLog.MaxMessageLength);
        Assert.EndsWith("…", entry.Message);
        Assert.Equal(RecentLog.MaxExceptionLength, entry.Exception!.Length);
        Assert.Equal(8192, RecentLog.MaxExceptionLength);
        Assert.EndsWith("…", entry.Exception);

        // Entries of about 12 KiB each: the oldest go once the text passes 2 MiB.
        for (var i = 0; i < 400; i++)
            logger.Error(new InvalidOperationException(long2), "{Text}", long1);

        var entries = recent.After(0);
        var bytes = entries.Sum(e => Encoding.UTF8.GetByteCount(e.Message) + Encoding.UTF8.GetByteCount(e.Exception ?? "") + Encoding.UTF8.GetByteCount(e.Source));
        Assert.InRange(bytes, 2 * 1024 * 1024 - 13 * 1024, 2 * 1024 * 1024);
        Assert.Equal(401, entries[^1].Seq);
        Assert.True(entries[0].Seq > 1);
    }

    [Fact]
    public void ALongSource_IsCut_SoNoEntryCanPushTheBufferPast2MiB()
    {
        var recent = new RecentLog();
        var logger = LoggerOver(recent);
        for (var i = 1; i <= 3; i++)
            logger.Information("Line {N}", i);

        logger.ForContext("SourceContext", new string('s', RecentLog.MaxBytes + 1)).Information("Line 4");

        var entries = recent.After(0);
        Assert.Equal([1L, 2L, 3L, 4L], entries.Select(e => e.Seq));
        Assert.Equal(RecentLog.MaxSourceLength, entries[^1].Source.Length);
        Assert.Equal(256, RecentLog.MaxSourceLength);
        Assert.EndsWith("…", entries[^1].Source);
        Assert.InRange(entries.Sum(e => Encoding.UTF8.GetByteCount(e.Message) + Encoding.UTF8.GetByteCount(e.Exception ?? "") + Encoding.UTF8.GetByteCount(e.Source)),
            0, RecentLog.MaxBytes);
    }

    [Fact]
    public void ManyThreadsLogging_GetDistinctConsecutiveSequenceNumbers()
    {
        var recent = new RecentLog();
        var logger = LoggerOver(recent);

        Parallel.For(0, 1000, i => logger.Information("{I}", i));

        Assert.Equal(Enumerable.Range(1, 1000).Select(i => (long)i), recent.After(0).Select(e => e.Seq));
    }

    // ---- on the engine host -------------------------------------------------------------------------------------

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail("Timed out waiting for the condition.");
            await Task.Delay(20, Ct);
        }
    }

    private static string LogFiles(EngineFactory factory) =>
        string.Join("\n", Directory.GetFiles(Path.Combine(factory.DataDir, "logs")).Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));

    [Fact]
    public async Task TheSignInLinksToken_NeverReachesTheBuffer_WhichGetsWhatTheFileGets()
    {
        using var factory = new EngineFactory();
        using (var client = factory.CreateApiClient())
            Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/session", Ct)).StatusCode);
        var recent = factory.Services.GetRequiredService<RecentLog>();

        await Until(() => recent.After(0).Any(e => e.Message.Contains("Open the editor")) && recent.After(0).Any(e => e.Message.Contains("Pair the client")));

        var entries = recent.After(0);
        Assert.Contains(entries, e => e.Message.Contains("/auth/<token>"));
        Assert.DoesNotContain(entries, e => e.Message.Contains(EngineFactory.Token) || (e.Exception?.Contains(EngineFactory.Token) ?? false));
        Assert.DoesNotContain(EngineFactory.Token, LogFiles(factory));
    }

    [Fact]
    public async Task TheLogLevelSetting_FiltersTheFileAndTheBuffer_Live()
    {
        using var factory = new EngineFactory();
        var settings = factory.Services.GetRequiredService<SettingsStore>();
        var recent = factory.Services.GetRequiredService<RecentLog>();
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InfoPanel.Tests.LevelProbe");
        var marker = Guid.NewGuid().ToString("N");
        bool InBuffer(string text) => recent.After(0).Any(e => e.Message.Contains(text));

        // Information by default: debug is left out.
        logger.LogDebug("debug-1 {Marker}", marker);
        logger.LogInformation("information-1 {Marker}", marker);
        await Until(() => InBuffer($"information-1 {marker}"));
        Assert.False(InBuffer($"debug-1 {marker}"));

        settings.Apply(JsonDocument.Parse("""{"logLevel":"debug"}""").RootElement);
        logger.LogDebug("debug-2 {Marker}", marker);
        Assert.True(InBuffer($"debug-2 {marker}"));

        settings.Apply(JsonDocument.Parse("""{"logLevel":"warning"}""").RootElement);
        logger.LogInformation("information-3 {Marker}", marker);
        logger.LogWarning("warning-3 {Marker}", marker);
        Assert.True(InBuffer($"warning-3 {marker}"));
        Assert.False(InBuffer($"information-3 {marker}"));

        // The file got exactly what the buffer got.
        string file = "";
        await Until(() => (file = LogFiles(factory)).Contains($"warning-3 {marker}"));
        Assert.Contains($"information-1 {marker}", file);
        Assert.Contains($"debug-2 {marker}", file);
        Assert.DoesNotContain($"debug-1 {marker}", file);
        Assert.DoesNotContain($"information-3 {marker}", file);
    }

    [Fact]
    public async Task AStoredLogLevel_AppliesFromTheStart()
    {
        using var factory = new EngineFactory();
        Directory.CreateDirectory(factory.DataDir);
        File.WriteAllText(Path.Combine(factory.DataDir, "settings.json"), """{"format":"infopanel.settings/1","logLevel":"error"}""");
        var recent = factory.Services.GetRequiredService<RecentLog>();
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InfoPanel.Tests.LevelProbe");
        var marker = Guid.NewGuid().ToString("N");

        logger.LogWarning("warning {Marker}", marker);
        logger.LogError("error {Marker}", marker);

        await Until(() => recent.After(0).Any(e => e.Message.Contains($"error {marker}")));
        Assert.DoesNotContain(recent.After(0), e => e.Message.Contains($"warning {marker}"));
    }
}
