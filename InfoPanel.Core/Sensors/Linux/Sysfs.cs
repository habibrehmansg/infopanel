using InfoPanel.Persistence;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace InfoPanel.Sensors.Linux;

/// <summary>What reading a sysfs file gave.</summary>
public enum SysfsRead : byte
{
    /// <summary>Read; the text is the file's.</summary>
    Ok,

    /// <summary>No such file (or folder on its way): the device or channel isn't there.</summary>
    Missing,

    /// <summary>The operating system refused to let the engine read it.</summary>
    Denied,

    /// <summary>Any other failure (a driver's I/O error, a sensor with no data now).</summary>
    Failed,
}

/// <summary>
/// The sysfs tree the Linux readers read, behind a seam: paths are relative to its root (<c>class/hwmon</c>,
/// <c>devices/platform/it87.2624</c>), use <c>/</c>, and never leave the root. <see cref="Disk"/> reads a folder
/// (<c>/sys</c>, or the one <c>INFOPANEL_LINUX_SYSFS</c> names); tests give a tree of their own. Links are resolved
/// within the root by <see cref="RealPath"/>, which the readers use to find a device's place, its owner and their
/// ancestors, as sysfs's links are relative and its folders nest as the devices do.
/// </summary>
public abstract class Sysfs
{
    /// <summary>At most this many links are followed resolving one path (a loop ends there).</summary>
    public const int MaxLinks = 40;

    /// <summary>At most this many bytes of a file are read (a sysfs attribute is at most a page).</summary>
    public const int MaxRead = 4096;

    /// <summary>
    /// The names in the folder at <paramref name="path"/> (links resolved within the root, <see cref="RealPath"/>), or none
    /// when it can't be listed or a link on the way leads out of the root.
    /// </summary>
    public abstract IReadOnlyList<string> List(string path);

    /// <summary>The target of the link at <paramref name="path"/> as it is written, or null when it is no link.</summary>
    /// <remarks>Called with paths whose folders are already resolved (<see cref="RealPath"/> walks one name at a time).</remarks>
    public abstract string? ReadLink(string path);

    /// <summary>
    /// Reads the file at <paramref name="path"/> (links resolved within the root, <see cref="RealPath"/>; one that leads out
    /// of it, the last name's included, reads as <see cref="SysfsRead.Missing"/>), at most <see cref="MaxRead"/> bytes of
    /// it; anything but a regular file (a FIFO, a device) fails at once, never waited on.
    /// </summary>
    public abstract SysfsRead Read(string path, out string text);

    /// <summary>
    /// <see cref="Read(string, out string)"/>, telling whether the file had more than what was read (the limit the tree reads
    /// at: <see cref="MaxRead"/>, <see cref="MaxProcRead"/> for <c>/proc</c>). A tree that doesn't limit reads never truncates.
    /// </summary>
    public virtual SysfsRead Read(string path, out string text, out bool truncated)
    {
        truncated = false;
        return Read(path, out text);
    }

    /// <summary>
    /// Reads a table of rows (<c>/proc/stat</c>, <c>/proc/diskstats</c>): when the file was cut by the read limit, the row
    /// it cut is left out, so only whole rows come back and <paramref name="truncated"/> says some are missing.
    /// </summary>
    internal SysfsRead ReadRows(string path, out string text, out bool truncated)
    {
        var status = Read(path, out text, out truncated);
        if (status == SysfsRead.Ok && truncated)
            text = text[..(text.LastIndexOf('\n') + 1)];
        return status;
    }

    /// <summary>A file's text trimmed, or null when it can't be read or is empty.</summary>
    public string? ReadAttribute(string path) =>
        Read(path, out var text) == SysfsRead.Ok && text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>
    /// <paramref name="path"/> with every link in it resolved, as a path from the root; null when a link's target is
    /// absolute or leads above the root, or when more than <see cref="MaxLinks"/> links are followed. The path need
    /// not exist.
    /// </summary>
    public string? RealPath(string path)
    {
        var pending = new Stack<string>();
        Push(pending, path);
        var resolved = new List<string>();
        var links = 0;
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (name.Length == 0 || name == ".")
                continue;
            if (name == "..")
            {
                if (resolved.Count == 0)
                    return null;
                resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            resolved.Add(name);
            if (ReadLink(string.Join('/', resolved)) is not { } target)
                continue;
            if (++links > MaxLinks || target.StartsWith('/'))
                return null;
            resolved.RemoveAt(resolved.Count - 1);
            Push(pending, target);
        }

        return string.Join('/', resolved);
    }

