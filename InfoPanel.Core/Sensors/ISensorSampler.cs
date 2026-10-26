using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors;

/// <summary>One sensor a sampler found: its id under the source's key prefix (<c>cpu/load</c>), group, name and unit.</summary>
public sealed record SensorDefinition(string Id, string Group, string Name, string Unit);

/// <summary>The outcome of reading one sensor once.</summary>
public enum SampleOutcome : byte
{
    /// <summary>Not read this round (a rate sensor's first sample has no delta yet); the previous state stands.</summary>
    Skipped,

    /// <summary><see cref="SensorSample.Value"/> holds a fresh, finite value.</summary>
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

    public void Set(double value)
    {
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
}

/// <summary>
/// Reads a fixed set of numeric sensors for <see cref="PolledSensorSource"/>. Called from one thread at a time:
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
}
