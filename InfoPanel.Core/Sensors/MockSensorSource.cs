using InfoPanel.Models;
using InfoPanel.Plugins;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors;

/// <summary>
/// Mock sensors: a port of the web client's <c>src/sensors/mockSource.ts</c>. Each of the 35 catalog sensors does a
/// mean-reverting random walk with occasional bursts for rate sensors, one step per <see cref="TickInterval"/>,
/// and keeps now/min/max/avg and the last <see cref="HistoryLength"/> values. It starts with
/// <see cref="BackfillSamples"/> steps already taken, so graphs aren't empty on load. It also provides a process
/// table and a text sensor. Seeded: the same seed and number of ticks give the same values.
/// Reads are lock-free (each tick publishes an immutable snapshot).
/// </summary>
public sealed class MockSensorSource : ISensorSource, ISensorBackfill, IDisposable
{
    public const int HistoryLength = 240;
    public const int BackfillSamples = 120;
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private static readonly (string Name, double CpuBase, double CpuJitter, double MemBase, double MemJitter)[] Processes =
    [
        ("chrome.exe", 6, 3, 1850, 60),
        ("Code.exe", 3, 2, 960, 25),
        ("dotnet.exe", 4, 3, 420, 20),
        ("dwm.exe", 2, 1, 180, 5),
        ("Discord.exe", 1.5, 1, 510, 15),
        ("Spotify.exe", 1, 0.8, 330, 10),
        ("steam.exe", 0.8, 0.6, 290, 8),
        ("obs64.exe", 5, 2, 640, 20),
    ];

    private static readonly string[] Tracks =
    [
        "Daft Punk - Veridis Quo",
        "Massive Attack - Teardrop",
        "Boards of Canada - Roygbiv",
        "Tycho - Awake",
        "Bonobo - Kerala",
    ];

    /// <summary>Ticks between changes of the text sensor.</summary>
    private const int TrackTicks = 8;

    private sealed record Sample(double Now, double Min, double Max, double Avg, int Count, double[] History);

    private sealed record Snapshot(
        IReadOnlyDictionary<SensorKey, Sample> Numeric,
        DataTable Table,
        string TableText,
        string Text,
        long Ticks);

    private readonly Dictionary<SensorKey, MockSensorDef> _defs;
    private readonly Mulberry32 _rng;
    private readonly Mulberry32 _extrasRng;
    private readonly double[] _processCpu;
    private readonly double[] _processMem;
    private readonly TimeProvider _time;
    private readonly Lock _tickLock = new();
    private volatile Snapshot _snapshot;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    /// <param name="seed">Seed for the walk; null takes one from the clock.</param>
    /// <param name="time">Drives the tick timer; <see cref="TimeProvider.System"/> by default.</param>
    public MockSensorSource(uint? seed = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Seed = seed ?? unchecked((uint)_time.GetUtcNow().UtcTicks ^ (uint)(_time.GetUtcNow().UtcTicks >> 32));
        _rng = new Mulberry32(Seed);
        _extrasRng = new Mulberry32(Seed ^ 0x9E3779B9u);
        _defs = MockSensorCatalog.Sensors.ToDictionary(d => d.Key);
        _processCpu = Processes.Select(p => p.CpuBase).ToArray();
        _processMem = Processes.Select(p => p.MemBase).ToArray();

        var samples = new Dictionary<SensorKey, Sample>();
        for (var i = 0; i < BackfillSamples; i++)
            samples = Advance(samples);
        _snapshot = BuildSnapshot(samples, ticks: 0);
    }

    public string Id => MockSensorCatalog.SourceId;

    public uint Seed { get; }

    /// <summary>Ticks taken since construction (the backfill is not counted).</summary>
    public long Ticks => _snapshot.Ticks;

    /// <summary>Every key under <c>plugin:/infopanel.mock/</c>, known or not (unknown ones read as missing).</summary>
    public bool Handles(SensorKey key) =>
        key.Type == Enums.SensorType.Plugin && key.Id.StartsWith(MockSensorCatalog.KeyPrefix, StringComparison.Ordinal);

    public IReadOnlyList<SensorInfo> GetSensors()
    {
        var list = MockSensorCatalog.Sensors
            .Select(d => new SensorInfo(d.Key, Id, d.Group, d.Name, d.Unit))
            .ToList();
        list.Add(new SensorInfo(MockSensorCatalog.TopProcessesKey, Id, MockSensorCatalog.SystemGroup, MockSensorCatalog.TopProcessesName, string.Empty));
        list.Add(new SensorInfo(MockSensorCatalog.NowPlayingKey, Id, MockSensorCatalog.SystemGroup, MockSensorCatalog.NowPlayingName, string.Empty));
        return list;
    }

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        var snapshot = _snapshot;
        if (snapshot.Numeric.TryGetValue(key, out var sample))
        {
            reading = new SensorReading(sample.Min, sample.Max, sample.Avg, sample.Now, _defs[key].Unit);
            return true;
        }

        if (key == MockSensorCatalog.TopProcessesKey)
        {
            reading = new SensorReading(snapshot.Table, MockSensorCatalog.TopProcessesFormat, snapshot.TableText);
            return true;
        }

