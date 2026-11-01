using InfoPanel.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace InfoPanel.Sensors.Drives;

/// <summary>Which operating system's volumes a <see cref="DriveSampler"/> lists (<see cref="DriveSource.Includes"/>).</summary>
public enum VolumePlatform : byte
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>
/// The drives source, <c>infopanel.drives</c> ("Drives"): a <see cref="PolledSensorSource"/> over
/// <see cref="DriveSampler"/>, polled every 2 s (the 1.x Drive Info plugin's interval), the volumes followed as they
/// come and go. Each volume lists the 1.x plugin's entries (<see cref="Entries"/>) under
/// <c>plugin:/infopanel.drives/{drive}/{entry}</c>, its drive id the 1.x plugin SDK's id of its name
/// (<see cref="ContainerOf"/>: <c>C:\</c> → <c>c</c>, <c>/</c> → empty, <c>/Volumes/My Disk</c> →
/// <c>volumesmy-disk</c>), so a 1.x import binds the plugin's keys to these (<see cref="RetiredPlugins"/>). The engine
/// never answers the plugin's keys.
/// </summary>
public static partial class DriveSource
{
    public const string SourceId = "infopanel.drives";

    /// <summary>The source's name.</summary>
    public const string Name = "Drives";

    /// <summary>The unit of the sizes: whole MiB, as 1.x (bytes ÷ 1,048,576, rounded down).</summary>
    public const string SizeUnit = "MB";

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private static readonly SensorKeySpace Space = SensorKeySpace.Plugin(SourceId);

    /// <summary>Each volume's entries in the order it lists them (the 1.x plugin's): id, name and kind; the numbers in <see cref="SizeUnit"/>.</summary>
    public static IReadOnlyList<(string Id, string Name, SensorKind Kind)> Entries { get; } =
    [
        ("name", "Name", SensorKind.Text),
        ("type", "Type", SensorKind.Text),
        ("volume_label", "Volume Label", SensorKind.Text),
        ("format", "Format", SensorKind.Text),
        ("total_size", "Total Size", SensorKind.Number),
        ("free_space", "Free Space", SensorKind.Number),
        ("available_space", "Available Space", SensorKind.Number),
        ("used_space", "Used Space", SensorKind.Number),
    ];

    /// <summary>The key of the entry <paramref name="entryId"/> of the drive <paramref name="drive"/>: <c>plugin:/infopanel.drives/{drive}/{entryId}</c>.</summary>
    public static SensorKey KeyOf(string drive, string entryId) => Space.KeyOf(drive + "/" + entryId);

    /// <summary>The name of the entry <paramref name="entryId"/> (case as listed), or null when a volume has no such entry.</summary>
    public static string? EntryName(string entryId)
    {
        foreach (var (id, name, _) in Entries)
        {
            if (string.Equals(id, entryId, StringComparison.Ordinal))
                return name;
        }

        return null;
    }

    /// <summary>
    /// A volume's drive id: the 1.x plugin SDK's id (its container's, <see cref="Slug"/>) of the volume's name with
    /// trailing <c>\</c> trimmed, as the 1.x Drive Info plugin made it.
    /// </summary>
    public static string ContainerOf(string volumeName) => Slug(volumeName.TrimEnd('\\'));

