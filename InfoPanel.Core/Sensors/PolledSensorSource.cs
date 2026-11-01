using InfoPanel.Enums;
using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors;

public sealed class PolledSensorSourceOptions
{
    /// <summary>Time between polls.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A sensor whose last good sample is older than this (or whose latest read failed) is stale.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A sensor whose last good sample is older than this is unavailable.</summary>
    public TimeSpan UnavailableAfter { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// An <see cref="ISensorSource"/> over an <see cref="ISensorSampler"/>: <see cref="StartAsync"/> discovers the sensors
/// (off the caller's thread) and polls them every <see cref="PolledSensorSourceOptions.Interval"/> (the source's own)
/// on the thread pool, keeping now/min/max/avg per number sensor, or the source's own statistics when a sample carries
/// them, and the last good text per text sensor (no statistics; cut at <see cref="MaxTextChars"/> characters, with one
/// warning per key while it is listed: a key that returns after it was unlisted may warn again).
/// Keys are the source's <see cref="SensorKeySpace"/>'s (<c>plugin:{KeyPrefix}{definition id}</c> by default); the
/// source handles every key in it (unknown ones are unavailable). A sampler whose catalogue changes
/// (<see cref="ISensorSampler.CatalogueVersion"/>) is discovered again after the round that changed it: keys that stay
/// keep their state, keys that go are stale until they are unavailable, then unlisted. Status per key
/// (<see cref="ISensorStatusSource"/>):
/// <list type="bullet">
/// <item><see cref="SensorStatus.PermissionDenied"/> while the latest read was refused;</item>
/// <item><see cref="SensorStatus.Unavailable"/> with no good sample yet, or the last one older than
/// <see cref="PolledSensorSourceOptions.UnavailableAfter"/>;</item>
/// <item><see cref="SensorStatus.Stale"/> when the latest read failed or the last good sample is older than
/// <see cref="PolledSensorSourceOptions.StaleAfter"/> (the last value is still read);</item>
/// <item><see cref="SensorStatus.Live"/> otherwise.</item>
/// </list>
/// Reads take a short lock and allocate nothing.
/// </summary>
public sealed class PolledSensorSource : ISensorSource, ISensorStatusSource, IDisposable
{
    private struct Slot
    {
        // The kind its definition gave; a key whose kind changes at a rediscovery starts afresh.
        public SensorKind Kind;
        // A text sensor's last good text, read as it is (a read allocates nothing).
        public string? Text;
        // Whether its text was cut and warned of; it goes with the slot, so a key that returns after it expired warns again.
        public bool CutWarned;
        public double Now;
        // What a read answers: the source's statistics when the latest sample carried them, else the base's own.
        public double Min;
        public double Max;
        public double Avg;
        // The base's own, over every good value, whether or not a sample carried the source's.
        public double OwnMin;
        public double OwnMax;
        public double OwnAvg;
        public long Count;
        public long LastGood;
        public SampleOutcome Latest;
    }

    private readonly Func<ISensorSampler> _createSampler;
    private readonly PolledSensorSourceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private ISensorSampler? _sampler;
    private Dictionary<SensorKey, int> _index = [];
    private SensorInfo[] _sensors = [];
    private string[] _units = [];
    private Slot[] _slots = [];
    private SensorSample[] _buffer = [];
    private int[] _slotOf = [];
    private long _lastPoll;
    private bool _polled;
    private volatile bool _disposed;
    // Orders Dispose against a start publishing its loop: either the start sees the flag, or Dispose sees the loop.
    private readonly Lock _disposeLock = new();
    private bool _failedOnce;
    private bool _rediscoveryFailedOnce;
    private long _catalogueVersion;
    // The last _gone sensors listed are gone from the sampler's catalogue, kept (failed) until they are unavailable.
    private int _gone;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    /// <param name="id">The source id (<c>infopanel.macos</c>); keys are under <c>/{id}/</c>.</param>
    /// <param name="createSampler">Creates the sampler on <see cref="StartAsync"/>.</param>
    /// <param name="keySpace">How the keys look; <see cref="SensorKeySpace.Plugin"/> of <paramref name="id"/> by default.</param>
    /// <param name="log">Where its warnings go; the global logger by default.</param>
    public PolledSensorSource(string id, Func<ISensorSampler> createSampler, PolledSensorSourceOptions? options = null, TimeProvider? time = null, SensorKeySpace? keySpace = null, ILogger? log = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Id = id;
        KeyPrefix = "/" + id + "/";
        KeySpace = keySpace ?? SensorKeySpace.Plugin(id);
        _createSampler = createSampler;
        _options = options ?? new PolledSensorSourceOptions();
        _time = time ?? TimeProvider.System;
        _log = (log ?? Log.Logger).ForContext<PolledSensorSource>();
    }

    public string Id { get; }

    public string KeyPrefix { get; }

    public SensorKeySpace KeySpace { get; }

    public PolledSensorSourceOptions Options => _options;

    /// <summary>A text is cut to this many characters (4 KiB, as a plugin's).</summary>
    public const int MaxTextChars = 4096;

    /// <summary>Polls taken since start. For tests and diagnostics.</summary>
    public long Polls { get; private set; }

    public SensorKey KeyOf(string definitionId) => KeySpace.KeyOf(definitionId);

    public bool Handles(SensorKey key) => KeySpace.Handles(key);

    /// <summary>The discovered sensors (empty before <see cref="StartAsync"/>).</summary>
    public IReadOnlyList<SensorInfo> GetSensors() => Volatile.Read(ref _sensors);

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var state = ReadState(key);
        reading = state.Reading;
        return state.HasReading;
    }

    public SensorStatus GetStatus(SensorKey key) => ReadState(key).Status;

    /// <summary>A listed sensor is typed by its definition, with or without a reading; an unlisted key is a number.</summary>
    public SensorState ReadState(SensorKey key)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var i))
                return SensorState.Without(SensorStatus.Unavailable);

            ref var slot = ref _slots[i];
            var status = StatusOf(in slot, _time.GetTimestamp());
            if (status is not (SensorStatus.Live or SensorStatus.Stale))
                return SensorState.Without(status, slot.Kind);
            return slot.Kind == SensorKind.Text
                ? new SensorState(status, SensorKind.Text, new SensorReading(slot.Text!))
                : new SensorState(status, SensorKind.Number, new SensorReading(slot.Min, slot.Max, slot.Avg, slot.Now, _units[i]));
        }
    }

    private SensorStatus StatusOf(in Slot slot, long now)
    {
        if (slot.Latest == SampleOutcome.PermissionDenied)
            return SensorStatus.PermissionDenied;
        if (slot.Count == 0)
            return SensorStatus.Unavailable;

        var age = _time.GetElapsedTime(slot.LastGood, now);
        if (age > _options.UnavailableAfter)
            return SensorStatus.Unavailable;
        if (slot.Latest == SampleOutcome.Failed || age > _options.StaleAfter)
            return SensorStatus.Stale;
        return SensorStatus.Live;
    }

    /// <summary>Whether the poll loop is running. For tests.</summary>
    internal bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Discovers the sensors and starts polling (a no-op when running). The loop closes the sampler when it ends.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A loop whose stop timed out may have ended since: reap it so this start runs a new one.
            if (_loop is { IsCompleted: true })
                ReapLoop();
            if (_loop != null)
                return;

            // Discovery does blocking OS calls; keep it off the caller's thread.
            await Task.Run(Open, ct).ConfigureAwait(false);
            lock (_disposeLock)
            {
                if (!_disposed)
                {
                    _loopCts = new CancellationTokenSource();
                    _loop = RunAsync(new PeriodicTimer(_options.Interval, _time), _loopCts.Token);
                    return;
                }
            }

            // Disposed while discovering: Dispose left the sampler to us.
            CloseSampler();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the poll loop and waits for it to end (it closes the sampler). Start and stop are serialised; if
    /// <paramref name="ct"/> ends the wait first, the next start reaps the loop once it has ended.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loop == null)
                return;

            var loop = _loop;
            _loopCts!.Cancel();
            try
            {
                await loop.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (loop.IsCompleted)
                    ReapLoop();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void ReapLoop()
    {
        _loopCts?.Dispose();
        _loop = null;
        _loopCts = null;
    }

    /// <summary>
    /// Cancels the poll loop (which closes the sampler as it ends) and refuses later starts. Doesn't wait: a start in
    /// progress sees the flag after discovery and closes the sampler itself.
    /// </summary>
    public void Dispose()
    {
        lock (_disposeLock)
        {
            _disposed = true;
            try
            {
                _loopCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // Only when no start is running and no loop owns the sampler.
        if (_lifecycle.Wait(0))
        {
            try
            {
                if (_loop == null)
                    CloseSampler();
            }
            finally
            {
                _lifecycle.Release();
            }
        }
    }

    /// <summary>Creates the sampler and discovers its sensors, then takes the first poll. Called by <see cref="StartAsync"/>; tests call it directly.</summary>
    internal void Open()
    {
        if (_sampler != null)
            return;

        IReadOnlyList<SensorDefinition> definitions;
        try
        {
            _sampler = _createSampler();
            definitions = _sampler.Discover();
            _catalogueVersion = _sampler.CatalogueVersion;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Sensor source {Source} found no sensors: discovery failed", Id);
            CloseSampler();
            definitions = [];
        }

        // A fresh sampler starts with empty state: slots are carried only when the same sampler rediscovers.
        Rebuild(definitions, _time.GetTimestamp(), carry: false);
        _polled = false;
        Poll();
    }

    /// <summary>
    /// Makes <paramref name="definitions"/> the catalogue. With <paramref name="carry"/> (the same sampler's catalogue
    /// changed), a key that was listed with the same kind keeps its slot (now or text, min, max, avg, count, last good),
    /// and a listed key the catalogue no longer has stays listed after the catalogue's sensors, failed (stale), until it
    /// is unavailable (<see cref="PruneGone"/>); a new key, or one whose kind changed, starts with no reading. Without it
    /// (a new sampler), every key starts so.
    /// </summary>
    private void Rebuild(IReadOnlyList<SensorDefinition> definitions, long now, bool carry)
    {
        var oldIndex = carry ? _index : [];
        var oldSlots = carry ? _slots : [];
        var oldSensors = carry ? Volatile.Read(ref _sensors) : [];

        // One slot per distinct key; map[i] is definition i's slot (-1 for a duplicate, whose samples are ignored).
        var index = new Dictionary<SensorKey, int>(definitions.Count);
        var infos = new List<SensorInfo>(definitions.Count);
        var slots = new List<Slot>(definitions.Count);
        var map = new int[definitions.Count];
        for (var i = 0; i < definitions.Count; i++)
        {
            var d = definitions[i];
            SensorKey key;
            try
            {
                key = KeySpace.KeyOf(d.Id);
            }
            catch (ArgumentException)
            {
                map[i] = -1;
                _log.Warning("Sensor source {Source} skipped a sensor with the malformed id {Id}", Id, d.Id);
                continue;
            }

            if (d.Kind is not (SensorKind.Number or SensorKind.Text))
            {
                map[i] = -1;
                _log.Warning("Sensor source {Source} skipped the sensor {Key}: a polled source reads numbers and text, not {Kind}", Id, key, d.Kind);
                continue;
            }

            if (index.TryAdd(key, infos.Count))
            {
                map[i] = infos.Count;
                infos.Add(new SensorInfo(key, Id, d.Group, d.Name, d.Unit));
                slots.Add(oldIndex.TryGetValue(key, out var old) && oldSlots[old].Kind == d.Kind ? oldSlots[old] : new Slot { Kind = d.Kind });
            }
            else
            {
                map[i] = -1;
                _log.Warning("Sensor source {Source} skipped a duplicate sensor {Key}", Id, key);
            }
        }

        var gone = 0;
        for (var i = 0; i < oldSensors.Length; i++)
        {
            var key = oldSensors[i].Key;
            if (index.ContainsKey(key))
                continue;
            var slot = oldSlots[oldIndex[key]];
            if (slot.Count == 0 || _time.GetElapsedTime(slot.LastGood, now) > _options.UnavailableAfter)
                continue;

            // Gone from the catalogue: its reads fail from now on.
            slot.Latest = SampleOutcome.Failed;
            index[key] = infos.Count;
            infos.Add(oldSensors[i]);
            slots.Add(slot);
            gone++;
        }

        lock (_lock)
        {
            _index = index;
            _units = infos.Select(i => i.Unit).ToArray();
            _slots = slots.ToArray();
            _buffer = new SensorSample[definitions.Count];
            _slotOf = map;
            _gone = gone;
        }

        Volatile.Write(ref _sensors, infos.ToArray());
    }

    /// <summary>Unlists the sensors gone from the catalogue once they are unavailable.</summary>
    private void PruneGone(long now)
    {
        var sensors = Volatile.Read(ref _sensors);
        var first = sensors.Length - _gone;
        var expired = false;
        for (var i = first; i < sensors.Length && !expired; i++)
        {
            ref var slot = ref _slots[i];
            expired = _time.GetElapsedTime(slot.LastGood, now) > _options.UnavailableAfter;
        }

        if (!expired)
            return;

        var keep = new List<int>(sensors.Length);
        for (var i = 0; i < sensors.Length; i++)
        {
            if (i < first || _time.GetElapsedTime(_slots[i].LastGood, now) <= _options.UnavailableAfter)
                keep.Add(i);
        }

        var index = new Dictionary<SensorKey, int>(keep.Count);
        var infos = new SensorInfo[keep.Count];
        var slots = new Slot[keep.Count];
        for (var j = 0; j < keep.Count; j++)
        {
            infos[j] = sensors[keep[j]];
            slots[j] = _slots[keep[j]];
            index[infos[j].Key] = j;
        }

        lock (_lock)
        {
            _index = index;
            _units = infos.Select(i => i.Unit).ToArray();
            _slots = slots;
            _gone = keep.Count - first;
        }

        Volatile.Write(ref _sensors, infos);
    }

    /// <summary>
    /// Takes one sample of every sensor, then, when the sampler's catalogue changed, discovers it again. A sampler with
    /// no sensors is still called, so one whose sensors appear later is found. The poll loop calls this every interval;
    /// tests call it directly.
    /// </summary>
    internal void Poll()
    {
        var sampler = _sampler;
        if (sampler == null)
            return;

        var now = _time.GetTimestamp();
        var elapsed = _polled ? _time.GetElapsedTime(_lastPoll, now) : TimeSpan.Zero;
        _lastPoll = now;
        _polled = true;
        Array.Clear(_buffer);
        try
        {
            sampler.Sample(_buffer, elapsed);
            _failedOnce = false;
        }
        catch (Exception ex)
        {
            // A sampler bug fails this round; the sensors go stale, then unavailable, and the next round retries.
            if (!_failedOnce)
                _log.Warning(ex, "Sensor source {Source}: a poll failed", Id);
            _failedOnce = true;
            for (var i = 0; i < _buffer.Length; i++)
                _buffer[i].Outcome = SampleOutcome.Failed;
        }

        var sampled = _time.GetTimestamp();
        lock (_lock)
        {
            for (var i = 0; i < _buffer.Length; i++)
            {
                var s = _slotOf[i];
                if (s < 0)
                    continue;

                ref var slot = ref _slots[s];
                var sample = _buffer[i];
                switch (sample.Outcome)
                {
                    case SampleOutcome.Ok when slot.Kind == SensorKind.Text && sample.Text is { } text:
                        slot.Count++;
                        slot.Text = Cut(text, ref slot, s);
                        slot.LastGood = sampled;
                        slot.Latest = SampleOutcome.Ok;
                        break;
                    case SampleOutcome.Ok when slot.Kind == SensorKind.Number && sample.Text is null && double.IsFinite(sample.Value):
                        slot.Count++;
                        slot.Now = sample.Value;
                        if (slot.Count == 1)
                        {
                            slot.OwnMin = slot.OwnMax = slot.OwnAvg = sample.Value;
                        }
                        else
                        {
                            slot.OwnMin = Math.Min(slot.OwnMin, sample.Value);
                            slot.OwnMax = Math.Max(slot.OwnMax, sample.Value);
                            slot.OwnAvg += (sample.Value - slot.OwnAvg) / slot.Count;
                        }

                        (slot.Min, slot.Max, slot.Avg) = sample.HasStatistics
                            ? (sample.Min, sample.Max, sample.Avg)
                            : (slot.OwnMin, slot.OwnMax, slot.OwnAvg);

                        slot.LastGood = sampled;
                        slot.Latest = SampleOutcome.Ok;
                        break;
                    case SampleOutcome.Ok:
                    case SampleOutcome.Failed:
                        slot.Latest = SampleOutcome.Failed;
                        break;
                    case SampleOutcome.PermissionDenied:
                        slot.Latest = SampleOutcome.PermissionDenied;
                        break;
                }
            }
        }

        long version;
        try
        {
            version = sampler.CatalogueVersion;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Sensor source {Source}: reading the catalogue's version failed", Id);
            version = _catalogueVersion;
        }

        // Expired keys go first, so a rediscovery never carries one (and a rediscovery that fails doesn't keep them).
        if (_gone > 0)
            PruneGone(sampled);
        if (version != _catalogueVersion)
            Rediscover(sampler, version, sampled);

        Polls++;
    }

    /// <summary>
    /// <paramref name="text"/> cut at <see cref="MaxTextChars"/> (never inside a surrogate pair), warning once for
    /// <paramref name="slot"/> (the slot at <paramref name="index"/>) while it lasts; the sampler's own string when it fits.
    /// </summary>
    private string Cut(string text, ref Slot slot, int index)
    {
        if (text.Length <= MaxTextChars)
            return text;

        if (!slot.CutWarned)
        {
            slot.CutWarned = true;
            _log.Warning("Sensor source {Source}: the text of {Key} is longer than {Max} characters; the rest is cut", Id, _sensors[index].Key, MaxTextChars);
        }

        return text[..(char.IsHighSurrogate(text[MaxTextChars - 1]) ? MaxTextChars - 1 : MaxTextChars)];
    }

    private void Rediscover(ISensorSampler sampler, long version, long now)
    {
        IReadOnlyList<SensorDefinition> definitions;
        try
        {
            definitions = sampler.Discover();
        }
        catch (Exception ex)
        {
            // The catalogue stands; the next round tries again.
            if (!_rediscoveryFailedOnce)
                _log.Warning(ex, "Sensor source {Source}: discovering its changed sensors failed", Id);
            _rediscoveryFailedOnce = true;
            return;
        }

        _rediscoveryFailedOnce = false;
        _catalogueVersion = version;
        Rebuild(definitions, now, carry: true);
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        using var _ = timer;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Poll();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CloseSampler();
        }
    }

    private void CloseSampler()
    {
        var sampler = Interlocked.Exchange(ref _sampler, null);
        try
        {
            sampler?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Sensor source {Source}: closing the sampler failed", Id);
        }
    }
}
