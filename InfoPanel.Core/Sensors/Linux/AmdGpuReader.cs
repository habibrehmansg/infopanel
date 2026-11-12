using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// AMD GPUs from amdgpu's sysfs, with no library (ROCm isn't needed), under the Linux fork's ids
/// <c>system/amdgpu/pci+{0000-03-00.0}/{metric}</c>: each card (<c>class/drm/cardN</c>) whose PCI device has
/// <c>gpu_busy_percent</c>. From the device: <c>utilization</c> (<c>gpu_busy_percent</c>), <c>memory_utilization</c>
/// (<c>mem_busy_percent</c>), <c>memory_used</c> and <c>memory_total</c> (<c>mem_info_vram_used</c>, <c>_total</c>, MB),
/// <c>memory_percent</c>; from its hwmon device: <c>clock_graphics</c> and <c>clock_memory</c> (<c>freq1_input</c>,
/// <c>freq2_input</c>, MHz), <c>power</c> (<c>power1_input</c>, else <c>power1_average</c>, W), <c>temperature</c>,
/// <c>temperature_junction</c> and <c>temperature_memory</c> (the temperatures labelled <c>edge</c>, <c>junction</c>,
/// <c>mem</c>), <c>fan_rpm</c> (<c>fan1_input</c>). Each is listed when its file is there and read as a hwmon channel is
/// (a file each, while in use); <c>memory_percent</c> reads both memory files.
/// </summary>
internal sealed partial class AmdGpuReader : ILinuxReader
{
    private const double MB = 1024 * 1024;

    private readonly Sysfs _sysfs;

    // The memory_percent sensors' files (used, total); a channel's slot is its place here.
    private (string Used, string Total)[] _percents = [];

    [GeneratedRegex(@"^card[0-9]+$")]
    private static partial Regex Card();

    [GeneratedRegex(@"^hwmon[0-9]+$")]
    private static partial Regex Hwmon();

    public AmdGpuReader(Sysfs sysfs) => _sysfs = sysfs;

    public void Scan(List<LinuxChannel> found)
    {
        var percents = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in _sysfs.List("class/drm").Where(c => Card().IsMatch(c)).Order(StringComparer.Ordinal))
        {
            if (_sysfs.RealPath("class/drm/" + card) is not { } folder
                || _sysfs.ReadLink(folder + "/device") == null
                || _sysfs.RealPath(folder + "/device") is not { } device)
            {
                continue;
            }

            var files = _sysfs.List(device);
            if (!files.Contains("gpu_busy_percent") || !seen.Add(device))
                continue;

            var address = device[(device.LastIndexOf('/') + 1)..];
            var anchor = new LinuxAnchor("pci", address.Replace(':', '-'));
            var group = $"AMD GPU ({address})";
            var index = 0;

            void Add(string metric, string name, string unit, string? path, double scale)
            {
                if (path != null && _sysfs.RealPath(path) is { } real)
                    found.Add(new LinuxChannel(LinuxSensorIds.SystemDevice("amdgpu", anchor, null, metric), group, name, unit, real, scale, "", index, $"{card}/{metric}"));
                index++;
            }

            string? File(string folder, IReadOnlyList<string> names, string name) => names.Contains(name) ? $"{folder}/{name}" : null;

            Add("utilization", "GPU Load", "%", File(device, files, "gpu_busy_percent"), 1);
            Add("memory_utilization", "GPU Memory Controller Load", "%", File(device, files, "mem_busy_percent"), 1);
            var used = File(device, files, "mem_info_vram_used");
            var total = File(device, files, "mem_info_vram_total");
            Add("memory_used", "GPU Memory Used", "MB", used, MB);
            Add("memory_total", "GPU Memory Total", "MB", total, MB);
            if (used != null && total != null && _sysfs.RealPath(used) is { } realUsed && _sysfs.RealPath(total) is { } realTotal)
            {
                found.Add(new LinuxChannel(LinuxSensorIds.SystemDevice("amdgpu", anchor, null, "memory_percent"), group, "GPU Memory Load", "%", "", 1, "", index, card + "/memory_percent")
                {
                    Reader = this,
                    Slot = percents.Count,
                });
                percents.Add((realUsed, realTotal));
            }

            index++;

            var hwmon = _sysfs.List(device + "/hwmon").Where(h => Hwmon().IsMatch(h)).Order(StringComparer.Ordinal).FirstOrDefault();
            var hwFolder = hwmon != null ? _sysfs.RealPath($"{device}/hwmon/{hwmon}") : null;
            var hw = hwFolder != null ? _sysfs.List(hwFolder) : [];
            Add("clock_graphics", "GPU Core Clock", "MHz", hwFolder != null ? File(hwFolder, hw, "freq1_input") : null, 1_000_000);
            Add("clock_memory", "GPU Memory Clock", "MHz", hwFolder != null ? File(hwFolder, hw, "freq2_input") : null, 1_000_000);
            Add("power", "GPU Power", "W", hwFolder != null ? File(hwFolder, hw, "power1_input") ?? File(hwFolder, hw, "power1_average") : null, 1_000_000);
            Add("temperature", "GPU Temperature", "°C", hwFolder != null ? Temperature(hwFolder, hw, "edge", fallback: "temp1_input") : null, 1_000);
            Add("temperature_junction", "GPU Hot Spot Temperature", "°C", hwFolder != null ? Temperature(hwFolder, hw, "junction") : null, 1_000);
            Add("temperature_memory", "GPU Memory Temperature", "°C", hwFolder != null ? Temperature(hwFolder, hw, "mem") : null, 1_000);
            Add("fan_rpm", "GPU Fan", "RPM", hwFolder != null ? File(hwFolder, hw, "fan1_input") : null, 1);
        }

