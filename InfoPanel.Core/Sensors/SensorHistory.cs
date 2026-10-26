using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors;

/// <summary>
/// Samples the sensors someone asked for. <see cref="GetSamples"/> registers interest in a key (seeding it from the
/// source's backfill when there is one); <see cref="SampleNow"/>, called every <see cref="Interval"/> by
/// <see cref="RunAsync"/>, appends each tracked key's current value (at most <see cref="MaxSamples"/>, oldest
/// dropped first; buffers grow on demand) and drops keys nobody asked for in <see cref="IdleTimeout"/>. Text and
/// table readings, and keys with no reading, add no sample. A key with no reading when first asked for is not
/// tracked, and at most <see cref="MaxSeries"/> keys are (the least recently requested is evicted).
/// </summary>
public sealed class SensorHistory : ISensorHistory
{
    public const int MaxSamples = 4096;

    /// <summary>At most this many keys are tracked; a new one evicts the least recently requested.</summary>
    public const int MaxSeries = 1024;
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1000);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>A ring buffer that starts small and doubles on demand up to <see cref="MaxSamples"/>.</summary>
    private sealed class Series
    {
        private const int InitialCapacity = 64;

        private double[] _ring = new double[InitialCapacity];
        private int _start;
        private double[]? _snapshot;

        public int Count { get; private set; }

        public int Capacity => _ring.Length;

        public long LastAccess;

        public void Add(double value)
        {
            if (Count == _ring.Length && _ring.Length < MaxSamples)
            {
                var grown = new double[Math.Min(MaxSamples, _ring.Length * 2)];
                for (var i = 0; i < Count; i++)
                    grown[i] = _ring[(_start + i) % _ring.Length];
                _ring = grown;
                _start = 0;
            }

            if (Count < _ring.Length)
            {
                _ring[(_start + Count) % _ring.Length] = value;
                Count++;
            }
            else
            {
                _ring[_start] = value;
                _start = (_start + 1) % _ring.Length;
            }

            _snapshot = null;
        }

        public double[] Snapshot()
        {
            if (_snapshot != null)
                return _snapshot;

            var result = new double[Count];
            for (var i = 0; i < Count; i++)
                result[i] = _ring[(_start + i) % _ring.Length];
            return _snapshot = result;
        }
    }

    private readonly ISensorReader _reader;
    private readonly TimeProvider _time;
    private readonly Func<SensorKey, IReadOnlyList<double>>? _backfill;
    private readonly Lock _lock = new();
    private readonly Dictionary<SensorKey, Series> _series = [];

    /// <param name="reader">Where samples are read from (the <see cref="SensorHub"/>).</param>
    /// <param name="time">The clock for idle eviction and <see cref="RunAsync"/>'s timer.</param>
    /// <param name="backfill">Initial values for a key that starts being tracked, oldest first.</param>
    public SensorHistory(ISensorReader reader, TimeProvider time, Func<SensorKey, IReadOnlyList<double>>? backfill = null)
    {
        _reader = reader;
        _time = time;
        _backfill = backfill;
    }

    /// <summary>The buffer length of a tracked key's series (grows on demand), or 0 when it isn't tracked. For tests.</summary>
    internal int CapacityOf(SensorKey key)
    {
        lock (_lock)
            return _series.TryGetValue(key, out var series) ? series.Capacity : 0;
    }

    /// <summary>The keys being sampled.</summary>
    public IReadOnlyCollection<SensorKey> TrackedKeys
    {
        get
        {
            lock (_lock)
                return _series.Keys.ToArray();
        }
    }

    public IReadOnlyList<double> GetSamples(SensorKey key)
    {
        lock (_lock)
        {
            if (_series.TryGetValue(key, out var series))
            {
                series.LastAccess = _time.GetTimestamp();
                return series.Snapshot();
            }
        }

        // A key with no reading (unknown to every source, or past the hub's simulation cap) isn't tracked.
        // Read outside the lock: the reader takes its own locks.
        if (!_reader.TryRead(key, out _))
            return [];

        lock (_lock)
        {
            if (!_series.TryGetValue(key, out var series))
            {
                if (_series.Count >= MaxSeries)
                {
                    var leastRecent = _series.MinBy(pair => pair.Value.LastAccess).Key;
                    _series.Remove(leastRecent);
                }

                series = new Series();
                if (_backfill?.Invoke(key) is { Count: > 0 } initial)
                {
                    foreach (var value in initial.Skip(Math.Max(0, initial.Count - MaxSamples)))
                        series.Add(value);
                }

                _series[key] = series;
            }

            series.LastAccess = _time.GetTimestamp();
            return series.Snapshot();
        }
    }

    /// <summary>Drops idle keys, then appends one sample to every other tracked key.</summary>
    public void SampleNow()
    {
        KeyValuePair<SensorKey, Series>[] tracked;
        lock (_lock)
        {
            var now = _time.GetTimestamp();
            foreach (var (key, series) in _series.ToArray())
            {
                if (_time.GetElapsedTime(series.LastAccess, now) > IdleTimeout)
                    _series.Remove(key);
            }

            tracked = _series.ToArray();
        }

        // Read outside the lock: the reader takes its own locks.
        var values = new List<(Series Series, double Value)>(tracked.Length);
        foreach (var (key, series) in tracked)
        {
            if (_reader.TryRead(key, out var reading) && reading.ValueTable == null && reading.ValueText == null
                && double.IsFinite(reading.ValueNow))
            {
                values.Add((series, reading.ValueNow));
            }
        }

        lock (_lock)
        {
            foreach (var (series, value) in values)
                series.Add(value);
        }
    }

    /// <summary>Calls <see cref="SampleNow"/> every <see cref="Interval"/> until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                SampleNow();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
