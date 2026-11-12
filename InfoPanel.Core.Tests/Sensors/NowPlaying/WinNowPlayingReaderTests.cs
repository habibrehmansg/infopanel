using System.Diagnostics;
using System.Runtime.InteropServices;
using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

// The fake session waits for its operation through WinRt.Await, as the native layer does: over a fake, on any OS.
#pragma warning disable CA1416

/// <summary>
/// The Windows reader (<see cref="WinNowPlayingReader"/>) over a fake manager, on any OS: before Windows 10 version 1809
/// (or without the manager's class) it is <c>unavailable</c> · <c>notSupported</c> and asks for nothing; a session reads
/// into a snapshot (state, position and when, duration, texts, shuffle, repeat, rate, the app's name looked up once per
/// id); none is nothing playing; the manager is asked for at the first read, let go of when stopped, and after a failure
/// asked for again 5 s later; a missing media service is nothing playing only in session 0; a timed-out session fails the
/// round; the cover art is read when the track changes and while it has none, an oversized one left out for its track,
/// one that fails asked for again after a delay; cover art or a name lookup that never ends holds up neither a round nor
/// stopping, and a manager a round still uses is let go of after it; names and ids kept at most the text bound; and no
/// title, artist, album, app id or art reaches the log.
/// </summary>
public sealed class WinNowPlayingReaderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(Start);
    private readonly List<LogEvent> _events = [];
    private readonly FakeSessions _sessions = new();
    private readonly List<string> _appLookups = [];
    private int _activations;
    private Exception? _activationFails;

    private sealed class FakeProperties : IWinMediaProperties
    {
        public string? Title { get; init; } = "A Secret Song";
        public string? Artist { get; init; } = "Secret Band";
        public string? Album { get; init; } = "Secret Album";
        public int TrackNumber { get; init; } = 3;
        public IReadOnlyList<string> Genres { get; init; } = ["Secret Genre", "Other"];

        /// <summary>The thumbnail it answers (null: none), and how often it was asked.</summary>
        public WinThumbnail? Art;
        public Exception? ArtFails;
        public int ArtReads;
        public int Disposed;

        /// <summary>When set, the thumbnail's read waits for it (a stream that never answers); <see cref="ArtEntered"/> is set as it starts.</summary>
        public ManualResetEventSlim? ArtBlocks;
        public readonly ManualResetEventSlim ArtEntered = new();
        public readonly ManualResetEventSlim DisposedOnce = new();
        public bool UsedAfterDispose;

        public WinThumbnail? Thumbnail(int maxBytes, TimeSpan timeout)
        {
            if (Volatile.Read(ref Disposed) > 0)
                UsedAfterDispose = true;
            Interlocked.Increment(ref ArtReads);
            ArtEntered.Set();
            ArtBlocks?.Wait();
            if (Volatile.Read(ref Disposed) > 0)
                UsedAfterDispose = true;
            if (ArtFails is { } e)
                throw e;
            // As the native layer answers it (WinNowPlayingAbiTests holds that layer to the bound): an oversized one without bytes.
            return Art;
        }

        public void Dispose()
        {
            Interlocked.Increment(ref Disposed);
            DisposedOnce.Set();
        }
    }

    private sealed class FakeSession : IWinMediaSession
    {
        public string AppId { get; set; } = "SecretPlayer.App";
        public FakeProperties? Props = new();
        public Exception? Fails;
        public WinPlayback PlaybackInfo = new(WinPlayback.Playing, true, 1, 1.5);
        public WinTimeline TimelineInfo = new(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(10), Start);
        public int Disposed;

        /// <summary>When set, asking for the properties waits for it; <see cref="PropsEntered"/> is set as it starts.</summary>
        public ManualResetEventSlim? PropsBlocks;
        public readonly ManualResetEventSlim PropsEntered = new();

        /// <summary>The reader's own operations' slots, as its manager hands them to its sessions.</summary>
        public WinPending? Pending;

        /// <summary>
        /// When set, the properties' operation, waited for as the native layer waits (claimed, then at most 50 ms, then
        /// cancelled and kept by the reader's slot); <see cref="Released"/> is set when it is let go of.
        /// </summary>
        public WinNowPlayingAbiTests.FakeAsync? Operation;
        public int Releases;
        public readonly ManualResetEventSlim Released = new();

        public IWinMediaProperties? Properties(TimeSpan timeout)
        {
            PropsEntered.Set();
            PropsBlocks?.Wait();
            if (Operation is { } op)
            {
                Pending!.Properties.Claim();
                return WinRt.Await<IWinMediaProperties?>(op, () => Props, () =>
                {
                    Interlocked.Increment(ref Releases);
                    Released.Set();
                }, null, TimeSpan.FromMilliseconds(50), Pending.Properties, "TryGetMediaPropertiesAsync");
            }

            return Fails is { } e ? throw e : Props;
        }

        public WinPlayback Playback() => PlaybackInfo;
        public WinTimeline Timeline() => TimelineInfo;
        public void Dispose() => Disposed++;
    }

    private sealed class FakeSessions : IWinMediaSessions
    {
        public FakeSession? Session = new();
        public Exception? Fails;
        public int Disposed;
        public bool UsedAfterDispose;

        /// <summary>When set, letting go of it waits for it (a release that stalls); <see cref="DisposeEntered"/> is set as it starts.</summary>
        public ManualResetEventSlim? DisposeBlocks;
        public readonly ManualResetEventSlim DisposeEntered = new();
        public readonly ManualResetEventSlim DisposedOnce = new();

        /// <summary>The threads it was last used and let go of on.</summary>
        public int UseThread, DisposeThread;

        /// <summary>The slots the reader handed it.</summary>
        public WinPending? Pending;

        public IWinMediaSession? Current()
        {
            UseThread = Environment.CurrentManagedThreadId;
            if (Volatile.Read(ref Disposed) > 0)
                UsedAfterDispose = true;
            if (Session != null)
                Session.Pending = Pending;
            return Fails is { } e ? throw e : Session;
        }

        public void Dispose()
        {
            DisposeThread = Environment.CurrentManagedThreadId;
            DisposeEntered.Set();
            DisposeBlocks?.Wait();
            Interlocked.Increment(ref Disposed);
            DisposedOnce.Set();
        }
    }

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    /// <summary>
    /// How long a test's read waits for the round it asks for (the system's reader answers at once what is ready:
    /// <see cref="A_read_answers_at_once_what_the_readers_own_thread_has_read_and_never_waits_for_a_round"/>).
    /// </summary>
    private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(10);

    private WinNowPlayingReader Reader(bool supported = true, bool sessionZero = false, FakeSessions? sessions = null, TimeSpan? answerWait = null)
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        var manager = sessions ?? _sessions;
        return new WinNowPlayingReader(pending =>
        {
            Interlocked.Increment(ref _activations);
            manager.Pending = pending;
            return _activationFails is { } e ? throw e : manager;
        }, supported, _time, id =>
        {
            lock (_appLookups)
                _appLookups.Add(id);
            return _lookup?.Invoke(id) ?? (id == "SecretPlayer.App" ? "Secret Player" : null);
        }, log, () => sessionZero, answerWait ?? AnswerWait);
    }

    /// <summary>A test's own app name lookup, asked before the usual one.</summary>
    private Func<string, string?>? _lookup;

    private static NowPlayingSnapshot Read(WinNowPlayingReader reader)
    {
        Assert.True(reader.TryRead(out var snapshot));
        return Assert.IsType<NowPlayingSnapshot>(snapshot);
    }

    [Fact]
    public void Before_Windows_10_1809_it_is_notSupported_and_asks_for_nothing()
    {
        using var reader = Reader(supported: false);

        reader.Start();

        Assert.False(reader.TryRead(out var snapshot));
        Assert.Null(snapshot);
        Assert.Equal(0, _activations);
        Assert.Equal(SourceStateKind.Unavailable, reader.State!.Kind);
        Assert.Equal(NowPlayingSource.NotSupported, reader.State.Reason!.Code);
    }

    [Fact]
    public void Through_the_source_before_1809_it_reads_unavailable_notSupported_and_every_entry_unavailable()
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        using var source = NowPlayingSource.Create(Reader(supported: false), _time, log: log);
        source.Open();
        source.TryRead(NowPlayingSource.KeyOf("state"), out _);
        _time.Advance(NowPlayingSource.Interval);
        source.Poll();

        Assert.Equal((SourceStateKind.Unavailable, "notSupported"), (source.State.Kind, source.State.Reason!.Code));
        Assert.All(source.GetSensors(), s => Assert.Equal(SensorStatus.Unavailable, source.GetStatus(s.Key)));
        Assert.Equal(0, _activations);
    }

    [Fact]
    public void A_manager_whose_class_isnt_registered_is_notSupported_too_and_isnt_asked_for_again()
    {
        _activationFails = new COMException("RoGetActivationFactory failed", WinNowPlayingReader.ClassNotRegistered);
        using var reader = Reader();
        reader.Start();

        Assert.False(reader.TryRead(out _));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(reader.TryRead(out _));

        Assert.Equal(1, _activations);
        Assert.Equal(NowPlayingSource.NotSupported, reader.State!.Reason!.Code);
    }

    [Fact]
    public void A_session_reads_into_a_snapshot_its_apps_name_looked_up_once()
    {
        using var reader = Reader();
        reader.Start();

        var s = Read(reader);
        Read(reader);

        Assert.Equal(("A Secret Song", "Secret Band", "Secret Album", "Secret Player"), (s.Title, s.Artist, s.Album, s.App));
        Assert.Equal(NowPlayingState.Playing, s.State);
        Assert.Equal(10, s.Position);
        Assert.Equal(Start, s.PositionAt);
        Assert.Equal(200, s.Duration);
        Assert.Equal(1.5, s.Rate);
        Assert.Equal(3, s.TrackNumber);
        Assert.Equal("Secret Genre, Other", s.Genre);
        Assert.True(s.Shuffle);
        Assert.Equal(NowPlayingRepeat.One, s.Repeat);
        Assert.Null(reader.State);
        Assert.Equal(["SecretPlayer.App"], _appLookups);
        Assert.Equal(1, _activations);
        Assert.Equal(2, _sessions.Session!.Disposed);
        Assert.Equal(2, _sessions.Session.Props!.Disposed);
    }

    [Theory]
    [InlineData(WinPlayback.Paused, NowPlayingState.Paused)]
    [InlineData(3, NowPlayingState.Stopped)]
    [InlineData(0, NowPlayingState.Stopped)]
    public void The_playback_status_reads_its_state(int status, NowPlayingState state)
    {
        _sessions.Session!.PlaybackInfo = new WinPlayback(status, null, null, null);
        using var reader = Reader();
        reader.Start();

        var s = Read(reader);

        Assert.Equal(state, s.State);
        Assert.Null(s.Shuffle);
        Assert.Null(s.Repeat);
        Assert.Null(s.Rate);
    }

    [Fact]
    public void A_timeline_from_its_start_gives_position_and_duration_and_none_reads_no_duration_nor_instant()
    {
        _sessions.Session!.TimelineInfo = new WinTimeline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(20), null);
        using var reader = Reader();
        reader.Start();
        var s = Read(reader);
        Assert.Equal(15, s.Position);
        Assert.Equal(60, s.Duration);
        Assert.Null(s.PositionAt);

        _sessions.Session.TimelineInfo = default;
        s = Read(reader);
        Assert.Null(s.Duration);
    }

    [Fact]
    public void An_app_without_a_name_reads_its_id_and_empty_texts_none()
    {
        _sessions.Session!.AppId = "Unknown.App";
        _sessions.Session.Props = new FakeProperties { Title = "", Artist = null, Album = "", TrackNumber = 0, Genres = [] };
        using var reader = Reader();
        reader.Start();

        var s = Read(reader);

        Assert.Equal("Unknown.App", s.App);
        Assert.Null(s.Title);
        Assert.Null(s.Artist);
        Assert.Null(s.Album);
        Assert.Null(s.TrackNumber);
        Assert.Null(s.Genre);
    }

    [Fact]
    public void No_session_is_nothing_playing()
    {
        _sessions.Session = null;
        using var reader = Reader();
        reader.Start();

        Assert.True(reader.TryRead(out var snapshot));
        Assert.Null(snapshot);
    }

    [Fact]
    public void Nothing_is_asked_before_the_start_and_stopping_lets_go_of_the_manager()
    {
        using var reader = Reader();
        Assert.False(reader.TryRead(out _));
        Assert.Equal(0, _activations);

        reader.Start();
        Read(reader);
        reader.Stop();
        Assert.False(reader.TryRead(out _));

        // Let go of by the reader's own thread, once the stop has woken it.
        Assert.True(_sessions.DisposedOnce.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, _activations);
        Assert.Equal(1, Volatile.Read(ref _sessions.Disposed));

        reader.Start();
        Read(reader);
        Assert.Equal(2, _activations);
    }

    [Fact]
    public void A_failed_activation_or_read_is_asked_for_again_5_s_later_with_one_warning()
    {
        _activationFails = new COMException("RequestAsync failed", unchecked((int)0x80004005));
        using var reader = Reader();
        reader.Start();

        Assert.False(reader.TryRead(out _));
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(reader.TryRead(out _));
        Assert.Equal(1, _activations);

        _activationFails = null;
        _time.Advance(TimeSpan.FromSeconds(1));
        Read(reader);
        Assert.Equal(2, _activations);

        // A read that fails lets go of the manager; it is asked for again 5 s later.
        _sessions.Fails = new COMException("GetCurrentSession failed", unchecked((int)0x800706BA));
        Assert.False(reader.TryRead(out _));
        Assert.Equal(1, _sessions.Disposed);
        _sessions.Fails = null;
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(reader.TryRead(out _));
        _time.Advance(TimeSpan.FromSeconds(1));
        Read(reader);
        Assert.Equal(3, _activations);
        Assert.Equal(2, _events.Count(e => e.Level == LogEventLevel.Warning));
    }

    [Fact]
    public void In_session_0_a_logon_without_the_media_service_reads_nothing_playing_and_asks_again_5_s_later_without_a_warning()
    {
        // Session 0 (services, SSH): the manager's RequestAsync fails with ERROR_SERVICE_DOES_NOT_EXIST.
        _activationFails = new COMException("RequestAsync failed", WinNowPlayingReader.ServiceMissing);
        using var reader = Reader(sessionZero: true);
        reader.Start();

        Assert.True(reader.TryRead(out var snapshot));
        Assert.Null(snapshot);
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.True(reader.TryRead(out snapshot));
        Assert.Null(snapshot);
        Assert.Equal(1, _activations);

        _activationFails = null;
        _time.Advance(TimeSpan.FromSeconds(1));
        Read(reader);
        Assert.Equal(2, _activations);
        Assert.Null(reader.State);
        Assert.DoesNotContain(_events, e => e.Level >= LogEventLevel.Warning);
        Assert.Single(_events, e => e.RenderMessage().Contains("no media service"));
    }

    [Fact]
    public void Outside_session_0_the_same_missing_media_service_fails_the_read_and_asks_again_5_s_later_with_one_warning()
    {
        // An interactive logon whose media service is missing or broken: not "nothing playing".
        _activationFails = new COMException("RequestAsync failed", WinNowPlayingReader.ServiceMissing);
        using var reader = Reader(sessionZero: false);
        reader.Start();

        Assert.False(reader.TryRead(out var snapshot));
        Assert.Null(snapshot);
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.False(reader.TryRead(out _));
        Assert.Equal(1, _activations);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(reader.TryRead(out _));
        Assert.Equal(2, _activations);
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning);
        Assert.DoesNotContain(_events, e => e.RenderMessage().Contains("no media service"));

        _activationFails = null;
        _time.Advance(TimeSpan.FromSeconds(5));
        Read(reader);
    }

    [Fact]
    public void A_session_that_doesnt_answer_in_time_fails_the_round_and_keeps_the_manager()
    {
        using var reader = Reader();
        reader.Start();
        Read(reader);

        _sessions.Session!.Fails = new TimeoutException("TryGetMediaPropertiesAsync didn't answer within 2 s");
        Assert.False(reader.TryRead(out _));
        _sessions.Session.Fails = null;
        Read(reader);

        Assert.Equal(1, _activations);
        Assert.Equal(0, _sessions.Disposed);
    }

    [Fact]
    public void The_art_is_read_when_the_track_changes_and_while_it_has_none_never_again_once_read()
    {
        var props = _sessions.Session!.Props!;
        using var reader = Reader();
        reader.Start();

        // None yet: asked each round until it comes.
        Assert.Null(Read(reader).Art);
        Assert.Null(Read(reader).Art);
        Assert.Equal(2, props.ArtReads);
        props.Art = new WinThumbnail(new byte[] { 1, 2, 3 }, "image/png", 3);
        var s = Read(reader);
        Assert.Equal(new byte[] { 1, 2, 3 }, s.Art!.Value.ToArray());
        Assert.Equal("image/png", s.ArtType);
        Read(reader);
        Assert.Equal(3, props.ArtReads);

        // A new track: read again, its art its own.
        var next = new FakeProperties { Title = "Another Secret", Art = new WinThumbnail(new byte[] { 9 }, "image/jpeg", 1) };
        _sessions.Session.Props = next;
        s = Read(reader);
        Assert.Equal(new byte[] { 9 }, s.Art!.Value.ToArray());
        Read(reader);
        Assert.Equal(1, next.ArtReads);
    }

    [Fact]
    public void Art_over_8_MiB_is_left_out_and_not_asked_for_again_for_that_track_but_for_the_next()
    {
        // The native layer answers an oversized thumbnail without its bytes, unread.
        var props = _sessions.Session!.Props!;
        props.Art = new WinThumbnail(null, "image/png", NowPlayingSource.MaxArtBytes + 1UL);
        using var reader = Reader();
        reader.Start();

        Assert.Null(Read(reader).Art);
        Assert.Null(Read(reader).Art);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(Read(reader).Art);
        Assert.Equal(1, props.ArtReads);
        Assert.Single(_events, e => e.RenderMessage().Contains("is over"));

        var next = new FakeProperties { Title = "Another Secret", Art = new WinThumbnail(new byte[] { 4 }, "image/png", 1) };
        _sessions.Session.Props = next;
        Assert.Equal(new byte[] { 4 }, Read(reader).Art!.Value.ToArray());
    }

    [Fact]
    public void The_log_never_holds_a_title_artist_album_genre_app_or_app_id_and_the_arts_byte_count_is_logged()
    {
        _sessions.Session!.Props!.Art = new WinThumbnail(new byte[] { 1, 2, 3, 4, 5 }, "image/png", 5);
        using var reader = Reader();
        reader.Start();
        Read(reader);
        // Failures whose text names what plays: their text is never logged.
        _sessions.Session.Fails = new TimeoutException("Secret timeout");
        reader.TryRead(out _);
        _sessions.Session.Fails = null;
        Read(reader);
        _sessions.Fails = new COMException("Secret failure", unchecked((int)0x80004005));
        reader.TryRead(out _);
        _sessions.Fails = null;
        _time.Advance(TimeSpan.FromSeconds(5));
        _sessions.Session.Props = new FakeProperties { Title = "Another Secret", ArtFails = new COMException("Secret art", unchecked((int)0x80004005)) };
        Read(reader);
        reader.Stop();

        Assert.Contains(_events, e => e.RenderMessage().Contains("cover art read (5 bytes)"));
        foreach (var e in _events)
        {
            var text = e.RenderMessage() + " " + string.Join(" ", e.Properties.Values) + " " + e.Exception;
            Assert.DoesNotContain("Secret", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Cover_art_whose_read_never_ends_holds_up_neither_the_round_nor_stop_nor_dispose_and_keeps_its_properties_until_it_ends()
    {
        var ct = TestContext.Current.CancellationToken;
        using var gate = new ManualResetEventSlim();
        var props = _sessions.Session!.Props!;
        props.ArtBlocks = gate;
        var reader = Reader();
        reader.Start();
        try
        {
            var read = Task.Run(() => reader.TryRead(out _), ct);
            Assert.True(props.ArtEntered.Wait(TimeSpan.FromSeconds(5), ct));
            Assert.True(await read.WaitAsync(TimeSpan.FromSeconds(3), ct), "the round waited for the cover art");
            await Task.Run(reader.Stop, ct).WaitAsync(Prompt, ct);
            await Task.Run(reader.Dispose, ct).WaitAsync(Prompt, ct);

            // The art's read owns the media properties until it ends: not let go of under it.
            Assert.Equal(0, Volatile.Read(ref props.Disposed));
        }
        finally
        {
            gate.Set();
        }

        Assert.True(props.DisposedOnce.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(1, Volatile.Read(ref props.Disposed));
        Assert.False(props.UsedAfterDispose);
    }

    [Fact]
    public async Task An_apps_name_lookup_that_never_ends_holds_up_neither_the_round_nor_stop_nor_dispose()
    {
        var ct = TestContext.Current.CancellationToken;
        using var gate = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        _sessions.Session!.AppId = "Hanging.App";
        _lookup = id =>
        {
            entered.Set();
            gate.Wait();
            return "Hanging Player";
        };
        var reader = Reader();
        reader.Start();
        try
        {
            var read = Task.Run(() => reader.TryRead(out var s) ? s : null, ct);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), ct));
            var snapshot = await read.WaitAsync(TimeSpan.FromSeconds(3), ct);

            // Its id meanwhile; the lookup isn't asked again while it runs.
            Assert.Equal("Hanging.App", snapshot!.App);
            Assert.Equal("Hanging.App", Read(reader).App);
            Assert.Single(_appLookups);
            await Task.Run(reader.Stop, ct).WaitAsync(Prompt, ct);
            await Task.Run(reader.Dispose, ct).WaitAsync(Prompt, ct);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Stopping_during_a_read_doesnt_wait_for_it_and_the_manager_is_let_go_of_once_the_read_has_ended()
    {
        var ct = TestContext.Current.CancellationToken;
        using var gate = new ManualResetEventSlim();
        var session = _sessions.Session!;
        session.PropsBlocks = gate;
        var reader = Reader();
        reader.Start();
        Task<bool> read;
        try
        {
            read = Task.Run(() => reader.TryRead(out _), ct);
            Assert.True(session.PropsEntered.Wait(TimeSpan.FromSeconds(5), ct));
            await Task.Run(reader.Stop, ct).WaitAsync(Prompt, ct);
            await Task.Run(reader.Dispose, ct).WaitAsync(Prompt, ct);
            Assert.Equal(0, _sessions.Disposed);
        }
        finally
        {
            gate.Set();
        }

        // What the read had after the stop isn't answered, and the manager is let go of once, after it.
        Assert.False(await read.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.True(_sessions.DisposedOnce.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(1, Volatile.Read(ref _sessions.Disposed));
        Assert.False(_sessions.UsedAfterDispose);
        Assert.False(reader.TryRead(out _));
        Assert.Equal(1, _activations);
    }

    [Fact]
    public async Task A_manager_whose_release_never_ends_holds_up_neither_stop_nor_dispose_and_is_let_go_of_on_the_thread_that_used_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var gate = new ManualResetEventSlim();
        _sessions.DisposeBlocks = gate;
        var reader = Reader();
        reader.Start();
        try
        {
            Read(reader);
            await Task.Run(reader.Stop, ct).WaitAsync(Prompt, ct);
            await Task.Run(reader.Dispose, ct).WaitAsync(Prompt, ct);
            Assert.True(_sessions.DisposeEntered.Wait(TimeSpan.FromSeconds(5), ct));
        }
        finally
        {
            gate.Set();
        }

        Assert.True(_sessions.DisposedOnce.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.Equal(1, Volatile.Read(ref _sessions.Disposed));
        // The reader's own thread let go of it, the one that used it: never the caller of Stop or Dispose.
        Assert.Equal(_sessions.UseThread, _sessions.DisposeThread);
        Assert.NotEqual(Environment.CurrentManagedThreadId, _sessions.DisposeThread);
        Assert.False(_sessions.UsedAfterDispose);
    }

    [Fact]
    public async Task A_round_held_up_in_its_properties_across_stop_and_dispose_starts_neither_the_art_nor_a_name_lookup_when_it_resumes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var gate = new ManualResetEventSlim();
        using var looked = new ManualResetEventSlim();
        var session = _sessions.Session!;
        var props = session.Props!;
        session.PropsBlocks = gate;
        _lookup = _ =>
        {
            looked.Set();
            return null;
        };
        var reader = Reader();
        reader.Start();
        Task<bool> read;
        try
        {
            read = Task.Run(() => reader.TryRead(out _), ct);
            Assert.True(session.PropsEntered.Wait(TimeSpan.FromSeconds(5), ct));
            await Task.Run(reader.Stop, ct).WaitAsync(Prompt, ct);
            await Task.Run(reader.Dispose, ct).WaitAsync(Prompt, ct);
        }
        finally
        {
            gate.Set();
        }

        // The round ends: what it had is let go of, and nothing more is started for it.
        Assert.False(await read.WaitAsync(TimeSpan.FromSeconds(5), ct));
        Assert.True(props.DisposedOnce.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.True(_sessions.DisposedOnce.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.False(looked.Wait(TimeSpan.FromMilliseconds(500), ct), "an app's name was looked up after the stop");
        Assert.Equal(0, Volatile.Read(ref props.ArtReads));
        Assert.Empty(_appLookups);
        Assert.Equal(1, Volatile.Read(ref props.Disposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_properties_read_not_done_in_time_is_closed_and_let_go_of_by_the_reader_once_it_ends_after_a_stop_or_dispose_without_another_read(bool dispose)
    {
        var ct = TestContext.Current.CancellationToken;
        var session = _sessions.Session!;
        var op = new WinNowPlayingAbiTests.FakeAsync(WinRt.Started);
        session.Operation = op;
        var reader = Reader();
        reader.Start();

        // Not done in time: the round fails, the operation cancelled and kept.
        Assert.False(reader.TryRead(out _));
        if (dispose)
            reader.Dispose();
        else
            reader.Stop();

        // Still running: neither closed nor let go of.
        Assert.False(session.Released.Wait(TimeSpan.FromMilliseconds(300), ct));
        Assert.Equal((1, 0), (op.Cancels, op.Closes));

        // It ends, with nothing read since: the reader's own thread closes it, then lets go of it.
        op.Now = WinRt.Canceled;
        Assert.True(session.Released.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.Equal((1, 0, 1), (op.Closes, op.RejectedCloses, Volatile.Read(ref session.Releases)));
        reader.Dispose();
    }

    [Fact]
    public void Two_readers_each_keep_their_own_operations_neither_refused_for_the_others_nor_letting_go_of_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new WinNowPlayingAbiTests.FakeAsync(WinRt.Started);
        var second = new WinNowPlayingAbiTests.FakeAsync(WinRt.Started);
        var other = new FakeSessions();
        _sessions.Session!.Operation = first;
        other.Session!.Operation = second;
        using var one = Reader();
        using var two = Reader(sessions: other);
        one.Start();
        two.Start();

        // Both time out, the second while the first's still runs: it was started all the same, and each is kept by its reader.
        Assert.False(one.TryRead(out _));
        Assert.False(two.TryRead(out _));
        Assert.Equal((1, 1), (first.Cancels, second.Cancels));
        Assert.NotSame(_sessions.Pending, other.Pending);
        Assert.Equal((0, 0), (first.Closes, second.Closes));
        Assert.Equal((0, 0), (Volatile.Read(ref _sessions.Session.Releases), Volatile.Read(ref other.Session.Releases)));

        // The first ends: its reader closes it and lets go of it; the second's, still running, stays kept by its own.
        first.Now = WinRt.Completed;
        Assert.True(_sessions.Session.Released.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.Equal((1, 0), (first.Closes, first.RejectedCloses));
        Assert.False(other.Session.Released.Wait(TimeSpan.FromMilliseconds(300), ct));
        Assert.Equal((0, 0), (second.Closes, Volatile.Read(ref other.Session.Releases)));

        second.Now = WinRt.Canceled;
        Assert.True(other.Session.Released.Wait(TimeSpan.FromSeconds(5), ct));
        Assert.Equal((1, 0), (second.Closes, second.RejectedCloses));
    }

    [Fact]
    public void A_read_answers_at_once_what_the_readers_own_thread_has_read_and_never_waits_for_a_round()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = _sessions.Session!;
        using var gate = new ManualResetEventSlim();
        session.PropsBlocks = gate;
        using var reader = Reader(answerWait: TimeSpan.Zero);
        reader.Start();
        try
        {
            // Nothing read yet: none. The round it asked for is held up in Windows, and reads still answer at once.
            Assert.False(reader.TryRead(out _));
            Assert.True(session.PropsEntered.Wait(TimeSpan.FromSeconds(5), ct));
            var watch = Stopwatch.StartNew();
            Assert.False(reader.TryRead(out _));
            Assert.True(watch.Elapsed < Prompt, $"the read took {watch.Elapsed}");
        }
        finally
        {
            gate.Set();
        }

        // Once a round has answered, a read gives what it read.
        NowPlayingSnapshot? snapshot = null;
        Assert.True(SpinWait.SpinUntil(() => reader.TryRead(out snapshot), TimeSpan.FromSeconds(5)));
        Assert.Equal("A Secret Song", snapshot!.Title);

        // Held up again for longer than an answer is kept: the last one isn't given any more.
        using var held = new ManualResetEventSlim();
        session.PropsBlocks = held;
        session.PropsEntered.Reset();
        try
        {
            reader.TryRead(out _);
            Assert.True(session.PropsEntered.Wait(TimeSpan.FromSeconds(5), ct));
            Assert.True(reader.TryRead(out _));
            _time.Advance(WinNowPlayingReader.AnswerAge + TimeSpan.FromSeconds(1));
            Assert.False(reader.TryRead(out _));
        }
        finally
        {
            held.Set();
        }
    }

    [Fact]
    public void Cover_art_that_fails_to_read_is_asked_for_again_after_5_s_doubling_up_to_a_minute_with_one_warning()
    {
        var props = _sessions.Session!.Props!;
        props.ArtFails = new TimeoutException("OpenReadAsync didn't answer within 2 s");
        using var reader = Reader();
        reader.Start();

        Assert.Null(Read(reader).Art);
        Assert.Null(Read(reader).Art);
        Assert.Equal(1, props.ArtReads);

        // 5 s, then 10, 20, 40, and 60 s at most.
        var reads = 1;
        foreach (var seconds in new[] { 5, 10, 20, 40, 60, 60 })
        {
            _time.Advance(TimeSpan.FromSeconds(seconds - 1));
            Read(reader);
            Assert.Equal(reads, props.ArtReads);
            _time.Advance(TimeSpan.FromSeconds(1));
            Read(reader);
            Assert.Equal(++reads, props.ArtReads);
        }

        // Once it reads, it is shown.
        props.ArtFails = null;
        props.Art = new WinThumbnail(new byte[] { 7, 8 }, "image/png", 2);
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(new byte[] { 7, 8 }, Read(reader).Art!.Value.ToArray());
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public void An_apps_name_and_its_id_are_kept_at_most_the_text_bound()
    {
        var bound = NowPlayingSource.MaxTextChars + 1;
        _lookup = id => id == "Long.App" ? new string('n', 100_000) : null;
        _sessions.Session!.AppId = "Long.App";
        using var reader = Reader();
        reader.Start();

        Assert.Equal(new string('n', bound), Read(reader).App);

        var longId = new string('i', 100_000);
        _sessions.Session.AppId = longId;
        Assert.Equal(longId[..bound], Read(reader).App);
    }
}
