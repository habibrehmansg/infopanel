using InfoPanel.Models;
using System;

namespace InfoPanel.Sensors;

/// <summary>
/// A made-up number that walks, seeded from its key: each <see cref="Step"/> pulls it back towards where it started and
/// nudges it at random, within its band, and keeps the min, max and average of the values it took (its start
/// included). The same key and number of steps give the same values. Not thread-safe: callers lock.
/// <list type="bullet">
/// <item><see cref="ForUnknownKey"/>: mock mode's walk for keys no source handles, which starts from 20 to 80 and stays
/// within 0 to 100.</item>
/// <item><see cref="Around"/>: one that starts at a reading and stays within a band proportional to it; a percentage
/// stays within 0 to 100, and a reading that isn't negative never turns negative.</item>
/// </list>
/// </summary>
internal sealed class SensorWalk
{
    /// <summary>The share of the way back to the start each step takes.</summary>
    private const double Pull = 0.15;

    /// <summary>The largest random nudge of a step, and the band's half width, as shares of a reading's size.</summary>
    private const double Spread = 0.04, Band = 0.2;

    private readonly Mulberry32 _rng;
    private readonly double _start;
    private readonly double _spread;
    private readonly double _low;
    private readonly double _high;

    private SensorWalk(Mulberry32 rng, double start, double spread, double low, double high)
    {
        _rng = rng;
        _start = start;
        _spread = spread;
        _low = low;
        _high = high;
        Now = Min = Max = Avg = start;
        Count = 1;
    }

    public double Now { get; private set; }

    public double Min { get; private set; }

    public double Max { get; private set; }

    public double Avg { get; private set; }

    /// <summary>The values taken: the start and one a step.</summary>
    public long Count { get; private set; }

    /// <summary>Mock mode's walk for a key no source handles: it starts from 20 to 80 and moves within 0 to 100, by at most 4 a step.</summary>
    public static SensorWalk ForUnknownKey(SensorKey key)
    {
        var rng = new Mulberry32(StableHash.Fnv1a(key.ToString()));
        return new SensorWalk(rng, 20 + rng.NextDouble() * 60, 4, 0, 100);
    }

    /// <summary>
    /// A walk that starts at <paramref name="reading"/>. With the unit <c>%</c>, it moves as <see cref="ForUnknownKey"/>
    /// does, within 0 to 100 (a reading outside them starts at the nearer end); otherwise by at most 4 % of the
    /// reading's size a step (its absolute value, at least 1), within 20 % of it either side, and not below 0 when the
    /// reading isn't negative; never past the largest finite doubles. A reading that isn't a finite number walks as
    /// <see cref="ForUnknownKey"/>.
    /// </summary>
    public static SensorWalk Around(SensorKey key, double reading, string? unit)
    {
        if (!double.IsFinite(reading))
            return ForUnknownKey(key);

        var rng = new Mulberry32(StableHash.Fnv1a(key.ToString()));
        if (unit?.Trim() == "%")
            return new SensorWalk(rng, Math.Clamp(reading, 0, 100), 4, 0, 100);

        // The band's ends saturate at the largest doubles, so a step past them is clamped back to a finite value.
        var size = Math.Max(Math.Abs(reading), 1);
        var low = Math.Max(reading - Band * size, -double.MaxValue);
        if (reading >= 0)
            low = Math.Max(low, 0);
        return new SensorWalk(rng, reading, Spread * size, low, Math.Min(reading + Band * size, double.MaxValue));
    }

    /// <summary>Takes one step.</summary>
    public void Step()
    {
        var next = Now + (_start - Now) * Pull + (_rng.NextDouble() * 2 - 1) * _spread;
        Now = Math.Clamp(next, _low, _high);
        Count++;
        Min = Math.Min(Min, Now);
        Max = Math.Max(Max, Now);
        Avg += (Now - Avg) / Count;
    }

    /// <summary>The walk's reading now, in <paramref name="unit"/>.</summary>
    public SensorReading Reading(string unit) => new(Min, Max, Avg, Now, unit);
}
