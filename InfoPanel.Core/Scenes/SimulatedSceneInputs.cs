using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using InfoPanel.Sensors;
using InfoPanel.Spec;

namespace InfoPanel.Scenes;

/// <summary>
/// A simulated subscription's inputs (spec/docs/api.md, "Simulated sensors"): every <c>number</c> input bound to a
/// well-formed key reads <see cref="SceneInputStatus.Live"/>, with a made-up value that walks (<see cref="SensorWalk"/>),
/// a step a second from the first read; every text and table <paramref name="live"/> has a reading for reads
/// <see cref="SceneInputStatus.Live"/> too, made up by <see cref="SimulatedText"/> from the key's source, the sensor's
/// display name in <paramref name="catalogue"/> (by default the hub's, under <see cref="SensorSceneInputs"/>) and the
/// value's shape. The engine's clock (<see cref="SimulatedText.IsRealTime"/>), keys that aren't well formed and text or
/// tables <paramref name="live"/> has no reading for read as <paramref name="live"/> reads them.
/// <list type="bullet">
/// <item>Text: each key keeps what it first showed while it is read; an address, name or sample is the next of its
/// kind, a source's texts of one adapter, drive or audio device the same one. System Info's uptime starts from
/// <see cref="SimulatedText.UptimeStart"/> at the first read and moves as time does. At most a text key for each binding
/// a document within <paramref name="limits"/> holds is kept: a new one past that forgets the key read longest ago, and
/// only its sample, which the next new key of its kind may take.</item>
/// <item>Tables: the columns and rows of <paramref name="live"/>'s (<see cref="SimulatedText.Table"/>), their numbers
/// made up again each step.</item>
/// <item>Where it starts: a key <paramref name="live"/> reads a number for walks <see cref="SensorWalk.Around"/> that
/// first reading, shown in its unit when that is one of <see cref="SimulatedText.Units"/> (in the list's spelling,
/// <see cref="SimulatedText.Unit"/>) and in none otherwise; one it doesn't, as mock mode's walk for the key (<see cref="SensorWalk.ForUnknownKey"/>),
/// which starts from 20 to 80, kept within <see cref="NoReadingLow"/> to <see cref="NoReadingHigh"/>. Each starts when
/// it is first read and keeps its start.</item>
/// <item>History: <see cref="Backfill"/> values already walked when it starts, the start the oldest, then one a step,
/// at most <see cref="SensorHistory.MaxSamples"/>. As the live history, at most <see cref="SensorHistory.MaxSeries"/>
/// keys walking at once keep one, the first started; the others keep only their now, min, max and average, and their
/// history reads empty.</item>
/// <item>A key no read has asked for in <see cref="ForgetAfter"/> is forgotten (an item bound to another key, say); read
/// again, it starts afresh. At most a key for each binding a document within <paramref name="limits"/> holds walk at
/// once (its items times their bindings): a new key past that forgets the key read longest ago, which a document within
/// them no longer binds once a tick has read its keys, so every binding of a document is simulated.</item>
/// </list>
/// One per simulated render loop; thread-safe.
/// </summary>
internal sealed class SimulatedSceneInputs(ISceneInputSource live, TimeProvider time, SpecLimits? limits = null, Func<IReadOnlyList<SensorInfo>>? catalogue = null) : ISceneInputSource
{
    /// <summary>The values a number's history holds when it starts.</summary>
    public const int Backfill = 120;

    /// <summary>The band a key with no reading stays within.</summary>
    public const double NoReadingLow = 20, NoReadingHigh = 80;

    /// <summary>A key no read has asked for this long is forgotten.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan StepInterval = SensorHub.TickInterval;

    /// <summary>Keys that walk at once: a document's every binding, each a key of its own.</summary>
    private readonly int _maxKeys = MaxKeys(limits ?? SpecLimits.Default);

    internal static int MaxKeys(SpecLimits limits) => (int)Math.Clamp((long)limits.Items * limits.Bindings, 1, int.MaxValue);

    /// <summary>A key's walk, its values held within <c>low</c> to <c>high</c>, with the min, max and average of them.</summary>
    private sealed class Walked(string key, SensorWalk walk, string unit, double low, double high, bool keepsHistory)
    {
        /// <summary>The history's room at the start: the backfill's, then doubled as it fills, up to a full history.</summary>
        private const int FirstCapacity = 128;

