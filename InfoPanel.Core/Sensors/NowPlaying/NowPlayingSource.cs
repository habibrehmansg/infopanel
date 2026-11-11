using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.NowPlaying;

/// <summary>
/// The Now Playing source, <c>infopanel.now-playing</c> ("Now Playing"): what the system's current media session plays,
/// under <c>infopanel:/now-playing/{entry}</c> (<see cref="Entries"/>), a <see cref="PolledSensorSource"/> over
/// <see cref="NowPlayingSampler"/> polled every second, the platform's reads behind <see cref="INowPlayingReader"/>
/// (macOS: <see cref="MacNowPlayingReader"/>).
/// <para>
/// It reads only while in use: while one of its keys was read (<see cref="TryRead"/>, <see cref="ReadState"/>: a shown
/// profile's and a client's value reads) or its samples used (<see cref="NoteUse"/>, a graph's draw) within
/// <see cref="UseWindow"/>, as the weather is asked for only then. A listing, a status and a peek
/// (<see cref="GetStatus"/>, <see cref="PeekState"/>, <see cref="TryPeek"/>) aren't uses. Out of use the reader is
/// stopped and the readings go stale, then unavailable; a read starts it again and values come at the next poll.
/// </para>
/// <para>
/// Titles, artists, albums and apps are personal: the source logs its state changes, errors and counts only, never them.
/// </para>
/// </summary>
public sealed class NowPlayingSource : ISensorSource, ISensorStatusSource, ISensorSourceState, IDisposable
{
    public const string SourceId = "infopanel.now-playing";

    /// <summary>The source's name, and its sensors' group.</summary>
    public const string Name = "Now Playing";

    /// <summary>The reason codes of its <c>unavailable</c> state (spec/docs/api.md, Sensor sources).</summary>
    public const string HelperMissing = "helperMissing";

    public const string HelperFailed = "helperFailed";

    public const string NotSupported = "notSupported";

    public const string NoSessionBus = "noSessionBus";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>A key used within this long is in use (the weather's and the Linux sensors' 2 minutes).</summary>
    public static readonly TimeSpan UseWindow = TimeSpan.FromMinutes(2);

    /// <summary>Texts are cut at this many characters.</summary>
    public const int MaxTextChars = 1024;

    /// <summary>Cover art over this many bytes is left out.</summary>
    public const int MaxArtBytes = 8 * 1024 * 1024;

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>The entries, in the order it lists them: id, name, kind and unit.</summary>
    public static IReadOnlyList<(string Id, string Name, SensorKind Kind, string Unit)> Entries { get; } =
    [
        ("title", "Title", SensorKind.Text, ""),
        ("artist", "Artist", SensorKind.Text, ""),
        ("album", "Album", SensorKind.Text, ""),
        ("app", "App", SensorKind.Text, ""),
        ("state", "State", SensorKind.Text, ""),
        ("position", "Position", SensorKind.Number, "s"),
        ("duration", "Duration", SensorKind.Number, "s"),
        ("progress", "Progress", SensorKind.Number, "%"),
        ("track-number", "Track Number", SensorKind.Number, ""),
        ("genre", "Genre", SensorKind.Text, ""),
        ("shuffle", "Shuffle", SensorKind.Text, ""),
        ("repeat", "Repeat", SensorKind.Text, ""),
    ];

    private readonly PolledSensorSource _polled;
    private readonly INowPlayingReader _reader;
    private readonly Demand _demand;

    private NowPlayingSource(INowPlayingReader reader, TimeProvider time, PolledSensorSourceOptions? options, ILogger? log)
    {
        _reader = reader;
        _demand = new Demand(time);
        _polled = new PolledSensorSource(SourceId, () => new NowPlayingSampler(reader, _demand, time, log),
            options ?? new PolledSensorSourceOptions { Interval = Interval }, time, log: log);
    }

    /// <summary>The key of the entry <paramref name="entryId"/>: <c>infopanel:/now-playing/{entryId}</c>.</summary>
    public static SensorKey KeyOf(string entryId) => Space.KeyOf(entryId);

    /// <summary>A source over <paramref name="reader"/> (a test's), which it disposes with itself.</summary>
    public static NowPlayingSource Create(INowPlayingReader reader, TimeProvider? time = null, PolledSensorSourceOptions? options = null, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new NowPlayingSource(reader, time ?? TimeProvider.System, options, log);
    }

    /// <summary>The source over this computer's media session: on macOS through the media helper (<see cref="MacNowPlayingReader"/>).</summary>
    [SupportedOSPlatform("macos")]
    public static NowPlayingSource CreateSystem(TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        return Create(MacNowPlayingReader.CreateSystem(time), time);
    }

    public string Id => _polled.Id;

    public SourceState State => _polled.State;

    public bool Handles(SensorKey key) => _polled.Handles(key);

    public IReadOnlyList<SensorInfo> GetSensors() => _polled.GetSensors();

    /// <summary>A consumer's read: puts the source in use.</summary>
    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        NoteUse(key);
        return _polled.TryRead(key, out reading);
    }

    /// <summary>A consumer's read: puts the source in use.</summary>
    public SensorState ReadState(SensorKey key)
    {
        NoteUse(key);
        return _polled.ReadState(key);
    }

    /// <summary>A peek isn't a use (the history's own sampling).</summary>
    public bool TryPeek(SensorKey key, out SensorReading reading) => _polled.TryRead(key, out reading);

    /// <summary>A peek isn't a use (listing the sensors).</summary>
    public SensorState PeekState(SensorKey key) => _polled.ReadState(key);

    /// <summary>A status isn't a use.</summary>
    public SensorStatus GetStatus(SensorKey key) => _polled.GetStatus(key);

    /// <summary>A consumer uses a sensor's samples (a graph's draw), or reads it: puts the source in use when the key is one of its own.</summary>
    public void NoteUse(SensorKey key)
    {
        if (_polled.Handles(key))
            _demand.Note();
    }

    public Task StartAsync(CancellationToken ct) => _polled.StartAsync(ct);

    public Task StopAsync(CancellationToken ct) => _polled.StopAsync(ct);

    public void Dispose()
    {
        _polled.Dispose();
        _reader.Dispose();
    }

    /// <summary>Discovers the sensors and takes the first poll. For tests.</summary>
    internal void Open() => _polled.Open();

    /// <summary>Takes one poll. For tests.</summary>
    internal void Poll() => _polled.Poll();

    /// <summary>
    /// Whether the source is in use: one of its keys used (<see cref="Note"/>) within <see cref="UseWindow"/>. One clock
    /// for the whole source; <see cref="Note"/> takes no lock and allocates nothing.
    /// </summary>
    internal sealed class Demand(TimeProvider time)
    {
        private const long Never = long.MinValue;

        private long _lastUse = Never;

        public void Note() => Volatile.Write(ref _lastUse, time.GetTimestamp());

        public bool InUse
        {
            get
            {
                var last = Volatile.Read(ref _lastUse);
                return last != Never && time.GetElapsedTime(last) <= UseWindow;
            }
        }
    }
}