    private static void Push(Stack<string> pending, string path)
    {
        var names = path.Split('/');
        for (var i = names.Length - 1; i >= 0; i--)
            pending.Push(names[i]);
    }

    /// <summary>
    /// The tree in the folder <paramref name="root"/> (a full path): every read and listing resolves its path within the
    /// root first (<see cref="RealPath"/>), so a link out of it, a file's or a folder's, is never followed; files are
    /// opened as <see cref="RegularFile.OpenRead"/> opens them, and at most <paramref name="maxRead"/> bytes of each read
    /// (<see cref="MaxRead"/>, a sysfs attribute's page; <c>/proc</c>'s tables, read through the same seam, are longer:
    /// <see cref="MaxProcRead"/>).
    /// </summary>
    public static Sysfs Disk(string root, int maxRead = MaxRead) => new DiskSysfs(root, maxRead);

    /// <summary>
    /// At most this many bytes of a <c>/proc</c> table are read (<c>/proc/stat</c> on a machine of many cores, with its
    /// interrupt counts, or <c>/proc/diskstats</c> with many loop devices passes a page; what the readers need comes first).
    /// </summary>
    public const int MaxProcRead = 256 * 1024;

    private sealed class DiskSysfs : Sysfs
    {
        private readonly string _root;
        private readonly int _maxRead;

        public DiskSysfs(string root, int maxRead)
        {
            if (!Path.IsPathFullyQualified(root))
                throw new ArgumentException("The sysfs root is a full path.", nameof(root));
            ArgumentOutOfRangeException.ThrowIfLessThan(maxRead, 1);
            _root = root;
            _maxRead = maxRead;
        }

        private string Full(string path) => path.Length == 0 ? _root : Path.Join(_root, path);

        public override IReadOnlyList<string> List(string path)
        {
            if (RealPath(path) is not { } real)
                return [];
            try
            {
                var names = new List<string>();
                foreach (var entry in Directory.EnumerateFileSystemEntries(Full(real)))
                    names.Add(Path.GetFileName(entry));
                return names;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        public override string? ReadLink(string path)
        {
            try
            {
                return new FileInfo(Full(path)).LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public override SysfsRead Read(string path, out string text) => Read(path, out text, out _);

        public override SysfsRead Read(string path, out string text, out bool truncated)
        {
            text = "";
            truncated = false;
            if (RealPath(path) is not { } real)
                return SysfsRead.Missing;
            try
            {
                // A regular file only, opened without waiting (a FIFO's plain open waits for a writer), unbuffered: one
                // read gives a sysfs attribute whole, and the driver reads the hardware once.
                using var file = RegularFile.OpenRead(Full(real), FileShare.ReadWrite);
                byte[]? rented = _maxRead > MaxRead ? ArrayPool<byte>.Shared.Rent(_maxRead) : null;
                try
                {
                    Span<byte> buffer = rented != null ? rented.AsSpan(0, _maxRead) : stackalloc byte[MaxRead];
                    buffer = buffer[..Math.Min(buffer.Length, _maxRead)];
                    var length = 0;
                    int read;
                    while (length < buffer.Length && (read = file.Read(buffer[length..])) > 0)
                        length += read;
                    if (length == buffer.Length)
                    {
                        // The buffer is full: is there more?
                        Span<byte> more = stackalloc byte[1];
                        truncated = file.Read(more) > 0;
                    }

                    text = Encoding.UTF8.GetString(buffer[..length]);
                    return SysfsRead.Ok;
                }
                finally
                {
                    if (rented != null)
                        ArrayPool<byte>.Shared.Return(rented);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return SysfsRead.Missing;
            }
            catch (UnauthorizedAccessException)
            {
                return SysfsRead.Denied;
            }
            catch (IOException)
            {
                return SysfsRead.Failed;
            }
        }
    }
}
