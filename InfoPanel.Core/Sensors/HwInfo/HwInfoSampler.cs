using Serilog;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;

namespace InfoPanel.Sensors.HwInfo;

/// <summary>
/// Reads HWiNFO's sensors from its shared memory for <see cref="PolledSensorSource"/>: the local mapping
/// (<see cref="LocalName"/>, remote index −1) and the remote PCs HWiNFO connects to (<see cref="RemotePrefix"/>1, 2 …,
/// probed from 1 to the first missing one at the first round and every <see cref="ReprobeRounds"/> rounds while the
/// local one is there). Each round copies each mapping's used bytes once and reads the copy
/// (<see cref="HwInfoLayout"/>); the catalogue is compared with the previous round's every time, so readings HWiNFO
/// adds, drops or reorders are found (<see cref="ISensorSampler.CatalogueVersion"/>). Keys are 1.x's,
/// <c>{remote}:{sensor id}:{sensor instance}:{reading id}</c>; values carry HWiNFO's own min, max and average. A
/// mapping that is gone or <c>DEAD</c> drops its readings from the catalogue (the source keeps them stale, then
/// unavailable, then unlists them); so does one whose poll time stops for longer than max(5 s, 3 × HWiNFO's polling
/// period), whose frozen time is kept, so opening it again (each round) never makes it fresh: its readings come back
/// when its poll time moves. One that can't be trusted fails its readings and is opened again next round; one that
/// can't be opened withdraws them but keeps its poll time (only gone or <c>DEAD</c> ends a mapping's lifetime). No
/// mutex is taken: one copy per round keeps a torn read to one element.
/// </summary>
public sealed class HwInfoSampler : ISensorSampler
{
    /// <summary>Why a mapping didn't read in a round.</summary>
    private enum Lapse : byte
    {
        /// <summary>It read.</summary>
        None,

        /// <summary>It isn't there, or says it ended (<c>DEAD</c>).</summary>
        Gone,

        /// <summary>It is there, its poll time stopped.</summary>
        Frozen,

        /// <summary>It couldn't be opened or read, or its image was refused.</summary>
        Unreadable,
    }

    public const string LocalName = @"Global\HWiNFO_SENS_SM2";
    public const string RemotePrefix = @"Global\HWiNFO_SENS_SM2_REMOTE_";
    public const int ReprobeRounds = 5;
    public static readonly TimeSpan FrozenAfterAtLeast = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Receives each round's copies (the recorder); called on the poll thread: <see cref="Copied"/> after the copy,
    /// before parsing; <see cref="RoundEnded"/> once the round's catalogue is published; <see cref="Stopped"/> when the
    /// sampler closes (the source stopped). One that throws is logged once and called no more; reading goes on.
    /// </summary>
    public interface IObserver
    {
        void Copied(int remote, ReadOnlySpan<byte> image);

        void RoundEnded();

        void Stopped()
        {
        }
    }

    private sealed class Connection(int remote, string name)
    {
        public readonly int Remote = remote;
        public readonly string Name = name;
        public IHwInfoMemory? Memory;
        public byte[] Image = [];
        public HwInfoHeader Header;
        public byte[] Catalogue = [];
        public int CatalogueLength = -1;
        public List<(SensorDefinition Definition, int Element)> Definitions = [];
        public Dictionary<string, int> ElementOf = [];
        public bool Seen;
        public long PollTime;
        public long PollTimeChangedAt;
        public bool Fresh;
        public string? Trouble;
        // Why it didn't read this round, for the source's state (the local one's).
        public Lapse Lapse;

        public string Label => Remote < 0 ? "local" : "remote " + Remote.ToString(CultureInfo.InvariantCulture);
    }

    private readonly IHwInfoMemoryOpener _opener;
    private readonly TimeProvider _time;
    private IObserver? _observer;
    private readonly ILogger _log;
    private readonly Connection _local;
    private readonly string _remotePrefix;
    private readonly SortedDictionary<int, Connection> _remotes = [];
    // Remotes a probe couldn't open (a refusal, not a gap), kept so a refusal that stands is warned about once.
    private readonly Dictionary<int, Connection> _refused = [];
    private byte[] _scratch = [];
    private long _version;
    private long _rounds;
    private (Connection? Connection, int Element)[] _locators = [];
    private List<(int Remote, string Id)> _handedOut = [];
    private readonly HwInfoProcessCheck _process;
    private SourceState? _state;