        _percents = percents.ToArray();
    }

    /// <summary>The <c>tempN_input</c> labelled <paramref name="label"/>, else <paramref name="fallback"/> when it is there.</summary>
    private string? Temperature(string folder, IReadOnlyList<string> files, string label, string? fallback = null)
    {
        foreach (var file in files.Where(f => f.StartsWith("temp", StringComparison.Ordinal) && f.EndsWith("_label", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var input = file[..^"_label".Length] + "_input";
            if (files.Contains(input) && string.Equals(_sysfs.ReadAttribute($"{folder}/{file}"), label, StringComparison.Ordinal))
                return $"{folder}/{input}";
        }

        return fallback != null && files.Contains(fallback) ? $"{folder}/{fallback}" : null;
    }

    public void Read(IReadOnlyList<LinuxRead> inUse, Span<SensorSample> samples, TimeSpan elapsed, long round)
    {
        foreach (var read in inUse)
        {
            ref var sample = ref samples[read.Sample];
            if (read.Channel.Slot >= _percents.Length)
            {
                sample.SetUnavailable();
                continue;
            }

            var (usedPath, totalPath) = _percents[read.Channel.Slot];
            var used = Number(usedPath, out var usedStatus);
            var total = Number(totalPath, out var totalStatus);
            if (used is { } u && total is { } t && t > 0)
            {
                sample.Set(u * 100.0 / t);
                continue;
            }

            var status = usedStatus != SysfsRead.Ok ? usedStatus : totalStatus;
            sample.Outcome = status switch
            {
                SysfsRead.Denied => SampleOutcome.PermissionDenied,
                SysfsRead.Missing => SampleOutcome.Unavailable,
                _ => SampleOutcome.Failed,
            };
        }
    }

    private long? Number(string path, out SysfsRead status)
    {
        status = _sysfs.Read(path, out var text);
        if (status != SysfsRead.Ok)
            return null;
        if (long.TryParse(text.AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return value;
        status = SysfsRead.Failed;
        return null;
    }

    public void Dispose()
    {
    }
}
