using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace InfoPanel.Documents;

/// <summary>A point where a storage operation is about to change the disk; the fault-injection seam's steps.</summary>
internal enum StorageStep
{
    /// <summary>Creating a temp file.</summary>
    CreateTemp,

    /// <summary>Half of the content is in the temp file, the rest not yet.</summary>
    WriteTemp,

    /// <summary>Flushing the temp file to disk.</summary>
    FlushTemp,

    /// <summary>Flushing a file that is kept as it is to disk (its content, on the handle it was read through).</summary>
    FlushFile,

    /// <summary>Renaming the temp file over its target.</summary>
    ReplaceFile,

    /// <summary>Flushing a folder's entries to disk after a rename, a new child or a removal in it.</summary>
    SyncFolder,

    /// <summary>Creating a folder.</summary>
    CreateFolder,

    /// <summary>Renaming a folder.</summary>
    MoveFolder,

    /// <summary>Deleting a file or a link.</summary>
    DeleteFile,

    /// <summary>Deleting an empty folder.</summary>
    DeleteFolder,

    /// <summary>Setting a file's modification time.</summary>
    TouchFile,
}

/// <summary>
/// Called before every step that changes the disk, so tests can fail an operation at each one. An exception thrown here
/// is the step failing; a test simulating a crash throws something that is not an <see cref="IOException"/> from that
/// call and from every later one, so nothing after it touches the disk (cleanup included).
/// </summary>
internal interface IStorageFaults
{
    void Before(StorageStep step, string path);

    /// <summary>An <c>errno</c> for flushing <paramref name="folder"/> to report instead of calling <c>fsync</c>; 0 calls it.</summary>
    int FolderSyncError(string folder) => 0;

    /// <summary>
    /// macOS: called once per folder flush, which is a full flush first; an <c>errno</c> for <c>F_FULLFSYNC</c> on
    /// <paramref name="folder"/> to report instead of calling it; 0 calls it.
    /// </summary>
    int FullSyncError(string folder) => 0;

    /// <summary>
    /// Unix: an <c>errno</c> for flushing the content of <paramref name="path"/>, a file kept as it is, to report instead
    /// of calling the native flush; 0 calls it.
    /// </summary>
    int FileSyncError(string path) => 0;
}

/// <summary>
/// Crash-safe file replacement. The content goes to a uniquely named <c>{path}.{32 hex}.tmp</c> in the target's folder,
/// is flushed to disk, and is renamed over the target (<see cref="File.Move(string, string, bool)"/>: <c>rename</c> on
/// Unix, <c>MoveFileEx</c> with replace on Windows), so a reader sees the old file or the new one, never a mix. On Unix
/// the folder is then flushed too, so the rename itself survives a power loss, and every folder it creates is flushed
/// into its parent; a flush that fails is an error, except on a file system that can't flush folders at all
/// (<c>EINVAL</c>, <c>ENOTSUP</c>), which is logged once per process.
/// <para>
/// What survives, by platform. After a crash of the engine, at any step, each file is its previous version or its new
/// one, never a mix, everywhere. After a power loss, on a drive that honours flush requests:
/// <list type="bullet">
/// <item><b>macOS</b>: once a write has returned it survives. The file and every folder are flushed with
/// <c>F_FULLFSYNC</c>, which also empties the drive's own cache (.NET's file flush does the same); a file system that
/// can't do that gets a plain <c>fsync</c>, logged once per process, and then has the Windows guarantee below.</item>
/// <item><b>Linux</b>: once a write has returned it survives (<c>fsync</c> on the file and the folder, a full flush
/// where write barriers are on, the default on ext4 and xfs).</item>
/// <item><b>Windows</b>: folders are not flushed, so a power loss right after a write can bring back the file's previous
/// version, or lose a file the write created, but never leaves a partial one.</item>
/// </list>
/// A temp file left by a crash is never read; the stores remove them when they open.
/// </para>
/// </summary>
public static class AtomicFile
{
    // Resolved at each use: the warnings here are rare, and follow the logger configured at the time.
    private static ILogger Logger => Log.ForContext(typeof(AtomicFile));

    /// <summary>The suffix of every temp file and staging folder the stores write.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>The length of the random part of a temp file's or a staging folder's name: a GUID's 32 hex digits.</summary>
    internal const int RandomLength = 32;

    private static volatile bool _folderSyncUnavailable;

    /// <summary>Set by the first folder whose file system can't flush folders, which is logged; the later ones aren't.</summary>
    private static int _folderSyncWarned;

    /// <summary>Set by the first folder whose file system can't take a full flush (macOS), which is logged; the later ones aren't.</summary>
    private static int _fullSyncWarned;

