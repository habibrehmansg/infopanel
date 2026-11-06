using InfoPanel.Models;
using InfoPanel.Sensors.Drives;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.Volume;

/// <summary>
/// The volume source, <c>infopanel.volume</c> ("Volume"): a <see cref="PolledSensorSource"/> over
/// <see cref="VolumeSampler"/>, polled every 250 ms (the 1.x Volume Info plugin's interval), the devices followed as
/// they come and go. It lists the 1.x plugin's entries (<see cref="Entries"/>) for the default output device, under
/// <c>plugin:/infopanel.volume/default/{entry}</c> (following a change of default), and for each output device, under
/// <c>plugin:/infopanel.volume/{device}/{entry}</c>, its device id the 1.x plugin SDK's id (<see cref="DriveSource.Slug"/>)
/// of the system's device id (<see cref="ContainerOf"/>). The engine never answers the plugin's keys.
/// </summary>
public static class VolumeSource
{
    public const string SourceId = "infopanel.volume";

    /// <summary>The source's name.</summary>
    public const string Name = "Volume";

    /// <summary>The default output device's device id, and its group (the 1.x plugin's).</summary>
    public const string DefaultContainer = "default";

    public const string DefaultGroup = "Default";

    /// <summary>The volume's unit.</summary>
    public const string VolumeUnit = "%";

    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>Each device's entries in the order it lists them (the 1.x plugin's): id, name and kind; the volume in <see cref="VolumeUnit"/>.</summary>
    public static IReadOnlyList<(string Id, string Name, SensorKind Kind)> Entries { get; } =
    [
        ("device_friendly_name", "Device Name", SensorKind.Text),
        ("friendly_name", "Name", SensorKind.Text),
        ("short_name", "Short Name", SensorKind.Text),
        ("volume", "Volume", SensorKind.Number),
        ("mute", "Mute", SensorKind.Text),
    ];

    /// <summary>The key of the entry <paramref name="entryId"/> of the device <paramref name="container"/>: <c>plugin:/infopanel.volume/{container}/{entryId}</c>.</summary>
    public static SensorKey KeyOf(string container, string entryId) => Space.KeyOf(container + "/" + entryId);

    /// <summary>The name of the entry <paramref name="entryId"/> (case as listed), or null when a device has no such entry.</summary>
    public static string? EntryName(string entryId)
    {
        foreach (var (id, name, _) in Entries)
        {
            if (string.Equals(id, entryId, StringComparison.Ordinal))
                return name;
        }

        return null;
    }

    /// <summary>A device's device id: the 1.x plugin SDK's id (<see cref="DriveSource.Slug"/>) of the system's device id, as the plugin made its containers' ids.</summary>
    public static string ContainerOf(string deviceId) => DriveSource.Slug(deviceId);

    /// <summary>
    /// A device's Short Name: its name less its device's name in brackets, trimmed (<c>Speakers (Realtek(R) Audio)</c> →
    /// <c>Speakers</c>), as the 1.x plugin made the default device's. The 1.x plugin made every other device's with the
    /// default device's name and didn't trim it; each device's own is used here. A name without it is kept whole (macOS,
    /// where both names are one).
    /// </summary>
    public static string ShortName(string name, string deviceName) => name.Replace($"({deviceName})", "", StringComparison.Ordinal).Trim();

    /// <summary>The volume reading of a scalar from 0 to 1, as the 1.x plugin rounded it: <c>Math.Round(scalar × 100)</c>, to even.</summary>
    public static double Percent(float scalar) => (float)Math.Round(scalar * 100);

    /// <summary>The mute reading, as the 1.x plugin wrote it: <c>True</c> or <c>False</c>.</summary>
    public static string MuteText(bool mute) => mute.ToString();

    /// <summary>A source over <paramref name="lister"/> (a test's).</summary>
    public static PolledSensorSource Create(IAudioLister lister, TimeProvider? time = null, PolledSensorSourceOptions? options = null, ILogger? log = null) =>
        new(SourceId, () => new VolumeSampler(lister, log), options ?? new PolledSensorSourceOptions { Interval = Interval }, time, log: log);

    /// <summary>The source over this Mac's output devices (Core Audio, <see cref="MacAudioLister"/>).</summary>
    [SupportedOSPlatform("macos")]
    public static PolledSensorSource CreateSystem(TimeProvider? time = null) => Create(new MacAudioLister(), time);
}

