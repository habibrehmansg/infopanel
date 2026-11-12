using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Serilog;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// The CPUs, under the Linux fork's ids: the load of all of them (<c>system/cpu/total</c>) and of each
/// (<c>system/cpu/core{N}</c>, N the kernel's CPU number), %, from <c>/proc/stat</c>'s deltas; each one's clock
/// (<c>system/cpufreq/core{N}</c>, MHz) from cpufreq's <c>scaling_cur_freq</c> (kHz) in sysfs, a file each, read as
/// the hwmon channels are. A <c>cpu</c> line's busy time is user + nice + system + irq + softirq + steal (guest time is in
/// user's already), its idle time idle + iowait. The first read has no interval, so no reading; neither has a round in
/// which any one counter went back (a wrap, a reset; iowait too, which the kernel's docs say can fall; one falling while
/// busy and the total still rise is no interval either), whose counters the next round reads from; nor one not read the
/// round before (its sensors were out of use), so a delta never spans a gap. A <c>/proc/stat</c> longer than the read
/// limit (<see cref="Sysfs.MaxProcRead"/>) is read as far as its whole rows go, the cut row left out, and said so once.
/// </summary>
internal sealed partial class CpuReader : ILinuxReader
{
    public const string Group = "CPU";

    // The total's slot; a core's is its number.
    private const int Total = -1;

    private readonly Sysfs _sysfs;
    private readonly Sysfs? _proc;

    // Each line's counters and the round they were read in (by line name: "cpu", "cpu3").
    private readonly Dictionary<string, (Counters Counters, long Round)> _last = new(StringComparer.Ordinal);
    private readonly ILogger _log;
    private bool _truncationReported;

    // The kernel's counters a CPU's load is made of (guest time is in user's already).
    private readonly record struct Counters(ulong User, ulong Nice, ulong System, ulong Idle, ulong IoWait, ulong Irq, ulong SoftIrq, ulong Steal)
    {
        public ulong Busy => User + Nice + System + Irq + SoftIrq + Steal;

        public ulong Total => Busy + Idle + IoWait;

        /// <summary>Whether any one counter is lower than it was in <paramref name="last"/>.</summary>
        public bool WentBack(Counters last) =>
            User < last.User || Nice < last.Nice || System < last.System || Idle < last.Idle
            || IoWait < last.IoWait || Irq < last.Irq || SoftIrq < last.SoftIrq || Steal < last.Steal;
    }

    [GeneratedRegex(@"^cpu(?<n>[0-9]+)$")]
    private static partial Regex CpuEntry();

    public CpuReader(Sysfs sysfs, Sysfs? proc, ILogger? log = null)
    {
        _sysfs = sysfs;
        _proc = proc;
        _log = (log ?? Log.Logger).ForContext<CpuReader>();
    }

    public void Scan(List<LinuxChannel> found)
    {
        if (_proc != null && ReadStat(_proc, out var text) == SysfsRead.Ok)
        {
            foreach (var (name, _) in Lines(text))
            {
                if (name == "cpu")
                {
                    found.Add(new LinuxChannel("system/cpu/total", Group, "CPU Total", "%", "", 1, "", 0, "stat/cpu") { Reader = this, Slot = Total });
                }
                else if (CpuEntry().Match(name) is { Success: true } match && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    found.Add(new LinuxChannel($"system/cpu/core{n}", Group, $"CPU Core {n}", "%", "", 1, "core", n, "stat/" + name) { Reader = this, Slot = n });
                }
            }
        }

        // Each CPU's clock: a file in sysfs, read as a hwmon channel is (the sampler reads it while it is in use).
        foreach (var entry in _sysfs.List("devices/system/cpu"))
        {
            if (CpuEntry().Match(entry) is not { Success: true } match
                || !int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                continue;
            }

            var folder = $"devices/system/cpu/{entry}/cpufreq";
            if (!_sysfs.List(folder).Contains("scaling_cur_freq") || _sysfs.RealPath(folder + "/scaling_cur_freq") is not { } path)
                continue;
            found.Add(new LinuxChannel($"system/cpufreq/core{n}", Group, $"CPU Core {n} Clock", "MHz", path, 1_000, "core", n, $"{entry}/scaling_cur_freq"));
        }
    }

    public void Read(IReadOnlyList<LinuxRead> inUse, Span<SensorSample> samples, TimeSpan elapsed, long round)
    {
        var status = ReadStat(_proc!, out var text);
        if (status != SysfsRead.Ok)
        {
            _last.Clear();
            foreach (var read in inUse)
            {
                if (status == SysfsRead.Denied)
                    samples[read.Sample].Outcome = SampleOutcome.PermissionDenied;
                else if (status == SysfsRead.Missing)
                    samples[read.Sample].SetUnavailable();
                else
                    samples[read.Sample].Outcome = SampleOutcome.Failed;
            }

            return;
        }

        var now = new Dictionary<string, Counters>(StringComparer.Ordinal);
        foreach (var (name, counters) in Lines(text))
            now[name] = counters;

        foreach (var read in inUse)
        {
            var name = read.Channel.Slot == Total ? "cpu" : "cpu" + read.Channel.Slot.ToString(CultureInfo.InvariantCulture);
            if (!now.TryGetValue(name, out var counters))
            {
                // That CPU went offline.
                samples[read.Sample].SetUnavailable();
                continue;
            }

            if (_last.TryGetValue(name, out var last) && last.Round == round - 1
                && !counters.WentBack(last.Counters) && counters.Total > last.Counters.Total)
            {
                samples[read.Sample].Set((counters.Busy - last.Counters.Busy) / (double)(counters.Total - last.Counters.Total) * 100);
            }
        }

        // Every line's counters are kept, so a core coming into use beside the total reads from the next round.
        _last.Clear();
        foreach (var (name, counters) in now)
            _last[name] = (counters, round);
    }

    /// <summary>
    /// <c>/proc/stat</c>'s whole rows. One longer than the read limit has its cut row left out (never read with zeros for
    /// what is missing); that the CPUs' rows (they come first) were cut, losing CPUs, is logged once.
    /// </summary>
    private SysfsRead ReadStat(Sysfs proc, out string text)
    {
        var status = proc.ReadRows("stat", out text, out var truncated);
        if (status == SysfsRead.Ok && truncated && !_truncationReported && CutInCpuRows(text))
        {
            _truncationReported = true;
            _log.Warning("Linux sensors: /proc/stat is longer than {Limit} bytes; only the CPUs in its first {Rows} rows are read", Sysfs.MaxProcRead, text.Count(c => c == '\n'));
        }

        return status;
    }

    // Whether the rows read are all CPUs' (the limit fell inside them, not in the interrupt counts after).
    private static bool CutInCpuRows(string text)
    {
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (!line.IsEmpty && !line.StartsWith("cpu", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The <c>cpu</c> lines (<c>cpu</c>, then <c>cpu0</c> …) at the start of <c>/proc/stat</c>, each one's counters; the
    /// lines after them (the interrupt counts, long on a large machine) aren't split.
    /// </summary>
    private static List<(string Name, Counters Counters)> Lines(string text)
    {
        var lines = new List<(string, Counters)>();
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty)
                continue;
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
                break;

            var fields = line.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
                continue;
            ulong Field(int i) => i < fields.Length && ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
            lines.Add((fields[0], new Counters(Field(1), Field(2), Field(3), Field(4), Field(5), Field(6), Field(7), Field(8))));
        }

        return lines;
    }

    public void Dispose()
    {
    }
}