        public readonly string Key = key;
        public readonly SensorWalk Walk = walk;
        public readonly string Unit = unit;
        public long Steps;
        public long LastRead;
        public double Now, Min, Max, Avg;

        /// <summary>Values the history has copied, growing or read: the work its upkeep costs.</summary>
        public long Copied;

        private long _count;

        // The history, a ring (as SensorHistory's): _length values, the oldest at _first; none for a key that keeps none.
        private double[]? _ring = keepsHistory ? new double[FirstCapacity] : null;
        private int _first, _length;

        public bool KeepsHistory => _ring is not null;

        /// <summary>The values the history holds room for.</summary>
        public long Held => _ring?.Length ?? 0;

        /// <summary>Takes the walk's value now, within the band, into the history and the min, max and average.</summary>
        public void Take()
        {
            Now = Math.Clamp(Walk.Now, low, high);
            _count++;
            (Min, Max, Avg) = _count == 1 ? (Now, Now, Now) : (Math.Min(Min, Now), Math.Max(Max, Now), Avg + (Now - Avg) / _count);

            if (_ring is null)
                return;
            if (_length == _ring.Length && _ring.Length < SensorHistory.MaxSamples)
            {
                var grown = new double[Math.Min(_ring.Length * 2, SensorHistory.MaxSamples)];
                for (var i = 0; i < _length; i++)
                    grown[i] = _ring[(_first + i) % _ring.Length];
                Copied += _length;
                _ring = grown;
                _first = 0;
            }
            if (_length < _ring.Length)
            {
                _ring[(_first + _length) % _ring.Length] = Now;
                _length++;
            }
            else
            {
                _ring[_first] = Now;
                _first = (_first + 1) % _ring.Length;
            }
        }

        /// <summary>A copy of the history, oldest first (as the live history's read): the ring is the only one kept.</summary>
        public double[] History()
        {
            if (_ring is null)
                return [];
            var history = new double[_length];
            for (var i = 0; i < _length; i++)
                history[i] = _ring[(_first + i) % _ring.Length];
            Copied += _length;
            return history;
        }
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<Walked>> _walks = new(StringComparer.Ordinal);

    /// <summary>The keys walking, the one read longest ago first.</summary>
    private readonly LinkedList<Walked> _byRead = new();

    private int _histories;
    private long? _origin;

    /// <summary>
    /// A text key's place: its kind, its turn among its kind's (shared by its scope's keys, <see cref="SimulatedText.Scope"/>),
    /// its label, and whether it is placed for good (the catalogue listed it, or there is no catalogue).
    /// </summary>
    private sealed class Placed(string key)
    {
        public readonly string Key = key;
        public SimulatedText.Kind Kind;
        public (SimulatedText.Kind, string) Scope;
        public int Turn;
        public string Label = "";
        public bool ForGood;
    }

    /// <summary>The text keys placed, the one read longest ago first: at most <see cref="_maxKeys"/>.</summary>
    private readonly Dictionary<string, LinkedListNode<Placed>> _texts = new(StringComparer.Ordinal);
    private readonly LinkedList<Placed> _textsByRead = new();

    /// <summary>Each scope's turn and the keys holding it: an entry for each scope a placed key holds, so no more than them.</summary>
    private readonly Dictionary<(SimulatedText.Kind, string), (int Turn, int Keys)> _turns = [];

    /// <summary>Each kind's turns: the next never given, and those given back, the lowest taken first.</summary>
    private readonly Dictionary<SimulatedText.Kind, (int Next, SortedSet<int> Free)> _kindTurns = [];

    private readonly Func<IReadOnlyList<SensorInfo>>? _catalogue = catalogue ?? (live as SensorSceneInputs)?.Catalogue;
    private readonly Lock _catalogueLock = new();
    private Dictionary<SensorKey, SensorInfo>? _infos;
    private long _infosAt;

