using System;
using System.Collections.Generic;
using System.Globalization;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// Memory, under the Linux fork's ids, from <c>/proc/meminfo</c> (kB): <c>system/memory/total</c>, <c>used</c>
/// (the total less <c>MemAvailable</c>, what the kernel says can be had without swapping) and <c>available</c> in GB
/// (GiB, as the fork gives them), <c>used_percent</c>; with swap, <c>swap_used</c> (GB) and <c>swap_percent</c>. Listed
/// when the kernel gives <c>MemAvailable</c> (3.14 and later); swap when there is some.
/// </summary>
internal sealed class MemoryReader : ILinuxReader
{
    public const string Group = "Memory";

    private const double GB = 1024 * 1024; // kB in a GB (GiB)

    private enum Metric
    {
        Total,
        Used,
        Available,
        UsedPercent,
        SwapUsed,
        SwapPercent,
    }

    private static readonly (Metric Metric, string Id, string Name, string Unit)[] Metrics =
    [
        (Metric.Total, "total", "Memory Total", "GB"),
        (Metric.Used, "used", "Memory Used", "GB"),
        (Metric.Available, "available", "Memory Available", "GB"),
        (Metric.UsedPercent, "used_percent", "Memory Load", "%"),
        (Metric.SwapUsed, "swap_used", "Swap Used", "GB"),
        (Metric.SwapPercent, "swap_percent", "Swap Load", "%"),
    ];

    private readonly Sysfs _proc;

    public MemoryReader(Sysfs proc) => _proc = proc;

    public void Scan(List<LinuxChannel> found)
    {
        if (_proc.Read("meminfo", out var text) != SysfsRead.Ok || Parse(text) is not { } info)
            return;
        for (var i = 0; i < Metrics.Length; i++)
        {
            var (metric, id, name, unit) = Metrics[i];
            if (metric is Metric.SwapUsed or Metric.SwapPercent && info.SwapTotal == 0)
                continue;
            found.Add(new LinuxChannel("system/memory/" + id, Group, name, unit, "", 1, "", i, "meminfo/" + id) { Reader = this, Slot = (int)metric });
        }
    }

    public void Read(IReadOnlyList<LinuxRead> inUse, Span<SensorSample> samples, TimeSpan elapsed, long round)
    {
        var status = _proc.Read("meminfo", out var text);
        var info = status == SysfsRead.Ok ? Parse(text) : null;
        foreach (var read in inUse)
        {
            ref var sample = ref samples[read.Sample];
            if (info is not { } m)
            {
                sample.Outcome = status switch
                {
                    SysfsRead.Denied => SampleOutcome.PermissionDenied,
                    SysfsRead.Missing => SampleOutcome.Unavailable,
                    _ => SampleOutcome.Failed,
                };
                continue;
            }

            var used = m.Total - Math.Min(m.Available, m.Total);
            var swapUsed = m.SwapTotal - Math.Min(m.SwapFree, m.SwapTotal);
            switch ((Metric)read.Channel.Slot)
            {
                case Metric.Total:
                    sample.Set(m.Total / GB);
                    break;
                case Metric.Used:
                    sample.Set(used / GB);
                    break;
                case Metric.Available:
                    sample.Set(m.Available / GB);
                    break;
                case Metric.UsedPercent:
                    sample.Set(m.Total > 0 ? used * 100.0 / m.Total : 0);
                    break;
                case Metric.SwapUsed:
                    sample.Set(swapUsed / GB);
                    break;
                case Metric.SwapPercent:
                    if (m.SwapTotal > 0)
                        sample.Set(swapUsed * 100.0 / m.SwapTotal);
                    else
                        sample.SetUnavailable();
                    break;
            }
        }
    }

    private readonly record struct MemInfo(ulong Total, ulong Available, ulong SwapTotal, ulong SwapFree);

    /// <summary>The lines read, in kB; null without <c>MemTotal</c> and <c>MemAvailable</c>.</summary>
    private static MemInfo? Parse(string text)
    {
        ulong? total = null, available = null;
        ulong swapTotal = 0, swapFree = 0;
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            var value = line.AsSpan(colon + 1).Trim();
            if (value.EndsWith("kB", StringComparison.Ordinal))
                value = value[..^2].TrimEnd();
            if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kb))
                continue;
            switch (line.AsSpan(0, colon))
            {
                case "MemTotal":
                    total = kb;
                    break;
                case "MemAvailable":
                    available = kb;
                    break;
                case "SwapTotal":
                    swapTotal = kb;
                    break;
                case "SwapFree":
                    swapFree = kb;
                    break;
            }
        }

        return total is { } t && available is { } a ? new MemInfo(t, a, swapTotal, swapFree) : null;
    }

    public void Dispose()
    {
    }
}