/// <summary>
/// Reads the output devices for <see cref="VolumeSource"/>: every round asks for the default device and lists the
/// devices afresh (<see cref="IAudioLister"/>), and reads each one's names, volume and mute. The default device is
/// listed first, always, under <see cref="VolumeSource.DefaultContainer"/> and the group "Default", reading whichever
/// device is the default this round (none: its readings are unavailable); then each device under its device id, its
/// group its name. The catalogue is compared with the last one every round (<see cref="ISensorSampler.CatalogueVersion"/>),
/// so a device that appears is listed after the round that saw it and read at the next, and one that goes fails (stale,
/// then unavailable, then unlisted, as <see cref="PolledSensorSource"/> does). A device with no volume the system can set
/// reads its volume unavailable, one with no mute control its mute.
/// <para>
/// Bounded: at most <see cref="MaxDevices"/> devices (the rest left out unread, one warning), names cut at
/// <see cref="MaxTextChars"/> characters; a device whose id makes no device id of its own (empty, or
/// <see cref="VolumeSource.DefaultContainer"/>) or keys longer than <see cref="MaxKeyChars"/> is left out. A device whose
/// read throws is left out of that round alone (as one that went), and two devices with one device id list the first.
/// Each is logged once while it stands; a listing that throws fails the round (every reading fails, the catalogue stands).
/// </para>
/// </summary>
public sealed class VolumeSampler : ISensorSampler
{
    /// <summary>At most this many devices are listed.</summary>
    public const int MaxDevices = 64;

    /// <summary>A device's names are cut at this many characters.</summary>
    public const int MaxTextChars = 256;

    /// <summary>A device whose keys would be longer than this (a document's sensor key's bound) is left out.</summary>
    public const int MaxKeyChars = 512;

    private const int VolumeEntry = 3;
    private const int MuteEntry = 4;

    private sealed record Device(string Container, string Group, string[] Texts, float? Volume, bool? Mute);

    private readonly IAudioLister _lister;
    private readonly ILogger _log;
    private static readonly string LongestEntry = VolumeSource.Entries.MaxBy(e => e.Id.Length).Id;

    private SensorDefinition[] _definitions = [];
    // Each handed-out definition's device id and its entry's index in VolumeSource.Entries.
    private (string Container, int Entry)[] _handedOut = [];
    // This round's devices by device id, the default one under DefaultContainer when there is one; null when the
    // listing failed (every reading fails).
    private Dictionary<string, Device>? _read;
    // This round's default device couldn't be read (its readings fail), or there is none (its readings are unavailable).
    private bool _defaultFailed;
    private long _version;
    private bool _rounded;

    // What has been logged, so each is logged once while it stands.
    private bool _listFailed;
    private bool _defaultFailing;
    private bool _tooMany;
    private HashSet<string> _failing = [];
    private HashSet<string> _clashing = [];
    private HashSet<string> _refused = [];

    /// <param name="lister">The devices: <see cref="MacAudioLister"/>, or a test's.</param>
    /// <param name="log">Where it logs; Serilog's global logger but in tests.</param>
    public VolumeSampler(IAudioLister lister, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _lister = lister;
        _log = (log ?? Log.Logger).ForContext<VolumeSampler>();
    }

