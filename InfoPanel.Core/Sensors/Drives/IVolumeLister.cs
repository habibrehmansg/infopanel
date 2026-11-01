using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace InfoPanel.Sensors.Drives;

/// <summary>
/// One volume as the operating system lists it, read as <see cref="DriveInfo"/> reads it: each property is read when
/// asked and may throw (a drive that went, a share that doesn't answer).
/// </summary>
public interface IVolume
{
    /// <summary>Its root: <c>C:\</c> on Windows, its mount point (<c>/</c>, <c>/Volumes/Backup</c>) elsewhere.</summary>
    string Name { get; }

    DriveType DriveType { get; }

    bool IsReady { get; }

    string VolumeLabel { get; }

    string DriveFormat { get; }

    long TotalSize { get; }

    long TotalFreeSpace { get; }

    /// <summary>The free space this user may use (quotas).</summary>
    long AvailableFreeSpace { get; }

    /// <summary>
    /// Whether the system mounted it not to be shown: on macOS with <c>MNT_DONTBROWSE</c> (<c>nobrowse</c>, as the
    /// Recovery volume and the system's own), read with the listing; false elsewhere.
    /// </summary>
    bool IsHidden => false;
}

/// <summary>Lists the volumes the operating system has now, in its order.</summary>
public interface IVolumeLister
{
    IReadOnlyList<IVolume> List();
}

/// <summary>
/// The volumes .NET lists (<see cref="DriveInfo.GetDrives"/>), each read through its <see cref="DriveInfo"/>; on macOS
/// with their mount flags (<see cref="MacMounts"/>), read after the listing.
/// </summary>
public sealed class SystemVolumeLister : IVolumeLister
{
    public IReadOnlyList<IVolume> List()
    {
        var drives = DriveInfo.GetDrives();
        var hidden = OperatingSystem.IsMacOS() ? MacMounts.Hidden() : null;
        return drives.Select(d => (IVolume)new SystemVolume(d, hidden?.Contains(d.Name) == true)).ToArray();
    }

    private sealed class SystemVolume(DriveInfo drive, bool hidden) : IVolume
    {
        public bool IsHidden => hidden;
        public string Name => drive.Name;
        public DriveType DriveType => drive.DriveType;
        public bool IsReady => drive.IsReady;
        public string VolumeLabel => drive.VolumeLabel;
        public string DriveFormat => drive.DriveFormat;
        public long TotalSize => drive.TotalSize;
        public long TotalFreeSpace => drive.TotalFreeSpace;
        public long AvailableFreeSpace => drive.AvailableFreeSpace;
    }
}

/// <summary>
/// macOS: the mount points mounted not to be shown (<c>MNT_DONTBROWSE</c>), from <c>getfsstat</c> with
/// <c>MNT_NOWAIT</c>, the kernel's list as it stands (no file system is asked, so a share that doesn't answer doesn't
/// hold it up), into a buffer of its own (<c>getmntinfo</c>'s is shared by the process).
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacMounts
{
    private const uint MNT_DONTBROWSE = 0x00100000;
    private const int MNT_NOWAIT = 2;

    // The 64-bit-inode struct statfs (getfsstat$INODE64 on x64, the only one on arm64): 2,168 bytes, f_flags (32 bits)
    // at 64, f_mntonname (MAXPATHLEN, 1,024 bytes, NUL-terminated) at 88.
    private const int StatfsSize = 2168;
    private const int FlagsOffset = 64;
    private const int MountOnOffset = 88;
    private const int MountOnLength = 1024;

    /// <summary>The mount points mounted <c>nobrowse</c>, as the kernel spells them (and .NET lists them).</summary>
    /// <exception cref="IOException">The kernel's list can't be read.</exception>
    public static HashSet<string> Hidden()
    {
        for (var attempt = 0; ; attempt++)
        {
            var count = GetFsStat(null, 0);
            // Room for a few mounted meanwhile; a full buffer may have missed some, so it is read again.
            var capacity = count + 8;
            var buffer = new byte[capacity * StatfsSize];
            int got;
            fixed (byte* p = buffer)
                got = GetFsStat(p, buffer.Length);
            if (got == capacity && attempt < 3)
                continue;

            var hidden = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < got; i++)
            {
                var entry = buffer.AsSpan(i * StatfsSize, StatfsSize);
                if ((BinaryPrimitives.ReadUInt32LittleEndian(entry[FlagsOffset..]) & MNT_DONTBROWSE) == 0)
                    continue;
                var name = entry.Slice(MountOnOffset, MountOnLength);
                var end = name.IndexOf((byte)0);
                hidden.Add(Encoding.UTF8.GetString(end < 0 ? name : name[..end]));
            }

            return hidden;
        }
    }

    private static int GetFsStat(byte* buffer, int bytes)
    {
        var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? getfsstat_inode64(buffer, bytes, MNT_NOWAIT)
            : getfsstat(buffer, bytes, MNT_NOWAIT);
        if (result < 0)
            throw new IOException($"Could not read the mount flags: getfsstat failed (errno {Marshal.GetLastPInvokeError()}).");
        return result;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getfsstat(byte* buf, int bufsize, int mode);

    [DllImport("libc", EntryPoint = "getfsstat$INODE64", SetLastError = true)]
    private static extern int getfsstat_inode64(byte* buf, int bufsize, int mode);
}
