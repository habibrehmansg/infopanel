using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Serilog;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// Each disk's read and write rates, MB/s (MiB, as the macOS source's), under the Linux fork's ids
/// <c>system/disk/{anchor}/read_speed</c> and <c>…/write_speed</c>, from <c>/proc/diskstats</c>'s sectors read and
/// written (512 bytes each, whatever the disk's own sector size). Whole disks only: no partition (sysfs's
/// <c>partition</c> attribute; for a disk sysfs doesn't know, as in a container, a name that is another's with a number
/// after it), no loop, RAM, zram, device-mapper, RAID or network block device. Anchored by what identifies the disk,
/// never its kernel name where anything else does: the NVMe controller's serial (<c>nvme-serial+…</c>), else the disk's
/// WWID (<c>block-wwid+…</c>) or serial (<c>block-serial+…</c>), else its name (<c>name+sda</c>, which can change from
/// boot to boot). The namespaces of one NVMe controller (<c>nvme0n1</c>, <c>nvme0n2</c>) are one disk under the serial,
/// their counters summed. Rates come from the deltas of two rounds in a row; a counter that went back gives none that
/// round. Each counter is kept with the anchor of the device it was read from: a name that now names another device (a
/// disk replaced, two names swapped) gives no rate from the counters of the one before, and its sensors read unavailable
/// until the next scan lists the device under its own id. A <c>/proc/diskstats</c> longer than the read limit
/// (<see cref="Sysfs.MaxProcRead"/>) is read as far as its whole rows go, the cut row left out, and said so once.
/// </summary>
internal sealed class DiskIoReader : ILinuxReader
{
    private const double MB = 1024 * 1024;
    private const int SectorBytes = 512;

    // Kernel names of block devices that are no disk of their own.
    private static readonly string[] NotDisks = ["loop", "ram", "zram", "dm-", "md", "nbd", "zd", "sr", "fd"];

    private readonly Sysfs _proc;
    private readonly Sysfs _sysfs;

    // A disk as the last scan listed it: its kernel names (one, but for an NVMe controller's namespaces) and its anchor.
    private sealed record Disk(string[] Names, LinuxAnchor Anchor);

    // The disks the last scan listed (a channel's slot is its place here), and each kernel name's counters, their round
    // and the anchor of the device they were read from.
    private Disk[] _disks = [];
    private readonly Dictionary<string, (ulong Read, ulong Written, long Round, LinuxAnchor Anchor)> _last = new(StringComparer.Ordinal);
    private readonly ILogger _log;
    private bool _truncationReported;

    public DiskIoReader(Sysfs proc, Sysfs sysfs, ILogger? log = null)
    {
        _proc = proc;
        _sysfs = sysfs;
        _log = (log ?? Log.Logger).ForContext<DiskIoReader>();
    }

    public void Scan(List<LinuxChannel> found)
    {
        if (ReadDiskstats(out var text) != SysfsRead.Ok)
        {
            _disks = [];
            return;
        }

        var names = Parse(text).Keys.ToHashSet(StringComparer.Ordinal);
        var whole = new List<(string Name, string? Device, LinuxAnchor Anchor)>();
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            if (IsDisk(name, names, out var device))
                whole.Add((name, device, Anchor(name, device)));
        }

        // The namespaces of one NVMe controller share its serial: one disk of them, so their ids don't collide. Anything
        // else with an anchor in common stays two, and the sampler leaves both out.
        var groups = new List<(List<string> Names, string? Device, LinuxAnchor Anchor)>();
        foreach (var (name, device, anchor) in whole)
        {
            var group = anchor.Kind == "nvme-serial" ? groups.FindIndex(g => g.Anchor.Equals(anchor)) : -1;
            if (group >= 0)
                groups[group].Names.Add(name);
            else
                groups.Add(([name], device, anchor));
        }

        var disks = new List<Disk>();
        foreach (var (diskNames, device, anchor) in groups)
        {
            var slot = disks.Count;
            disks.Add(new Disk([.. diskNames], anchor));
            var label = string.Join(", ", diskNames);
            var model = device != null ? _sysfs.ReadAttribute(device + "/device/model") : null;
            var group = model != null ? $"{model} ({label})" : $"Disk {label}";
            found.Add(new LinuxChannel(LinuxSensorIds.SystemDevice("disk", anchor, null, "read_speed"), group, "Read Rate", "MB/s", "", 1, "", 0, string.Join('+', diskNames) + "/read") { Reader = this, Slot = slot });
            found.Add(new LinuxChannel(LinuxSensorIds.SystemDevice("disk", anchor, null, "write_speed"), group, "Write Rate", "MB/s", "", 1, "", 1, string.Join('+', diskNames) + "/write") { Reader = this, Slot = slot });
        }

