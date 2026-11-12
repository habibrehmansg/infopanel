using System.Diagnostics;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Sensors;
using InfoPanel.Sensors.NowPlaying;
using Serilog.Events;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The Linux reader (<see cref="LinuxNowPlayingReader"/>, its rounds <see cref="MprisRounds"/>) over a fake bus, on any OS:
/// a player's properties into a snapshot (lists joined, microseconds as seconds, what a player leaves out as null, the
/// variants players send); the player choice (one that plays, the one that started last first; else the one that played
/// last; else the first listed; one whose call fails passed over for the next); no player is nothing playing, listed
/// players none of which answers a failed read; one call pending per player and operation, stalled rounds letting go of
/// the connection, a call failing after its timeout observed with its text dropped; the app's name once per player; the
/// cover art (a <c>file://</c> one read when it changes, regular files only, at most 8 MiB, one that fails read again
/// after its delay, one that hangs holding up no round and no second read; an <c>http(s)://</c> one handed on as its
/// address); no session bus is <c>noSessionBus</c>; one
/// owner per start that <see cref="LinuxNowPlayingReader.Stop"/> and <see cref="LinuxNowPlayingReader.Dispose"/> never wait
/// for, which lets go of the connection itself; a failed listing connects again; an owner held up reads as failed; and no
/// title, artist, album, app, player's name or art's path reaches the log. The real bus's reads are the opt-in
/// <see cref="MprisFakePlayerTests"/>'s.
/// </summary>
public sealed class LinuxNowPlayingReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private const string Player = "org.mpris.MediaPlayer2.secretplayer";
    private const string Other = "org.mpris.MediaPlayer2.otherplayer.instance4242";

    private readonly ManualTimeProvider _time = new(Now);
    private readonly List<LogEvent> _events = [];
    private readonly Serilog.ILogger _log;
    private readonly TempDirectory _dir = new();

    public LinuxNowPlayingReaderTests()
    {
        _log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_events)).CreateLogger();
    }

    public void Dispose() => _dir.Dispose();

    private sealed class CollectingSink(List<LogEvent> events) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    /// <summary>A bus of players: each name's status, properties and identity; a call to a name it doesn't know fails.</summary>
    private sealed class FakeBus : IMprisBus
    {
        public readonly List<string> Names = [];
        public readonly Dictionary<string, string> Statuses = [];
        public readonly Dictionary<string, Dictionary<string, object?>> Players = [];
        public readonly Dictionary<string, string?> Identities = [];
        public readonly List<string> IdentityAsks = [];

        public Exception? ConnectFails;
        public Exception? ListFails;
        public Exception? GetAllFails;

        /// <summary>When set, the call waits for it (a bus that never answers).</summary>
        public TaskCompletionSource? ConnectBlocks;
        public TaskCompletionSource? ListBlocks;

        public readonly ManualResetEventSlim Connecting = new();
        public readonly ManualResetEventSlim Listed = new();
        public readonly ManualResetEventSlim ListStalled = new();
        public int Lists;
        public int Disposed;

        public async Task ConnectAsync()
        {
            Connecting.Set();
            if (ConnectBlocks is { } blocks)
                await blocks.Task;
            if (ConnectFails is { } e)
                throw e;
        }

        public async Task<IReadOnlyList<string>> ListPlayersAsync()
        {
            Interlocked.Increment(ref Lists);
            Listed.Set();
            if (Volatile.Read(ref ListBlocks) is { } blocks)
            {
                ListStalled.Set();
                await blocks.Task;
            }
            if (ListFails is { } e)
                throw e;
            lock (Names)
                return Names.ToList();
        }

        /// <summary>Players whose <c>GetAll</c> fails; players whose <c>PlaybackStatus</c> is never answered (kept in <see cref="Unanswered"/>).</summary>
        public readonly HashSet<string> GetAllFailsFor = [];
        public readonly HashSet<string> StatusNeverAnswers = [];
        private readonly List<(string Name, TaskCompletionSource<object?> Call)> _unanswered = [];

        /// <summary>The calls not answered yet, of <paramref name="name"/>.</summary>
        public int Outstanding(string name)
        {
            lock (_unanswered)
                return _unanswered.Count(c => c.Name == name && !c.Call.Task.IsCompleted);
        }

        /// <summary>Takes the unanswered calls out of the fake (so it no longer holds them).</summary>
        public List<TaskCompletionSource<object?>> TakeUnanswered()
        {
            lock (_unanswered)
            {
                var calls = _unanswered.Select(c => c.Call).ToList();
                _unanswered.Clear();
                return calls;
            }
        }

        public Task<object?> GetAsync(string name, string iface, string property)
        {
            if (iface == MprisBus.PlayerInterface && property == "PlaybackStatus" && StatusNeverAnswers.Contains(name))
            {
                var call = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_unanswered)
                    _unanswered.Add((name, call));
                return call.Task;
            }

            if (iface == MprisBus.RootInterface && property == "Identity" && Identities.TryGetValue(name, out var identity))
            {
                IdentityAsks.Add(name);
                return Task.FromResult<object?>(identity);
            }

            if (iface == MprisBus.PlayerInterface && property == "PlaybackStatus" && Statuses.TryGetValue(name, out var status))
                return Task.FromResult<object?>(status);
            return Task.FromException<object?>(new InvalidOperationException("org.freedesktop.DBus.Error.ServiceUnknown"));
        }

        public Task<IReadOnlyDictionary<string, object?>> GetAllAsync(string name, string iface)
        {
            if (GetAllFails is { } e)
                return Task.FromException<IReadOnlyDictionary<string, object?>>(e);
            if (GetAllFailsFor.Contains(name))
                return Task.FromException<IReadOnlyDictionary<string, object?>>(new InvalidOperationException("org.freedesktop.DBus.Error.NoReply"));
            if (iface == MprisBus.PlayerInterface && Players.TryGetValue(name, out var props))
            {
                var answer = new Dictionary<string, object?>(props) { ["PlaybackStatus"] = Statuses.GetValueOrDefault(name) };
                return Task.FromResult<IReadOnlyDictionary<string, object?>>(answer);
            }

            return Task.FromException<IReadOnlyDictionary<string, object?>>(new InvalidOperationException("org.freedesktop.DBus.Error.ServiceUnknown"));
        }

        public void Dispose() => Interlocked.Increment(ref Disposed);

        public void Add(string name, string status, Dictionary<string, object?>? metadata = null, string? identity = "Secret Player App")
        {
            lock (Names)
                Names.Add(name);
            Statuses[name] = status;
            Identities[name] = identity;
            Players[name] = new Dictionary<string, object?>
            {
                ["Metadata"] = metadata ?? Metadata(),
                ["Position"] = 61_500_000L,
                ["Rate"] = 1.0,
                ["Shuffle"] = true,
                ["LoopStatus"] = "Track",
                ["Volume"] = 0.5,
            };
        }
    }

    private static Dictionary<string, object?> Metadata(string? artUrl = null) => new()
    {
        ["mpris:trackid"] = "/org/secret/track/1",
        ["xesam:title"] = "A Secret Song",
        ["xesam:artist"] = new List<string> { "Secret Band", "Hidden Guest" },
        ["xesam:album"] = "Secret Album",
        ["xesam:genre"] = new List<string> { "Secret Genre" },
        ["xesam:trackNumber"] = 3L,
        ["mpris:length"] = 245_000_000L,
        ["mpris:artUrl"] = artUrl,
    };

    private static Dictionary<string, object?> OtherSong()
    {
        var metadata = Metadata();
        metadata["xesam:title"] = "Other Song";
        return metadata;
    }

    private MprisRounds Rounds() => new(_time, _log, TimeSpan.FromSeconds(2));

    private static MprisRound Read(MprisRounds rounds, FakeBus bus) => rounds.ReadAsync(bus, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void A_player_reads_into_a_snapshot()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Playing");

        var round = Read(Rounds(), bus);

        Assert.True(round.Read);
        var s = Assert.IsType<NowPlayingSnapshot>(round.Snapshot);
        Assert.Equal("A Secret Song", s.Title);
        Assert.Equal("Secret Band, Hidden Guest", s.Artist);
        Assert.Equal("Secret Album", s.Album);
        Assert.Equal("Secret Genre", s.Genre);
        Assert.Equal("Secret Player App", s.App);
        Assert.Equal(NowPlayingState.Playing, s.State);
        Assert.Equal(61.5, s.Position);
        Assert.Equal(Now, s.PositionAt);
        Assert.Equal(245, s.Duration);
        Assert.Equal(3, s.TrackNumber);
        Assert.Equal(1.0, s.Rate);
        Assert.True(s.Shuffle);
        Assert.Equal(NowPlayingRepeat.One, s.Repeat);
        Assert.Null(s.Art);
        Assert.Null(s.ArtAddress);
    }

    [Fact]
    public void The_variants_players_send_read_and_what_they_leave_out_is_null()
    {
        // A single artist as a string, the length as a double, a status and loop status it doesn't know.
        var s = MprisRounds.Snapshot(Player, new Dictionary<string, object?>
        {
            ["PlaybackStatus"] = "Paused",
            ["LoopStatus"] = "Playlist",
            ["Position"] = 2_000_000.0,
        }, new Dictionary<string, object?>
        {
            ["xesam:artist"] = "Solo Artist",
            ["xesam:genre"] = new List<string> { "", "Ambient", "" },
            ["mpris:length"] = 90_500_000.0,
            ["xesam:trackNumber"] = "7",
        }, identity: null, art: null, artAddress: null, Now);

        Assert.Equal(NowPlayingState.Paused, s.State);
        Assert.Equal(NowPlayingRepeat.All, s.Repeat);
        Assert.Equal(2, s.Position);
        Assert.Equal("Solo Artist", s.Artist);
        Assert.Equal("Ambient", s.Genre);
        Assert.Equal(90.5, s.Duration);
        Assert.Null(s.TrackNumber);
        Assert.Null(s.Title);
        Assert.Null(s.Album);
        Assert.Null(s.Shuffle);
        Assert.Null(s.Rate);
        Assert.Equal("secretplayer", s.App);

        var bare = MprisRounds.Snapshot(Other, new Dictionary<string, object?> { ["PlaybackStatus"] = "Buffering", ["LoopStatus"] = "Sometimes" }, null, " ", null, null, Now);
        Assert.Equal(NowPlayingState.Stopped, bare.State);
        Assert.Null(bare.Repeat);
        Assert.Null(bare.Position);
        Assert.Null(bare.PositionAt);
        Assert.Null(bare.Duration);
        Assert.Null(bare.Artist);
        Assert.Equal("otherplayer", bare.App);
        Assert.Equal(NowPlayingRepeat.Off, MprisRounds.Snapshot(Player, new Dictionary<string, object?> { ["LoopStatus"] = "None" }, null, null, null, null, Now).Repeat);
    }

    [Fact]
    public void A_player_without_an_identity_is_named_by_its_bus_name()
    {
        Assert.Equal("vlc", MprisRounds.AppOf("org.mpris.MediaPlayer2.vlc"));
        Assert.Equal("spotify", MprisRounds.AppOf("org.mpris.MediaPlayer2.spotify.instance12345"));
        Assert.Equal("firefox", MprisRounds.AppOf("org.mpris.MediaPlayer2.firefox.instance_1_23"));

        var bus = new FakeBus();
        bus.Add(Player, "Playing", identity: null);
        bus.Identities.Remove(Player);
        Assert.Equal("secretplayer", Read(Rounds(), bus).Snapshot!.App);
    }

    [Fact]
    public void No_player_is_nothing_playing()
    {
        var round = Read(Rounds(), new FakeBus());

        Assert.True(round.Read);
        Assert.Null(round.Snapshot);
    }

    [Fact]
    public void One_that_plays_is_chosen_the_one_that_started_last_first()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Paused");
        bus.Add(Other, "Playing", OtherSong());
        var rounds = Rounds();
        Assert.Equal("Other Song", Read(rounds, bus).Snapshot!.Title);

        // Both play: the one that started playing last.
        bus.Statuses[Player] = "Playing";
        Assert.Equal("A Secret Song", Read(rounds, bus).Snapshot!.Title);
        Assert.Equal("A Secret Song", Read(rounds, bus).Snapshot!.Title);

        // It pauses: the one still playing.
        bus.Statuses[Player] = "Paused";
        Assert.Equal("Other Song", Read(rounds, bus).Snapshot!.Title);
    }

    [Fact]
    public void Without_one_that_plays_the_one_that_played_last_else_the_first_listed()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Stopped");
        bus.Add(Other, "Paused", OtherSong());
        var rounds = Rounds();

        // None ever played: the first listed.
        var first = Read(rounds, bus).Snapshot!;
        Assert.Equal("A Secret Song", first.Title);
        Assert.Equal(NowPlayingState.Stopped, first.State);

        // The second played, then paused: it stays the one read.
        bus.Statuses[Other] = "Playing";
        Assert.Equal("Other Song", Read(rounds, bus).Snapshot!.Title);
        bus.Statuses[Other] = "Paused";
        var paused = Read(rounds, bus).Snapshot!;
        Assert.Equal("Other Song", paused.Title);
        Assert.Equal(NowPlayingState.Paused, paused.State);
    }

    [Fact]
    public void A_player_whose_calls_fail_is_passed_over_for_the_next_and_all_failing_fails_the_round()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Playing");
        bus.Add(Other, "Paused", OtherSong());
        bus.Statuses.Remove(Player);
        var rounds = Rounds();

        Assert.Equal("Other Song", Read(rounds, bus).Snapshot!.Title);

        // It answers Playing but fails GetAll: the next candidate is read, every round.
        bus.Statuses[Player] = "Playing";
        bus.GetAllFailsFor.Add(Player);
        Assert.Equal("Other Song", Read(rounds, bus).Snapshot!.Title);
        Assert.Equal("Other Song", Read(rounds, bus).Snapshot!.Title);

        bus.GetAllFails = new InvalidOperationException("gone");
        Assert.False(Read(rounds, bus).Read);
    }

    [Fact]
    public void No_player_listed_is_nothing_playing_and_listed_players_that_all_fail_fail_the_round()
    {
        var none = Read(Rounds(), new FakeBus());
        Assert.True(none.Read);
        Assert.Null(none.Snapshot);

        var bus = new FakeBus();
        bus.Add(Player, "Playing");
        bus.Add(Other, "Paused", OtherSong());
        bus.Statuses.Remove(Player);
        bus.Statuses.Remove(Other);
        Assert.False(Read(Rounds(), bus).Read);
    }

    [Fact]
    public void A_player_that_never_answers_has_one_call_pending_per_operation_and_stalls_retire_the_connection()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Playing");
        bus.StatusNeverAnswers.Add(Player);
        var rounds = new MprisRounds(TimeProvider.System, _log, TimeSpan.FromMilliseconds(20));

        Assert.False(Read(rounds, bus).Read);
        Assert.False(Read(rounds, bus).Read);
        Assert.Equal(1, bus.Outstanding(Player));

        // The third round in a row with nothing answered: the connection is to be let go of.
        Assert.Throws<TimeoutException>(() => Read(rounds, bus));
        Assert.Equal(1, bus.Outstanding(Player));
    }

    [Fact]
    public void A_bus_whose_players_stop_answering_is_let_go_of_and_connected_again()
    {
        var buses = new List<FakeBus>();
        using var reader = new LinuxNowPlayingReader(() => "unix:path=/run/user/1000/bus", _ =>
        {
            var bus = new FakeBus();
            bus.Add(Player, "Playing");
            bus.StatusNeverAnswers.Add(Player);
            lock (buses)
                buses.Add(bus);
            return bus;
        }, TimeProvider.System, _log, interval: TimeSpan.FromMilliseconds(10), callTimeout: TimeSpan.FromMilliseconds(20), retry: TimeSpan.FromMilliseconds(50));

        reader.Start();
        Until(() => { lock (buses) return buses.Count >= 2; }, "a second connection");

        lock (buses)
        {
            Assert.Equal(1, Volatile.Read(ref buses[0].Disposed));
            Assert.Equal(1, buses[0].Outstanding(Player));
        }
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void A_call_that_fails_after_its_timeout_is_observed_and_its_text_goes_nowhere()
    {
        const string Secret = "Secret Late Title 7f3a";
        var unobserved = new List<string>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            lock (unobserved)
                unobserved.Add(e.Exception.ToString());
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            ReadThenFailLate(Secret);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
                Assert.DoesNotContain(unobserved, e => e.Contains(Secret, StringComparison.Ordinal));
            lock (_events)
                Assert.All(_events, e => Assert.DoesNotContain(Secret, e.RenderMessage() + " " + string.Join(" ", e.Properties.Values) + " " + e.Exception));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    /// <summary>A round whose status call times out; then the player answers it with an error carrying its text. Holds nothing after.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ReadThenFailLate(string secret)
    {
        var bus = new FakeBus();
        bus.Add(Player, "Playing");
        bus.StatusNeverAnswers.Add(Player);
        var rounds = new MprisRounds(TimeProvider.System, _log, TimeSpan.FromMilliseconds(20));
        Read(rounds, bus);
        foreach (var call in bus.TakeUnanswered())
            call.SetException(new InvalidOperationException(secret));
        // Its continuations run elsewhere: give them time to.
        Thread.Sleep(200);
    }

    [Fact]
    public void A_cover_file_that_never_answers_holds_up_no_round_and_no_second_read_starts()
    {
        var reads = 0;
        using var gate = new ManualResetEventSlim();
        var bus = new FakeBus();
        bus.Add(Player, "Playing", Metadata(new Uri(_dir.Combine("stalled.png")).AbsoluteUri));
        var reader = new LinuxNowPlayingReader(() => "unix:path=/run/user/1000/bus", _ => bus, TimeProvider.System, _log,
            interval: TimeSpan.FromMilliseconds(10), callTimeout: TimeSpan.FromSeconds(30), retry: TimeSpan.FromMilliseconds(50),
            readFile: (_, _) =>
            {
                Interlocked.Increment(ref reads);
                gate.Wait();
                return [42];
            }, artTimeout: TimeSpan.FromMilliseconds(100));
        try
        {
            reader.Start();
            NowPlayingSnapshot? s = null;
            Until(() => reader.TryRead(out s) && s is not null, "a round past the stalled cover");
            Assert.Null(s!.Art);
            Assert.Equal("A Secret Song", s.Title);

            // Stopped and started while the read still hangs: rounds go on, and no second read starts.
            var stopping = Stopwatch.StartNew();
            reader.Stop();
            Assert.True(stopping.ElapsedMilliseconds < 500, $"Stop took {stopping.ElapsedMilliseconds} ms");
            reader.Start();
            Until(() => reader.TryRead(out s) && s is not null, "a round of the second start");
            Thread.Sleep(300);
            Assert.Equal(1, Volatile.Read(ref reads));

            // The file answers at last: its bytes are the cover.
            gate.Set();
            Until(() => reader.TryRead(out s) && s?.Art is not null, "the cover once its file answered");
            Assert.Equal(new byte[] { 42 }, s!.Art!.Value.ToArray());
        }
        finally
        {
            gate.Set();
            reader.Dispose();
        }
    }

    [Fact]
    public void A_listing_that_fails_fails_the_round_as_the_bus()
    {
        var bus = new FakeBus { ListFails = new IOException("closed") };

        Assert.Throws<IOException>(() => Read(Rounds(), bus));
    }

    [Fact]
    public void The_apps_name_is_asked_once_per_player_while_it_is_listed()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Playing");
        var rounds = Rounds();

        Read(rounds, bus);
        Read(rounds, bus);
        Assert.Single(bus.IdentityAsks);

        // It leaves the bus and comes back: asked again.
        bus.Names.Clear();
        Assert.Null(Read(rounds, bus).Snapshot);
        bus.Names.Add(Player);
        Read(rounds, bus);
        Assert.Equal(2, bus.IdentityAsks.Count);
    }

    [Fact]
    public void A_file_cover_is_read_when_its_address_changes()
    {
        var first = _dir.Combine("cover one.png");
        File.WriteAllBytes(first, [1, 2, 3]);
        var bus = new FakeBus();
        bus.Add(Player, "Playing", Metadata(new Uri(first).AbsoluteUri));
        var rounds = Rounds();

        Assert.Equal(new byte[] { 1, 2, 3 }, Read(rounds, bus).Snapshot!.Art!.Value.ToArray());

        // The same address isn't read again.
        File.WriteAllBytes(first, [9, 9]);
        Assert.Equal(new byte[] { 1, 2, 3 }, Read(rounds, bus).Snapshot!.Art!.Value.ToArray());

        // A new one is.
        var second = _dir.Combine("cover-two.jpg");
        File.WriteAllBytes(second, [4, 5]);
        bus.Players[Player]["Metadata"] = Metadata(new Uri(second).AbsoluteUri);
        var s = Read(rounds, bus).Snapshot!;
        Assert.Equal(new byte[] { 4, 5 }, s.Art!.Value.ToArray());
        Assert.Null(s.ArtAddress);
    }

    [Fact]
    public void A_file_cover_is_read_up_to_8_MiB_and_a_larger_one_left_out_for_its_track()
    {
        var exact = _dir.Combine("exact.png");
        File.WriteAllBytes(exact, new byte[NowPlayingSource.MaxArtBytes]);
        var over = _dir.Combine("over.png");
        File.WriteAllBytes(over, new byte[NowPlayingSource.MaxArtBytes + 1]);
        var bus = new FakeBus();
        bus.Add(Player, "Playing", Metadata(new Uri(exact).AbsoluteUri));
        var rounds = Rounds();

        Assert.Equal(NowPlayingSource.MaxArtBytes, Read(rounds, bus).Snapshot!.Art!.Value.Length);

        bus.Players[Player]["Metadata"] = Metadata(new Uri(over).AbsoluteUri);
        Assert.Null(Read(rounds, bus).Snapshot!.Art);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(Read(rounds, bus).Snapshot!.Art);
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public void A_cover_that_isnt_a_regular_file_is_refused_without_waiting_on_it()
    {
        Assert.SkipUnless(Fifo.Supported, "Windows has no FIFOs.");
        var fifo = _dir.Combine("cover.fifo");
        Fifo.Create(fifo);
        var bus = new FakeBus();
        bus.Add(Player, "Playing", Metadata(new Uri(fifo).AbsoluteUri));
        var rounds = Rounds();

        var s = Fifo.Within(10, () => Read(rounds, bus).Snapshot!, fifo);

        Assert.Null(s.Art);
        Assert.Equal("A Secret Song", s.Title);
    }

    [Fact]
    public void A_cover_that_fails_to_read_is_read_again_after_its_delay()
    {
        var later = _dir.Combine("later.png");
        var bus = new FakeBus();
        bus.Add(Player, "Playing", Metadata(new Uri(later).AbsoluteUri));
        var rounds = Rounds();

        Assert.Null(Read(rounds, bus).Snapshot!.Art);
        File.WriteAllBytes(later, [7]);
        Assert.Null(Read(rounds, bus).Snapshot!.Art);

        _time.Advance(LinuxNowPlayingReader.ArtRetry);
        Assert.Equal(new byte[] { 7 }, Read(rounds, bus).Snapshot!.Art!.Value.ToArray());
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public void A_web_cover_is_handed_on_as_its_address_and_any_other_left_out()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Playing", Metadata("https://images.example/cover/1.jpg"));
        var rounds = Rounds();

        var web = Read(rounds, bus).Snapshot!;
        Assert.Equal("https://images.example/cover/1.jpg", web.ArtAddress);
        Assert.Null(web.Art);

        foreach (var url in new[] { "http://127.0.0.1:52312/cover", "data:image/png;base64,AAAA", "ftp://example/cover.png", "file://remotehost/cover.png",
                     "not a url", "https://images.example/" + new string('a', MprisRounds.MaxAddressChars) })
        {
            bus.Players[Player]["Metadata"] = Metadata(url);
            var s = Read(rounds, bus).Snapshot!;
            Assert.Null(s.Art);
            Assert.Equal(url.StartsWith("http://", StringComparison.Ordinal) ? url : null, s.ArtAddress);
        }
    }

    [Fact]
    public void The_session_bus_is_the_variables_else_the_runtime_folders_socket()
    {
        Assert.Equal("unix:path=/run/user/1000/bus", LinuxNowPlayingReader.SessionBusAddress("unix:path=/run/user/1000/bus", "/elsewhere", _ => false));
        Assert.Equal("unix:path=/run/user/1000/bus", LinuxNowPlayingReader.SessionBusAddress(null, "/run/user/1000", p => p == "/run/user/1000/bus"));
        Assert.Equal("unix:path=/tmp/a%20b/bus", LinuxNowPlayingReader.SessionBusAddress("", "/tmp/a b", _ => true));
        Assert.Null(LinuxNowPlayingReader.SessionBusAddress(null, "/run/user/1000", _ => false));
        Assert.Null(LinuxNowPlayingReader.SessionBusAddress(" ", "relative/dir", _ => true));
        Assert.Null(LinuxNowPlayingReader.SessionBusAddress(null, null, _ => true));
    }

    [Fact]
    public void Texts_keep_at_least_what_is_shown_and_at_most_their_bound()
    {
        var threeByte = string.Concat(Enumerable.Repeat("€", 5000));
        var kept = MprisBus.Text(System.Text.Encoding.UTF8.GetBytes(threeByte));
        Assert.True(kept.Length >= NowPlayingSource.MaxTextChars);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(kept) <= MprisBus.MaxStringBytes + 3);
        Assert.Equal(threeByte[..NowPlayingSource.MaxTextChars], NowPlayingSampler.Cut(kept));

        var s = MprisRounds.Snapshot(Player, new Dictionary<string, object?>(), new Dictionary<string, object?>
        {
            ["xesam:artist"] = Enumerable.Repeat(new string('x', 300), MprisBus.MaxListItems).ToList(),
        }, null, null, null, Now);
        Assert.InRange(s.Artist!.Length, NowPlayingSource.MaxTextChars + 1, NowPlayingSource.MaxTextChars + 302);
    }

    [Fact]
    public void Signatures_are_split_into_complete_types()
    {
        Assert.Equal(1, MprisBus.CompleteTypeLength("s"u8));
        Assert.Equal(2, MprisBus.CompleteTypeLength("as"u8));
        Assert.Equal(5, MprisBus.CompleteTypeLength("a{sv}x"u8));
        Assert.Equal(8, MprisBus.CompleteTypeLength("(sa(ii))s"u8));
        Assert.Equal(3, MprisBus.CompleteTypeLength("aay"u8));
        Assert.Throws<InvalidDataException>(() => MprisBus.CompleteTypeLength("(ss"u8));
        Assert.Throws<InvalidDataException>(() => MprisBus.CompleteTypeLength("()"u8));
        Assert.Throws<InvalidDataException>(() => MprisBus.CompleteTypeLength("a"u8));
        Assert.Throws<InvalidDataException>(() => MprisBus.CompleteTypeLength("z"u8));
    }

    // The reader's life cycle: real timers, short intervals.

    private LinuxNowPlayingReader Reader(Func<string, IMprisBus> open, Func<string?>? address = null, TimeProvider? time = null) =>
        new(address ?? (() => "unix:path=/run/user/1000/bus"), open, time ?? TimeProvider.System, _log,
            interval: TimeSpan.FromMilliseconds(10), callTimeout: TimeSpan.FromSeconds(30), retry: TimeSpan.FromMilliseconds(50));

    private static void Until(Func<bool> condition, string what) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10)), "timed out waiting: " + what);

    [Fact]
    public void Without_a_session_bus_it_is_noSessionBus_and_reads_nothing()
    {
        var opened = 0;
        using var reader = Reader(_ => { opened++; return new FakeBus(); }, address: () => null);
        Assert.Null(reader.State);

        reader.Start();
        Until(() => reader.State is not null, "the state");

        Assert.Equal(LinuxNowPlayingReader.NoSessionBusState, reader.State);
        Assert.Equal(NowPlayingSource.NoSessionBus, reader.State!.Reason!.Code);
        Assert.False(reader.TryRead(out _));
        Assert.Equal(0, opened);
    }

    [Fact]
    public void A_bus_that_cant_be_connected_to_is_noSessionBus_and_tried_again()
    {
        var buses = new List<FakeBus>();
        using var reader = Reader(_ =>
        {
            var bus = new FakeBus { ConnectFails = new IOException("refused") };
            lock (buses)
                buses.Add(bus);
            return bus;
        });

        reader.Start();
        Until(() => { lock (buses) return buses.Count >= 3; }, "three tries");

        Assert.Equal(LinuxNowPlayingReader.NoSessionBusState, reader.State);
        Assert.False(reader.TryRead(out _));
        Until(() => { lock (buses) return buses.Take(2).All(b => b.Disposed == 1); }, "the failed buses let go of");
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public void Started_it_answers_what_its_owner_last_read()
    {
        var bus = new FakeBus();
        bus.Add(Player, "Playing");
        using var reader = Reader(_ => bus);
        Assert.False(reader.TryRead(out _));

        reader.Start();
        NowPlayingSnapshot? s = null;
        Until(() => reader.TryRead(out s), "a round");

        Assert.Equal("A Secret Song", s!.Title);
        Assert.Null(reader.State);

        // The player leaves: nothing playing.
        lock (bus.Names)
            bus.Names.Clear();
        Until(() => reader.TryRead(out s) && s is null, "nothing playing");
    }

    [Fact]
    public void Stop_and_Dispose_never_wait_for_the_bus_and_its_owner_lets_go_of_it()
    {
        // A connection that never completes.
        var hung = new FakeBus { ConnectBlocks = new TaskCompletionSource() };
        using (var reader = Reader(_ => hung))
        {
            reader.Start();
            Assert.True(hung.Connecting.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var stopping = Stopwatch.StartNew();
            reader.Stop();
            Assert.True(stopping.ElapsedMilliseconds < 500, $"Stop took {stopping.ElapsedMilliseconds} ms");
            Until(() => Volatile.Read(ref hung.Disposed) == 1, "the connection let go of");
            Assert.False(reader.TryRead(out _));
        }

        // A listing that never answers, after a good round.
        var stalls = new FakeBus();
        stalls.Add(Player, "Playing");
        var reader2 = Reader(_ => stalls);
        reader2.Start();
        Until(() => reader2.TryRead(out _), "a round");
        Volatile.Write(ref stalls.ListBlocks, new TaskCompletionSource());
        Assert.True(stalls.ListStalled.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the stalled listing");
        var disposing = Stopwatch.StartNew();
        reader2.Dispose();
        Assert.True(disposing.ElapsedMilliseconds < 500, $"Dispose took {disposing.ElapsedMilliseconds} ms");
        Until(() => Volatile.Read(ref stalls.Disposed) == 1, "the connection let go of");
        Assert.False(reader2.TryRead(out _));

        // Disposed, it doesn't start again.
        reader2.Start();
        Assert.False(reader2.TryRead(out _));
    }

    [Fact]
    public void An_owner_held_up_on_the_bus_reads_as_failed()
    {
        var time = new ManualTimeProvider(Now);
        var bus = new FakeBus();
        bus.Add(Player, "Playing");
        using var reader = Reader(_ => bus, time: time);
        reader.Start();
        Until(() => reader.TryRead(out _), "a round");

        Volatile.Write(ref bus.ListBlocks, new TaskCompletionSource());
        Assert.True(bus.ListStalled.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the stalled listing");
        Assert.True(reader.TryRead(out _));
        time.Advance(LinuxNowPlayingReader.AnswerAge + TimeSpan.FromSeconds(1));
        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void A_failed_listing_lets_go_of_the_connection_and_connects_again()
    {
        var buses = new List<FakeBus>();
        using var reader = Reader(_ =>
        {
            var bus = new FakeBus();
            bus.Add(Player, "Playing");
            lock (buses)
            {
                if (buses.Count == 0)
                    bus.ListFails = new IOException("connection closed");
                buses.Add(bus);
            }
            return bus;
        });

        reader.Start();
        NowPlayingSnapshot? s = null;
        Until(() => reader.TryRead(out s), "a round on the second connection");

        Assert.Equal("A Secret Song", s!.Title);
        lock (buses)
        {
            Assert.Equal(2, buses.Count);
            Assert.Equal(1, buses[0].Disposed);
            Assert.Equal(0, buses[1].Disposed);
        }
        Assert.Single(_events, e => e.Level == LogEventLevel.Warning);
    }

    [Fact]
    public void Stopped_and_started_again_it_connects_anew()
    {
        var buses = new List<FakeBus>();
        using var reader = Reader(_ =>
        {
            var bus = new FakeBus();
            bus.Add(Player, "Playing");
            lock (buses)
                buses.Add(bus);
            return bus;
        });

        reader.Start();
        Until(() => reader.TryRead(out _), "a round");
        reader.Stop();
        Assert.False(reader.TryRead(out _));
        Until(() => { lock (buses) return buses[0].Disposed == 1; }, "the first connection let go of");

        reader.Start();
        Until(() => reader.TryRead(out _), "a round of the second start");
        lock (buses)
            Assert.Equal(2, buses.Count);
    }

    [Fact]
    public void The_source_over_it_says_noSessionBus()
    {
        using var source = NowPlayingSource.Create(Reader(_ => new FakeBus(), address: () => null), _time);
        source.Open();
        Assert.False(source.TryRead(NowPlayingSource.KeyOf("state"), out _));
        _time.Advance(NowPlayingSource.Interval);
        source.Poll();

        Until(() =>
        {
            source.Poll();
            return source.State.Kind == SourceStateKind.Unavailable;
        }, "the source's state");
        Assert.Equal(NowPlayingSource.NoSessionBus, source.State.Reason!.Code);
    }

    [Fact]
    public void Nothing_a_player_says_reaches_the_log()
    {
        var art = _dir.Combine("Secret Cover Path.png");
        File.WriteAllBytes(art, [1, 2, 3]);
        var missing = _dir.Combine("Secret Missing Cover.png");
        var big = _dir.Combine("Secret Big Cover.png");
        File.WriteAllBytes(big, new byte[NowPlayingSource.MaxArtBytes + 1]);
        var buses = new List<FakeBus>();
        using var reader = Reader(_ =>
        {
            var bus = new FakeBus();
            bus.Add(Player, "Playing", Metadata(new Uri(art).AbsoluteUri));
            lock (buses)
            {
                if (buses.Count == 0)
                    bus.ListFails = new IOException("org.mpris.MediaPlayer2.secretplayer went away");
                if (buses.Count == 1)
                    bus.ConnectFails = new IOException("Secret Player App refused");
                buses.Add(bus);
            }
            return bus;
        });

        reader.Start();
        NowPlayingSnapshot? s = null;
        Until(() => reader.TryRead(out s) && s is not null, "a round");
        var rounds = Rounds();
        var other = new FakeBus();
        other.Add(Player, "Playing", Metadata(new Uri(missing).AbsoluteUri));
        Read(rounds, other);
        other.Players[Player]["Metadata"] = Metadata(new Uri(big).AbsoluteUri);
        Read(rounds, other);
        reader.Dispose();

        Assert.Contains(_events, e => e.Level == LogEventLevel.Warning);
        Assert.Contains(_events, e => e.RenderMessage().Contains("cover art read (3 bytes)"));
        foreach (var secret in new[] { "Secret", "secretplayer", "Hidden Guest", "mpris", ".png", _dir.Path })
        {
            lock (_events)
                Assert.All(_events, e => Assert.DoesNotContain(secret, e.RenderMessage() + " " + string.Join(" ", e.Properties.Values) + " " + e.Exception));
        }
    }
}