    /// <param name="localName">The local mapping's name; <see cref="LocalName"/> but in tests.</param>
    /// <param name="remotePrefix">The remote mappings' names before their index; <see cref="RemotePrefix"/> but in tests.</param>
    /// <param name="log">Where it logs; Serilog's global logger but in tests.</param>
    /// <param name="hwinfoRunning">
    /// Whether HWiNFO runs (<see cref="HwInfoProcess.IsRunning"/> on Windows), asked only for the state while the local
    /// mapping isn't there; without it, HWiNFO counts as not running.
    /// </param>
    public HwInfoSampler(IHwInfoMemoryOpener opener, TimeProvider? time = null, IObserver? observer = null, string localName = LocalName, string remotePrefix = RemotePrefix, ILogger? log = null, Func<bool>? hwinfoRunning = null)
    {
        _log = (log ?? Log.Logger).ForContext<HwInfoSampler>();
        _local = new Connection(-1, localName);
        _remotePrefix = remotePrefix;
        _opener = opener;
        _time = time ?? TimeProvider.System;
        _observer = observer;
        _process = new HwInfoProcessCheck(hwinfoRunning, _time, _log, "HWiNFO's shared memory");
    }

    public long CatalogueVersion => _version;

    /// <summary>
    /// The local mapping's state after the latest round: running while it reads; gone (or <c>DEAD</c>),
    /// <see cref="HwInfoSource.SharedMemoryOff"/> while HWiNFO runs, else <see cref="HwInfoSource.NotRunning"/> (HWiNFO's
    /// process asked at most every <see cref="HwInfoProcessCheck.Every"/>, only then); a poll time that stopped,
    /// <see cref="HwInfoSource.Stopped"/>; one that can't be read, unavailable with no reason. Remotes don't change it.
    /// </summary>
    public SourceState? State => _state;

    /// <summary>A buffer's new length when it must hold <paramref name="needed"/> bytes: doubled, but never past what a round copies at most.</summary>
    internal static int Grown(int length, int needed) => Math.Max(needed, (int)Math.Min(length * 2L, HwInfoLayout.MaxImageBytes));

    /// <summary>The remote mappings open now, by index. For tests and diagnostics.</summary>
    public IReadOnlyCollection<int> Remotes => _remotes.Keys;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        if (_rounds == 0)
            Refresh();

        var definitions = new List<SensorDefinition>();
        _handedOut = [];
        foreach (var connection in Connections())
        {
            foreach (var (definition, _) in connection.Definitions)
            {
                definitions.Add(definition);
                _handedOut.Add((connection.Remote, definition.Id));
            }
        }