        _disks = disks.ToArray();
    }

    public void Read(IReadOnlyList<LinuxRead> inUse, Span<SensorSample> samples, TimeSpan elapsed, long round)
    {
        var status = ReadDiskstats(out var text);
        if (status != SysfsRead.Ok)
        {
            _last.Clear();
            foreach (var read in inUse)
            {
                samples[read.Sample].Outcome = status switch
                {
                    SysfsRead.Denied => SampleOutcome.PermissionDenied,
                    SysfsRead.Missing => SampleOutcome.Unavailable,
                    _ => SampleOutcome.Failed,
                };
            }

            return;
        }

        var now = Parse(text);
        var seconds = elapsed.TotalSeconds;

        // What each listed name names now (sysfs's view of it), asked once a round for the names read.
        var anchors = new Dictionary<string, LinuxAnchor>(StringComparer.Ordinal);
        LinuxAnchor Current(string name)
        {
            if (!anchors.TryGetValue(name, out var anchor))
                anchors[name] = anchor = AnchorNow(name);
            return anchor;
        }

        foreach (var read in inUse)
        {
            var disk = read.Channel.Slot < _disks.Length ? _disks[read.Channel.Slot] : null;
            if (disk == null || !disk.Names.Any(now.ContainsKey) || disk.Names.Any(n => !Current(n).Equals(disk.Anchor) && now.ContainsKey(n)))
            {
                // The disk is gone, or its name is another device's now (until the next scan lists that one).
                samples[read.Sample].SetUnavailable();
                continue;
            }

            // The sum over its names; a name missing now or without counters from the round before gives no rate.
            ulong sectors = 0;
            var ok = seconds > 0;
            foreach (var name in disk.Names)
            {
                if (!ok)
                    break;
                if (!now.TryGetValue(name, out var counters) || !_last.TryGetValue(name, out var last) || last.Round != round - 1
                    || !last.Anchor.Equals(disk.Anchor) || counters.Read < last.Read || counters.Written < last.Written)
                {
                    ok = false;
                    break;
                }

                sectors += read.Channel.Index == 0 ? counters.Read - last.Read : counters.Written - last.Written;
            }

            if (ok)
                samples[read.Sample].Set(sectors * (double)SectorBytes / MB / seconds);
        }

        // Every listed disk's counters are kept, so a disk coming into use beside another reads from the next round.
        _last.Clear();
        foreach (var disk in _disks)
        {
            foreach (var name in disk.Names)
            {
                if (now.TryGetValue(name, out var counters))
                    _last[name] = (counters.Read, counters.Written, round, Current(name));
            }
        }
    }

    /// <summary><c>/proc/diskstats</c>'s whole rows; one cut by the read limit is said so once.</summary>
    private SysfsRead ReadDiskstats(out string text)
    {
        var status = _proc.ReadRows("diskstats", out text, out var truncated);
        if (status == SysfsRead.Ok && truncated && !_truncationReported)
        {
            _truncationReported = true;
            _log.Warning("Linux sensors: /proc/diskstats is longer than {Limit} bytes; only its first whole rows are read, and disks after them are left out", Sysfs.MaxProcRead);
        }

        return status;
    }

    /// <summary>The anchor of what the kernel name names now: the device sysfs has under it, else the name.</summary>
    private LinuxAnchor AnchorNow(string name) =>
        _sysfs.ReadLink("class/block/" + name) != null && _sysfs.RealPath("class/block/" + name) is { } real
            ? Anchor(name, real)
            : Anchor(name, null);

    /// <summary>
    /// Whether the block device <paramref name="name"/> is a whole disk; <paramref name="device"/> its folder in sysfs
    /// (a real path) when sysfs has it.
    /// </summary>
    private bool IsDisk(string name, HashSet<string> names, out string? device)
    {
        device = null;
        if (NotDisks.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            return false;

        if (_sysfs.ReadLink("class/block/" + name) != null && _sysfs.RealPath("class/block/" + name) is { } real)
        {
            device = real;
            return !_sysfs.List(real).Contains("partition");
        }

        // A disk sysfs doesn't know (a container's own tree): a partition is a disk's name and its number
        // (sda1, vda2), with a 'p' between when the disk's name ends in a digit (nvme0n1p1, mmcblk0p1).
        var end = name.Length;
        while (end > 0 && char.IsAsciiDigit(name[end - 1]))
            end--;
        if (end == name.Length || end == 0)
            return true;
        var stem = name[..end];
        return !(names.Contains(stem) && !char.IsAsciiDigit(stem[^1]))
            && !(stem.Length > 1 && stem[^1] == 'p' && char.IsAsciiDigit(stem[^2]) && names.Contains(stem[..^1]));
    }

    /// <summary>What identifies the disk: its NVMe controller's serial, its WWID or serial, else its kernel name.</summary>
    private LinuxAnchor Anchor(string name, string? device)
    {
        if (device == null)
            return new LinuxAnchor("name", name);

        // The device and the devices above it, each with its subsystem (an NVMe namespace's controller, or with native
        // multipath its subsystem, which has the same serial; a SATA or SCSI disk's SCSI device).
        var chain = new List<(string Path, string? Subsystem)>();
        for (var path = device; path.StartsWith("devices/", StringComparison.Ordinal); path = path[..path.LastIndexOf('/')])
            chain.Add((path, _sysfs.ReadLink(path + "/subsystem") is { } target ? target[(target.LastIndexOf('/') + 1)..] : null));

        foreach (var (path, subsystem) in chain)
        {
            if (subsystem is "nvme" or "nvme-subsystem" && _sysfs.ReadAttribute(path + "/serial") is { } serial)
                return new LinuxAnchor("nvme-serial", serial);
        }

        foreach (var attribute in new[] { ("wwid", "block-wwid"), ("serial", "block-serial") })
        {
            foreach (var (path, subsystem) in chain)
            {
                if (subsystem is "block" or "scsi" && _sysfs.ReadAttribute(path + "/" + attribute.Item1) is { } value)
                    return new LinuxAnchor(attribute.Item2, value);
            }
        }

        return new LinuxAnchor("name", name);
    }

    /// <summary>Each device's sectors read (field 6) and written (field 10).</summary>
    private static Dictionary<string, (ulong Read, ulong Written)> Parse(string text)
    {
        var devices = new Dictionary<string, (ulong, ulong)>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10
                || !ulong.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var read)
                || !ulong.TryParse(fields[9], NumberStyles.None, CultureInfo.InvariantCulture, out var written))
            {
                continue;
            }

            devices[fields[2]] = (read, written);
        }

        return devices;
    }

    public void Dispose()
    {
    }
}
