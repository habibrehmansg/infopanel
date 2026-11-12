using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The Now Playing source (<c>infopanel.now-playing</c>) over a fake reader: its 13 entries, the position extrapolated
/// while playing and frozen while paused, what is unavailable (never 0 or empty), nothing playing, the demand (the
/// reader started by a read and stopped 2 minutes after the last; a listing, a status and a peek start nothing), the
/// reader's states, texts cut, the cover read as a token of what it keeps, and no title, artist, album, app or cover
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
    public void It_lists_its_13_entries_with_their_names_kinds_and_units_under_infopanel_now_playing()
    {
        var source = Open();

        string[] ids = ["title", "artist", "album", "app", "state", "position", "duration", "progress", "track-number", "genre", "shuffle", "repeat", "art"];
        var sensors = source.GetSensors();
        Assert.Equal(ids.Select(id => $"infopanel:/now-playing/{id}"), sensors.Select(s => s.Key.ToString()));
        Assert.Equal(["Title", "Artist", "Album", "App", "State", "Position", "Duration", "Progress", "Track Number", "Genre", "Shuffle", "Repeat", "Cover Art"], sensors.Select(s => s.Name));
        Assert.Equal(["", "", "", "", "", "s", "s", "%", "", "", "", "", ""], sensors.Select(s => s.Unit));
        Assert.All(sensors, s => Assert.Equal("infopanel.now-playing", s.SourceId));
        Assert.All(sensors, s => Assert.Equal(NowPlayingSource.Name, s.Group));
        Assert.Equal("infopanel.now-playing", source.Id);
        Assert.Equal(
            [SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Number, SensorKind.Number, SensorKind.Number, SensorKind.Number, SensorKind.Text, SensorKind.Text, SensorKind.Text, SensorKind.Text],
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

        var token = Text("art");
        Assert.Equal(TokenOf(cover), token);
        Assert.True(source.Art.TryGet(token!, out var picture));
        Assert.Equal(cover, picture.Bytes);
        Assert.Null(picture.Address);

        // The same cover in a later snapshot (a copy): the same token, one picture kept.
        _reader.Snapshot = Song() with { Art = cover.ToArray() };
        Round();
        Assert.Equal(token, Text("art"));
        Assert.Equal(1, source.Art.Count);

        // None: unavailable, never empty; the cover before stays kept for a while (a picture held across a change).
        _reader.Snapshot = Song();
        Round();
        Assert.Null(Text("art"));
        Assert.Equal(SensorStatus.Unavailable, Status("art"));
        Assert.True(source.Art.TryGet(token!, out _));
        _reader.Snapshot = null;
        Round();
        Assert.Null(Text("art"));
    }

    [Fact]
    public void A_cover_over_8_MiB_reads_unavailable_and_is_never_kept()
    {
        _reader.Snapshot = Song() with { Art = new byte[NowPlayingSource.MaxArtBytes + 1] };
        var source = OpenInUse();

        Assert.Null(Text("art"));
        Assert.Equal(0, source.Art.Count);

        _reader.Snapshot = Song() with { Art = new byte[NowPlayingSource.MaxArtBytes] };
        Round();
        Assert.NotNull(Text("art"));
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
            tokens.Add(Text("art")!);
        }

        Assert.Equal(5, tokens.Distinct().Count());
        Assert.Equal(NowPlayingArt.MaxKept, source.Art.Count);
        Assert.False(source.Art.TryGet(tokens[0], out _));
        Assert.All(tokens.Skip(1), t => Assert.True(source.Art.TryGet(t, out _)));
        Assert.True(source.Art.Bytes <= 32L * 1024 * 1024);

        // An older cover shown again is the current one again, and the oldest other goes next.
        _reader.Snapshot = Song() with { Art = source.Art.TryGet(tokens[1], out var second) ? second.Bytes : null };
        Round();
        Assert.Equal(tokens[1], Text("art"));
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

        var token = Text("art");
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

        Assert.Null(Text("art"));
        Assert.Equal(0, source.Art.Count);
    }

    [Fact]
    public void When_it_stops_reading_it_forgets_the_covers_and_the_log_never_holds_one()
    {
        byte[] cover = [7, 7, 7];
        _reader.Snapshot = Song() with { Art = cover };
        var source = OpenInUse();
        var token = Text("art")!;
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