        Locate();
        return definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        Refresh();
        for (var i = 0; i < samples.Length && i < _locators.Length; i++)
        {
            var (connection, element) = _locators[i];
            if (connection is not { Fresh: true } || element < 0)
            {
                samples[i].Outcome = SampleOutcome.Failed;
                continue;
            }

            var e = HwInfoLayout.ReadingElement(connection.Image, connection.Header, element);
            var (value, min, max, avg) = HwInfoLayout.ReadingValuesOf(e);
            samples[i].Set(value, min, max, avg);
        }
    }

    public void Dispose()
    {
        Close(_local);
        foreach (var remote in _remotes.Values)
            Close(remote);
        _remotes.Clear();
        _refused.Clear();
        try
        {
            _observer?.Stopped();
        }
        catch (Exception ex)
        {
            ObserverFailed(ex);
        }
    }

    private void Copied(int remote, ReadOnlySpan<byte> image)
    {
        try
        {
            _observer?.Copied(remote, image);
        }
        catch (Exception ex)
        {
            ObserverFailed(ex);
        }
    }

    /// <summary>The observer threw: logged once and let go, so it never holds up a round (a recording stops; reading goes on).</summary>
    private void ObserverFailed(Exception ex)
    {
        _log.Error(ex, "HWiNFO's shared memory: its observer (a recording) failed and is let go; reading goes on");
        _observer = null;
    }

    private IEnumerable<Connection> Connections()
    {
        yield return _local;
        foreach (var remote in _remotes.Values)
            yield return remote;
    }

    /// <summary>One round: every mapping read once, the remotes probed when due, the catalogue's version moved on a change.</summary>
    private void Refresh()
    {
        var now = _time.GetTimestamp();
        var changed = Update(_local, now);
        _state = _local.Lapse switch
        {
            Lapse.None => null,
            Lapse.Gone => SourceState.UnavailableBecause(_process.IsRunning(now) ? HwInfoSource.SharedMemoryOff : HwInfoSource.NotRunning),
            Lapse.Frozen => SourceState.UnavailableBecause(HwInfoSource.Stopped),
            _ => SourceState.Unavailable,
        };
        var probed = _local.Memory != null && _rounds % ReprobeRounds == 0 ? Probe(now, ref changed) : null;

        List<int>? gone = null;
        foreach (var remote in _remotes.Values)
        {
            if (probed == null || !probed.Contains(remote.Remote))
                changed |= Update(remote, now);
            // Gone for good, unless frozen: that one is kept, with its frozen time, until it moves or goes.
            if (remote.Memory == null && remote.Definitions.Count == 0 && !remote.Seen)
                (gone ??= []).Add(remote.Remote);
        }

        if (gone != null)
        {
            foreach (var index in gone)
            {
                // One that failed keeps its warning for the probes to come.
                if (_remotes.Remove(index, out var remote) && remote.Trouble != null)
                    _refused[index] = remote;
            }
        }

        _rounds++;
        // The catalogue is published before the observer hears of the round, so nothing it does can hold it back.
        if (changed)
        {
            _version++;
            Locate();
        }

        try
        {
            _observer?.RoundEnded();
        }
        catch (Exception ex)
        {
            ObserverFailed(ex);
        }
    }

    /// <summary>
    /// Opens the remotes from 1 up to the first that doesn't exist or can't be opened; one already open counts as
    /// there. One refused keeps its state from probe to probe (warned about once) until it opens or is gone.
    /// </summary>
    private HashSet<int> Probe(long now, ref bool changed)
    {
        var probed = new HashSet<int>();
        for (var i = 1; i <= HwInfoLayout.MaxSensors; i++)
        {
            if (_remotes.ContainsKey(i))
                continue;

            var remote = _refused.GetValueOrDefault(i) ?? new Connection(i, _remotePrefix + i.ToString(CultureInfo.InvariantCulture));
            if (!TryOpen(remote, out var refused))
            {
                if (refused)
                    _refused[i] = remote;
                else
                    _refused.Remove(i);
                break;
            }

            _refused.Remove(i);
            _remotes[i] = remote;
            probed.Add(i);
            _log.Information("HWiNFO's remote connection {Index} found", i);
            changed |= Update(remote, now);
        }

        return probed;
    }

    /// <summary>Opens the mapping: true when it did; false when it isn't there, or, with <paramref name="refused"/>, when opening it threw.</summary>
    private bool TryOpen(Connection connection, out bool refused)
    {
        refused = false;
        try
        {
            connection.Memory = _opener.TryOpen(connection.Name);
            // No mapping is a state of its own: a refusal after it is a new one, warned about again.
            if (connection.Memory == null)
                connection.Trouble = null;
        }
        catch (Exception ex)
        {
            Trouble(connection, $"it can't be opened: {ex.GetType().Name}: {ex.Message}");
            connection.Memory = null;
            refused = true;
        }

        return connection.Memory != null;
    }

    /// <summary>Reads one mapping; true when its catalogue changed.</summary>
    private bool Update(Connection connection, long now)
    {
        connection.Fresh = false;
        // Gone, the mapping's lifetime ended; refused, it may still be the same one: its readings are withdrawn but its
        // poll time kept, so a frozen image opened again later is still frozen.
        if (connection.Memory == null && !TryOpen(connection, out var refused))
        {
            connection.Lapse = refused ? Lapse.Unreadable : Lapse.Gone;
            return refused ? Withdraw(connection) : Drop(connection, null);
        }

        try
        {
            var memory = connection.Memory!;
            Span<byte> head = stackalloc byte[HwInfoLayout.HeaderSizeWithPeriod];
            head = head[..(int)Math.Min(head.Length, Math.Max(0, memory.Length))];
            memory.Read(0, head);
            var state = HwInfoLayout.ReadHeader(head, memory.Length, out var header, out var problem);
            if (state == HwInfoImageState.Ok)
            {
                // One copy of everything the header names, read again from the copy (HWiNFO may have moved on).
                var end = (int)header.End;
                if (connection.Image.Length < end)
                    connection.Image = new byte[Grown(connection.Image.Length, end)];
                var image = connection.Image.AsSpan(0, end);
                memory.Read(0, image);
                Copied(connection.Remote, image);
                state = HwInfoLayout.ReadHeader(image, end, out header, out problem);
            }

            switch (state)
            {
                case HwInfoImageState.Dead:
                    if (head.Length >= 4 && HwInfoLayout.ReadHeader(head, memory.Length, out _, out _) == HwInfoImageState.Dead)
                        Copied(connection.Remote, head[..4]);
                    Close(connection);
                    connection.Lapse = Lapse.Gone;
                    return Drop(connection, "its signature is DEAD");
                case HwInfoImageState.Refused:
                    Close(connection);
                    connection.Lapse = Lapse.Unreadable;
                    Trouble(connection, "its image was refused: " + problem);
                    return false;
            }

            connection.Header = header;
            if (!connection.Seen || header.PollTime != connection.PollTime)
            {
                connection.Seen = true;
                connection.PollTime = header.PollTime;
                connection.PollTimeChangedAt = now;
            }

            var frozenAfter = TimeSpan.FromMilliseconds(Math.Max(FrozenAfterAtLeast.TotalMilliseconds, 3.0 * header.PollingPeriodMs));
            if (_time.GetElapsedTime(connection.PollTimeChangedAt, now) > frozenAfter)
            {
                // Hung, or ended without saying so: let go of the mapping so a HWiNFO that starts again makes a new one,
                // and withdraw its readings (the source unlists them once unavailable). Its frozen time stays, so the
                // same image opened again isn't fresh; the catalogue is read again once the poll time moves.
                Close(connection);
                connection.Lapse = Lapse.Frozen;
                Trouble(connection, $"its poll time hasn't moved for over {frozenAfter.TotalSeconds:0.#} s");
                return Withdraw(connection);
            }

            var changed = ReadCatalogue(connection);

            if (connection.Trouble != null)
                _log.Information("HWiNFO's shared memory ({Connection}) reads again", connection.Label);
            connection.Trouble = null;
            connection.Fresh = true;
            connection.Lapse = Lapse.None;
            return changed;
        }
        catch (Exception ex)
        {
            Close(connection);
            connection.Lapse = Lapse.Unreadable;
            Trouble(connection, $"reading it failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Compares the image's catalogue (its section counts and element sizes, then each element's catalogue bytes) with
    /// the last one; on a change reads the catalogue again.
    /// </summary>
    private bool ReadCatalogue(Connection connection)
    {
        var h = connection.Header;
        var image = connection.Image.AsSpan(0, (int)h.End);
        var size = 16 + (int)h.SensorCount * HwInfoLayout.SensorCatalogueBytes + (int)h.ReadingCount * HwInfoLayout.ReadingCatalogueBytes;
        if (_scratch.Length < size)
            _scratch = new byte[Grown(_scratch.Length, size)];

        // The same bytes split differently (other counts or sizes) are another catalogue: every element moves.
        BinaryPrimitives.WriteUInt32LittleEndian(_scratch, h.SensorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan(4), h.ReadingCount);
        BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan(8), h.SensorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(_scratch.AsSpan(12), h.ReadingSize);
        var at = 16;
        for (var i = 0; i < h.SensorCount; i++)
        {
            HwInfoLayout.SensorElement(image, h, i)[..HwInfoLayout.SensorCatalogueBytes].CopyTo(_scratch.AsSpan(at));
            at += HwInfoLayout.SensorCatalogueBytes;
        }

        for (var i = 0; i < h.ReadingCount; i++)
        {
            HwInfoLayout.CatalogueBytesOf(HwInfoLayout.ReadingElement(image, h, i), _scratch.AsSpan(at), out var written);
            at += written;
        }

        var bytes = _scratch.AsSpan(0, at);
        if (connection.CatalogueLength == at && bytes.SequenceEqual(connection.Catalogue.AsSpan(0, at)))
            return false;

        if (connection.Catalogue.Length < at)
            connection.Catalogue = new byte[at];
        bytes.CopyTo(connection.Catalogue);
        connection.CatalogueLength = at;

        var snapshot = HwInfoLayout.Read(image, h);
        var definitions = new List<(SensorDefinition, int)>(snapshot.Readings.Count);
        var elementOf = new Dictionary<string, int>(snapshot.Readings.Count);
        var element = 0;
        var duplicates = 0;
        for (var i = 0; i < h.ReadingCount; i++)
        {
            var e = HwInfoLayout.ReadingElement(image, h, i);
            var sensorIndex = HwInfoLayout.ReadingSensorIndexOf(e);
            if (sensorIndex >= h.SensorCount)
                continue;

            var reading = snapshot.Readings[element++];
            var sensor = snapshot.Sensors[(int)sensorIndex];
            var id = SensorKey.HwInfo(connection.Remote, sensor.Id, sensor.Instance, reading.Id).Id;
            if (!elementOf.TryAdd(id, i))
            {
                duplicates++;
                continue;
            }

            definitions.Add((new SensorDefinition(id, sensor.Name, reading.Name, reading.Unit), i));
        }

        var first = connection.Definitions.Count == 0;
        connection.Definitions = definitions;
        connection.ElementOf = elementOf;
        if (first && definitions.Count > 0)
        {
            _log.Information(
                "HWiNFO's shared memory ({Connection}) read: {Sensors} sensors, {Readings} readings (version {Version}.{Revision}, polling every {Period} ms)",
                connection.Label, h.SensorCount, definitions.Count, h.Version, h.Revision, h.PollingPeriodMs);
        }

        if (snapshot.Skipped > 0 || duplicates > 0)
        {
            _log.Debug("HWiNFO's shared memory ({Connection}): skipped {Skipped} readings past the sensor list and {Duplicates} duplicates",
                connection.Label, snapshot.Skipped, duplicates);
        }

        return true;
    }

    /// <summary>
    /// The mapping is gone or dead (its lifetime ended): its readings leave the catalogue and its poll time is forgotten.
    /// A warning about it stands until it reads or is gone. True when it had readings.
    /// </summary>
    private bool Drop(Connection connection, string? why)
    {
        var had = connection.Definitions.Count > 0;
        if (had)
            _log.Information("HWiNFO's shared memory ({Connection}) ended: {Why}", connection.Label, why ?? "it is gone");
        Withdraw(connection);
        connection.Seen = false;
        return had;
    }

    /// <summary>The mapping's readings leave the catalogue, read again when it next reads. True when it had any.</summary>
    private static bool Withdraw(Connection connection)
    {
        var had = connection.Definitions.Count > 0;
        connection.Definitions = [];
        connection.ElementOf = [];
        connection.CatalogueLength = -1;
        return had;
    }

    private void Trouble(Connection connection, string trouble)
    {
        if (connection.Trouble == null)
            _log.Warning("HWiNFO's shared memory ({Connection}) can't be read: {Trouble}", connection.Label, trouble);
        connection.Trouble = trouble;
    }

    private static void Close(Connection connection)
    {
        var memory = connection.Memory;
        connection.Memory = null;
        memory?.Dispose();
    }

    /// <summary>Where each handed-out definition's reading is in the current images (-1 when it is no longer there).</summary>
    private void Locate()
    {
        var locators = new (Connection?, int)[_handedOut.Count];
        for (var i = 0; i < locators.Length; i++)
        {
            var (remote, id) = _handedOut[i];
            var connection = remote < 0 ? _local : _remotes.GetValueOrDefault(remote);
            locators[i] = connection != null && connection.ElementOf.TryGetValue(id, out var element) ? (connection, element) : (connection, -1);
        }

        _locators = locators;
    }
}
