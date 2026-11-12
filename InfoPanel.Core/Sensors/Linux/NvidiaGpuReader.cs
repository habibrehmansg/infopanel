using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InfoPanel.Sensors.Linux;

/// <summary>What an NVML call gave.</summary>
internal enum NvmlStatus : byte
{
    Ok,

    /// <summary>The GPU or its driver doesn't have it (<c>NVML_ERROR_NOT_SUPPORTED</c>, or a call the library lacks).</summary>
    NotSupported,

    /// <summary>The driver refused the engine (<c>NVML_ERROR_NO_PERMISSION</c>).</summary>
    NoPermission,

    /// <summary>The GPU is gone or fell off the bus (<c>NVML_ERROR_GPU_IS_LOST</c>, <c>NVML_ERROR_NOT_FOUND</c>).</summary>
    Lost,

    /// <summary>Any other failure.</summary>
    Failed,
}

/// <summary>NVML's clocks (<c>nvmlClockType_t</c>).</summary>
internal enum NvmlClock : uint
{
    Graphics = 0,
    Sm = 1,
    Memory = 2,
    Video = 3,
}

/// <summary>
/// The few NVML calls the NVIDIA reader makes, behind a seam (<see cref="NvmlLibrary"/> over <c>libnvidia-ml.so.1</c>;
/// a test's fake). A device is NVML's handle; disposing shuts NVML down.
/// </summary>
internal interface INvml : IDisposable
{
    NvmlStatus DeviceCount(out uint count);

    NvmlStatus Device(uint index, out nint device);

    /// <summary>The GPU's UUID as NVML gives it (<c>GPU-xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>).</summary>
    NvmlStatus Uuid(nint device, out string uuid);

    /// <summary>Its PCI address as NVML gives it (<c>00000000:01:00.0</c>).</summary>
    NvmlStatus PciBusId(nint device, out string busId);

    NvmlStatus Name(nint device, out string name);

    /// <summary>The GPU's and its memory controller's load, %.</summary>
    NvmlStatus Utilization(nint device, out uint gpu, out uint memory);

    /// <summary>The GPU's temperature, °C.</summary>
    NvmlStatus Temperature(nint device, out uint celsius);

    /// <summary>Its memory's temperature, °C (the field <c>NVML_FI_DEV_MEMORY_TEMP</c>).</summary>
    NvmlStatus MemoryTemperature(nint device, out uint celsius);

    /// <summary>Its memory used and in all, bytes.</summary>
    NvmlStatus Memory(nint device, out ulong used, out ulong total);

    /// <summary>Its power draw, mW.</summary>
    NvmlStatus Power(nint device, out uint milliwatts);

    /// <summary>Its power limit as enforced, mW.</summary>
    NvmlStatus PowerLimit(nint device, out uint milliwatts);

    /// <summary>A clock, MHz.</summary>
    NvmlStatus Clock(nint device, NvmlClock clock, out uint mhz);

    NvmlStatus FanCount(nint device, out uint count);

    /// <summary>The first fan's speed as a share of its maximum, %.</summary>
    NvmlStatus FanSpeed(nint device, out uint percent);

    /// <summary>Fan <paramref name="fan"/>'s speed, RPM.</summary>
    NvmlStatus FanRpm(nint device, uint fan, out uint rpm);
}

/// <summary>
/// NVIDIA GPUs through NVML (<see cref="INvml"/>; <see cref="NvmlLibrary"/> loads the driver's
/// <c>libnvidia-ml.so.1</c>, no package), under the Linux fork's ids
/// <c>system/gpu/gpu-uuid+{uuid}~pci+{0000-01-00.0}/{metric}</c> (the UUID alone when NVML gives no PCI address,
/// <c>pci+{address}</c> alone when it gives no UUID; neither: not listed). Metrics: <c>utilization</c>,
/// <c>memory_utilization</c>, <c>temperature</c>, <c>temperature_vram</c>, <c>memory_used</c>, <c>memory_total</c>,
/// <c>memory_percent</c>, <c>power</c>, <c>power_limit</c>, <c>clock_graphics</c>, <c>clock_memory</c>,
/// <c>fan_speed</c> and <c>fan{N}_rpm</c>, each listed when NVML gives it for that GPU at the scan (one the driver refuses
/// is listed and reads denied); only the metrics in use are asked for. What a GPU offers is asked when it is first
/// listed, and kept while it stays (the UUID and PCI address unchanged), an empty offer too: a rescan lists from that,
/// with no metric read while nothing is in use, but for one NVML failed on (not "not supported"), asked again at each
/// rescan until NVML gives it or says it doesn't support it. NVML is opened at the first scan, tried again at each scan until it opens (a driver loaded
/// later; <see cref="Retrying"/> logs a failure once), and shut down with the reader; without it (no NVIDIA driver)
/// nothing is listed.
/// </summary>
internal sealed class NvidiaGpuReader : ILinuxReader
{
    private const double MB = 1024 * 1024;

