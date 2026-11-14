using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.AiUsage;

/// <summary>Which file a path holds, whatever its name: its device and inode (Windows: its volume and file index); 0, 0 when unknown.</summary>
internal readonly record struct FileId(ulong Device, ulong Index);

/// <summary>A regular file's length, last write time and identity, as its open handle tells them.</summary>
internal readonly record struct FileStamp(long Length, DateTimeOffset LastWriteUtc, FileId Id = default);

/// <summary>A regular file open to read, with its stamp as of the open.</summary>
internal sealed class UsageFile(Stream stream, FileStamp stamp) : IDisposable
{
    public Stream Stream { get; } = stream;

    public FileStamp Stamp { get; } = stamp;

    /// <summary>The file from its start, at most <paramref name="maxBytes"/> of it; null when it holds more (no more than one byte past the limit is read).</summary>
    public byte[]? ReadAtMost(int maxBytes)
    {
        using var content = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = Stream.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes + 1L - content.Length))) > 0)
        {
            content.Write(chunk, 0, read);
            if (content.Length > maxBytes)
                return null;
        }
        return content.ToArray();
    }

    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// A folder's listing: the paths found and whether it stopped before the folder's end (a limit on matches or entries
/// reached, or told to stop), so the folder may hold more than it gives.
/// </summary>
internal readonly record struct Listing(IReadOnlyList<string> Paths, bool Truncated)
{
    public static readonly Listing None = new([], false);
}

/// <summary>A file a listing found, with its length and last write time as the listing tells them (the file wasn't opened).</summary>
internal readonly record struct ListedFile(string Path, long Length, DateTimeOffset LastWriteUtc);

/// <summary>A folder's files as <see cref="IUsageFiles.RecentFiles"/> lists them; <see cref="Truncated"/> as <see cref="Listing"/>'s.</summary>
internal readonly record struct FileListing(IReadOnlyList<ListedFile> Files, bool Truncated)
{
    public static readonly FileListing None = new([], false);
}

/// <summary>A link, or something other than a regular file (a FIFO, a socket, a device), where the plugin reads one; it wasn't read.</summary>
internal sealed class RefusedFileException(string what) : IOException($"Not read: {what}.");

/// <summary>
/// Every touch of the file system the plugin makes, so a test can give it a fixture home and see what it touched. Only
/// <see cref="DiskFiles"/> reaches the disk. A file is read only below a folder the user's setup names (its
/// <c>root</c>: a Claude folder, Codex's folder), and only when it and every folder between the root and it are no
/// link and it is a regular file (<see cref="DiskFiles"/>).
/// </summary>
internal interface IUsageFiles
{
    bool DirectoryExists(string path);

    /// <summary>
    /// The folders in <paramref name="path"/> whose names match <paramref name="pattern"/>, as full paths, from no more
    /// than <paramref name="maxEntries"/> entries looked at (files and folders alike), stopping too once
    /// <paramref name="stop"/> says so; none when it can't be listed.
    /// </summary>
    Listing Directories(string path, string pattern, int maxEntries, Func<bool>? stop = null);

    /// <summary>
    /// The files in <paramref name="path"/> whose names match <paramref name="pattern"/>, as full paths: at most
    /// <paramref name="max"/>, from no more than <paramref name="maxEntries"/> entries looked at (files and folders
    /// alike), stopping too once <paramref name="stop"/> says so; none when it can't be listed.
    /// </summary>
    Listing Files(string path, string pattern, int max, int maxEntries, Func<bool>? stop = null);

    /// <summary>
    /// <see cref="Files"/>, keeping only the files last written at or after <paramref name="since"/>, and no link, each
    /// with its length and time as the listing tells them (no file is opened).
    /// </summary>
    FileListing RecentFiles(string path, string pattern, DateTimeOffset since, int max, int maxEntries, Func<bool>? stop = null);

    /// <summary>The stamp of the regular file at <paramref name="path"/> below <paramref name="root"/>; null when it is absent, refused (<see cref="Open"/>) or can't be read.</summary>
    FileStamp? Stat(string root, string path);

    /// <summary><see cref="Stat(string, string)"/>, telling a file that may not be read (<paramref name="unreadable"/>) from one absent or refused.</summary>
    FileStamp? Stat(string root, string path, out bool unreadable);

