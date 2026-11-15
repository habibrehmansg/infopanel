using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The Now Playing source (<c>infopanel.now-playing</c>) over a fake reader: its 14 entries, the position extrapolated
/// while playing and frozen while paused, what is unavailable (never 0 or empty), nothing playing, the demand (the
/// reader started by a read and stopped 2 minutes after the last; a listing, a status and a peek start nothing), the
/// reader's states, texts cut, the cover read as a token of what it keeps, its main colour published with it, and no title, artist, album, app or cover
/// in the log.
/// </summary>
public sealed class NowPlayingSourceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _time = new(Start);
    private readonly FakeReader _reader = new();
    private readonly List<LogEvent> _events = [];
    private NowPlayingSource? _source;

    public void Dispose() => _source?.Dispose();

    /// <summary>A reader whose snapshot the test sets; it counts its starts and stops.</summary>
    private sealed class FakeReader : INowPlayingReader
    {
        public int Starts;
        public int Stops;
        public bool Running;

        /// <summary>Whether it has anything to say yet (a helper that hasn't printed its first line has not).</summary>
        public bool HasData = true;

        public NowPlayingSnapshot? Snapshot;
        public SourceState? ReaderState;

        public SourceState? State => ReaderState;

        public void Start()
        {
            Starts++;
            Running = true;
        }

        public void Stop()
        {
            Stops++;
            Running = false;
        }

        public bool TryRead(out NowPlayingSnapshot? snapshot)
        {
            snapshot = Snapshot;
            return Running && HasData;
        }

        public void Dispose()
        {
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

    /// <summary>The source over the fake reader, discovered and polled once, nothing read yet.</summary>
    private NowPlayingSource Open()
    {
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
        _source = NowPlayingSource.Create(_reader, _time, log: log);
        _source.Open();
        return _source;
    }

    /// <summary>The source opened, a key read (so in use) and polled twice: the reader started, then read.</summary>
    private NowPlayingSource OpenInUse()
    {
        var source = Open();
        source.TryRead(Key("title"), out _);
        Round();
        Round();
        return source;
    }

    /// <summary>One poll, a second after the last (the source's interval); or <paramref name="after"/>.</summary>
    private void Round(TimeSpan? after = null)
    {
        _time.Advance(after ?? NowPlayingSource.Interval);
        _source!.Poll();
    }

    private static SensorKey Key(string entry) => NowPlayingSource.KeyOf(entry);

    private static NowPlayingSnapshot Song(NowPlayingState state = NowPlayingState.Playing, double? position = 10, double? duration = 200, DateTimeOffset? at = null, double? rate = 1) => new()
    {
        Title = "A Secret Song",
        Artist = "Secret Band",
        Album = "Secret Album",
        App = "Secret Player",
        State = state,
        Position = position,
        PositionAt = at ?? Start,
        Rate = rate,
        Duration = duration,
        TrackNumber = 3,
        Genre = "Secret Genre",
        Shuffle = false,
        Repeat = NowPlayingRepeat.All,
    };

    private string? Text(string entry)
    {
        var state = _source!.ReadState(Key(entry));
        if (!state.HasReading)
            return null;
        Assert.Equal(SensorKind.Text, state.Type);
        return state.Reading.ValueText;
    }

    /// <summary>The cover's reading: an image sensor's reference (its token), typed image.</summary>
    private string? Art()
    {
        var state = _source!.ReadState(Key("art"));
        if (!state.HasReading)
            return null;
        Assert.Equal(SensorKind.Image, state.Type);
        return state.Reading.ValueText;
    }

    private double? Number(string entry)
    {
        var state = _source!.ReadState(Key(entry));
        if (!state.HasReading)
            return null;
        Assert.Equal(SensorKind.Number, state.Type);
        return state.Reading.ValueNow;
    }

    private SensorStatus Status(string entry) => _source!.GetStatus(Key(entry));

    // ---- Entries ----

    [Fact]
    public void It_lists_its_14_entries_with_their_names_kinds_and_units_under_infopanel_now_playing()
    {
        var source = Open();

        string[] ids = ["title", "artist", "album", "app", "state", "position", "duration", "progress", "track-number", "genre", "shuffle", "repeat", "art", "art-color"];
        var sensors = source.GetSensors();
        Assert.Equal(ids.Select(id => $"infopanel:/now-playing/{id}"), sensors.Select(s => s.Key.ToString()));
        Assert.Equal(["Title", "Artist", "Album", "App", "State", "Position", "Duration", "Progress", "Track Number", "Genre", "Shuffle", "Repeat", "Cover Art", "Cover Colour"], sensors.Select(s => s.Name));
        Assert.Equal(["", "", "", "", "", "s", "s", "%", "", "", "", "", "", ""], sensors.Select(s => s.Unit));
        Assert.All(sensors, s => Assert.Equal("infopanel.now-playing", s.SourceId));
        Assert.All(sensors, s => Assert.Equal(NowPlayingSource.Name, s.Group));
        Assert.Equal("infopanel.now-playing", source.Id);
        Assert.Equal(
            [SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Number, SensorKind.Number, SensorKind.Number, SensorKind.Number, SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Image, SensorKind.Text],
            ids.Select(id => source.PeekState(Key(id)).Type));
    }

    [Fact]
    public void A_song_reads_its_title_artist_album_app_state_numbers_and_words()
    {
        _reader.Snapshot = Song();
        OpenInUse();

        Assert.Equal("A Secret Song", Text("title"));
        Assert.Equal("Secret Band", Text("artist"));
        Assert.Equal("Secret Album", Text("album"));
        Assert.Equal("Secret Player", Text("app"));
        Assert.Equal("Playing", Text("state"));
        Assert.Equal(200, Number("duration"));
        Assert.Equal(3, Number("track-number"));
        Assert.Equal("Secret Genre", Text("genre"));
        Assert.Equal("Off", Text("shuffle"));
        Assert.Equal("All", Text("repeat"));
        Assert.Equal(SensorStatus.Live, Status("title"));
        Assert.Equal(SourceStateKind.Running, _source!.State.Kind);
    }

    [Theory]
    [InlineData(true, NowPlayingRepeat.Off, "On", "Off")]
    [InlineData(false, NowPlayingRepeat.One, "Off", "One")]
    [InlineData(null, null, null, null)]
    public void Shuffle_and_repeat_read_their_words_and_unavailable_when_the_player_says_nothing(bool? shuffle, NowPlayingRepeat? repeat, string? shuffleText, string? repeatText)
    {
        _reader.Snapshot = Song() with { Shuffle = shuffle, Repeat = repeat };
        OpenInUse();

        Assert.Equal(shuffleText, Text("shuffle"));
        Assert.Equal(repeatText, Text("repeat"));
    }

    // ---- Position and progress ----

    [Fact]
    public void While_playing_the_position_moves_on_from_the_systems_last_update_by_the_rate_and_progress_follows()
    {
        // 10 s in at Start; the source reads it 2 s later (two polls).
        _reader.Snapshot = Song(position: 10, duration: 200);
        OpenInUse();

        Assert.Equal(12, Number("position"));
        Assert.Equal(6, Number("progress"));
        Round();
        Assert.Equal(13, Number("position"));

        // Twice the speed: 2 s a second.
        _reader.Snapshot = Song(position: 20, duration: 200, at: _time.GetUtcNow(), rate: 2);
        Round();
        Assert.Equal(22, Number("position"));
        Assert.Equal(11, Number("progress"));
    }

    [Fact]
    public void While_paused_the_position_stays_and_it_is_clamped_to_the_track()
    {
        _reader.Snapshot = Song(NowPlayingState.Paused, position: 10, duration: 200);
        OpenInUse();
        Round();
        Round();
        Assert.Equal(10, Number("position"));
        Assert.Equal("Paused", Text("state"));

        // Playing past the end stops at the duration; a position before the start reads 0.
        _reader.Snapshot = Song(position: 199, duration: 200);
        Round(TimeSpan.FromSeconds(5));
        Assert.Equal(200, Number("position"));
        Assert.Equal(100, Number("progress"));
        _reader.Snapshot = Song(NowPlayingState.Paused, position: -3, duration: 200);
        Round();
        Assert.Equal(0, Number("position"));
    }

    [Fact]
    public void Without_a_duration_progress_and_duration_are_unavailable_never_0_and_the_position_still_reads()
    {
        _reader.Snapshot = Song(position: 30, duration: null) with { TrackNumber = 0, Genre = "", Album = null };
        OpenInUse();

        Assert.Equal(32, Number("position"));
        Assert.Null(Number("duration"));
        Assert.Null(Number("progress"));
        Assert.Null(Number("track-number"));
        Assert.Null(Text("genre"));
        Assert.Null(Text("album"));
        Assert.Equal(SensorStatus.Unavailable, Status("progress"));
        Assert.Equal(SensorStatus.Unavailable, Status("album"));

        // A duration of 0 is none.
        _reader.Snapshot = Song(duration: 0);
        Round();
        Assert.Null(Number("duration"));
        Assert.Null(Number("progress"));
    }

    // ---- Nothing playing ----

    [Fact]
    public void Nothing_playing_reads_Stopped_and_every_other_entry_unavailable()
    {
        _reader.Snapshot = null;
        OpenInUse();

        Assert.Equal("Stopped", Text("state"));
        foreach (var sensor in _source!.GetSensors().Where(s => s.Key != Key("state")))
            Assert.Equal(SensorStatus.Unavailable, _source.GetStatus(sensor.Key));
        Assert.Equal(SourceStateKind.Running, _source.State.Kind);

        // A song that ends: its readings go at once.
        _reader.Snapshot = Song();
        Round();
        Assert.Equal("A Secret Song", Text("title"));
        _reader.Snapshot = null;
        Round();
        Assert.Null(Text("title"));
        Assert.Null(Number("position"));
        Assert.Equal("Stopped", Text("state"));
    }

    // ---- Demand ----

    [Fact]
    public void Nothing_starts_the_reader_but_a_read_listing_status_and_peek_dont_and_it_is_starting_until_then()
    {
        _reader.Snapshot = Song();
        var source = Open();
        Round();

        source.GetSensors();
        source.GetStatus(Key("title"));
        source.PeekState(Key("title"));
        source.TryPeek(Key("title"), out _);
        Round();
        Round();

        Assert.Equal(0, _reader.Starts);
        Assert.Equal(SourceStateKind.Starting, source.State.Kind);
        Assert.Equal(SensorStatus.Unavailable, Status("title"));
    }

    [Theory]
    [InlineData("TryRead")]
    [InlineData("ReadState")]
    [InlineData("NoteUse")]
    public void A_read_starts_the_reader_and_the_values_come_at_the_next_poll(string use)
    {
        _reader.Snapshot = Song();
        var source = Open();
        Round();

        switch (use)
        {
            case "TryRead":
                Assert.False(source.TryRead(Key("artist"), out _));
                break;
            case "ReadState":
                Assert.False(source.ReadState(Key("artist")).HasReading);
                break;
            default:
                source.NoteUse(Key("artist"));
                break;
        }
        Assert.Equal(0, _reader.Starts);

        Round();
        Assert.Equal(1, _reader.Starts);
        Assert.Equal("Secret Band", Text("artist"));
        Assert.Equal(SourceStateKind.Running, source.State.Kind);
    }

    [Fact]
    public void Two_minutes_after_the_last_read_the_reader_stops_its_readings_go_stale_then_unavailable_and_a_read_starts_it_again()
    {
        _reader.Snapshot = Song();
        var source = OpenInUse();
        Assert.Equal(1, _reader.Starts);

        // Read again 60 s in: in use until 2 minutes after that.
        Round(TimeSpan.FromSeconds(60));
        source.TryRead(Key("title"), out _);
        Round(TimeSpan.FromSeconds(119));
        Assert.Equal(0, _reader.Stops);
        Assert.True(_reader.Running);

        Round(TimeSpan.FromSeconds(2));
        Assert.Equal(1, _reader.Stops);
        Assert.False(_reader.Running);
        // Peeked, not read: no use.
        Assert.Equal(SensorStatus.Stale, source.PeekState(Key("title")).Status);
        Round(TimeSpan.FromSeconds(31));
        Assert.Equal(SensorStatus.Unavailable, source.PeekState(Key("title")).Status);
        Assert.Equal(1, _reader.Starts);

        // A read starts it again.
        source.TryRead(Key("title"), out _);
        Round();
        Assert.Equal(2, _reader.Starts);
        Assert.Equal("A Secret Song", Text("title"));
    }

    [Fact]
    public void A_reader_with_nothing_to_say_yet_fails_the_round_its_last_readings_kept_stale()
    {
        _reader.Snapshot = Song();
        OpenInUse();
        Assert.Equal("A Secret Song", Text("title"));

        _reader.HasData = false;
        Round();
        Assert.Equal(SensorStatus.Stale, Status("title"));
        Assert.Equal("A Secret Song", Text("title"));
    }

    [Fact]
    public void Its_state_is_the_readers_when_the_reader_has_one()
    {
        _reader.ReaderState = SourceState.UnavailableBecause(new SourceReason(NowPlayingSource.HelperMissing, "missing"));
        var source = Open();
        Round();
        Assert.Equal(SourceStateKind.Unavailable, source.State.Kind);
        Assert.Equal("helperMissing", source.State.Reason!.Code);

        _reader.ReaderState = null;
        source.TryRead(Key("title"), out _);
        Round();
        Assert.Equal(SourceStateKind.Running, source.State.Kind);
    }

    [Fact]
    public void Stopping_the_source_stops_the_reader()
    {
        _reader.Snapshot = Song();
        OpenInUse();
        _source!.Dispose();
        Assert.Equal(1, _reader.Stops);
        Assert.False(_reader.Running);
    }

    // ---- The cover ----

    /// <summary>The token of <paramref name="bytes"/>, as the source reads a cover: 16 hex digits of their SHA-256.</summary>
    private static string TokenOf(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16];

    [Fact]
    public void The_cover_reads_as_a_token_of_its_bytes_kept_for_the_provider_and_unavailable_without_one()
    {
        byte[] cover = [1, 2, 3, 4, 5];
        _reader.Snapshot = Song() with { Art = cover, ArtType = "image/png" };
        var source = OpenInUse();

        var token = Art();
        Assert.Equal(TokenOf(cover), token);
        Assert.True(source.Art.TryGet(token!, out var picture));
        Assert.Equal(cover, picture.Bytes);
        Assert.Null(picture.Address);

        // The same cover in a later snapshot (a copy): the same token, one picture kept.
        _reader.Snapshot = Song() with { Art = cover.ToArray() };
        Round();
        Assert.Equal(token, Art());
        Assert.Equal(1, source.Art.Count);

        // None: unavailable, never empty; the cover before stays kept for a while (a picture held across a change).
        _reader.Snapshot = Song();
        Round();
        Assert.Null(Art());
        Assert.Equal(SensorStatus.Unavailable, Status("art"));
        Assert.True(source.Art.TryGet(token!, out _));
        _reader.Snapshot = null;
        Round();
        Assert.Null(Art());
    }

    [Fact]
    public void A_cover_over_8_MiB_reads_unavailable_and_is_never_kept()
    {
        _reader.Snapshot = Song() with { Art = new byte[NowPlayingSource.MaxArtBytes + 1] };
        var source = OpenInUse();

        Assert.Null(Art());
        Assert.Equal(0, source.Art.Count);

        _reader.Snapshot = Song() with { Art = new byte[NowPlayingSource.MaxArtBytes] };
        Round();
        Assert.NotNull(Art());
    }

    [Fact]
    public void At_most_four_covers_are_kept_the_oldest_going_first_so_together_at_most_32_MiB()
    {
        var source = OpenInUse();
        var tokens = new List<string>();
        for (byte i = 0; i < 5; i++)
        {
            var cover = new byte[NowPlayingSource.MaxArtBytes];
            cover[0] = i;
            _reader.Snapshot = Song() with { Art = cover };
            Round();
            tokens.Add(Art()!);
        }

        Assert.Equal(5, tokens.Distinct().Count());
        Assert.Equal(NowPlayingArt.MaxKept, source.Art.Count);
        Assert.False(source.Art.TryGet(tokens[0], out _));
        Assert.All(tokens.Skip(1), t => Assert.True(source.Art.TryGet(t, out _)));
        Assert.True(source.Art.Bytes <= 32L * 1024 * 1024);

        // An older cover shown again is the current one again, and the oldest other goes next.
        _reader.Snapshot = Song() with { Art = source.Art.TryGet(tokens[1], out var second) ? second.Bytes : null };
        Round();
        Assert.Equal(tokens[1], Art());
        _reader.Snapshot = Song() with { Art = new byte[] { 9 } };
        Round();
        Assert.True(source.Art.TryGet(tokens[1], out _));
        Assert.False(source.Art.TryGet(tokens[2], out _));
    }

    [Theory]
    [InlineData("https://covers.example/a.jpg", true)]
    [InlineData("http://covers.example/a.jpg", true)]
    [InlineData("file:///home/me/.cache/cover.png", false)]
    [InlineData("covers/a.jpg", false)]
    public void A_cover_given_as_a_web_address_reads_a_token_of_its_address_and_any_other_address_none(string address, bool web)
    {
        _reader.Snapshot = Song() with { ArtAddress = address };
        var source = OpenInUse();

        var token = Art();
        if (!web)
        {
            Assert.Null(token);
            Assert.Equal(0, source.Art.Count);
            return;
        }
        Assert.Equal(TokenOf(System.Text.Encoding.UTF8.GetBytes(address)), token);
        Assert.True(source.Art.TryGet(token!, out var picture));
        Assert.Equal(address, picture.Address);
        Assert.Null(picture.Bytes);
    }

    [Fact]
    public void A_cover_address_over_2048_characters_reads_unavailable()
    {
        _reader.Snapshot = Song() with { ArtAddress = "https://covers.example/" + new string('a', NowPlayingArt.MaxAddressChars) };
        var source = OpenInUse();

        Assert.Null(Art());
        Assert.Equal(0, source.Art.Count);
    }

    // ---- The cover's main colour ----

    private static readonly byte[] RedCover = NowPlayingArtColorTests.Png(new SkiaSharp.SKColor(0xFF, 0, 0));
    private static readonly byte[] BlueCover = NowPlayingArtColorTests.Png(new SkiaSharp.SKColor(0, 0, 0xFF), 64, 48);

    /// <summary>A bound on what a test waits for (a round, the colour's worker): never a timing guess, only a stop for a hang.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>Waits until the cover's colour worker has nothing to compute, then takes a round: the one a computed colour is published in.</summary>
    private void Settle()
    {
        Assert.True(_source!.Art.WaitColored(Bound));
        Round();
    }

    /// <summary>A one-colour cover and its colour.</summary>
    private static (byte[] Cover, string Color) Cover(byte r, byte g, byte b) =>
        (NowPlayingArtColorTests.Png(new SkiaSharp.SKColor(r, g, b)), $"#{r:X2}{g:X2}{b:X2}");

    [Fact]
    public void The_covers_colour_follows_its_token_once_computed_and_a_new_cover_never_shows_the_old_colour()
    {
        _reader.Snapshot = Song() with { Art = RedCover };
        var source = OpenInUse();
        Settle();
        Assert.Equal((TokenOf(RedCover), "#FF0000"), (Art(), Text("art-color")));

        // The round of the change: the new token, its colour unavailable until computed (never the old one); then its own.
        _reader.Snapshot = Song() with { Art = BlueCover };
        Round();
        Assert.Equal((TokenOf(BlueCover), (string?)null), (Art(), Text("art-color")));
        Settle();
        Assert.Equal((TokenOf(BlueCover), "#0000FF"), (Art(), Text("art-color")));
        Assert.Equal(SensorStatus.Live, Status("art-color"));

        // Back to a cover still kept: its token and its colour again.
        _reader.Snapshot = Song() with { Art = RedCover };
        Round();
        Assert.Equal((TokenOf(RedCover), "#FF0000"), (Art(), Text("art-color")));
        Assert.Equal(2, source.Art.Count);
    }

    [Fact]
    public void The_colour_is_computed_once_per_new_cover_not_per_round_or_read()
    {
        _reader.Snapshot = Song() with { Art = RedCover };
        var source = OpenInUse();
        Settle();
        var computed = 0;
        source.Art.Coloring = () => computed++;

        _reader.Snapshot = Song() with { Art = BlueCover };
        Round();
        Settle();
        for (var i = 0; i < 5; i++)
        {
            Round();
            for (var j = 0; j < 3; j++)
                Assert.Equal("#0000FF", Text("art-color"));
        }
        Assert.Equal(1, computed);

        // A copy of the same cover is the same cover; one kept before is never computed again.
        _reader.Snapshot = Song() with { Art = BlueCover.ToArray() };
        Round();
        _reader.Snapshot = Song() with { Art = RedCover };
        Round();
        Assert.Equal("#FF0000", Text("art-color"));
        Assert.Equal(1, computed);
    }

    [Fact]
    public void A_cover_given_only_as_an_address_and_no_cover_have_no_colour()
    {
        _reader.Snapshot = Song() with { Art = RedCover };
        OpenInUse();
        Settle();
        Assert.Equal("#FF0000", Text("art-color"));

        _reader.Snapshot = Song() with { ArtAddress = "https://covers.example/a.jpg" };
        Round();
        Assert.NotNull(Art());
        Assert.Null(Text("art-color"));
        Assert.Equal(SensorStatus.Unavailable, Status("art-color"));

        _reader.Snapshot = Song();
        Round();
        Assert.Null(Art());
        Assert.Equal(SensorStatus.Unavailable, Status("art-color"));

        _reader.Snapshot = null;
        Round();
        Assert.Equal("Stopped", Text("state"));
        Assert.Equal(SensorStatus.Unavailable, Status("art-color"));
    }

    [Fact]
    public void A_cover_that_does_not_decode_has_no_colour_and_the_round_reads_everything_else()
    {
        byte[] broken = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        _reader.Snapshot = Song() with { Art = RedCover };
        OpenInUse();
        Settle();
        Assert.Equal("#FF0000", Text("art-color"));

        _reader.Snapshot = Song() with { Title = "Another Song", Art = broken };
        Round();
        Settle();
        Assert.Equal(TokenOf(broken), Art());
        Assert.Null(Text("art-color"));
        Assert.Equal(SensorStatus.Unavailable, Status("art-color"));
        Assert.Equal("Another Song", Text("title"));
        Assert.Equal(SensorStatus.Live, Status("title"));
    }

    [Fact]
    public async Task A_round_never_waits_for_a_covers_colour_which_reads_unavailable_until_computed_then_goes_with_its_token()
    {
        var source = OpenInUse();
        using var hold = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        source.Art.Coloring = () =>
        {
            entered.Set();
            hold.Wait();
        };
        try
        {
            // The decode held: the round completes all the same, every other entry read, the colour unavailable.
            _reader.Snapshot = Song() with { Title = "Another Song", Art = RedCover };
            await Task.Run(() => Round(), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.True(entered.Wait(Bound, TestContext.Current.CancellationToken));
            Assert.Equal("Another Song", Text("title"));
            Assert.Equal(SensorStatus.Live, Status("title"));
            Assert.NotNull(Number("position"));
            Assert.NotNull(Number("progress"));
            Assert.Equal(TokenOf(RedCover), Art());
            Assert.Null(Text("art-color"));
            Assert.Equal(SensorStatus.Unavailable, Status("art-color"));

            await Task.Run(() => Round(), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal((TokenOf(RedCover), (string?)null), (Art(), Text("art-color")));
        }
        finally
        {
            hold.Set();
        }

        // Released: the round after it is computed carries it, with the same token.
        Settle();
        Assert.Equal((TokenOf(RedCover), "#FF0000"), (Art(), Text("art-color")));
    }

    [Fact]
    public async Task A_cover_changed_while_its_colour_is_computed_never_shows_that_colour_and_the_new_covers_follows()
    {
        var source = OpenInUse();
        using var holdRed = new ManualResetEventSlim();
        using var enteredRed = new ManualResetEventSlim();
        using var holdBlue = new ManualResetEventSlim();
        using var enteredBlue = new ManualResetEventSlim();
        var calls = 0;
        source.Art.Coloring = () =>
        {
            // The worker computes one at a time, and each test step below waits for the one before it to be entered,
            // so the first computation is red's and the second blue's.
            switch (Interlocked.Increment(ref calls))
            {
                case 1:
                    enteredRed.Set();
                    holdRed.Wait();
                    break;
                case 2:
                    enteredBlue.Set();
                    holdBlue.Wait();
                    break;
            }
        };
        try
        {
            _reader.Snapshot = Song() with { Art = RedCover };
            await Task.Run(() => Round(), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.True(enteredRed.Wait(Bound, TestContext.Current.CancellationToken));
            _reader.Snapshot = Song() with { Art = BlueCover };
            await Task.Run(() => Round(), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal((TokenOf(BlueCover), (string?)null), (Art(), Text("art-color")));

            // Red released and completed (the worker takes blue only once red's result is kept), blue still held: the
            // round reads blue's token, and red's late result never goes out with it.
            holdRed.Set();
            Assert.True(enteredBlue.Wait(Bound, TestContext.Current.CancellationToken));
            await Task.Run(() => Round(), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal((TokenOf(BlueCover), (string?)null), (Art(), Text("art-color")));
            Assert.Equal(SensorStatus.Unavailable, Status("art-color"));
        }
        finally
        {
            holdRed.Set();
            holdBlue.Set();
        }

        // Blue released: its own colour follows.
        Settle();
        Assert.Equal((TokenOf(BlueCover), "#0000FF"), (Art(), Text("art-color")));
        Assert.Equal(2, calls);

        // Red, still kept, kept its own colour: at once, and not computed again.
        _reader.Snapshot = Song() with { Art = RedCover };
        Round();
        Assert.Equal((TokenOf(RedCover), "#FF0000"), (Art(), Text("art-color")));
        Assert.True(source.Art.WaitColored(Bound));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Covers_changed_fast_compute_one_at_a_time_with_at_most_one_waiting_and_the_last_ones_colour_is_published()
    {
        var source = OpenInUse();
        using var hold = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var calls = 0;
        var running = 0;
        var most = 0;
        source.Art.Coloring = () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                hold.Wait();
            }
            Interlocked.Decrement(ref running);
        };
        var covers = new[] { Cover(0x10, 0x20, 0x30), Cover(0x40, 0x50, 0x60), Cover(0x70, 0x80, 0x90), Cover(0xA0, 0xB0, 0xC0), Cover(0xD0, 0xE0, 0xF0) };
        try
        {
            // The first held (its computation entered before the next round); four more, one a round: each waits in
            // place of the one before it.
            foreach (var (cover, _) in covers)
            {
                _reader.Snapshot = Song() with { Art = cover };
                await Task.Run(() => Round(), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
                Assert.True(entered.Wait(Bound, TestContext.Current.CancellationToken));
                Assert.Equal((TokenOf(cover), (string?)null), (Art(), Text("art-color")));
            }
        }
        finally
        {
            hold.Set();
        }

        Settle();
        Assert.Equal((TokenOf(covers[^1].Cover), covers[^1].Color), (Art(), Text("art-color")));
        Assert.Equal(2, calls); // the first and the last: those between were replaced while waiting
        Assert.Equal(1, most);

        // The first was pushed out of the covers kept while it was computed: its result went with it, so it is computed again.
        _reader.Snapshot = Song() with { Art = covers[0].Cover };
        Round();
        Assert.Equal((TokenOf(covers[0].Cover), (string?)null), (Art(), Text("art-color")));
        Settle();
        Assert.Equal((TokenOf(covers[0].Cover), covers[0].Color), (Art(), Text("art-color")));
        Assert.Equal(3, calls);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen)
        {
            var was = Interlocked.CompareExchange(ref target, value, seen);
            if (was == seen)
                return;
            seen = was;
        }
    }

    [Fact]
    public async Task Disposed_while_a_colour_is_computed_it_returns_at_once_and_the_worker_ends_keeping_nothing()
    {
        var source = OpenInUse();
        using var hold = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        source.Art.Coloring = () =>
        {
            entered.Set();
            hold.Wait();
        };
        Thread? worker;
        try
        {
            _reader.Snapshot = Song() with { Art = RedCover };
            await Task.Run(() => Round(), TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.True(entered.Wait(Bound, TestContext.Current.CancellationToken));
            worker = source.Art.Worker;
            Assert.NotNull(worker);
            Assert.True(worker.IsAlive);

            // Disposed with the decode still held: it doesn't wait for it.
            await Task.Run(source.Dispose, TestContext.Current.CancellationToken).WaitAsync(Bound, TestContext.Current.CancellationToken);
            Assert.Equal(0, source.Art.Count);
        }
        finally
        {
            hold.Set();
        }

        // Released: the computation's result is dropped, and the worker ends.
        Assert.NotNull(worker);
        Assert.True(worker.Join(Bound));
        Assert.True(source.Art.WaitColored(Bound));
        Assert.Equal(0, source.Art.Count);
        Assert.False(worker.IsAlive);
        Assert.Same(worker, source.Art.Worker);
    }

    [Fact]
    public void When_it_stops_reading_it_forgets_the_covers_and_the_log_never_holds_one()
    {
        byte[] cover = [7, 7, 7];
        _reader.Snapshot = Song() with { Art = cover };
        var source = OpenInUse();
        var token = Art()!;
        Assert.Equal(1, source.Art.Count);

        // Not read for 2 minutes: the reader stops and the covers go.
        Round(TimeSpan.FromMinutes(3));
        Assert.Equal(1, _reader.Stops);
        Assert.Equal(0, source.Art.Count);
        Assert.False(source.Art.TryGet(token, out _));

        Assert.NotEmpty(_events);
        foreach (var e in _events)
        {
            var text = e.RenderMessage() + " " + string.Join(" ", e.Properties.Values) + " " + e.Exception;
            Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
        }

        // Disposed: none kept either.
        source.TryRead(Key("art"), out _);
        Round();
        Round();
        Assert.Equal(1, source.Art.Count);
        source.Dispose();
        Assert.Equal(0, source.Art.Count);
    }

    [Fact]
    public void The_art_is_an_image_sensor_whose_kept_covers_it_answers_by_token_and_nothing_else()
    {
        byte[] first = [1, 2, 3];
        byte[] second = [4, 5, 6, 7];
        _reader.Snapshot = Song() with { Art = first };
        var source = OpenInUse();
        var one = Art()!;
        _reader.Snapshot = Song() with { Art = second };
        Round();
        var two = Art()!;
        var hub = new SensorHub([source]);

        // The current cover and the one before it, by their tokens, exactly the bytes kept (no copy).
        Assert.True(hub.TryGetImage(Key("art"), two, out var bytes));
        Assert.Equal(second, bytes.ToArray());
        Assert.True(source.Art.TryGet(two, out var kept) && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var segment) && ReferenceEquals(kept.Bytes, segment.Array));
        Assert.True(source.TryGetImage(Key("art"), one, out bytes));
        Assert.Equal(first, bytes.ToArray());

        // Another token, another entry of the source, another source's key: none.
        Assert.False(source.TryGetImage(Key("art"), "0000000000000000", out bytes));
        Assert.True(bytes.IsEmpty);
        Assert.False(source.TryGetImage(Key("title"), two, out _));
        Assert.False(source.TryGetImage(SensorKey.Plugin("/other/art"), two, out _));
    }

    [Fact]
    public void A_cover_given_as_a_web_address_has_no_bytes_to_answer()
    {
        _reader.Snapshot = Song() with { ArtAddress = "https://covers.example/a.jpg" };
        var source = OpenInUse();
        var token = Art()!;

        Assert.True(source.Art.TryGet(token, out _));
        Assert.False(source.TryGetImage(Key("art"), token, out var bytes));
        Assert.True(bytes.IsEmpty);
    }

    [Fact]
    public void Asking_for_a_cover_is_no_use_it_starts_no_reader_and_keeps_none_in_use()
    {
        byte[] cover = [9, 9, 9];
        _reader.Snapshot = Song() with { Art = cover };
        var source = Open();
        var hub = new SensorHub([source]);

        // Out of use: asked for, nothing starts.
        Assert.False(hub.TryGetImage(Key("art"), TokenOf(cover), out _));
        Round();
        Round();
        Assert.Equal(0, _reader.Starts);

        // In use, then asked for every 30 s with no read: 2 minutes after the last read the reader stops all the same.
        source.TryRead(Key("title"), out _);
        Round();
        Round();
        var token = Art()!;
        for (var i = 0; i < 4; i++)
        {
            Round(TimeSpan.FromSeconds(30));
            hub.TryGetImage(Key("art"), token, out _);
        }
        Round(TimeSpan.FromSeconds(30));
        Assert.Equal(1, _reader.Stops);
        Assert.False(hub.TryGetImage(Key("art"), token, out _));
    }

    // ---- Bounds and the log ----

    [Fact]
    public void Texts_are_cut_at_1024_characters_a_pair_kept_whole()
    {
        var title = new string('a', 1023) + "\U0001F3B5" + "tail";
        _reader.Snapshot = Song() with { Title = title, Artist = new string('b', 5000) };
        OpenInUse();

        Assert.Equal(new string('a', 1023), Text("title"));
        Assert.Equal(1024, Text("artist")!.Length);
    }

    [Fact]
    public void The_log_never_holds_a_title_artist_album_genre_or_app()
    {
        _reader.Snapshot = Song();
        var source = OpenInUse();
        Round(TimeSpan.FromMinutes(3));
        source.TryRead(Key("title"), out _);
        Round();
        Round();
        _reader.Snapshot = null;
        Round();

        Assert.NotEmpty(_events);
        foreach (var e in _events)
        {
            var text = e.RenderMessage() + " " + string.Join(" ", e.Properties.Values) + " " + e.Exception;
            Assert.DoesNotContain("Secret", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
