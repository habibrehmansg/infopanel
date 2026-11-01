using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors;

/// <summary>
/// One sensor a sampler found: its id in the source's key space (<c>cpu/load</c> under a plugin prefix,
/// <c>-1:4026532608:0:16777216</c> for HWiNFO; <see cref="SensorKeySpace"/>), group, name and unit, and the kind of
/// value it reads: <see cref="SensorKind.Number"/> (<see cref="SensorSample.Set(double)"/>) by default, or
/// <see cref="SensorKind.Text"/> (<see cref="SensorSample.SetText"/>). A polled source reads no tables.
/// </summary>
public sealed record SensorDefinition(string Id, string Group, string Name, string Unit, SensorKind Kind = SensorKind.Number);

/// <summary>The outcome of reading one sensor once.</summary>
public enum SampleOutcome : byte
{
    /// <summary>Not read this round (a rate sensor's first sample has no delta yet); the previous state stands.</summary>
    Skipped,

    /// <summary><see cref="SensorSample.Value"/> holds a fresh, finite value, or <see cref="SensorSample.Text"/> a text.</summary>
    Ok,

    /// <summary>The read failed or the sensor is absent.</summary>
    Failed,

    /// <summary>The operating system refused access.</summary>
    PermissionDenied,
}

/// <summary>One sensor's reading from one <see cref="ISensorSampler.Sample"/> round.</summary>
public struct SensorSample
{
    public double Value;
    public SampleOutcome Outcome;

    /// <summary>A text sensor's reading, set with <see cref="SetText"/>; null for a number.</summary>
    public string? Text;

    /// <summary>The source's own minimum, maximum and average, set with <see cref="HasStatistics"/>.</summary>
    public double Min;
    public double Max;
    public double Avg;

    /// <summary>
    /// <see cref="Min"/>, <see cref="Max"/> and <see cref="Avg"/> are the source's own statistics (HWiNFO's session
    /// values), read instead of the ones <see cref="PolledSensorSource"/> keeps.
    /// </summary>
    public bool HasStatistics;

    public void Set(double value)
    {
        HasStatistics = false;
        Text = null;
        if (double.IsFinite(value))
        {
            Value = value;
            Outcome = SampleOutcome.Ok;
        }
        else
        {
            Outcome = SampleOutcome.Failed;
        }
    }

    /// <summary>A value with the source's own statistics; a non-finite statistic leaves the source's ones unused.</summary>
    public void Set(double value, double min, double max, double avg)
    {
        Set(value);
        if (Outcome == SampleOutcome.Ok && double.IsFinite(min) && double.IsFinite(max) && double.IsFinite(avg))
        {
            Min = min;
            Max = max;
            Avg = avg;
            HasStatistics = true;
        }
    }

    /// <summary>
    /// A text sensor's reading. Null fails the read. Hand back the same string while the text is unchanged: the source
    /// keeps the one it was given.
    /// </summary>
    public void SetText(string? text)
    {
        HasStatistics = false;
        Text = text;
        Outcome = text is null ? SampleOutcome.Failed : SampleOutcome.Ok;
    }
}

/// <summary>
/// Reads a set of number and text sensors for <see cref="PolledSensorSource"/>. Called from one thread at a time:
/// <see cref="Discover"/> once, then <see cref="Sample"/> once per poll, then <see cref="IDisposable.Dispose"/>.
/// </summary>
public interface ISensorSampler : IDisposable
{
    /// <summary>The sensors, in the order <see cref="Sample"/> fills them. Must not throw for a missing device.</summary>
    IReadOnlyList<SensorDefinition> Discover();

    /// <summary>
    /// Reads every sensor once into <paramref name="samples"/> (one entry per discovered sensor, reset to
    /// <see cref="SampleOutcome.Skipped"/> before the call). <paramref name="elapsed"/> is the time since the previous
    /// call (zero on the first), for rates.
    /// </summary>
    void Sample(Span<SensorSample> samples, TimeSpan elapsed);

    /// <summary>
    /// The catalogue's version, for a sampler whose sensors change at run time (0, never changing, by default). A
    /// <see cref="Sample"/> round still fills the catalogue <see cref="Discover"/> last returned; when the version
    /// differs after it, <see cref="PolledSensorSource"/> calls <see cref="Discover"/> again, which returns the new one.
    /// </summary>
    long CatalogueVersion => 0;
}
