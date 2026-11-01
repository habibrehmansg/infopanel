using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.Sensors.Drives;

/// <summary>
/// Reads the volumes for <see cref="DriveSource"/>: every round lists them afresh (<see cref="IVolumeLister"/>), keeps
/// the ones <see cref="DriveSource.Includes"/> lists on its platform that are ready, and reads each one's name, type,
/// label and format and its sizes (whole MiB, as 1.x: the used space is the total less the free space). The catalogue
/// is compared with the last one every round (<see cref="ISensorSampler.CatalogueVersion"/>), so a volume that appears
/// is listed after the round that saw it and read at the next, and one that goes, or stops being ready, fails (stale,
/// then unavailable, then unlisted, as <see cref="PolledSensorSource"/> does); its texts are read every round, and its
/// group is its name with its label when it has another (<c>C: (Windows)</c>).
/// <para>
/// Bounded: at most <see cref="MaxVolumes"/> volumes (the rest left out unread, one warning), names and labels cut at
/// <see cref="MaxTextChars"/> characters (its drive id comes from the whole name, as 1.x's); a volume whose key would be
/// longer than <see cref="MaxKeyChars"/> is left out. A volume whose read throws is left out of that round alone (as one
/// that went), and two volumes with one drive id list the first. Each is logged once while it stands; a listing that
/// throws fails the round (every reading fails, the catalogue stands).
/// </para>
/// </summary>
public sealed class DriveSampler : ISensorSampler
{
    /// <summary>At most this many volumes are listed.</summary>
    public const int MaxVolumes = 64;

    /// <summary>A volume's name and label are cut at this many characters.</summary>
    public const int MaxTextChars = 256;

    /// <summary>A volume whose keys would be longer than this (a document's sensor key's bound) is left out.</summary>
    public const int MaxKeyChars = 512;

    private const long Mebibyte = 1024 * 1024;

    private sealed record Volume(string Drive, string Group, string[] Texts, double[] Sizes);

    private readonly IVolumeLister _lister;
    private readonly VolumePlatform _platform;
    private readonly ILogger _log;
    // The longest entry id: a volume whose key for it would pass MaxKeyChars is left out.
    private static readonly string LongestEntry = DriveSource.Entries.MaxBy(e => e.Id.Length).Id;

    private SensorDefinition[] _definitions = [];
    // Each handed-out definition's drive and its entry's index in DriveSource.Entries.
    private (string Drive, int Entry)[] _handedOut = [];
    // This round's volumes by drive id; null when the listing failed (every reading fails).
    private Dictionary<string, Volume>? _read;
    private long _version;
    private bool _rounded;

    // What has been logged, so each is logged once while it stands.
    private bool _listFailed;
    private bool _tooMany;
    private HashSet<string> _failing = [];
    private HashSet<string> _clashing = [];
    private HashSet<string> _tooLong = [];

    /// <param name="lister">The volumes: <see cref="SystemVolumeLister"/>, or a test's.</param>
    /// <param name="platform">Which volumes are listed (<see cref="DriveSource.Includes"/>).</param>
    /// <param name="log">Where it logs; Serilog's global logger but in tests.</param>
    public DriveSampler(IVolumeLister lister, VolumePlatform platform, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        _lister = lister;
        _platform = platform;
        _log = (log ?? Log.Logger).ForContext<DriveSampler>();
    }

    public long CatalogueVersion => _version;

    public IReadOnlyList<SensorDefinition> Discover()
    {
        if (!_rounded)
            Refresh();
        _handedOut = _definitions.Select(d =>
        {
            var slash = d.Id.LastIndexOf('/');
            var entry = d.Id[(slash + 1)..];
            return (d.Id[..slash], IndexOfEntry(entry));
        }).ToArray();
        return _definitions;
    }

    public void Sample(Span<SensorSample> samples, TimeSpan elapsed)
    {
        Refresh();
        var read = _read;
        for (var i = 0; i < samples.Length; i++)
        {
            if (read == null || i >= _handedOut.Length || !read.TryGetValue(_handedOut[i].Drive, out var volume))
            {
                samples[i].Outcome = SampleOutcome.Failed;
                continue;
            }

            var entry = _handedOut[i].Entry;
            if (DriveSource.Entries[entry].Kind == SensorKind.Text)
                samples[i].SetText(volume.Texts[entry]);
            else
                samples[i].Set(volume.Sizes[entry - volume.Texts.Length]);
        }
    }

    public void Dispose()
    {
    }

    private static int IndexOfEntry(string id)
    {
        for (var i = 0; i < DriveSource.Entries.Count; i++)
        {
            if (DriveSource.Entries[i].Id == id)
                return i;
        }

        throw new InvalidOperationException($"No drive entry '{id}'");
    }

