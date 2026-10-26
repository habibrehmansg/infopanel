using InfoPanel.Enums;
using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors;

public sealed class SensorHubOptions
{
    /// <summary>
    /// Give well-formed keys that no source handles (HWiNFO and Libre bindings in imported Windows profiles) a
    /// deterministic 0–100 walk, so they still render. Off by default (such keys are
    /// <see cref="SensorStatus.Unavailable"/>); the engine turns it on in mock mode only (<c>--mock</c>, <c>--mock-seed</c>).
    /// </summary>
    public bool SimulateUnknown { get; init; }

    /// <summary>At most this many keys are simulated at once; further unknown keys read as missing.</summary>
    public int MaxSimulatedKeys { get; init; } = 4096;
}

/// <summary>
/// The engine's <see cref="ISensorReader"/>: asks the sources in order, the first that
/// <see cref="ISensorSource.Handles"/> a key answers for it; keys no source handles are simulated when
/// <see cref="SensorHubOptions.SimulateUnknown"/> is set. <see cref="Tick"/> (every
/// <see cref="TickInterval"/>) steps the simulated walks.
/// </summary>
public sealed class SensorHub : ISensorReader
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private sealed class Simulated(uint seed)
    {
        public readonly Mulberry32 Rng = new(seed);
        public double Base;
        public double Now;
        public double Min;
        public double Max;
        public double Avg;
        public long Count;
    }

    private readonly ISensorSource[] _sources;
    private readonly SensorHubOptions _options;
    private readonly Lock _lock = new();
    private readonly Dictionary<SensorKey, Simulated> _simulated = [];

    public SensorHub(IEnumerable<ISensorSource> sources, SensorHubOptions? options = null)
    {
        _sources = sources.ToArray();
        _options = options ?? new SensorHubOptions();
    }

    public IReadOnlyList<ISensorSource> Sources => _sources;

    public bool SimulateUnknown => _options.SimulateUnknown;

    /// <summary>Every source's sensors, in source order. Simulated keys are not listed.</summary>
    public IReadOnlyList<SensorInfo> GetSensors() => _sources.SelectMany(s => s.GetSensors()).ToList();

    public bool TryRead(SensorKey key, out SensorReading reading)
    {
        reading = default;
        if (!IsWellFormed(key))
            return false;

        if (FindSource(key) is { } source)
            return source.TryRead(key, out reading);

        if (!_options.SimulateUnknown)
            return false;

        lock (_lock)
        {
            if (!_simulated.TryGetValue(key, out var sim))
            {
                if (_simulated.Count >= _options.MaxSimulatedKeys)
                    return false;
                sim = CreateSimulated(key);
                _simulated[key] = sim;
            }

            reading = new SensorReading(sim.Min, sim.Max, sim.Avg, sim.Now, string.Empty);
            return true;
        }
    }

    /// <summary>
    /// <see cref="SensorStatus.Missing"/> for unbound or malformed keys; for a key a source handles, what an
    /// <see cref="ISensorStatusSource"/> says, else <see cref="SensorStatus.Live"/> with a reading and
    /// <see cref="SensorStatus.Unavailable"/> without; for keys no source handles, <see cref="SensorStatus.Simulated"/>
    /// when simulating (and under the cap), else <see cref="SensorStatus.Unavailable"/>.
    /// </summary>
    public SensorStatus GetStatus(SensorKey key)
    {
        if (!IsWellFormed(key))
            return SensorStatus.Missing;

        if (FindSource(key) is { } source)
        {
            if (source is ISensorStatusSource statusSource)
            {
                return statusSource.GetStatus(key) switch
                {
                    SensorStatus.Live => SensorStatus.Live,
                    SensorStatus.Stale => SensorStatus.Stale,
                    SensorStatus.PermissionDenied => SensorStatus.PermissionDenied,
                    _ => SensorStatus.Unavailable,
                };
            }

            return source.TryRead(key, out _) ? SensorStatus.Live : SensorStatus.Unavailable;
        }

        if (!_options.SimulateUnknown)
            return SensorStatus.Unavailable;

        lock (_lock)
        {
            return _simulated.ContainsKey(key) || _simulated.Count < _options.MaxSimulatedKeys
                ? SensorStatus.Simulated
                : SensorStatus.Unavailable;
        }
    }

    /// <summary>Recent values from the source that handles <paramref name="key"/>, when it keeps any.</summary>
    public IReadOnlyList<double> GetBackfill(SensorKey key) =>
        IsWellFormed(key) && FindSource(key) is ISensorBackfill backfill ? backfill.GetBackfill(key) : [];

    /// <summary>Steps every simulated walk once.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            foreach (var sim in _simulated.Values)
                Step(sim);
        }
    }

    /// <summary>Calls <see cref="Tick"/> every <see cref="TickInterval"/> of <paramref name="time"/> until cancelled.</summary>
    public async Task RunAsync(TimeProvider time, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TickInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// A key that parses back to itself and is bound: HWiNFO keys need a non-zero sensor id (1.x items start out
    /// bound to <c>hwinfo:…:0:0:0</c>, which means unbound).
    /// </summary>
    public static bool IsWellFormed(SensorKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Id) || !Enum.IsDefined(key.Type))
            return false;
        if (!SensorKey.TryParse(key.ToString(), out var parsed) || parsed != key)
            return false;
        if (key.Type == SensorType.HwInfo && key.Id.Split(':')[1] == "0")
            return false;
        return true;
    }

    private ISensorSource? FindSource(SensorKey key)
    {
        foreach (var source in _sources)
        {
            if (source.Handles(key))
                return source;
        }

        return null;
    }

    private static Simulated CreateSimulated(SensorKey key)
    {
        var sim = new Simulated(StableHash.Fnv1a(key.ToString()));
        sim.Base = 20 + sim.Rng.NextDouble() * 60;
        sim.Now = sim.Min = sim.Max = sim.Avg = sim.Base;
        sim.Count = 1;
        return sim;
    }

    private static void Step(Simulated sim)
    {
        var next = sim.Now + (sim.Base - sim.Now) * 0.15 + (sim.Rng.NextDouble() * 2 - 1) * 4;
        sim.Now = Math.Clamp(next, 0, 100);
        sim.Count++;
        sim.Min = Math.Min(sim.Min, sim.Now);
        sim.Max = Math.Max(sim.Max, sim.Now);
        sim.Avg += (sim.Now - sim.Avg) / sim.Count;
    }
}