    /// <summary>Set by the first kept file whose file system can't take a full flush (macOS), which is logged; the later ones aren't.</summary>
    private static int _fileFullSyncWarned;

    /// <summary>Lets a test see the warnings again.</summary>
    internal static void ResetFolderSyncWarning()
    {
        Volatile.Write(ref _folderSyncWarned, 0);
        Volatile.Write(ref _fullSyncWarned, 0);
        Volatile.Write(ref _fileFullSyncWarned, 0);
    }

    /// <summary>Replaces <paramref name="path"/> with <paramref name="content"/>, creating its folder when missing.</summary>
    /// <param name="root">As for <see cref="CreateFolder(string, string?)"/>.</param>
    /// <exception cref="IOException">
    /// The write failed (the target is unchanged and the temp file removed), or the target was replaced but its folder
    /// could not be flushed to disk.
    /// </exception>
    public static void Write(string path, ReadOnlyMemory<byte> content, string? root = null) => Write(path, content, root, faults: null);

    /// <summary>Replaces <paramref name="path"/> with what <paramref name="write"/> writes, creating its folder when missing.</summary>
    /// <param name="root">As for <see cref="CreateFolder(string, string?)"/>.</param>
    /// <exception cref="IOException">
    /// The write failed (the target is unchanged and the temp file removed), or the target was replaced but its folder
    /// could not be flushed to disk.
    /// </exception>
    public static void Write(string path, Action<Stream> write, string? root = null) => Write(path, write, root, faults: null);

    /// <summary>
    /// As <see cref="Write(string, ReadOnlyMemory{byte}, string?)"/>, for a file only its owner may read: on Unix the
    /// temp file is created owner read and write only (0600), so the target never has wider permissions, not even briefly.
    /// </summary>
    public static void WritePrivate(string path, ReadOnlyMemory<byte> content, string? root = null) =>
        WritePrivate(path, content, root, faults: null);

    internal static void WritePrivate(string path, ReadOnlyMemory<byte> content, string? root, IStorageFaults? faults) =>
        Write(path, content, root, faults, ownerOnly: true);

    /// <summary>A random part for a temp file's or a staging folder's name: <see cref="RandomLength"/> lower-case hex digits.</summary>
    internal static string NewRandom() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Whether <paramref name="name"/> is a temp file <see cref="Write(string, Action{Stream})"/> makes for a target named
    /// so that <paramref name="target"/> accepts it: exactly <c>{target}.{32 hex}.tmp</c>.
    /// </summary>
    internal static bool IsTempFile(string name, Func<string, bool> target)
    {
        var tail = 1 + RandomLength + TempSuffix.Length;
        return name.Length > tail && IsRandomPart(name.AsSpan(name.Length - tail)) && target(name[..^tail]);
    }

    /// <summary>Whether <paramref name="part"/> is exactly <c>.{32 hex}.tmp</c>.</summary>
    internal static bool IsRandomPart(ReadOnlySpan<char> part) =>
        part.Length == 1 + RandomLength + TempSuffix.Length && part[0] == '.' && part.EndsWith(TempSuffix, StringComparison.Ordinal)
        && IsLowerHex(part.Slice(1, RandomLength));

    internal static bool IsLowerHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    internal static void Write(string path, ReadOnlyMemory<byte> content, string? root, IStorageFaults? faults, Action? replaced = null,
        bool ownerOnly = false) =>
        Write(path, stream =>
        {
            var half = content.Length / 2;
            stream.Write(content.Span[..half]);
            faults?.Before(StorageStep.WriteTemp, path);
            stream.Write(content.Span[half..]);
        }, root, faults, replaced, ownerOnly);

    /// <param name="replaced">Called once the target is replaced, before its folder is flushed (which may still fail).</param>
    /// <param name="ownerOnly">Create the temp file 0600 on Unix (ignored on Windows).</param>
    internal static void Write(string path, Action<Stream> write, string? root, IStorageFaults? faults, Action? replaced = null, bool ownerOnly = false)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        CreateFolder(folder, root, faults);
        var temp = $"{path}.{NewRandom()}{TempSuffix}";
        try
        {
            faults?.Before(StorageStep.CreateTemp, temp);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (ownerOnly && !OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temp, options))
            {
                write(stream);
                faults?.Before(StorageStep.FlushTemp, temp);
                stream.Flush(flushToDisk: true);
            }