    /// <summary>One round: the volumes listed and read once, the catalogue updated.</summary>
    private void Refresh()
    {
        _rounded = true;
        IReadOnlyList<IVolume> volumes;
        try
        {
            volumes = _lister.List();
            if (_listFailed)
                _log.Information("Drives: the volumes are listed again");
            _listFailed = false;
        }
        catch (Exception ex)
        {
            if (!_listFailed)
                _log.Warning(ex, "Drives: listing the volumes failed: {Message}", ex.Message);
            _listFailed = true;
            _read = null;
            return;
        }

        var read = new Dictionary<string, Volume>(StringComparer.Ordinal);
        var listed = new List<Volume>();
        var failing = new HashSet<string>(StringComparer.Ordinal);
        var clashing = new HashSet<string>(StringComparer.Ordinal);
        var tooLong = new HashSet<string>(StringComparer.Ordinal);
        var tooMany = false;
        foreach (var v in volumes)
        {
            if (v == null)
                continue;

            // At the cap, the volumes after it are left out unread: one that doesn't answer can't hold up the round.
            if (listed.Count == MaxVolumes)
            {
                tooMany = true;
                if (!_tooMany)
                    _log.Warning("Drives: at most {Max} volumes are listed; the rest are left out unread", MaxVolumes);
                break;
            }

            string? name = null;
            Volume volume;
            try
            {
                name = v.Name;
                if (name == null || !DriveSource.Includes(_platform, name, v) || !v.IsReady)
                    continue;
                volume = ReadVolume(v, name, _read);
            }
            catch (Exception ex)
            {
                // Left out this round, as a volume that went: its readings fail, then go.
                var which = name ?? "";
                failing.Add(which);
                if (!_failing.Contains(which))
                    _log.Warning(ex, "Drives: reading the volume {Volume} failed: {Message}", name ?? "(no name)", ex.Message);
                continue;
            }

            if (DriveSource.KeyOf(volume.Drive, LongestEntry).ToString().Length > MaxKeyChars)
            {
                tooLong.Add(volume.Drive);
                if (!_tooLong.Contains(volume.Drive))
                    _log.Warning("Drives: the volume {Volume} is left out: its keys would be longer than {Max} characters", volume.Texts[0], MaxKeyChars);
                continue;
            }

            if (read.ContainsKey(volume.Drive))
            {
                clashing.Add(volume.Drive);
                if (!_clashing.Contains(volume.Drive))
                    _log.Warning("Drives: the volume {Volume} has the drive id '{Drive}' of a volume listed before it, and is left out", volume.Texts[0], volume.Drive);
                continue;
            }

            read.Add(volume.Drive, volume);
            listed.Add(volume);
        }

        _failing = failing;
        _clashing = clashing;
        _tooLong = tooLong;
        _tooMany = tooMany;
        _read = read;
        Publish(listed);
    }

    /// <summary>A ready volume's texts and sizes, as the 1.x plugin read them.</summary>
    /// <remarks>A text unchanged since <paramref name="last"/>'s round is handed back as the same string.</remarks>
    private static Volume ReadVolume(IVolume v, string name, Dictionary<string, Volume>? last)
    {
        var drive = DriveSource.ContainerOf(name);
        var shown = Cut(name.TrimEnd('\\'));
        var label = Cut(v.VolumeLabel ?? "");
        var texts = new[] { shown, v.DriveType.ToString(), label, Cut(v.DriveFormat ?? "") };
        long total = v.TotalSize, free = v.TotalFreeSpace, available = v.AvailableFreeSpace;
        // Whole MiB, as 1.x (long division); used is the total less the free space, then in MiB.
        double[] sizes = [total / Mebibyte, free / Mebibyte, available / Mebibyte, (total - free) / Mebibyte];
        if (last != null && last.TryGetValue(drive, out var before))
        {
            for (var i = 0; i < texts.Length; i++)
            {
                if (texts[i] == before.Texts[i])
                    texts[i] = before.Texts[i];
            }
        }

        var group = label.Length > 0 && label != shown ? $"{shown} ({label})" : shown;
        return new Volume(drive, group, texts, sizes);
    }

    private static string Cut(string text) =>
        text.Length <= MaxTextChars ? text : text[..(char.IsHighSurrogate(text[MaxTextChars - 1]) ? MaxTextChars - 1 : MaxTextChars)];

    private void Publish(List<Volume> volumes)
    {
        var definitions = new List<SensorDefinition>(volumes.Count * DriveSource.Entries.Count);
        foreach (var volume in volumes)
        {
            foreach (var (id, name, kind) in DriveSource.Entries)
                definitions.Add(new SensorDefinition($"{volume.Drive}/{id}", volume.Group, name, kind == SensorKind.Number ? DriveSource.SizeUnit : "", kind));
        }

        if (definitions.SequenceEqual(_definitions))
            return;
        _definitions = [.. definitions];
        _version++;
    }
}