    public long CatalogueVersion => _version;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        if (!_rounded)
            Refresh();
        _handedOut = _definitions.Select(d =>
        {
            var slash = d.Id.LastIndexOf('/');
            return (d.Id[..slash], IndexOfEntry(d.Id[(slash + 1)..]));
        }).ToArray();
        return _definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        Refresh();
        var read = _read;
        for (var i = 0; i < samples.Length; i++)
        {
            if (read == null || i >= _handedOut.Length)
            {
                samples[i].Outcome = SampleOutcome.Failed;
                continue;
            }

            var (container, entry) = _handedOut[i];
            if (!read.TryGetValue(container, out var device))
            {
                // The default device unread this round fails; none at all has no reading.
                if (container == VolumeSource.DefaultContainer && !_defaultFailed)
                    samples[i].SetUnavailable();
                else
                    samples[i].Outcome = SampleOutcome.Failed;
                continue;
            }

            switch (entry)
            {
                case VolumeEntry:
                    if (device.Volume is { } volume)
                        samples[i].Set(VolumeSource.Percent(volume));
                    else
                        samples[i].SetUnavailable();
                    break;
                case MuteEntry:
                    if (device.Mute is { } mute)
                        samples[i].SetText(VolumeSource.MuteText(mute));
                    else
                        samples[i].SetUnavailable();
                    break;
                default:
                    samples[i].SetText(device.Texts[entry]);
                    break;
            }
        }
    }

    public void Dispose()
    {
    }

    private static int IndexOfEntry(string id)
    {
        for (var i = 0; i < VolumeSource.Entries.Count; i++)
        {
            if (VolumeSource.Entries[i].Id == id)
                return i;
        }

        throw new InvalidOperationException($"No volume entry '{id}'");
    }

    /// <summary>One round: the default device and the devices read once, the catalogue updated.</summary>
    private void Refresh()
    {
        _rounded = true;
        IReadOnlyList<IAudioDevice> devices;
        try
        {
            devices = _lister.List();
            if (_listFailed)
                _log.Information("Volume: the output devices are listed again");
            _listFailed = false;
        }
        catch (Exception ex)
        {
            if (!_listFailed)
                _log.Warning(ex, "Volume: listing the output devices failed: {Message}", ex.Message);
            _listFailed = true;
            _read = null;
            return;
        }

        var read = new Dictionary<string, Device>(StringComparer.Ordinal);
        var listed = new List<Device>();

        // The default device, whichever it is this round.
        _defaultFailed = false;
        try
        {
            if (_lister.Default() is { } d)
                read.Add(VolumeSource.DefaultContainer, ReadDevice(d, VolumeSource.DefaultContainer, VolumeSource.DefaultGroup, _read));
            _defaultFailing = false;
        }
        catch (Exception ex)
        {
            _defaultFailed = true;
            if (!_defaultFailing)
                _log.Warning(ex, "Volume: reading the default output device failed: {Message}", ex.Message);
            _defaultFailing = true;
        }

        var failing = new HashSet<string>(StringComparer.Ordinal);
        var clashing = new HashSet<string>(StringComparer.Ordinal);
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var tooMany = false;
        foreach (var v in devices)
        {
            if (v == null)
                continue;

            // At the cap, the devices after it are left out unread.
            if (listed.Count == MaxDevices)
            {
                tooMany = true;
                if (!_tooMany)
                    _log.Warning("Volume: at most {Max} output devices are listed; the rest are left out unread", MaxDevices);
                break;
            }

            string? id = null;
            Device device;
            try
            {
                id = v.Id;
                if (id == null)
                    continue;
                device = ReadDevice(v, VolumeSource.ContainerOf(id), null, _read);
            }
            catch (Exception ex)
            {
                // Left out this round, as a device that went: its readings fail, then go.
                var which = id ?? "";
                failing.Add(which);
                if (!_failing.Contains(which))
                    _log.Warning(ex, "Volume: reading the output device {Device} failed: {Message}", id ?? "(no id)", ex.Message);
                continue;
            }

            if (device.Container.Length == 0 || device.Container == VolumeSource.DefaultContainer
                || VolumeSource.KeyOf(device.Container, LongestEntry).ToString().Length > MaxKeyChars)
            {
                refused.Add(id);
                if (!_refused.Contains(id))
                    _log.Warning("Volume: the output device {Device} is left out: its id makes no device id of its own, or keys longer than {Max} characters", device.Group, MaxKeyChars);
                continue;
            }

            if (read.ContainsKey(device.Container))
            {
                clashing.Add(device.Container);
                if (!_clashing.Contains(device.Container))
                    _log.Warning("Volume: the output device {Device} has the device id '{Container}' of a device listed before it, and is left out", device.Group, device.Container);
                continue;
            }

            read.Add(device.Container, device);
            listed.Add(device);
        }

        _failing = failing;
        _clashing = clashing;
        _refused = refused;
        _tooMany = tooMany;
        _read = read;
        Publish(listed);
    }

    /// <summary>A device's names, volume and mute; its group <paramref name="group"/>, else its name.</summary>
    /// <remarks>A text unchanged since <paramref name="last"/>'s round is handed back as the same string.</remarks>
    private static Device ReadDevice(IAudioDevice v, string container, string? group, Dictionary<string, Device>? last)
    {
        string name = v.Name ?? "", deviceName = v.DeviceName ?? "";
        var texts = new[] { Cut(deviceName), Cut(name), Cut(VolumeSource.ShortName(name, deviceName)) };
        var volume = v.Volume;
        var mute = v.Mute;
        if (last != null && last.TryGetValue(container, out var before))
        {
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] == before.Texts[i])
                    texts[i] = before.Texts[i];
            }
        }

        return new Device(container, group ?? texts[1], texts, volume, mute);
    }

    private static string Cut(string text) =>
        text.Length <= MaxTextChars ? text : text[..(char.IsHighSurrogate(text[MaxTextChars - 1]) ? MaxTextChars - 1 : MaxTextChars)];

    private void Publish(List<Device> devices)
    {
        var definitions = new List<SensorDefinition>((devices.Count + 1) * VolumeSource.Entries.Count);
        void Add(string container, string group)
        {
            foreach (var (id, name, kind) in VolumeSource.Entries)
                definitions.Add(new SensorDefinition($"{container}/{id}", group, name, kind == SensorKind.Number ? VolumeSource.VolumeUnit : "", kind));
        }

        Add(VolumeSource.DefaultContainer, VolumeSource.DefaultGroup);
        foreach (var device in devices)
            Add(device.Container, device.Group);

        if (definitions.SequenceEqual(_definitions))
            return;
        _definitions = [.. definitions];
        _version++;
    }
}
