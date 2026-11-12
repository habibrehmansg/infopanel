using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The Windows reader on this PC's own media sessions, opt-in (<c>INFOPANEL_WINDOWS_TESTS=1</c> on Windows; skipped
/// elsewhere): the manager is had and the current session read, every call through the hand-written ABI (passes with
/// nothing playing too, and in session 0, which has no sessions); the source over it reads its state live; and whatever
/// plays never reaches the log. What it read is written to the test output as kinds and counts, never the texts.
/// </summary>
public sealed class WinNowPlayingLiveTests
{
    private static bool Enabled => OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INFOPANEL_WINDOWS_TESTS") == "1";

    private const string Skip = "Reads this PC's media sessions: set INFOPANEL_WINDOWS_TESTS=1 on Windows to run it.";

    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    [Fact]
    public void The_current_session_is_read_through_the_ABI_or_none_and_nothing_it_plays_is_logged()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;
        var events = new List<LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        // Each read waits for the round it asks for (the system's reader answers at once what its thread has read).
        using var reader = new WinNowPlayingReader(WinMediaSessions.Activate, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763), TimeProvider.System, WinAppNames.Of, log,
            WinNowPlayingReader.InSessionZero, answerWait: TimeSpan.FromSeconds(10));
        Assert.Null(reader.State);
        reader.Start();

        // Twice: the second round reads the art only if the first found none.
        Assert.True(reader.TryRead(out var first), "the first read failed: " + string.Join(" | ", events.Select(e => e.RenderMessage())));
        Assert.True(reader.TryRead(out var snapshot), "the second read failed: " + string.Join(" | ", events.Select(e => e.RenderMessage())));
        reader.Stop();

        if (snapshot is null)
        {
            Log("no current session (nothing playing)");
        }
        else
        {
            Log($"state {snapshot.State}, title {snapshot.Title?.Length} chars, artist {snapshot.Artist?.Length}, album {snapshot.Album?.Length}, app {snapshot.App?.Length} chars, " +
                $"position {snapshot.Position:0.0} at {snapshot.PositionAt:O}, duration {snapshot.Duration:0.0}, rate {snapshot.Rate}, shuffle {snapshot.Shuffle}, repeat {snapshot.Repeat}, " +
                $"track {snapshot.TrackNumber}, genre {snapshot.Genre?.Length} chars, art {snapshot.Art?.Length} bytes {snapshot.ArtType}");
            foreach (var text in new[] { snapshot.Title, snapshot.Artist, snapshot.Album }.Where(t => t is { Length: > 2 }))
                Assert.All(events, e => Assert.DoesNotContain(text!, e.RenderMessage() + " " + string.Join(" ", e.Properties.Values)));
        }

        foreach (var e in events)
            Log($"log: {e.Level} {e.RenderMessage()}");
    }

    [Fact]
    public void The_source_over_it_reads_its_state_live()
    {
        Assert.SkipUnless(Enabled, Skip);
        if (!OperatingSystem.IsWindows())
            return;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var source = NowPlayingSource.CreateSystem(time);
        source.Open();
        Assert.False(source.TryRead(NowPlayingSource.KeyOf("state"), out _));

        // The first poll in use starts the reader, whose thread hasn't read yet: starting. A later poll has its first round.
        time.Advance(NowPlayingSource.Interval);
        source.Poll();
        Assert.Equal(SourceStateKind.Starting, source.State.Kind);
        var polls = 1;
        while (source.GetStatus(NowPlayingSource.KeyOf("state")) != SensorStatus.Live && polls++ < 20)
        {
            Thread.Sleep(250);
            time.Advance(NowPlayingSource.Interval);
            source.Poll();
        }

        Assert.Equal(SensorStatus.Live, source.GetStatus(NowPlayingSource.KeyOf("state")));
        Assert.Equal(SourceStateKind.Running, source.State.Kind);
        var state = source.ReadState(NowPlayingSource.KeyOf("state")).Reading.ValueText;
        Assert.Contains(state, new[] { "Playing", "Paused", "Stopped" });
        Log($"state {state}");
    }
}
