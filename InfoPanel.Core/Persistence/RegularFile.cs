using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.Persistence;

/// <summary>Something other than a regular file (a FIFO, a socket, a device) is where the engine reads one; it wasn't read.</summary>
public sealed class NotARegularFileException(string path, string kind)
    : IOException($"{path} is {kind}, not a regular file, so it isn't opened.");

/// <summary>
/// A link where the engine reads or lists whose target can't be reached (a link to nothing, a drive that isn't there,
/// links that loop): what it stands for is unknown, so it is never read as empty or as absent.
/// </summary>
public sealed class UnresolvedLinkException(string path)
    : IOException($"{path} is a link whose target can't be reached (a link to nothing, a drive that isn't there, or links that loop), so what it holds can't be told.");

/// <summary>A listing of the data folder went past its budget (<see cref="ListingBudget"/>): what lies beyond wasn't looked at.</summary>
public sealed class ListingBudgetException(string message) : IOException(message);

/// <summary>Another process holds the lock the engine takes (<see cref="RegularFile.OpenLock"/>).</summary>
public sealed class LockHeldException(string path, Exception? inner = null)
    : IOException($"{path} is locked by another process.", inner);

/// <summary>What is at a path, as <see cref="RegularFile.StateOf"/> tells it.</summary>
public enum PathState
{
    /// <summary>Nothing: no file, no folder, no link.</summary>
    Absent,

    /// <summary>A folder, or a link to one.</summary>
    Folder,

    /// <summary>Anything else that can be reached: a file (regular or not), or a link to one.</summary>
    Other,

    /// <summary>A link whose target can't be reached (<see cref="UnresolvedLinkException"/>).</summary>
    Unresolved,
}

/// <summary>
/// How many entries a listing of the data folder may look at (<see cref="RegularFile.List"/>), shared by every listing
/// it is passed to, so one walk is bounded however its folders are laid out or linked.
/// </summary>
public sealed class ListingBudget(int entries, string what)
{
    /// <summary>The entries it allows.</summary>
    public int Entries { get; } = entries;

    /// <summary>The entries looked at so far.</summary>
    public int Seen { get; private set; }

    /// <summary>What it bounds, for the message when it runs out.</summary>
    public string What { get; } = what;

    /// <exception cref="ListingBudgetException">It is spent.</exception>
    internal void Take(string path)
    {
        if (++Seen > Entries)
            throw new ListingBudgetException($"{What} looked at more than {Entries} entries, so it stopped at {path}.");
    }
}

/// <summary>
/// The one way the engine opens and lists what is in its data folder (docs/2.0/data-folder.md, "Special files and
/// links"), at start-up, in the blob sweep and after:
/// <list type="bullet">
/// <item>Only regular files are opened, without waiting: on macOS and Linux the file is opened non-blocking and its type
/// checked on that open handle, so nothing can swap it between the check and the read (a FIFO's plain open waits for the
/// other end); on Windows the name is taken literally (<c>\\?\</c>, so a file named <c>NUL</c> or ending in a dot is that
/// file, never a device) and the handle's type checked before anything is read.</item>
/// <item>A link is followed, but a link whose target can't be reached is never "nothing there": <see cref="StateOf"/>
/// says <see cref="PathState.Unresolved"/>, and a read or a listing of it throws <see cref="UnresolvedLinkException"/>.
/// Only a path with nothing at all is absent.</item>
/// <item>Every listing is bounded (<see cref="ListingBudget"/>); a listing never reads a folder it can't list, or
/// something that isn't a folder, as empty.</item>
/// </list>
/// </summary>
public static class RegularFile
{
    /// <summary>The most entries one listing of a data folder's folder looks at, when its caller sets no tighter budget.</summary>
    public const int MaxListing = 1_000_000;