    /// <summary>A text or table: made up from <paramref name="live"/>'s reading; one without a reading as it reads.</summary>
    private SceneReading MadeUp(string key, string inputType)
    {
        var reading = live.Read(key, inputType);
        if (!reading.Available)
            return reading;
        if (!SensorKey.TryParse(key, out var parsed))
            return SceneReading.Unavailable;

        if (inputType == "table")
        {
            var table = SimulatedText.Table(parsed, reading.Table, (long)(Elapsed() / StepInterval));
            return new SceneReading { Status = SceneInputStatus.Live, Table = table, Text = SimulatedText.Summary(table) };
        }
        if (SimulatedText.IsRealTime(parsed))
            return reading;

        (SimulatedText.Kind Kind, int Turn, string Label) made;
        bool forGood;
        lock (_lock)
            forGood = Touch(key, out made);
        if (!forGood)
        {
            var info = Describe(parsed);
            var name = info?.Name ?? "";
            var kind = SimulatedText.Classify(parsed, name, reading.Text);
            lock (_lock)
                made = Place(key, parsed, kind, SimulatedText.Label(parsed, name), info is not null || _catalogue is null);
        }

        var text = made.Kind == SimulatedText.Kind.Uptime
            ? SimulatedText.Uptime(parsed, made.Label, Elapsed())
            : SimulatedText.Value(made.Kind, made.Turn, made.Label, reading.Text);
        return new SceneReading { Status = SceneInputStatus.Live, Text = text };
    }

    /// <summary>Under the lock: the key's place, now the one read last; false when it has none for good yet.</summary>
    private bool Touch(string key, out (SimulatedText.Kind, int, string) made)
    {
        made = default;
        if (!_texts.TryGetValue(key, out var node))
            return false;
        _textsByRead.Remove(node);
        _textsByRead.AddLast(node);
        var p = node.Value;
        made = (p.Kind, p.Turn, p.Label);
        return p.ForGood;
    }

    /// <summary>
    /// Under the lock: places the key, or places it again (a key the catalogue didn't list yet), as the one read last. A
    /// new key past <see cref="_maxKeys"/> forgets the key read longest ago, and gives back its turn when no other key of
    /// its scope holds it.
    /// </summary>
    private (SimulatedText.Kind, int, string) Place(string key, SensorKey parsed, SimulatedText.Kind kind, string label, bool forGood)
    {
        if (_texts.TryGetValue(key, out var node))
        {
            if (node.Value.ForGood)
                return (node.Value.Kind, node.Value.Turn, node.Value.Label);
            Remove(node);
        }
        else if (_texts.Count >= _maxKeys)
            Remove(_textsByRead.First!);

        var placed = new Placed(key) { Kind = kind, Scope = (kind, SimulatedText.Scope(parsed, kind) ?? key), Label = label, ForGood = forGood };
        if (_turns.TryGetValue(placed.Scope, out var held))
        {
            placed.Turn = held.Turn;
            _turns[placed.Scope] = (held.Turn, held.Keys + 1);
        }
        else
        {
            var (next, free) = _kindTurns.TryGetValue(kind, out var turns) ? turns : (0, new SortedSet<int>());
            if (free.Count > 0)
            {
                placed.Turn = free.Min;
                free.Remove(placed.Turn);
            }
            else
                placed.Turn = next++;
            _kindTurns[kind] = (next, free);
            _turns[placed.Scope] = (placed.Turn, 1);
        }
        _texts[key] = _textsByRead.AddLast(placed);
        return (placed.Kind, placed.Turn, placed.Label);
    }

    /// <summary>Under the lock: forgets a text key, and gives back its turn when no other key of its scope holds it.</summary>
    private void Remove(LinkedListNode<Placed> node)
    {
        _textsByRead.Remove(node);
        _texts.Remove(node.Value.Key);
        var scope = node.Value.Scope;
        var held = _turns[scope];
        if (held.Keys > 1)
        {
            _turns[scope] = (held.Turn, held.Keys - 1);
            return;
        }
        _turns.Remove(scope);
        _kindTurns[scope.Item1].Free.Add(held.Turn);
    }

    /// <summary>The text keys placed and the turns they hold.</summary>
    internal (int Keys, int Turns) TextState
    {
        get
        {
            lock (_lock) return (_texts.Count, _turns.Count);
        }
    }

    /// <summary>The time since the first read: what a table's numbers move with, and what the uptime adds to its start.</summary>
    private TimeSpan Elapsed()
    {
        lock (_lock)
        {
            var now = time.GetTimestamp();
            _origin ??= now;
            return time.GetElapsedTime(_origin.Value, now);
        }
    }