    private enum Metric
    {
        Utilization,
        MemoryUtilization,
        Temperature,
        TemperatureVram,
        MemoryUsed,
        MemoryTotal,
        MemoryPercent,
        Power,
        PowerLimit,
        ClockGraphics,
        ClockMemory,
        FanSpeed,
        FanRpm,
    }

    private static readonly (Metric Metric, string Id, string Name, string Unit)[] Metrics =
    [
        (Metric.Utilization, "utilization", "GPU Load", "%"),
        (Metric.MemoryUtilization, "memory_utilization", "GPU Memory Controller Load", "%"),
        (Metric.Temperature, "temperature", "GPU Temperature", "°C"),
        (Metric.TemperatureVram, "temperature_vram", "GPU Memory Temperature", "°C"),
        (Metric.MemoryUsed, "memory_used", "GPU Memory Used", "MB"),
        (Metric.MemoryTotal, "memory_total", "GPU Memory Total", "MB"),
        (Metric.MemoryPercent, "memory_percent", "GPU Memory Load", "%"),
        (Metric.Power, "power", "GPU Power", "W"),
        (Metric.PowerLimit, "power_limit", "GPU Power Limit", "W"),
        (Metric.ClockGraphics, "clock_graphics", "GPU Core Clock", "MHz"),
        (Metric.ClockMemory, "clock_memory", "GPU Memory Clock", "MHz"),
        (Metric.FanSpeed, "fan_speed", "GPU Fan", "%"),
    ];

    // At most this many fans a GPU are listed.
    private const uint MaxFans = 16;

    /// <summary>
    /// What a GPU offers: the metrics and fans NVML gave (or refused), listed; and those it failed on for now (anything
    /// but "not supported"), asked again at each rescan until NVML gives them or says it doesn't support them.
    /// </summary>
    private sealed class Offer
    {
        public HashSet<Metric> Metrics { get; } = [];
        public SortedSet<uint> Fans { get; } = [];
        public HashSet<Metric> PendingMetrics { get; } = [.. Array.ConvertAll(NvidiaGpuReader.Metrics, m => m.Metric)];
        public SortedSet<uint> PendingFans { get; } = [];

        // The fan count NVML failed on for now (or not asked yet).
        public bool PendingFanCount { get; set; } = true;
    }

    private readonly Func<INvml?> _open;
    private readonly ILogger _log;
    private INvml? _nvml;
    private readonly Dictionary<string, Offer> _offers = new(StringComparer.Ordinal);

    // The sensors the last scan listed (a channel's slot is its place here).
    private (nint Device, Metric Metric, uint Fan)[] _sensors = [];

    public NvidiaGpuReader(Func<INvml?> open, ILogger log)
    {
        _open = open;
        _log = log.ForContext<NvidiaGpuReader>();
    }

    /// <summary>
    /// <paramref name="open"/> as an open to try again at each scan, a failure logged (through the logger it is given) the
    /// first time only, and the NVML that opens after one failed logged as there.
    /// </summary>
    internal static Func<INvml?> Retrying(Func<ILogger, INvml?> open, ILogger log)
    {
        log = log.ForContext<NvidiaGpuReader>();
        var failed = false;
        return () =>
        {
            var nvml = open(failed ? Serilog.Core.Logger.None : log);
            if (nvml != null && failed)
                log.Information("Linux sensors: NVML is there now");
            failed = nvml == null;
            return nvml;
        };
    }

