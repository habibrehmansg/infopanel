using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace InfoPanel.Sensors.Linux;

/// <summary>
/// One sensor the Linux readers found: its id, group, name and unit; the file it reads (a real path) and what the
/// kernel's value is divided by; its channel's kind and number, for the order; and the kernel's name for it now
/// (<c>hwmon3/fan1</c>), which stays internal (logs only): it changes from boot to boot.
/// </summary>
internal sealed record LinuxChannel(string Id, string Group, string Name, string Unit, string Path, double Scale, string Kind, int Index, string Alias);

/// <summary>
/// The Linux sensors as one <see cref="ISensorSampler"/>: hwmon channels (<see cref="HwmonReader"/>) and thermal zones
/// (<see cref="ThermalReader"/>) read from <see cref="Sysfs"/>. Found at discovery, then again every
/// <see cref="RescanInterval"/>: a scan whose catalogue differs bumps <see cref="CatalogueVersion"/> (a device added or
/// removed), and every scan finds each sensor's file afresh, so a device the kernel numbers anew is read where it now is
/// under the same key. Listed by device (ordinally by id), then by channel kind and number. Two sensors with one id
/// (devices nothing tells apart) are both left out, with a warning, rather than one read for the other. A file that
/// can't be read is never 0: one the OS refuses reads <see cref="SampleOutcome.PermissionDenied"/>, a missing one
/// (its device gone) <see cref="SampleOutcome.Unavailable"/>, any other failure or a value that isn't a number fails.
/// Only the sensors in use (<see cref="LinuxDemand"/>) are read; the others are listed and left
/// <see cref="SampleOutcome.Skipped"/>.
/// </summary>
internal sealed class LinuxSensorSampler : ISensorSampler
{
    /// <summary>How often the devices are found again (hot-plug, a driver loaded or reloaded).</summary>
    public static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(10);

    // By device (the id but its channel, ordinally), then by channel kind and number (fan2 before fan10).
    private static readonly IComparer<LinuxChannel> Order = Comparer<LinuxChannel>.Create((a, b) =>
    {
        var device = a.Id.AsSpan(0, a.Id.LastIndexOf('/')).SequenceCompareTo(b.Id.AsSpan(0, b.Id.LastIndexOf('/')));
        if (device != 0)
            return device;
        var kind = string.CompareOrdinal(a.Kind, b.Kind);
        return kind != 0 ? kind : a.Index.CompareTo(b.Index);
    });

    private readonly Sysfs _sysfs;
    private readonly LinuxDemand _demand;
    private readonly ILogger _log;
    private SensorDefinition[] _definitions = [];
    private Dictionary<string, LinuxChannel> _byId = new(StringComparer.Ordinal);
    private HashSet<string> _ambiguous = new(StringComparer.Ordinal);
    // The catalogue Discover last handed out, and each of its sensors as the latest scan found it (null: gone).
    private string[] _handedOut = [];
    private SensorKey[] _keys = [];
    private LinuxChannel?[] _reads = [];
    private long _version;
    private TimeSpan _sinceScan;
    private bool _scanned;

    public LinuxSensorSampler(Sysfs sysfs, LinuxDemand demand, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(sysfs);
        ArgumentNullException.ThrowIfNull(demand);
        _sysfs = sysfs;
        _demand = demand;
        _log = (log ?? Log.Logger).ForContext<LinuxSensorSampler>();
    }

    public long CatalogueVersion => _version;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        if (!_scanned)
            Scan();
        _handedOut = _definitions.Select(d => d.Id).ToArray();
        _keys = _handedOut.Select(LinuxSensorSource.KeyOf).ToArray();
        _demand.List(_keys);
        Resolve();
        _log.Debug("Linux sensors: {Count} found", _definitions.Length);
        return _definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        _sinceScan += elapsed;
        if (_sinceScan >= RescanInterval)
        {
            _sinceScan = TimeSpan.Zero;
            Scan();
            Resolve();
        }

        for (var i = 0; i < samples.Length; i++)
        {
            if (i >= _reads.Length || _reads[i] is not { } channel)
            {
                samples[i].SetUnavailable();
                continue;
            }

            // Not in use: not read (left Skipped), so a disk's drivetemp isn't woken and a slow chip costs nothing.
            if (!_demand.InUse(_keys[i]))
                continue;

            switch (_sysfs.Read(channel.Path, out var text))
            {
                case SysfsRead.Ok when long.TryParse(text.AsSpan().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var raw):
                    samples[i].Set(raw / channel.Scale);
                    break;
                case SysfsRead.Missing:
                    samples[i].SetUnavailable();
                    break;
                case SysfsRead.Denied:
                    samples[i].Outcome = SampleOutcome.PermissionDenied;
                    break;
                default:
                    samples[i].Outcome = SampleOutcome.Failed;
                    break;
            }
        }
    }

    public void Dispose()
    {
    }

    /// <summary>Each handed-out sensor as the latest scan found it.</summary>
    private void Resolve() => _reads = _handedOut.Select(id => _byId.GetValueOrDefault(id)).ToArray();

    /// <summary>Finds every sensor; a catalogue that differs from the last is published under a new version.</summary>
    private void Scan()
    {
        _scanned = true;
        var found = new List<LinuxChannel>();
        HwmonReader.Scan(_sysfs, found, _log);
        ThermalReader.Scan(_sysfs, found, _log);

        var byId = new Dictionary<string, LinuxChannel>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in found)
        {
            if (ambiguous.Contains(channel.Id))
                continue;
            if (byId.TryAdd(channel.Id, channel))
                continue;

            ambiguous.Add(channel.Id);
            if (!_ambiguous.Contains(channel.Id))
                _log.Warning("Linux sensors: {First} and {Second} have the same id, {Id}, and nothing tells them apart; neither is listed", byId[channel.Id].Alias, channel.Alias, channel.Id);
            byId.Remove(channel.Id);
        }

        _ambiguous = ambiguous;
        _byId = byId;
        var definitions = byId.Values.Order(Order).Select(c => new SensorDefinition(c.Id, c.Group, c.Name, c.Unit)).ToArray();
        if (definitions.SequenceEqual(_definitions))
            return;
        _definitions = definitions;
        _version++;
    }
}
