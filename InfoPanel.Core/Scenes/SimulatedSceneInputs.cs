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
/// a step a second from the first read; text, tables and keys that aren't well formed read as <paramref name="live"/>
/// reads them.
/// <list type="bullet">
/// <item>Where it starts: a key <paramref name="live"/> reads a number for walks <see cref="SensorWalk.Around"/> that
/// first reading, in its unit; one it doesn't, as mock mode's walk for the key (<see cref="SensorWalk.ForUnknownKey"/>),
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
internal sealed class SimulatedSceneInputs(ISceneInputSource live, TimeProvider time, SpecLimits? limits = null) : ISceneInputSource
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

    public SceneReading Read(string key, string inputType)
    {
        if (inputType != "number" || Walk(key) is not { } walked)
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
        if (!SensorKey.TryParse(key, out var parsed) || !SensorHub.IsWellFormed(parsed))
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
            ? new Walked(key, SensorWalk.Around(parsed, reading.Now, reading.Unit), reading.Unit, double.NegativeInfinity, double.PositiveInfinity, keepsHistory)
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
