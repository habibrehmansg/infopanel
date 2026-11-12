using System.Diagnostics;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Sensors.NowPlaying;
using Serilog.Events;
using Tmds.DBus.Protocol;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The Linux reader on a real session bus, opt-in (<c>INFOPANEL_MPRIS_FAKE=1</c> on Linux, with
/// <c>DBUS_SESSION_BUS_ADDRESS</c> naming a bus, as <c>dbus-daemon --session --fork --print-address</c> starts one in the
/// Linux test image): a fake player (<see cref="MprisFakePlayer"/>, <c>org.mpris.MediaPlayer2.infopaneltest</c>) serves
/// its properties, with values a reader must skip over (a megabyte of bytes, a struct, nested variants) beside the ones it
/// reads; the reader reads it through <see cref="MprisBus"/> (lists joined, microseconds as seconds, the cover art's file),
/// sees it pause, and reads nothing playing once it has left the bus; nothing it plays reaches the log. An oversized
/// player (a title of two million bytes, a thousand repeated entries) is read within the bus's bounds. The last test
/// only serves the fake player for 60 s, for an engine started beside it (docs/agents/linux.md).
/// </summary>
public sealed class MprisFakePlayerTests
{
    private static string? Address => Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");

    private static bool Enabled => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("INFOPANEL_MPRIS_FAKE") == "1" && !string.IsNullOrEmpty(Address);

    private const string Skip = "Serves a fake media player on the session bus: set INFOPANEL_MPRIS_FAKE=1 and DBUS_SESSION_BUS_ADDRESS on Linux to run it.";

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
    public async Task The_reader_reads_a_player_over_the_session_bus()
    {
        Assert.SkipUnless(Enabled, Skip);
        using var dir = new TempDirectory();
        var cover = dir.Combine("cover.png");
        File.WriteAllBytes(cover, MprisFakePlayer.Cover);
        await using var player = await MprisFakePlayer.StartAsync(Address!, new Uri(cover).AbsoluteUri);

        var events = new List<LogEvent>();
        var log = new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(events)).CreateLogger();
        using var reader = new LinuxNowPlayingReader(() => Address, address => new MprisBus(address), TimeProvider.System, log);
        reader.Start();

        var s = await Until(reader, s => s is not null, "the player read");
        Assert.Equal(MprisFakePlayer.Title, s!.Title);
        Assert.Equal("Test Artist One, Test Artist Two", s.Artist);
        Assert.Equal("Test Album", s.Album);
        Assert.Equal("Test Genre", s.Genre);
        Assert.Equal(7, s.TrackNumber);
        Assert.Equal(180, s.Duration);
        Assert.Equal(NowPlayingState.Playing, s.State);
        Assert.Equal("InfoPanel Test Player", s.App);
        Assert.Equal(NowPlayingRepeat.All, s.Repeat);
        Assert.False(s.Shuffle);
        Assert.Equal(1.0, s.Rate);
        Assert.InRange(s.Position!.Value, 30, 90);
        Assert.Equal(MprisFakePlayer.Cover, s.Art!.Value.ToArray());
        Log($"read: state {s.State}, position {s.Position:0.000}, duration {s.Duration}, art {s.Art?.Length} bytes");

        player.Status = "Paused";
        s = await Until(reader, s => s?.State == NowPlayingState.Paused, "the pause");
        Assert.Equal(MprisFakePlayer.Title, s!.Title);

        await player.DisposeAsync();
        await Until(reader, s => s is null, "nothing playing once the player left");
        reader.Dispose();

        lock (events)
        {
            foreach (var e in events)
                Log($"log: {e.Level} {e.RenderMessage()}");
            foreach (var secret in new[] { MprisFakePlayer.Title, "Test Artist", "Test Album", "InfoPanel Test Player", "infopaneltest", cover })
                Assert.All(events, e => Assert.DoesNotContain(secret, e.RenderMessage() + " " + string.Join(" ", e.Properties.Values)));
        }
    }

    [Fact]
    public async Task An_oversized_player_is_read_within_its_bounds()
    {
        Assert.SkipUnless(Enabled, Skip);
        await using var player = await MprisFakePlayer.StartAsync(Address!, "https://images.example/cover.png", oversized: true);
        using var reader = new LinuxNowPlayingReader(() => Address, address => new MprisBus(address), TimeProvider.System);
        reader.Start();

        var s = await Until(reader, s => s is not null, "the oversized player read");
        Assert.Equal(MprisBus.MaxStringBytes, s!.Title!.Length);
        // Twelve entries come before the thousand albums: the entry budget reads 52 of them.
        Assert.Equal("Album " + (MprisBus.MaxEntries - 13), s.Album);
        Log($"read: title {s.Title.Length} characters of {MprisFakePlayer.OversizedTitleBytes}, album {s.Album}, state {s.State}");
    }

    [Fact]
    public async Task A_fake_player_serves_on_the_session_bus_for_60_seconds()
    {
        Assert.SkipUnless(Enabled, Skip);
        var folder = Path.Combine(Path.GetTempPath(), "infopanel-mpris-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cover = Path.Combine(folder, "cover.png");
        File.WriteAllBytes(cover, MprisFakePlayer.Cover);
        await using (await MprisFakePlayer.StartAsync(Address!, new Uri(cover).AbsoluteUri))
        {
            Log($"serving {MprisFakePlayer.Name} for 60 s");
            await Task.Delay(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        }

        Log("left the bus");
    }

    private static async Task<NowPlayingSnapshot?> Until(LinuxNowPlayingReader reader, Func<NowPlayingSnapshot?, bool> condition, string what)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (reader.TryRead(out var s) && condition(s))
                return s;
            await Task.Delay(100);
        }

        Assert.Fail("timed out waiting for " + what + "; state " + reader.State);
        return null;
    }
}

