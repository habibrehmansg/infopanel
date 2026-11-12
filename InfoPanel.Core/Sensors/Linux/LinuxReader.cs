using System;
using System.Collections.Generic;

namespace InfoPanel.Sensors.Linux;

/// <summary>One of a reader's sensors in use this round, and its place among the round's samples.</summary>
internal readonly record struct LinuxRead(LinuxChannel Channel, int Sample);

/// <summary>
/// A reader of Linux sensors that aren't one sysfs file's number each (<see cref="LinuxChannel.Reader"/>): the CPUs'
/// load from <c>/proc/stat</c>, memory, the disks' rates, a GPU's metrics through NVML. It lists its sensors at each
/// scan and reads, once a round, only when one of them is in use, filling the samples of those in use.
/// </summary>
internal interface ILinuxReader : IDisposable
{
    /// <summary>Adds the sensors found now to <paramref name="found"/>, each with <see cref="LinuxChannel.Reader"/> this reader.</summary>
    void Scan(List<LinuxChannel> found);

    /// <summary>
    /// Reads this round's values of <paramref name="inUse"/> (none empty: a reader with nothing in use isn't called) into
    /// <paramref name="samples"/>. <paramref name="round"/> counts the sampler's rounds, one up each; a rate has a delta
    /// only over two rounds in a row (a reader not read last round, its sensors out of use, starts again).
    /// <paramref name="elapsed"/> is the time since the last round.
    /// </summary>
    void Read(IReadOnlyList<LinuxRead> inUse, Span<SensorSample> samples, TimeSpan elapsed, long round);
}