    /// <summary>Opens <paramref name="path"/> to read, when it is a regular file.</summary>
    /// <param name="share">The sharing Windows applies; Unix has none to apply to a read.</param>
    /// <exception cref="NotARegularFileException">A FIFO, socket or device is at the path; it wasn't read.</exception>
    /// <exception cref="UnresolvedLinkException">A link is at the path whose target can't be reached.</exception>
    /// <exception cref="FileNotFoundException">Nothing is at the path.</exception>
    /// <exception cref="DirectoryNotFoundException">A folder on the way to it isn't there.</exception>
    /// <exception cref="UnauthorizedAccessException">It may not be read, or it is a folder.</exception>
    /// <exception cref="IOException">It couldn't be opened.</exception>
    public static FileStream OpenRead(string path, FileShare share = FileShare.Read)
    {
        if (!IsUnix)
        {
            var opened = OpenWindows(path, FileMode.Open, FileAccess.Read, share);
            return new FileStream(opened, FileAccess.Read, bufferSize: 0);
        }

        var fd = Posix.Open(path, Posix.O_RDONLY | Posix.Common);
        if (fd == -1)
            throw OpenFailed(path, Marshal.GetLastPInvokeError());
        return new FileStream(Verified(fd, path), FileAccess.Read, bufferSize: 0);
    }

    /// <summary>
    /// Opens <paramref name="path"/> to read and then flush to disk (<see cref="InfoPanel.Documents.AtomicFile.FlushFile"/>), when it is a
    /// regular file, as <see cref="OpenRead"/>; nothing is written through it. On Windows the handle may write too, since a
    /// file is flushed only through such a handle; on macOS and Linux it is read-only.
    /// </summary>
    /// <exception cref="NotARegularFileException">A FIFO, socket or device is at the path; it wasn't read.</exception>
    /// <exception cref="IOException">As <see cref="OpenRead"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">As <see cref="OpenRead"/>, or on Windows it may not be written.</exception>
    internal static FileStream OpenToFlush(string path)
    {
        if (IsUnix)
            return OpenRead(path);
        return new FileStream(OpenWindows(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read), FileAccess.ReadWrite, bufferSize: 0);
    }