/// <summary>
/// A media player on the session bus for the tests: <c>org.mpris.MediaPlayer2.infopaneltest</c>, answering
/// <c>org.freedesktop.DBus.Properties</c>' <c>Get</c> and <c>GetAll</c> for <c>org.mpris.MediaPlayer2</c> (its identity)
/// and <c>org.mpris.MediaPlayer2.Player</c> (playing a made-up track from 30 s in, its cover art a file), with metadata a
/// reader must skip over beside the entries it reads.
/// </summary>
internal sealed class MprisFakePlayer : IPathMethodHandler, IAsyncDisposable
{
    public const string Name = "org.mpris.MediaPlayer2.infopaneltest";

    public const string Title = "InfoPanel Test Title";

    /// <summary>The cover art's bytes (a PNG's signature and some: the reader doesn't decode it).</summary>
    public static readonly byte[] Cover = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Oversized, its title is this many bytes (in one string, across the message's buffers).</summary>
    public const int OversizedTitleBytes = 2_000_000;

    private readonly DBusConnection _connection;
    private readonly string _artUrl;
    private readonly bool _oversized;
    private readonly Stopwatch _playing = Stopwatch.StartNew();
    private int _disposed;

    public volatile string Status = "Playing";

    private MprisFakePlayer(DBusConnection connection, string artUrl, bool oversized)
    {
        _connection = connection;
        _artUrl = artUrl;
        _oversized = oversized;
    }

    /// <param name="oversized">Its title <see cref="OversizedTitleBytes"/> long, and its metadata ending in a thousand <c>xesam:album</c> entries.</param>
    public static async Task<MprisFakePlayer> StartAsync(string address, string artUrl, bool oversized = false)
    {
        var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        var player = new MprisFakePlayer(connection, artUrl, oversized);
        connection.AddMethodHandler(player);
        await connection.RequestNameAsync(Name);
        return player;
    }

    public string Path => MprisBus.ObjectPath;

    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        if (request.InterfaceAsString != "org.freedesktop.DBus.Properties" || request.MemberAsString is not ("Get" or "GetAll"))
        {
            context.ReplyUnknownMethodError();
            return default;
        }

        var body = request.GetBodyReader();
        var iface = body.ReadString();
        if (request.MemberAsString == "Get")
        {
            var property = body.ReadString();
            if (!Properties(iface).Contains(property))
            {
                context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", "No such property");
                return default;
            }

            var writer = context.CreateReplyWriter("v");
            try
            {
                Write(ref writer, property);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }

            return default;
        }

        var all = context.CreateReplyWriter("a{sv}");
        try
        {
            var start = all.WriteDictionaryStart();
            foreach (var property in Properties(iface))
            {
                all.WriteDictionaryEntryStart();
                all.WriteString(property);
                Write(ref all, property);
            }

            all.WriteDictionaryEnd(start);
            context.Reply(all.CreateMessage());
        }
        finally
        {
            all.Dispose();
        }

        return default;
    }