        if (key == MockSensorCatalog.NowPlayingKey)
        {
            reading = new SensorReading(snapshot.Text);
            return true;
        }

        reading = default;
        return false;
    }

    /// <summary>The sensor's last <see cref="HistoryLength"/> values, oldest first (the backfill to begin with).</summary>
    public IReadOnlyList<double> GetBackfill(SensorKey key) =>
        _snapshot.Numeric.TryGetValue(key, out var sample) ? sample.History : [];

    /// <summary>Whether the timer loop is running. For tests.</summary>
    internal bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Takes one step. The timer started by <see cref="StartAsync"/> calls this every <see cref="TickInterval"/>.</summary>
    public void Tick()
    {
        lock (_tickLock)
        {
            var current = _snapshot;
            var samples = Advance(current.Numeric);
            _snapshot = BuildSnapshot(samples, current.Ticks + 1);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loop != null)
                return;

            _loopCts = new CancellationTokenSource();
            // The timer starts now, not when the loop first runs.
            _loop = RunAsync(new PeriodicTimer(TickInterval, _time), _loopCts.Token);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the timer loop and waits for it to end. Start and stop are serialised, so a concurrent
    /// <see cref="StartAsync"/> runs before or after, never in between (which could orphan a loop).
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loop == null)
                return;

            var loop = _loop;
            var cts = _loopCts!;
            cts.Cancel();
            try
            {
                // Cancelled above, so it ends promptly; ct only bounds the wait.
                await loop.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (loop.IsCompleted)
                {
                    cts.Dispose();
                    _loop = null;
                    _loopCts = null;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            _loopCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A concurrent StopAsync finished first.
        }
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        using var _ = timer;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One mean-reverting random-walk step, with occasional bursts for rate sensors (the web client's <c>nextValue</c>).</summary>
    internal static double NextValue(MockSensorDef def, double prev, Func<double> rng)
    {
        if (def.Jitter == 0)
            return def.Base;

        var pull = (def.Base - prev) * 0.15;
        var noise = (rng() * 2 - 1) * def.Jitter;
        var next = prev + pull + noise;
        if (def.Bursty && rng() < 0.06)
            next = def.Base + rng() * (def.Max - def.Base) * 0.6;
        return Math.Clamp(next, def.Min, def.Max);
    }

    /// <summary>The web client's <c>step</c>: the next sample of one sensor.</summary>
    private Sample Step(MockSensorDef def, Sample? prev)
    {
        var now = NextValue(def, prev?.Now ?? def.Base, _rng.NextDouble);
        if (prev == null)
            return new Sample(now, now, now, now, 1, [now]);

        var count = prev.Count + 1;
        var keep = Math.Min(prev.History.Length, HistoryLength - 1);
        var history = new double[keep + 1];
        Array.Copy(prev.History, prev.History.Length - keep, history, 0, keep);
        history[keep] = now;

        return new Sample(
            now,
            Math.Min(prev.Min, now),
            Math.Max(prev.Max, now),
            prev.Avg + (now - prev.Avg) / count,
            count,
            history);
    }

    private Dictionary<SensorKey, Sample> Advance(IReadOnlyDictionary<SensorKey, Sample> samples)
    {
        var next = new Dictionary<SensorKey, Sample>(MockSensorCatalog.Sensors.Count);
        foreach (var def in MockSensorCatalog.Sensors)
            next[def.Key] = Step(def, samples.GetValueOrDefault(def.Key));

        for (var i = 0; i < Processes.Length; i++)
        {
            var p = Processes[i];
            _processCpu[i] = Walk(_processCpu[i], p.CpuBase, p.CpuJitter, 0, 100);
            _processMem[i] = Walk(_processMem[i], p.MemBase, p.MemJitter, 1, 65536);
        }

        return next;
    }

    private double Walk(double prev, double @base, double jitter, double min, double max)
    {
        var next = prev + (@base - prev) * 0.15 + (_extrasRng.NextDouble() * 2 - 1) * jitter;
        return Math.Clamp(next, min, max);
    }

    private Snapshot BuildSnapshot(Dictionary<SensorKey, Sample> samples, long ticks)
    {
        // A new table each tick: a published table is never modified, so renderers can read it without locks.
        var table = new DataTable();
        table.Columns.Add("Process", typeof(PluginText));
        table.Columns.Add("CPU", typeof(PluginSensor));
        table.Columns.Add("Memory", typeof(PluginSensor));

        foreach (var i in Enumerable.Range(0, Processes.Length).OrderByDescending(i => _processCpu[i]).ThenBy(i => i))
        {
            var name = Processes[i].Name;
            table.Rows.Add(
                new PluginText(name, name),
                new PluginSensor("cpu", (float)_processCpu[i], "%"),
                new PluginSensor("memory", (float)_processMem[i], "MB"));
        }

        var plugin = new PluginTable(MockSensorCatalog.TopProcessesName, table, MockSensorCatalog.TopProcessesFormat);
        var text = Tracks[(int)(ticks / TrackTicks % Tracks.Length)];
        return new Snapshot(samples, table, plugin.ToString(), text, ticks);
    }
}