    /// <summary>The sensor's entry in the catalogue, looked up again at most a step after a key it didn't list.</summary>
    private SensorInfo? Describe(SensorKey key)
    {
        if (_catalogue is null)
            return null;
        lock (_catalogueLock)
        {
            if (_infos is not null && _infos.TryGetValue(key, out var info))
                return info;
            var now = time.GetTimestamp();
            if (_infos is null || time.GetElapsedTime(_infosAt, now) >= StepInterval)
            {
                _infos = [];
                foreach (var sensor in _catalogue())
                    _infos.TryAdd(sensor.Key, sensor);
                _infosAt = now;
            }
            return _infos.GetValueOrDefault(key);
        }
    }

    public SceneReading Read(string key, string inputType)
    {
        if (inputType != "number")
            return MadeUp(key, inputType);
        if (Walk(key) is not { } walked)
            return live.Read(key, inputType);

        lock (_lock)
        {
            return new SceneReading
            {
                Status = SceneInputStatus.Live,
                Now = walked.Now,
                Min = walked.Min,
                Max = walked.Max,
                Avg = walked.Avg,
                Unit = walked.Unit,
            };
        }
    }

    public IReadOnlyList<double> History(string key)
    {
        if (Walk(key) is not { } walked)
            return live.History(key);

        lock (_lock)
            return walked.History();
    }

    /// <summary>Values the histories of the keys walking now have copied (each value moved or copied out counts one).</summary>
    internal long Copied
    {
        get
        {
            lock (_lock) return _byRead.Sum(w => w.Copied);
        }
    }

    /// <summary>Values the histories of the keys walking now hold room for.</summary>
    internal long Held
    {
        get
        {
            lock (_lock) return _byRead.Sum(w => w.Held);
        }
    }

    /// <summary>The key's walk, started on its first read and stepped up to now; null for a key that doesn't walk.</summary>
    private Walked? Walk(string key)
    {
        if (!SensorKey.TryParse(key, out var parsed) || !SensorHub.IsWellFormed(parsed) || SimulatedText.IsRealTime(parsed))
            return null;

        lock (_lock)
        {
            var now = time.GetTimestamp();
            _origin ??= now;
            Forget(now);
            var due = (long)(time.GetElapsedTime(_origin.Value, now) / StepInterval);
            if (_walks.TryGetValue(key, out var node))
            {
                _byRead.Remove(node);
                _byRead.AddLast(node);
            }
            else
            {
                if (_walks.Count >= _maxKeys)
                    Remove(_byRead.First!);
                var started = Start(key, parsed, _histories < SensorHistory.MaxSeries);
                if (started.KeepsHistory)
                    _histories++;
                started.Steps = due;
                node = _byRead.AddLast(started);
                _walks[key] = node;
            }

            var walked = node.Value;
            // A loop that ticked rarely catches up at most a history's worth of steps.
            var behind = Math.Min(due - walked.Steps, SensorHistory.MaxSamples);
            for (var i = 0; i < behind; i++)
                Step(walked);
            walked.Steps = due;
            walked.LastRead = now;
            return walked;
        }
    }

    /// <summary>Under the lock: forgets the keys no read has asked for in <see cref="ForgetAfter"/>, each the one read longest ago.</summary>
    private void Forget(long now)
    {
        while (_byRead.First is { } first && time.GetElapsedTime(first.Value.LastRead, now) >= ForgetAfter)
            Remove(first);
    }

    private void Remove(LinkedListNode<Walked> node)
    {
        _byRead.Remove(node);
        _walks.Remove(node.Value.Key);
        if (node.Value.KeepsHistory)
            _histories--;
    }

    /// <summary>Under the lock (<paramref name="live"/> reads without blocking).</summary>
    private Walked Start(string key, SensorKey parsed, bool keepsHistory)
    {
        var reading = live.Read(key, "number");
        var walked = reading.Available
            ? new Walked(key, SensorWalk.Around(parsed, reading.Now, reading.Unit), SimulatedText.Unit(parsed, reading.Unit), double.NegativeInfinity, double.PositiveInfinity, keepsHistory)
            : new Walked(key, SensorWalk.ForUnknownKey(parsed), "", NoReadingLow, NoReadingHigh, keepsHistory);
        walked.Take();
        for (var i = 1; i < Backfill; i++)
            Step(walked);
        return walked;
    }

    private static void Step(Walked walked)
    {
        walked.Walk.Step();
        walked.Take();
    }
}