    private static string[] Properties(string iface) => iface switch
    {
        MprisBus.RootInterface => ["Identity", "CanQuit", "SupportedUriSchemes"],
        MprisBus.PlayerInterface => ["PlaybackStatus", "LoopStatus", "Rate", "Shuffle", "Metadata", "Volume", "Position", "CanPlay"],
        _ => [],
    };

    /// <summary>A property's value as a variant.</summary>
    private void Write(ref MessageWriter w, string property)
    {
        switch (property)
        {
            case "Identity":
                w.WriteVariantString("InfoPanel Test Player");
                break;
            case "CanQuit":
            case "CanPlay":
                w.WriteVariantBool(true);
                break;
            case "SupportedUriSchemes":
                w.WriteSignature("as");
                w.WriteArray(new[] { "file" });
                break;
            case "PlaybackStatus":
                w.WriteVariantString(Status);
                break;
            case "LoopStatus":
                w.WriteVariantString("Playlist");
                break;
            case "Rate":
            case "Volume":
                w.WriteVariantDouble(1.0);
                break;
            case "Shuffle":
                w.WriteVariantBool(false);
                break;
            case "Position":
                w.WriteVariantInt64(30_000_000 + _playing.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
                break;
            case "Metadata":
                WriteMetadata(ref w);
                break;
        }
    }

    private void WriteMetadata(ref MessageWriter w)
    {
        w.WriteSignature("a{sv}");
        var start = w.WriteDictionaryStart();

        w.WriteDictionaryEntryStart();
        w.WriteString("mpris:trackid");
        w.WriteVariantObjectPath("/org/infopanel/test/track/1");

        // Values a reader skips over: a megabyte of bytes, a struct holding an array of structs, an empty list.
        w.WriteDictionaryEntryStart();
        w.WriteString("infopanel:bytes");
        w.WriteSignature("ay");
        w.WriteArray(new byte[1024 * 1024]);

        w.WriteDictionaryEntryStart();
        w.WriteString("infopanel:struct");
        w.WriteSignature("(sa(ii))");
        w.WriteStructureStart();
        w.WriteString("skipped");
        var pairs = w.WriteArrayStart(DBusType.Struct);
        for (var i = 0; i < 3; i++)
        {
            w.WriteStructureStart();
            w.WriteInt32(i);
            w.WriteInt32(-i);
        }

        w.WriteArrayEnd(pairs);

        w.WriteDictionaryEntryStart();
        w.WriteString("xesam:comment");
        w.WriteSignature("as");
        w.WriteArray(Array.Empty<string>());

        // A string within two variants.
        w.WriteDictionaryEntryStart();
        w.WriteString("infopanel:nested");
        w.WriteSignature("v");
        w.WriteSignature("v");
        w.WriteVariantString("nested");

        w.WriteDictionaryEntryStart();
        w.WriteString("xesam:title");
        w.WriteVariantString(_oversized ? new string('T', OversizedTitleBytes) : Title);

        w.WriteDictionaryEntryStart();
        w.WriteString("xesam:artist");
        w.WriteSignature("as");
        w.WriteArray(new[] { "Test Artist One", "Test Artist Two" });

        w.WriteDictionaryEntryStart();
        w.WriteString("xesam:album");
        w.WriteVariantString("Test Album");

        w.WriteDictionaryEntryStart();
        w.WriteString("xesam:genre");
        w.WriteSignature("as");
        w.WriteArray(new[] { "Test Genre" });

        w.WriteDictionaryEntryStart();
        w.WriteString("xesam:trackNumber");
        w.WriteVariantInt32(7);

        w.WriteDictionaryEntryStart();
        w.WriteString("mpris:length");
        w.WriteVariantInt64(180_000_000);

        w.WriteDictionaryEntryStart();
        w.WriteString("mpris:artUrl");
        w.WriteVariantString(_artUrl);

        if (_oversized)
        {
            for (var i = 0; i < 1000; i++)
            {
                w.WriteDictionaryEntryStart();
                w.WriteString("xesam:album");
                w.WriteVariantString("Album " + i);
            }
        }

        w.WriteDictionaryEnd(start);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        try
        {
            await _connection.ReleaseNameAsync(Name);
        }
        catch (Exception)
        {
            // Leaving the bus releases the name as well.
        }

        _connection.Dispose();
    }
}