            faults?.Before(StorageStep.ReplaceFile, path);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    faults?.Before(StorageStep.DeleteFile, temp);
                    File.Delete(temp);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }

        replaced?.Invoke();
        SyncFolder(folder, faults);
    }

    /// <summary>
    /// Creates <paramref name="folder"/> and every missing folder above it, flushing each new folder's parent to disk
    /// right after creating it, so nothing written in it can outlive the folder's own entry. Every folder below
    /// <paramref name="root"/> (the store's data folder) has its parent flushed on every call, whether it was created
    /// now or found, so a call after one whose flush failed flushes it again.
    /// </summary>
    /// <exception cref="IOException">A folder could not be created, or its parent could not be flushed.</exception>
    public static void CreateFolder(string folder, string? root = null) => CreateFolder(folder, root, faults: null);

    internal static void CreateFolder(string folder, string? root, IStorageFaults? faults)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var below = root != null && IsBelow(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        if (!below && Directory.Exists(folder))
            return;
        var parent = Path.GetDirectoryName(folder);
        if (parent != null)
            CreateFolder(parent, root, faults);
        if (!Directory.Exists(folder))
        {
            faults?.Before(StorageStep.CreateFolder, folder);
            Directory.CreateDirectory(folder);
        }

        if (parent != null)
            SyncFolder(parent, faults);
    }

    /// <summary>Whether <paramref name="folder"/> is inside <paramref name="root"/> (not <paramref name="root"/> itself).</summary>
    private static bool IsBelow(string folder, string root)
    {
        var relative = Path.GetRelativePath(root, folder);
        return relative != "." && !Path.IsPathRooted(relative)
            && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Flushes <paramref name="folder"/>'s entries (a rename, a new or removed child) to disk, on Unix. On macOS it is a
    /// full flush, <c>fcntl(F_FULLFSYNC)</c>, which also empties the drive's own cache; when that fails (some network and
    /// FUSE mounts can't do it) the flush falls back to <c>fsync</c>, as .NET's file flush and SQLite do, and the first
    /// such folder is logged once per process. Elsewhere it is <c>fsync</c>. Does nothing on Windows, where folders can't
    /// be flushed this way. A file system that can't flush folders (<c>fsync</c> fails with <c>EINVAL</c> or
    /// <c>ENOTSUP</c>) is logged once per process, not thrown.
    /// </summary>
    /// <exception cref="IOException">The folder could not be opened or flushed.</exception>
    public static void SyncFolder(string folder) => SyncFolder(folder, faults: null);

    internal static void SyncFolder(string folder, IStorageFaults? faults)
    {
        faults?.Before(StorageStep.SyncFolder, folder);
        if (OperatingSystem.IsWindows() || _folderSyncUnavailable)
            return;

        int fd, errno;
        try
        {
            do
            {
                fd = Posix.open(folder, Posix.O_RDONLY);
                errno = fd < 0 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (fd < 0 && errno == Posix.EINTR);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _folderSyncUnavailable = true;
            Logger.Warning(ex, "Folders can't be flushed to disk on this system; a power loss may bring back a file's previous version");
            return;
        }

        if (fd < 0)
            throw new IOException($"Could not open {folder} to flush it to disk: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).");

        try
        {
            errno = faults?.FolderSyncError(folder) ?? 0;
            if (errno == 0 && !(OperatingSystem.IsMacOS() && FullSync(folder, fd, faults)))
            {
                int result;
                do
                {
                    result = Posix.fsync(fd);
                    errno = result != 0 ? Marshal.GetLastPInvokeError() : 0;
                }
                while (result != 0 && errno == Posix.EINTR);
            }
        }
        finally
        {
            Posix.close(fd);
        }

        if (errno == 0)
            return;
        if (!Posix.IsSyncUnsupported(errno))
            throw new IOException($"Could not flush {folder} to disk: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).");
        if (Interlocked.Exchange(ref _folderSyncWarned, 1) == 0)
        {
            Logger.Warning("The file system of {Folder} can't flush folders to disk ({Error}, errno {Errno}); a power loss may bring back a file's previous version there",
                folder, Marshal.GetPInvokeErrorMessage(errno), errno);
        }
    }

    /// <summary>
    /// macOS: <c>fcntl(F_FULLFSYNC)</c> on the open folder, retried on <c>EINTR</c>. Whether it succeeded; when it didn't,
    /// the caller falls back to <c>fsync</c>, and the first such folder is logged once per process.
    /// </summary>
    private static bool FullSync(string folder, int fd, IStorageFaults? faults)
    {
        var errno = faults?.FullSyncError(folder) ?? 0;
        if (errno == 0)
        {
            int result;
            do
            {
                result = Posix.fcntl(fd, Posix.F_FULLFSYNC);
                errno = result == -1 ? Marshal.GetLastPInvokeError() : 0;
            }
            while (result == -1 && errno == Posix.EINTR);
        }

        if (errno == 0)
            return true;
        if (Interlocked.Exchange(ref _fullSyncWarned, 1) == 0)
        {
            Logger.Warning("A full flush (F_FULLFSYNC) of {Folder} failed ({Error}, errno {Errno}); folders there get a plain fsync, which a power loss can undo",
                folder, Marshal.GetPInvokeErrorMessage(errno), errno);
        }
        return false;
    }

    /// <summary>
    /// Flushes the content of a file that is kept as it is (<paramref name="handle"/>, opened on <paramref name="path"/>) to
    /// disk, and checks the result. Unix: <c>fcntl(F_FULLFSYNC)</c> on macOS, falling back to <c>fsync</c> when it fails
    /// (logged once per process), <c>fsync</c> elsewhere, retried on <c>EINTR</c>; any other failure, <c>EINVAL</c> and
    /// <c>ENOTSUP</c> included (which .NET's own file flush ignores), is an error, since a file that can't be flushed is
    /// not known to be on disk. Windows: <see cref="RandomAccess.FlushToDisk"/>, which reports every failure.
    /// </summary>
    /// <exception cref="IOException">The flush failed.</exception>
    internal static void FlushFile(SafeFileHandle handle, string path, IStorageFaults? faults)
    {
        faults?.Before(StorageStep.FlushFile, path);
        if (OperatingSystem.IsWindows())
        {
            RandomAccess.FlushToDisk(handle);
            return;
        }

        var errno = faults?.FileSyncError(path) ?? 0;
        if (errno == 0)
        {
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                var fd = (int)handle.DangerousGetHandle();
                if (OperatingSystem.IsMacOS() && FullSyncFile(path, fd))
                    return;
                int result;
                do
                {
                    result = Posix.fsync(fd);
                    errno = result != 0 ? Marshal.GetLastPInvokeError() : 0;
                }
                while (result != 0 && errno == Posix.EINTR);
            }
            finally
            {
                if (added)
                    handle.DangerousRelease();
            }
        }

        if (errno != 0)
            throw new IOException($"Could not flush {path} to disk: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno}).");
    }

    /// <summary>macOS: <c>fcntl(F_FULLFSYNC)</c> on a file, retried on <c>EINTR</c>; whether it succeeded (the caller falls back to <c>fsync</c>; the first failure is logged).</summary>
    private static bool FullSyncFile(string path, int fd)
    {
        int result, errno;
        do
        {
            result = Posix.fcntl(fd, Posix.F_FULLFSYNC);
            errno = result == -1 ? Marshal.GetLastPInvokeError() : 0;
        }
        while (result == -1 && errno == Posix.EINTR);

        if (errno == 0)
            return true;
        if (Interlocked.Exchange(ref _fileFullSyncWarned, 1) == 0)
        {
            Logger.Warning("A full flush (F_FULLFSYNC) of {Path} failed ({Error}, errno {Errno}); files there get a plain fsync, which a power loss can undo",
                path, Marshal.GetPInvokeErrorMessage(errno), errno);
        }
        return false;
    }

    private static class Posix
    {
        /// <summary><c>F_FULLFSYNC</c>, 51 on macOS.</summary>
        public const int F_FULLFSYNC = 51;

        /// <summary><c>O_RDONLY</c>, 0 on macOS and Linux.</summary>
        public const int O_RDONLY = 0;

        /// <summary><c>EINTR</c>, 4 on macOS and Linux.</summary>
        public const int EINTR = 4;

        /// <summary><c>EINVAL</c>, 22 on macOS and Linux.</summary>
        public const int EINVAL = 22;

        /// <summary><c>ENOTSUP</c>: 45 on macOS, 95 on Linux (where it is also <c>EOPNOTSUPP</c>).</summary>
        public static int ENOTSUP => OperatingSystem.IsMacOS() ? 45 : 95;

        /// <summary><c>EOPNOTSUPP</c>: 102 on macOS, 95 on Linux.</summary>
        public static int EOPNOTSUPP => OperatingSystem.IsMacOS() ? 102 : 95;

        /// <summary>Whether <c>fsync</c> on a folder failed because its file system can't flush folders.</summary>
        public static bool IsSyncUnsupported(int errno) => errno == EINVAL || errno == ENOTSUP || errno == EOPNOTSUPP;

        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        public static extern int fsync(int fd);

        /// <summary><c>fcntl</c> for a command that takes no argument (the call is variadic; none is passed).</summary>
        [DllImport("libc", SetLastError = true)]
        public static extern int fcntl(int fd, int cmd);

        [DllImport("libc")]
        public static extern int close(int fd);
    }
}
