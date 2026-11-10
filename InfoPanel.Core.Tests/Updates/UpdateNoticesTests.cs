using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Persistence;
using InfoPanel.Updates;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Updates;

/// <summary>
/// <c>updates.json</c>: the newest version the engine has notified the user of, so each version notifies once, ever;
/// written only when a notification is shown, and read tolerantly (a bad file is logged and treated as none).
/// </summary>
public sealed class UpdateNoticesTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<LogEvent> _log = [];
    private readonly ILogger _logger;
    private readonly DataPaths _paths;

    public UpdateNoticesTests()
    {
        _logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_log)).CreateLogger();
        _paths = new DataPaths(_temp.Path);
    }

    public void Dispose() => _temp.Dispose();

    private string FilePath => Path.Combine(_temp.Path, "updates.json");

    private UpdateNotices Open() => UpdateNotices.Open(_paths, _logger);

    private List<LogEvent> Warnings => [.. _log.Where(e => e.Level == LogEventLevel.Warning)];

    [Fact]
    public void WithNoFile_EveryVersionNotifies_AndNothingIsWritten()
    {
        var notices = Open();

        Assert.Null(notices.Notified);
        Assert.True(notices.ShouldNotify("2.0.0-beta.1"));
        Assert.False(File.Exists(FilePath));
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Remember_WritesTheFormatAndTheVersion()
    {
        Open().Remember("2.0.0-beta.2");

        Assert.Equal("""{"format":"infopanel.updates/1","notified":"2.0.0-beta.2"}""", File.ReadAllText(FilePath));
    }

    [Fact]
    public void EachVersion_NotifiesOnce_AcrossRestarts()
    {
        var first = Open();
        Assert.True(first.ShouldNotify("2.0.0-beta.1"));
        first.Remember("2.0.0-beta.1");
        Assert.False(first.ShouldNotify("2.0.0-beta.1"));

        var restarted = Open();
        Assert.Equal("2.0.0-beta.1", restarted.Notified);
        Assert.False(restarted.ShouldNotify("2.0.0-beta.1"));
        Assert.True(restarted.ShouldNotify("2.0.0-beta.2"));

        restarted.Remember("2.0.0-beta.2");
        Assert.False(Open().ShouldNotify("2.0.0-beta.2"));
        Assert.Empty(Warnings);
    }

    /// <summary>A high-water mark: once a version is notified, no lower one is (a newer release withdrawn, say).</summary>
    [Fact]
    public void AVersionBelowTheHighestNotified_IsNotNotified_AcrossRestarts()
    {
        var notices = Open();
        notices.Remember("2.0.0-beta.2");
        Assert.True(notices.ShouldNotify("2.0.0-beta.3"));
        notices.Remember("2.0.0-beta.3");

        Assert.False(notices.ShouldNotify("2.0.0-beta.2"));
        var restarted = Open();
        Assert.Equal("2.0.0-beta.3", restarted.Notified);
        Assert.False(restarted.ShouldNotify("2.0.0-beta.2"));
        Assert.False(restarted.ShouldNotify("2.0.0-beta.3"));
        // By SemVer 2 precedence: numeric identifiers as numbers, a release above its pre-releases, build metadata ignored.
        Assert.False(restarted.ShouldNotify("2.0.0-beta.3+build.7"));
        Assert.True(restarted.ShouldNotify("2.0.0-beta.10"));
        Assert.True(restarted.ShouldNotify("2.0.0"));
        Assert.False(restarted.ShouldNotify("not a version"));
        Assert.Empty(Warnings);
    }

    [Fact]
    public void Asking_NeverWrites()
    {
        File.WriteAllText(FilePath, """{"format":"infopanel.updates/1","notified":"2.0.0-beta.1"}""");
        var before = File.GetLastWriteTimeUtc(FilePath);

        var notices = Open();
        notices.ShouldNotify("2.0.0-beta.1");
        notices.ShouldNotify("2.0.0-beta.2");

        Assert.Equal("""{"format":"infopanel.updates/1","notified":"2.0.0-beta.1"}""", File.ReadAllText(FilePath));
        Assert.Equal(before, File.GetLastWriteTimeUtc(FilePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"notified":"2.0.0-beta.1"}""")]
    [InlineData("""{"format":"infopanel.other/1","notified":"2.0.0-beta.1"}""")]
    [InlineData("""{"format":"infopanel.updates/2","notified":"2.0.0-beta.1"}""")]
    [InlineData("""{"format":"infopanel.updates/1","notified":3}""")]
    [InlineData("""{"format":"infopanel.updates/1","notified":""}""")]
    [InlineData("""{"format":"infopanel.updates/1","notified":"not a version"}""")]
    // A lone surrogate: valid JSON, but no string .NET can hold.
    [InlineData("""{"format":"infopanel.updates/1","notified":"\uD800"}""")]
    [InlineData("""{"format":"\uD800","notified":"2.0.0-beta.1"}""")]
    public void ABadFile_IsLogged_TreatedAsNone_AndLeftAsItIs(string content)
    {
        File.WriteAllText(FilePath, content);

        var notices = Open();

        Assert.Null(notices.Notified);
        Assert.True(notices.ShouldNotify("2.0.0-beta.1"));
        Assert.Single(Warnings);
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    [Fact]
    public void AFileTooLarge_IsLogged_AndTreatedAsNone()
    {
        File.WriteAllText(FilePath, """{"format":"infopanel.updates/1","notified":"2.0.0-beta.1","pad":""" + "\"" + new string('x', 70_000) + "\"}");

        var notices = Open();

        Assert.True(notices.ShouldNotify("2.0.0-beta.1"));
        Assert.Single(Warnings);
    }

    [Fact]
    public void AFolderInItsPlace_IsLogged_AndTreatedAsNone()
    {
        Directory.CreateDirectory(FilePath);

        var notices = Open();

        Assert.True(notices.ShouldNotify("2.0.0-beta.1"));
        Assert.Single(Warnings);
    }

    [Fact]
    public void ABadFile_IsReplaced_WhenTheNextNotificationIsShown()
    {
        File.WriteAllBytes(FilePath, Encoding.UTF8.GetBytes("not json"));

        Open().Remember("2.0.0-beta.1");

        Assert.False(Open().ShouldNotify("2.0.0-beta.1"));
    }

    /// <summary>
    /// The mark is written before the notification is shown: a write that fails is logged once and answers false, so
    /// nothing is shown for that version in this run (at most once, ever), and it isn't tried again.
    /// </summary>
    [Fact]
    public void AFailedWrite_IsLoggedOnce_AndAnswersFalse_SoNothingIsShownForThatVersion()
    {
        var notices = Open();
        Directory.CreateDirectory(FilePath);

        Assert.True(notices.ShouldNotify("2.0.0-beta.1"));
        Assert.False(notices.Remember("2.0.0-beta.1"));

        Assert.False(notices.ShouldNotify("2.0.0-beta.1"));
        Assert.False(notices.Remember("2.0.0-beta.1"));
        Assert.Single(Warnings);
        Assert.True(Directory.Exists(FilePath));
    }

    /// <summary>One limit for writing and reading: a version of 256 characters is kept across a restart; one of 257 is refused.</summary>
    [Fact]
    public void AVersionOfTheLongestKeptLength_RoundTrips_AndALongerOneIsRefused()
    {
        var longest = "2.0.1+" + new string('a', 250);
        var longer = "2.0.2+" + new string('a', 251);
        Assert.Equal((256, 257), (longest.Length, longer.Length));
        var notices = Open();

        Assert.True(notices.ShouldNotify(longest));
        Assert.True(notices.Remember(longest));
        Assert.Equal(longest, Open().Notified);

        Assert.False(notices.ShouldNotify(longer));
        Assert.Throws<ArgumentException>(() => notices.Remember(longer));
        Assert.Equal(longest, Open().Notified);
        Assert.Empty(Warnings);
    }

    [Fact]
    public void UnknownMembers_AreIgnored()
    {
        File.WriteAllText(FilePath, """{"format":"infopanel.updates/1","notified":"2.0.0-beta.1","later":true}""");

        Assert.False(Open().ShouldNotify("2.0.0-beta.1"));
        Assert.Empty(Warnings);
    }

    [Fact]
    public void TheFile_IsAnEngineEntry_WhichFreshWipes_TempFilesIncluded()
    {
        Assert.Contains("updates.json", DataPaths.EngineFiles);
        Assert.True(DataPaths.IsEngineEntry("updates.json"));
        Assert.True(DataPaths.IsEngineEntry("updates.json.0123456789abcdef0123456789abcdef.tmp"));
        Assert.Equal(Path.Combine(_temp.Path, "updates.json"), _paths.UpdatesFile);
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }
}