    /// <summary>
    /// Whether <paramref name="drive"/> can be a drive id: only <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c> and <c>-</c>, as
    /// <see cref="Slug"/> makes them (empty included: the root on macOS and Linux).
    /// </summary>
    public static bool IsDriveId(string drive)
    {
        foreach (var c in drive)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The 1.x plugin SDK's id of a name (<c>InfoPanel.Plugins.IdUtil.Encode</c>, ported as it is): accents dropped,
    /// letters, digits and white space kept, lower case, white space runs to <c>-</c>, <c>-</c> trimmed at the ends, then
    /// everything but <c>a</c>–<c>z</c>, <c>0</c>–<c>9</c> and <c>-</c> dropped.
    /// </summary>
    public static string Slug(string input)
    {
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c)))
                sb.Append(c);
        }

        var cleaned = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var dashed = WhitespaceRegex().Replace(cleaned, "-").Trim('-');
        return AlphaNumericRegex().Replace(dashed, "");
    }

    [GeneratedRegex(@"[^a-z0-9-]", RegexOptions.IgnoreCase)]
    private static partial Regex AlphaNumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// The file systems a volume on Linux is listed with (its <see cref="IVolume.DriveFormat"/>, the type
    /// <c>/proc/self/mountinfo</c> names): disks, sticks, discs and network shares. An allow-list, not a deny-list:
    /// the kernel's pseudo file systems are many and open-ended, and .NET types many of them as fixed (<c>efivarfs</c>,
    /// <c>tracefs</c>, <c>nsfs</c>, <c>squashfs</c>, <c>overlay</c>) or network (<c>autofs</c>), so a new one would show
    /// up as a drive; a real one missing here only goes unlisted.
    /// </summary>
    public static IReadOnlySet<string> LinuxFileSystems { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "bcachefs", "f2fs", "jfs", "reiserfs", "nilfs2", "zfs",
        "vfat", "msdos", "exfat", "ntfs", "ntfs3", "ntfs-3g", "fuseblk", "hfs", "hfsplus", "apfs",
        "iso9660", "udf",
        "nfs", "nfs4", "cifs", "smb3", "ceph", "glusterfs",
        // FUSE storage, as the mount type spells it (fuse.<program>): not the plain "fuse" or the desktop's own
        // (fuse.gvfsd-fuse, fuse.portal), which hold no storage of their own.
        "fuse.sshfs", "fuse.rclone", "fuse.ntfs-3g", "fuse.s3fs", "fuse.mergerfs",
    };

    /// <summary>
    /// Whether a volume is listed on <paramref name="platform"/>: on Windows every one (that is ready, as 1.x); on macOS
    /// the root and the volumes mounted under <c>/Volumes/</c> but those mounted not to be shown
    /// (<see cref="IVolume.IsHidden"/>, <c>nobrowse</c>, as the Recovery volume) and dot-named ones such as Time
    /// Machine's <c>/Volumes/.timemachine</c> (never the system's own, <c>/System/Volumes/…</c>, <c>/dev</c> and the
    /// like); on Linux those of a real file system (<see cref="LinuxFileSystems"/>) that .NET doesn't type as memory or
    /// as having no root (not <c>proc</c>, <c>sysfs</c>, <c>tmpfs</c>, <c>efivarfs</c>, <c>tracefs</c> and the like).
    /// Only what the platform needs is read from <paramref name="volume"/>.
    /// </summary>
    public static bool Includes(VolumePlatform platform, string name, IVolume volume) => platform switch
    {
        VolumePlatform.Windows => true,
        VolumePlatform.MacOS => name == "/" || (name.StartsWith("/Volumes/", StringComparison.Ordinal) && name.Length > "/Volumes/".Length && name["/Volumes/".Length] != '.' && !volume.IsHidden),
        _ => volume.DriveType is not (DriveType.Ram or DriveType.NoRootDirectory) && LinuxFileSystems.Contains(volume.DriveFormat ?? ""),
    };

    /// <summary>This computer's platform.</summary>
    public static VolumePlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? VolumePlatform.Windows
        : OperatingSystem.IsMacOS() ? VolumePlatform.MacOS
        : VolumePlatform.Linux;

    /// <summary>A source over <paramref name="lister"/> (a test's) as on <paramref name="platform"/>.</summary>
    public static PolledSensorSource Create(IVolumeLister lister, VolumePlatform platform, TimeProvider? time = null, PolledSensorSourceOptions? options = null, ILogger? log = null) =>
        new(SourceId, () => new DriveSampler(lister, platform, log), options ?? new PolledSensorSourceOptions { Interval = Interval }, time, log: log);

    /// <summary>The source over this computer's volumes.</summary>
    public static PolledSensorSource CreateSystem(TimeProvider? time = null) => Create(new SystemVolumeLister(), CurrentPlatform, time);
}