    /// <summary>Reads <paramref name="path"/> whole, a regular file only (<see cref="OpenRead"/>), reading at most <paramref name="maxBytes"/> + 1 bytes of it.</summary>
    /// <exception cref="InvalidDataException">The file is larger than <paramref name="maxBytes"/>.</exception>
    /// <exception cref="IOException">As <see cref="OpenRead"/>, or the read failed.</exception>
    /// <exception cref="UnauthorizedAccessException">As <see cref="OpenRead"/>.</exception>
    public static byte[] ReadAtMost(string path, int maxBytes)
    {
        using var stream = OpenRead(path);
        using var content = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes + 1L - content.Length))) > 0)
        {
            content.Write(chunk, 0, read);
            if (content.Length > maxBytes)
                throw new InvalidDataException($"{path} is larger than {maxBytes} bytes.");
        }

        return content.ToArray();
    }

    /// <summary>
    /// Opens <paramref name="path"/> to append to, a regular file only, created (owner read and write) when nothing is
    /// there; the stream is at the file's end. A FIFO, socket or device there is refused without waiting on it.
    /// </summary>
    /// <exception cref="NotARegularFileException">A FIFO, socket or device is at the path.</exception>
    /// <exception cref="UnresolvedLinkException">A link is at the path whose target can't be reached; nothing was created.</exception>
    /// <exception cref="IOException">It couldn't be opened or created.</exception>
    /// <exception cref="UnauthorizedAccessException">It may not be written, or it is a folder.</exception>
    public static FileStream OpenAppend(string path)
    {
        var stream = OpenOrCreate(path, FileAccess.Write, FileShare.Read);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    /// <summary>
    /// Opens <paramref name="path"/> to hold as a lock for as long as the stream is open: a regular file only, created
    /// (owner read and write) when nothing is there, and locked exclusively (on macOS and Linux an advisory
    /// <c>flock</c>, which every .NET process honours; on Windows the open shares nothing).
    /// </summary>
    /// <exception cref="LockHeldException">Another process holds the lock.</exception>
    /// <exception cref="NotARegularFileException">A FIFO, socket or device is at the path.</exception>
    /// <exception cref="UnresolvedLinkException">A link is at the path whose target can't be reached; nothing was created.</exception>
    /// <exception cref="IOException">It couldn't be opened or created.</exception>
    /// <exception cref="UnauthorizedAccessException">It may not be opened, or it is a folder.</exception>
    public static FileStream OpenLock(string path)
    {
        if (!IsUnix)
        {
            try
            {
                return OpenOrCreate(path, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (ex is not (NotARegularFileException or UnresolvedLinkException) && (ex.HResult & 0xFFFF) is 32 or 33)
            {
                throw new LockHeldException(path, ex); // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            }
        }

        // A new file is created with FileShare.None, which .NET backs with flock(LOCK_EX | LOCK_NB) itself; an existing
        // one is opened here, its type checked, and locked the same way.
        var stream = OpenOrCreate(path, FileAccess.ReadWrite, FileShare.None, out var created);
        if (created)
            return stream;
        if (Posix.flock(stream.SafeFileHandle, Posix.LOCK_EX | Posix.LOCK_NB) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            // As .NET's own: only "it is held" matters; a file system without locks (ENOTSUP, ENOLCK) is used unlocked.
            if (errno == Posix.EWOULDBLOCK)
            {
                stream.Dispose();
                throw new LockHeldException(path);
            }
        }

        return stream;
    }

    /// <summary>
    /// What is at <paramref name="path"/>: nothing, a folder (or a link to one), anything else that can be reached, or a
    /// link whose target can't be (<see cref="PathState.Unresolved"/>). Only a path with nothing at all, not even a link,
    /// is <see cref="PathState.Absent"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">What is there can't be told.</exception>
    /// <exception cref="IOException">What is there can't be told.</exception>
    public static PathState StateOf(string path)
    {
        try
        {
            // Not followed when the link leads nowhere: an entry of any kind is found.
            File.GetAttributes(Literal(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathState.Absent;
        }

        if (Directory.Exists(path))
            return PathState.Folder;
        var entry = new FileInfo(path);
        if (entry.LinkTarget == null)
            return PathState.Other;
        try
        {
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            return target != null && (File.Exists(target.FullName) || Directory.Exists(target.FullName)) ? PathState.Other : PathState.Unresolved;
        }
        catch (IOException)
        {
            return PathState.Unresolved; // links that loop
        }
    }

    /// <summary>
    /// The entries of the folder at <paramref name="path"/>, each counted against <paramref name="budget"/> as it is
    /// listed; none when nothing is at the path. Links among them are listed as they are (a link to a folder as a
    /// <see cref="DirectoryInfo"/>, any other as a <see cref="FileInfo"/>), not followed.
    /// </summary>
    /// <exception cref="UnresolvedLinkException">The path is a link whose target can't be reached.</exception>
    /// <exception cref="ListingBudgetException">The budget ran out; the entries before it were listed.</exception>
    /// <exception cref="IOException">Something other than a folder is at the path, or the folder can't be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder may not be listed.</exception>
    public static IEnumerable<FileSystemInfo> List(string path, ListingBudget budget)
    {
        switch (StateOf(path))
        {
            case PathState.Absent:
                yield break;
            case PathState.Unresolved:
                throw new UnresolvedLinkException(path);
            case PathState.Other:
                throw new IOException($"{path} isn't a folder, so it can't be listed.");
        }

        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            budget.Take(entry.FullName);
            yield return entry;
        }
    }

    /// <summary>
    /// <paramref name="path"/> as Windows names it literally (<c>\\?\C:\…</c>, <c>\\?\UNC\server\share\…</c>): no device
    /// names, no trimmed dots or spaces, nothing rewritten. Elsewhere, and for a path already in that form or a relative
    /// one, as it is.
    /// </summary>
    internal static string Literal(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;
        var full = path.Replace('/', '\\');
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal) || !Path.IsPathFullyQualified(full))
            return path;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    private static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>A regular file opened at <paramref name="path"/>, or one created there when nothing is there.</summary>
    private static FileStream OpenOrCreate(string path, FileAccess access, FileShare share) => OpenOrCreate(path, access, share, out _);

    private static FileStream OpenOrCreate(string path, FileAccess access, FileShare share, out bool created)
    {
        for (var attempt = 0; ; attempt++)
        {
            created = false;
            if (IsUnix)
            {
                var fd = Posix.Open(path, (access == FileAccess.Write ? Posix.O_WRONLY | Posix.O_APPEND : Posix.O_RDWR) | Posix.Common);
                if (fd != -1)
                    return new FileStream(Verified(fd, path), access, bufferSize: 0);
                var errno = Marshal.GetLastPInvokeError();
                if (errno != Posix.ENOENT || StateOf(path) != PathState.Absent)
                    throw OpenFailed(path, errno);
            }
            else if (StateOf(path) is PathState.Folder or PathState.Other)
            {
                return new FileStream(OpenWindows(path, FileMode.Open, access, share), access, bufferSize: 0);
            }
            else if (StateOf(path) == PathState.Unresolved)
            {
                throw new UnresolvedLinkException(path);
            }

            // Nothing is there: created exclusively, so whatever appears meanwhile is never opened through this.
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = access, Share = share, BufferSize = 0 };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                created = true;
                return new FileStream(Literal(path), options);
            }
            catch (IOException) when (attempt == 0 && StateOf(path) != PathState.Absent)
            {
                // Something appeared at the path since: open that, once.
            }
        }
    }

    /// <summary>Windows: <paramref name="path"/> opened by its literal name, the handle's type checked (a disk file) before it is used.</summary>
    private static SafeFileHandle OpenWindows(string path, FileMode mode, FileAccess access, FileShare share)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(Literal(path), mode, access, share);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && StateOf(path) == PathState.Unresolved)
        {
            throw new UnresolvedLinkException(path);
        }

        try
        {
            if (Windows.GetFileType(handle) != Windows.FILE_TYPE_DISK)
                throw new NotARegularFileException(path, "a device");
            if ((File.GetAttributes(handle) & FileAttributes.Directory) != 0)
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied: it is a folder.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>macOS and Linux: <paramref name="fd"/> as a handle once its type, asked on that descriptor, is a regular file; closed otherwise.</summary>
    private static SafeFileHandle Verified(int fd, string path)
    {
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            switch (Posix.TypeOf(fd))
            {
                case Posix.S_IFREG or null: // null: the type can't be asked here; nothing on it can wait, being non-blocking
                    return handle;
                case Posix.S_IFDIR:
                    throw new UnauthorizedAccessException($"Access to the path '{path}' is denied: it is a folder.");
                case var kind:
                    throw new NotARegularFileException(path, kind switch
                    {
                        Posix.S_IFIFO => "a FIFO",
                        Posix.S_IFSOCK => "a socket",
                        Posix.S_IFCHR or Posix.S_IFBLK => "a device",
                        _ => "a special file",
                    });
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static Exception OpenFailed(string path, int errno)
    {
        var message = $"Could not open {path}: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).";
        if (errno == Posix.ELOOP)
            return new UnresolvedLinkException(path);
        return errno switch
        {
            // Nothing at the path, or a link there to nothing: only the first is "not found".
            Posix.ENOENT when StateOf(path) == PathState.Unresolved => new UnresolvedLinkException(path),
            Posix.ENOENT when Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))) => new FileNotFoundException(message, path),
            Posix.ENOENT or Posix.ENOTDIR => new DirectoryNotFoundException(message),
            Posix.EACCES or Posix.EPERM => new UnauthorizedAccessException(message),
            // A FIFO with nothing reading it, or a device with nothing behind it, refused by the non-blocking open.
            Posix.ENXIO => new NotARegularFileException(path, "a FIFO or a device"),
            _ => new IOException(message),
        };
    }

    private static class Windows
    {
        public const int FILE_TYPE_DISK = 0x0001;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int GetFileType(SafeFileHandle handle);
    }

    private static class Posix
    {
        public const int O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2;
        public static int O_APPEND => OperatingSystem.IsMacOS() ? 0x0008 : 0x0400;
        public static int O_NONBLOCK => OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
        public static int O_NOCTTY => OperatingSystem.IsMacOS() ? 0x20000 : 0x0100;
        public static int O_CLOEXEC => OperatingSystem.IsMacOS() ? 0x01000000 : 0x80000;

        /// <summary>Every open here: non-blocking, never the process's terminal, closed on exec.</summary>
        public static int Common => O_NONBLOCK | O_NOCTTY | O_CLOEXEC;

        public const int EPERM = 1, ENOENT = 2, EINTR = 4, ENXIO = 6, EACCES = 13, ENOTDIR = 20;
        public static int ELOOP => OperatingSystem.IsMacOS() ? 62 : 40;
        public static int EWOULDBLOCK => OperatingSystem.IsMacOS() ? 35 : 11;

        public const int LOCK_EX = 2, LOCK_NB = 4;

        // The file types in st_mode, the same on macOS and Linux.
        public const int S_IFMT = 0xF000, S_IFIFO = 0x1000, S_IFCHR = 0x2000, S_IFDIR = 0x4000, S_IFBLK = 0x6000, S_IFREG = 0x8000, S_IFSOCK = 0xC000;

        /// <summary><c>open(path, flags)</c>, again when a signal interrupts it; -1 with the error otherwise.</summary>
        public static int Open(string path, int flags)
        {
            int fd;
            do
            {
                fd = open(path, flags);
            }
            while (fd == -1 && Marshal.GetLastPInvokeError() == EINTR);
            return fd;
        }

        /// <summary>
        /// The type bits of the open file's mode, or null when this system can't be asked. macOS: <c>fstat</c>, whose
        /// <c>st_mode</c> (16 bits) is at offset 4 of the 64-bit-inode <c>struct stat</c> (<c>fstat$INODE64</c> on x64).
        /// Linux: <c>statx</c> on the descriptor, whose <c>stx_mode</c> (16 bits) is at offset 28 on every architecture.
        /// </summary>
        public static int? TypeOf(int fd)
        {
            var buffer = new byte[512];
            int result;
            try
            {
                if (OperatingSystem.IsMacOS())
                    result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstat_inode64(fd, buffer) : fstat(fd, buffer);
                else
                    result = statx(fd, "", AT_EMPTY_PATH, STATX_TYPE, buffer);
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }

            if (result != 0)
                throw new IOException($"Could not tell what kind of file is open (errno {Marshal.GetLastPInvokeError()}).");
            return BitConverter.ToUInt16(buffer, OperatingSystem.IsMacOS() ? 4 : 28) & S_IFMT;
        }

        private const int AT_EMPTY_PATH = 0x1000;
        private const uint STATX_TYPE = 0x1;

        // Two arguments only: open is variadic, and nothing here creates a file through it (no mode is passed).
        [DllImport("libc", SetLastError = true)]
        private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int flock(SafeFileHandle fd, int operation);

        [DllImport("libc", SetLastError = true)]
        private static extern int fstat(int fd, [Out] byte[] buffer);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int fstat_inode64(int fd, [Out] byte[] buffer);

        [DllImport("libc", SetLastError = true)]
        private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
    }
}