    public void Scan(List<LinuxChannel> found)
    {
        _nvml ??= _open();
        if (_nvml is not { } nvml || nvml.DeviceCount(out var count) != NvmlStatus.Ok)
        {
            _sensors = [];
            _offers.Clear();
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sensors = new List<(nint, Metric, uint)>();
        for (uint i = 0; i < count; i++)
        {
            if (nvml.Device(i, out var device) != NvmlStatus.Ok)
                continue;
            var uuid = nvml.Uuid(device, out var u) == NvmlStatus.Ok && u.Length > 0 ? u : null;
            var pci = nvml.PciBusId(device, out var bus) == NvmlStatus.Ok ? PciToken(bus) : null;
            (LinuxAnchor Anchor, LinuxAnchor? Secondary)? anchors = uuid != null
                ? (new LinuxAnchor("gpu-uuid", uuid), pci != null ? new LinuxAnchor("pci", pci) : null)
                : pci != null ? (new LinuxAnchor("pci", pci), null) : null;
            if (anchors is not { } a)
            {
                _log.Debug("Linux sensors: NVIDIA GPU {Index} has neither a UUID nor a PCI address and is left out", i);
                continue;
            }

            var group = nvml.Name(device, out var name) == NvmlStatus.Ok && name.Length > 0 ? name : "NVIDIA GPU";
            var key = a.Anchor + "~" + a.Secondary;
            seen.Add(key);
            if (!_offers.TryGetValue(key, out var offer))
                _offers[key] = offer = new Offer();
            // Only what NVML failed on so far is asked (all of it at a GPU's first scan; nothing once each is settled).
            Probe(nvml, device, offer);

            var index = 0;
            foreach (var (metric, id, label, unit) in Metrics)
            {
                if (offer.Metrics.Contains(metric))
                {
                    found.Add(new LinuxChannel(LinuxSensorIds.SystemDevice("gpu", a.Anchor, a.Secondary, id), group, label, unit, "", 1, "", index, $"nvml{i}/{id}")
                    {
                        Reader = this,
                        Slot = sensors.Count,
                    });
                    sensors.Add((device, metric, 0));
                }

                index++;
            }

            foreach (var fan in offer.Fans)
            {
                var id = $"fan{fan}_rpm";
                found.Add(new LinuxChannel(LinuxSensorIds.SystemDevice("gpu", a.Anchor, a.Secondary, id), group, $"GPU Fan {fan + 1}", "RPM", "", 1, "fan", (int)fan, $"nvml{i}/{id}")
                {
                    Reader = this,
                    Slot = sensors.Count,
                });
                sensors.Add((device, Metric.FanRpm, fan));
            }
        }

        foreach (var key in _offers.Keys.Where(k => !seen.Contains(k)).ToList())
            _offers.Remove(key);
        _sensors = sensors.ToArray();
    }

    /// <summary>
    /// Asks a GPU for what its offer still has pending: each metric, the fan count and each fan's speed. One NVML gives or
    /// refuses is listed; one it doesn't support is left out for good; one it failed on stays pending.
    /// </summary>
    private static void Probe(INvml nvml, nint device, Offer offer)
    {
        foreach (var metric in offer.PendingMetrics.ToList())
        {
            var status = Ask(nvml, device, metric, 0, out _);
            if (Settled(status))
                offer.PendingMetrics.Remove(metric);
            if (Listed(status))
                offer.Metrics.Add(metric);
        }

        if (offer.PendingFanCount)
        {
            var status = nvml.FanCount(device, out var count);
            // No fan count (none, or refused): no fan listed.
            offer.PendingFanCount = !Settled(status);
            if (status == NvmlStatus.Ok)
            {
                for (uint fan = 0; fan < Math.Min(count, MaxFans); fan++)
                    offer.PendingFans.Add(fan);
            }
        }

        foreach (var fan in offer.PendingFans.ToList())
        {
            var status = Ask(nvml, device, Metric.FanRpm, fan, out _);
            if (Settled(status))
                offer.PendingFans.Remove(fan);
            if (Listed(status))
                offer.Fans.Add(fan);
        }
    }

    // A metric is listed when NVML gives it, or the driver refuses it (it reads denied then).
    private static bool Listed(NvmlStatus status) => status is NvmlStatus.Ok or NvmlStatus.NoPermission;

    // NVML's answer is final when it gives the metric, refuses it or doesn't support it; any other failure (the GPU
    // starting up, lost for now) is asked again at the next rescan.
    private static bool Settled(NvmlStatus status) => status is NvmlStatus.Ok or NvmlStatus.NoPermission or NvmlStatus.NotSupported;

    public void Read(IReadOnlyList<LinuxRead> inUse, Span<SensorSample> samples, TimeSpan elapsed, long round)
    {
        if (_nvml is not { } nvml)
            return;
        foreach (var read in inUse)
        {
            ref var sample = ref samples[read.Sample];
            if (read.Channel.Slot >= _sensors.Length)
            {
                sample.SetUnavailable();
                continue;
            }

            var (device, metric, fan) = _sensors[read.Channel.Slot];
            switch (Ask(nvml, device, metric, fan, out var value))
            {
                case NvmlStatus.Ok:
                    sample.Set(value);
                    break;
                case NvmlStatus.NoPermission:
                    sample.Outcome = SampleOutcome.PermissionDenied;
                    break;
                case NvmlStatus.NotSupported or NvmlStatus.Lost:
                    sample.SetUnavailable();
                    break;
                default:
                    sample.Outcome = SampleOutcome.Failed;
                    break;
            }
        }
    }

    /// <summary>One metric of one GPU, in its unit: one NVML call.</summary>
    private static NvmlStatus Ask(INvml nvml, nint device, Metric metric, uint fan, out double value)
    {
        value = 0;
        NvmlStatus status;
        switch (metric)
        {
            case Metric.Utilization or Metric.MemoryUtilization:
                status = nvml.Utilization(device, out var gpu, out var memory);
                value = metric == Metric.Utilization ? gpu : memory;
                return status;
            case Metric.Temperature:
                status = nvml.Temperature(device, out var celsius);
                value = celsius;
                return status;
            case Metric.TemperatureVram:
                status = nvml.MemoryTemperature(device, out var vram);
                value = vram;
                return status;
            case Metric.MemoryUsed or Metric.MemoryTotal or Metric.MemoryPercent:
                status = nvml.Memory(device, out var used, out var total);
                if (status == NvmlStatus.Ok && metric == Metric.MemoryPercent && total == 0)
                    return NvmlStatus.NotSupported;
                value = metric switch
                {
                    Metric.MemoryUsed => used / MB,
                    Metric.MemoryTotal => total / MB,
                    _ => total > 0 ? used * 100.0 / total : 0,
                };
                return status;
            case Metric.Power:
                status = nvml.Power(device, out var milliwatts);
                value = milliwatts / 1000.0;
                return status;
            case Metric.PowerLimit:
                status = nvml.PowerLimit(device, out var limit);
                value = limit / 1000.0;
                return status;
            case Metric.ClockGraphics or Metric.ClockMemory:
                status = nvml.Clock(device, metric == Metric.ClockGraphics ? NvmlClock.Graphics : NvmlClock.Memory, out var mhz);
                value = mhz;
                return status;
            case Metric.FanSpeed:
                status = nvml.FanSpeed(device, out var percent);
                value = percent;
                return status;
            case Metric.FanRpm:
                status = nvml.FanRpm(device, fan, out var rpm);
                value = rpm;
                return status;
            default:
                return NvmlStatus.NotSupported;
        }
    }

    /// <summary>
    /// NVML's PCI address (<c>00000000:01:00.0</c>, its domain in 8 digits, upper case) as sysfs writes it and an id holds
    /// it: <c>0000-01-00.0</c>; null when it isn't one.
    /// </summary>
    internal static string? PciToken(string busId)
    {
        var parts = busId.Trim().Split(':');
        if (parts.Length != 3 || parts[2].Split('.') is not [var slot, var function]
            || !uint.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var domain)
            || !uint.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bus)
            || !uint.TryParse(slot, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var dev)
            || !uint.TryParse(function, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var fn)
            || domain > 0xFFFF || bus > 0xFF || dev > 0x1F || fn > 7)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{domain:x4}-{bus:x2}-{dev:x2}.{fn}");
    }

    public void Dispose()
    {
        var nvml = _nvml;
        _nvml = null;
        nvml?.Dispose();
    }
}