    /// <summary>
    /// Opens the regular file at <paramref name="path"/> below <paramref name="root"/> to read, sharing it with its
    /// writer, without following a link (it, or a folder between <paramref name="root"/> and it) and without waiting.
    /// </summary>
    /// <exception cref="RefusedFileException">A link, or not a regular file.</exception>
    /// <exception cref="IOException">It couldn't be opened (absent included).</exception>
    /// <exception cref="UnauthorizedAccessException">It may not be read.</exception>
    UsageFile Open(string root, string path);
}

/// <summary>
/// The disk. On macOS and Linux a file is opened folder by folder from its root (<c>openat</c> with
/// <c>O_NOFOLLOW</c>, so neither the file nor a folder on the way can be a link, whatever changes meanwhile), the file
/// itself non-blocking (a FIFO never waits), and its type, length, time and identity asked of that open descriptor
/// (<c>fstat</c>, <c>statx</c>). On Windows the folders on the way are checked for reparse points, the file opened as
/// itself (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>) and its type and attributes asked of that handle; a folder replaced by a
/// link between its check and the open isn't caught there. A listing looks at a bounded number of entries, one at a
/// time, and never takes in a folder whole. <paramref name="onEntry"/> is told each entry a listing looks at (the
/// tests' seam).
/// </summary>
internal sealed class DiskFiles(Action<string>? onEntry = null) : IUsageFiles
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Listing Directories(string path, string pattern, int maxEntries, Func<bool>? stop = null) =>
        List(path, pattern, folders: true, int.MaxValue, maxEntries, stop);

    public Listing Files(string path, string pattern, int max, int maxEntries, Func<bool>? stop = null) =>
        List(path, pattern, folders: false, max, maxEntries, stop);

    /// <summary>
    /// Looks at <paramref name="path"/>'s entries one by one, every entry counted, and keeps the regular files (no link)
    /// whose names match, last written at or after <paramref name="since"/>, with their lengths and times; stops,
    /// truncated, as <see cref="List"/> does.
    /// </summary>
    public FileListing RecentFiles(string path, string pattern, DateTimeOffset since, int max, int maxEntries, Func<bool>? stop = null)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var found = new List<ListedFile>();
        var looked = 0;
        var sinceUtc = since.UtcDateTime;
        try
        {
            // The name first, then the type, then the time: an entry that doesn't match costs no look at its time.
            var entries = new FileSystemEnumerable<(ListedFile? File, string Path)>(path,
                (ref FileSystemEntry e) =>
                {
                    if (FileSystemName.MatchesSimpleExpression(pattern, e.FileName) && !e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        var written = e.LastWriteTimeUtc;
                        if (written >= sinceUtc)
                        {
                            var full = e.ToFullPath();
                            return (new ListedFile(full, e.Length, written), full);
                        }
                    }
                    return (null, onEntry == null ? "" : e.ToFullPath());
                },
                options);
            foreach (var (file, full) in entries)
            {
                if (found.Count >= max || looked >= maxEntries || stop?.Invoke() == true)
                    return new FileListing(found, true);
                looked++;
                onEntry?.Invoke(full);
                if (file is { } match)
                    found.Add(match);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileListing.None;
        }
        return new FileListing(found, false);
    }

    /// <summary>
    /// Looks at <paramref name="path"/>'s entries one by one, every entry counted (a file, a folder, anything else), and
    /// keeps the folders or files whose names match; stops, truncated, before an entry past <paramref name="maxMatches"/>
    /// found, <paramref name="maxEntries"/> looked at, or <paramref name="stop"/> saying so.
    /// </summary>
    private Listing List(string path, string pattern, bool folders, int maxMatches, int maxEntries, Func<bool>? stop)
    {
        // Hidden entries too (on macOS and Linux every name starting with a dot is): ~/.claude is one.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var found = new List<string>();
        var looked = 0;
        try
        {
            // The name is matched first, so an entry that doesn't match costs neither a path nor a look at its type.
            var entries = new FileSystemEnumerable<(string Path, bool Match)>(path,
                (ref FileSystemEntry e) => FileSystemName.MatchesSimpleExpression(pattern, e.FileName) && e.IsDirectory == folders
                    ? (e.ToFullPath(), true)
                    : (onEntry == null ? "" : e.ToFullPath(), false),
                options);
            foreach (var (full, match) in entries)
            {
                if (found.Count >= maxMatches || looked >= maxEntries || stop?.Invoke() == true)
                    return new Listing(found, true);
                looked++;
                onEntry?.Invoke(full);
                if (match)
                    found.Add(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Listing.None;
        }
        return new Listing(found, false);
    }

    public FileStamp? Stat(string root, string path) => Stat(root, path, out _);

    public FileStamp? Stat(string root, string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            using var file = Open(root, path);
            return file.Stamp;
        }
        catch (UnauthorizedAccessException)
        {
            unreadable = true;
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public UsageFile Open(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".." or "." or ""))
            throw new RefusedFileException("a path outside its folder");
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return OperatingSystem.IsWindows() ? Windows.Open(root, parts) : Posix.Open(root, parts);
    }

    private static class Posix
    {
        private static bool Mac => OperatingSystem.IsMacOS();
        private static bool Arm => RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

        private const int O_RDONLY = 0;
        private static int O_NONBLOCK => Mac ? 0x0004 : 0x0800;
        private static int O_NOCTTY => Mac ? 0x20000 : 0x0100;
        private static int O_CLOEXEC => Mac ? 0x01000000 : 0x80000;
        private static int O_NOFOLLOW => Mac ? 0x0100 : Arm ? 0x8000 : 0x20000;
        private static int O_DIRECTORY => Mac ? 0x100000 : Arm ? 0x4000 : 0x10000;

        private const int ENOENT = 2, EINTR = 4, ENXIO = 6, EACCES = 13, ENOTDIR = 20;
        private static int ELOOP => Mac ? 62 : 40;

        private const int S_IFMT = 0xF000, S_IFREG = 0x8000;

        public static UsageFile Open(string root, string[] parts)
        {
            // The root as the user's setup names it (it may be a link: ~/.claude often is); below it, nothing is.
            var dir = Retry(() => open(root, O_RDONLY | O_DIRECTORY | O_CLOEXEC));
            if (dir == -1)
                throw Failed(Marshal.GetLastPInvokeError(), "a folder");
            try
            {
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    var at = dir;
                    var name = parts[i];
                    var next = Retry(() => openat(at, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC));
                    if (next == -1)
                        throw Failed(Marshal.GetLastPInvokeError(), "a folder");
                    close(dir);
                    dir = next;
                }
                var last = parts[^1];
                var fd = Retry(() => openat(dir, last, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_NOCTTY | O_CLOEXEC));
                if (fd == -1)
                    throw Failed(Marshal.GetLastPInvokeError(), "the file");
                var handle = new SafeFileHandle(fd, ownsHandle: true);
                try
                {
                    var stamp = StampOf(fd);
                    return new UsageFile(new FileStream(handle, FileAccess.Read, bufferSize: 0), stamp);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }
            finally
            {
                close(dir);
            }
        }

        private static int Retry(Func<int> call)
        {
            int result;
            do
                result = call();
            while (result == -1 && Marshal.GetLastPInvokeError() == EINTR);
            return result;
        }

        private static Exception Failed(int errno, string what)
        {
            if (errno == ELOOP)
                return new RefusedFileException($"{what} is a link");
            return errno switch
            {
                ENOENT => new FileNotFoundException($"{what} isn't there"),
                ENOTDIR => new RefusedFileException($"{what} isn't a folder"),
                ENXIO => new RefusedFileException($"{what} is a FIFO or a device"),
                EACCES => new UnauthorizedAccessException($"{what} may not be read"),
                _ => new IOException($"{what} couldn't be opened (errno {errno})"),
            };
        }

        /// <summary>
        /// A regular file's stamp from its open descriptor; refused when it isn't one. macOS: <c>fstat</c>'s 64-bit-inode
        /// <c>struct stat</c> (<c>st_dev</c> at 0, <c>st_mode</c> at 4, <c>st_ino</c> at 8, <c>st_mtimespec</c> at 48,
        /// <c>st_size</c> at 96). Linux: <c>statx</c> on the descriptor (<c>stx_mode</c> at 28, <c>stx_ino</c> at 32,
        /// <c>stx_size</c> at 40, <c>stx_mtime</c> at 112, <c>stx_dev_major</c> and <c>_minor</c> at 136 and 140).
        /// </summary>
        private static FileStamp StampOf(int fd)
        {
            var buffer = new byte[512];
            int result;
            if (Mac)
                result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstat_inode64(fd, buffer) : fstat(fd, buffer);
            else
                result = statx(fd, "", AT_EMPTY_PATH, STATX_BASIC_STATS, buffer);
            if (result != 0)
                throw new IOException($"what the file is couldn't be told (errno {Marshal.GetLastPInvokeError()})");

            var mode = BitConverter.ToUInt16(buffer, Mac ? 4 : 28);
            if ((mode & S_IFMT) != S_IFREG)
                throw new RefusedFileException("not a regular file");
            if (Mac)
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(buffer, 48)).AddTicks(BitConverter.ToInt64(buffer, 56) / 100);
                return new FileStamp(BitConverter.ToInt64(buffer, 96), time, new FileId(BitConverter.ToUInt32(buffer, 0), BitConverter.ToUInt64(buffer, 8)));
            }
            var mtime = DateTimeOffset.FromUnixTimeSeconds(BitConverter.ToInt64(buffer, 112)).AddTicks(BitConverter.ToUInt32(buffer, 120) / 100);
            var device = ((ulong)BitConverter.ToUInt32(buffer, 136) << 32) | BitConverter.ToUInt32(buffer, 140);
            return new FileStamp(BitConverter.ToInt64(buffer, 40), mtime, new FileId(device, BitConverter.ToUInt64(buffer, 32)));
        }

        private const int AT_EMPTY_PATH = 0x1000;
        private const uint STATX_BASIC_STATS = 0x7FF;

        // Two or three arguments only: open and openat are variadic, and nothing here creates a file (no mode is passed).
        [DllImport("libc", SetLastError = true)]
        private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int openat(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int fstat(int fd, [Out] byte[] buffer);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int fstat_inode64(int fd, [Out] byte[] buffer);

        [DllImport("libc", SetLastError = true)]
        private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
    }

    private static class Windows
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // read, write, delete
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
        private const int FILE_TYPE_DISK = 1;
        private const uint ATTRIBUTE_DIRECTORY = 0x10, ATTRIBUTE_REPARSE_POINT = 0x400;
        private const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5;

        public static UsageFile Open(string root, string[] parts)
        {
            var path = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                path = Path.Combine(path, parts[i]);
                var attributes = File.GetAttributes(Literal(path));
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new RefusedFileException("a folder is a link");
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new RefusedFileException("a folder isn't a folder");
            }
            path = Path.Combine(path, parts[^1]);

            var handle = CreateFileW(Literal(path), GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_SEQUENTIAL_SCAN, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw error switch
                {
                    ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => new FileNotFoundException("the file isn't there"),
                    ERROR_ACCESS_DENIED => new UnauthorizedAccessException("the file may not be read"),
                    _ => new IOException($"the file couldn't be opened (error {error})"),
                };
            }
            try
            {
                if (GetFileType(handle) != FILE_TYPE_DISK)
                    throw new RefusedFileException("not a regular file");
                if (!GetFileInformationByHandle(handle, out var info))
                    throw new IOException($"what the file is couldn't be told (error {Marshal.GetLastPInvokeError()})");
                if ((info.Attributes & (ATTRIBUTE_DIRECTORY | ATTRIBUTE_REPARSE_POINT)) != 0)
                    throw new RefusedFileException("a link or a folder");
                var length = ((long)info.SizeHigh << 32) | info.SizeLow;
                var written = DateTimeOffset.FromFileTime(((long)info.WriteHigh << 32) | info.WriteLow).ToUniversalTime();
                var stamp = new FileStamp(length, written, new FileId(info.VolumeSerial, ((ulong)info.IndexHigh << 32) | info.IndexLow));
                return new UsageFile(new FileStream(handle, FileAccess.Read, bufferSize: 0), stamp);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>A full path as Windows names it literally (<c>\\?\</c>): no device names, no trimmed dots or spaces.</summary>
        private static string Literal(string path)
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
                return full;
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint VolumeSerial;
            public uint SizeHigh, SizeLow;
            public uint Links;
            public uint IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int GetFileType(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    }
}
