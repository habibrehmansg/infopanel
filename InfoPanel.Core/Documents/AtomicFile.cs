using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using InfoPanel.Persistence;
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

    /// <summary>
    /// Unix: an <c>errno</c> for flushing <paramref name="folder"/> to report instead of calling <c>fsync</c>, once the
    /// folder is open; 0 calls it.
    /// </summary>
    int FolderSyncError(string folder) => 0;

    /// <summary>
    /// Windows: every folder flush goes through here once the folder is open. <paramref name="flush"/> is the native
    /// <c>FlushFileBuffers</c> itself, for the folder's <paramref name="handle"/>; the result is 0, or the Win32 error
    /// code the flush failed with. The default calls it; a test records the call, or reports an error instead.
    /// </summary>
    int FlushFolder(string folder, SafeFileHandle handle, Func<SafeFileHandle, bool> flush) => AtomicFile.CallNative(flush, handle);

    /// <summary>
    /// Windows: the name of the file system <paramref name="folder"/> is on (<c>NTFS</c>, <c>ReFS</c>, <c>FAT32</c>…), to
    /// report instead of asking Windows (<c>GetVolumeInformationByHandleW</c> on the folder's handle); null asks.
    /// </summary>
    string? FileSystemName(string folder) => null;

    /// <summary>
    /// Windows: called once per rename of a temp file (<paramref name="temp"/>) over its target (<paramref name="path"/>),
    /// both full paths, with the <c>MoveFileExW</c> flags it asks for; a Win32 error code to report instead of calling
    /// it; 0 calls it.
    /// </summary>
    int ReplaceError(string temp, string path, int flags) => 0;

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
/// is flushed to disk, and is renamed over the target (<c>rename</c> on Unix; on Windows <c>MoveFileExW</c> with replace
/// and write-through), so a reader sees the old file or the new one, never a mix. The folder is then flushed too, so the
/// rename itself survives a power loss, and every folder it creates is flushed into its parent; a flush that fails is an
/// error, except on a file system that can't flush folders at all (<c>EINVAL</c>, <c>ENOTSUP</c> on Unix;
/// <c>ERROR_INVALID_FUNCTION</c>, <c>ERROR_NOT_SUPPORTED</c>, or any file system but NTFS, on Windows), which is logged
/// once per process.
/// <para>
/// What survives, by platform. After a crash of the engine, at any step, each file is its previous version or its new
/// one, never a mix, everywhere. After a power loss, on a drive that honours flush requests:
/// <list type="bullet">
/// <item><b>macOS</b>: once a write has returned it survives. The file and every folder are flushed with
/// <c>F_FULLFSYNC</c>, which also empties the drive's own cache (.NET's file flush does the same); a file system that
/// can't do that gets a plain <c>fsync</c>, logged once per process, and then has the guarantee of a file system that
/// can't flush folders, below.</item>
/// <item><b>Linux</b>: once a write has returned it survives (<c>fsync</c> on the file and the folder, a full flush
/// where write barriers are on, the default on ext4 and xfs).</item>
/// <item><b>Windows</b>: on NTFS, once a write has returned it survives (the file flushed, then the folder flushed with
/// <c>FlushFileBuffers</c> after the rename, which is what makes the rename durable). Any other file system (ReFS, FAT,
/// exFAT) is logged once per process and has the guarantee of a file system that can't flush folders, below, since a
/// folder flush there can succeed without reaching the disk.</item>
/// </list>
/// On a file system that can't flush folders, a power loss right after a write can bring back the file's previous
/// version, or lose a file the write created, but never leaves a partial one.
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

    /// <summary>Windows: calls <paramref name="call"/>, a native function that sets the last error; 0, or the Win32 error code it failed with.</summary>
    internal static int CallNative(Func<SafeFileHandle, bool> call, SafeFileHandle handle) =>
        call(handle) ? 0 : Marshal.GetLastPInvokeError();

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

    /// <summary>As <see cref="Write(string, ReadOnlyMemory{byte}, string?)"/>, telling when the target is replaced.</summary>
    /// <param name="replaced">
    /// Called once the target is replaced, before its folder is flushed: an exception after it is the flush failing, with
    /// the target already holding <paramref name="content"/>.
    /// </param>
    public static void Write(string path, ReadOnlyMemory<byte> content, string? root, Action replaced) =>
        Write(path, content, root, faults: null, replaced);

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

    /// <summary>
    /// As <see cref="WritePrivate(string, ReadOnlyMemory{byte}, string?)"/>, the temp file made by
    /// <paramref name="createTemp"/> when given (a new file for writing, never an existing one): on Windows, where the
    /// Unix mode doesn't apply, a creator that gives the file a protected access list for this user only, which the rename
    /// keeps.
    /// </summary>
    /// <param name="replaced">
    /// Called once the target is replaced, before its folder is flushed: an exception after it is the flush failing, with
    /// the target already holding <paramref name="content"/>.
    /// </param>
    public static void WritePrivate(string path, ReadOnlyMemory<byte> content, string? root, Func<string, FileStream>? createTemp,
        Action? replaced = null) =>
        Write(path, content, root, faults: null, replaced, ownerOnly: true, createTemp: createTemp);

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
        bool ownerOnly = false, Func<string, FileStream>? createTemp = null) =>
        Write(path, stream =>
        {
            var half = content.Length / 2;
            stream.Write(content.Span[..half]);
            faults?.Before(StorageStep.WriteTemp, path);
            stream.Write(content.Span[half..]);
        }, root, faults, replaced, ownerOnly, createTemp);

    /// <param name="replaced">Called once the target is replaced, before its folder is flushed (which may still fail).</param>
    /// <param name="ownerOnly">Create the temp file 0600 on Unix (ignored on Windows).</param>
    /// <param name="createTemp">Creates the temp file in place of a plain <see cref="FileStream"/> (a new file, for writing).</param>
    internal static void Write(string path, Action<Stream> write, string? root, IStorageFaults? faults, Action? replaced = null, bool ownerOnly = false,
        Func<string, FileStream>? createTemp = null)
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
            using (var stream = createTemp?.Invoke(temp) ?? new FileStream(temp, options))
            {
                write(stream);
                faults?.Before(StorageStep.FlushTemp, temp);
                stream.Flush(flushToDisk: true);
            }

            faults?.Before(StorageStep.ReplaceFile, path);
            Replace(temp, path, faults);
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
    /// Renames <paramref name="temp"/> over <paramref name="path"/>: <c>rename</c> on Unix; on Windows
    /// <c>MoveFileExW</c> with <c>MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH</c>, by literal name. Write-through
    /// only matters for a move Windows does as a copy and a delete (to another volume), which a temp file beside its
    /// target never is: what makes this rename survive a power loss is the folder flush after it (<see cref="SyncFolder(string)"/>).
    /// A refused rename throws as <see cref="File.Move(string, string, bool)"/> does.
    /// </summary>
    private static void Replace(string temp, string path, IStorageFaults? faults)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(temp, path, overwrite: true);
            return;
        }

        const int flags = Windows.MOVEFILE_REPLACE_EXISTING | Windows.MOVEFILE_WRITE_THROUGH;
        var source = Path.GetFullPath(temp);
        var target = Path.GetFullPath(path);
        var error = faults?.ReplaceError(source, target, flags) ?? 0;
        if (error == 0 && !Windows.MoveFileExW(RegularFile.Literal(source), RegularFile.Literal(target), flags))
            error = Marshal.GetLastPInvokeError();
        if (error != 0)
            throw Windows.ExceptionFor(error, $"Could not replace {path} with {temp}");
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
    /// Flushes <paramref name="folder"/>'s entries (a rename, a new or removed child) to disk. On macOS it is a full
    /// flush, <c>fcntl(F_FULLFSYNC)</c>, which also empties the drive's own cache; when that fails (some network and FUSE
    /// mounts can't do it) the flush falls back to <c>fsync</c>, as .NET's file flush and SQLite do, and the first such
    /// folder is logged once per process. On Linux it is <c>fsync</c>; on Windows <c>FlushFileBuffers</c> on the folder
    /// opened for writing. A file system that can't flush folders (<c>fsync</c> fails with <c>EINVAL</c> or
    /// <c>ENOTSUP</c>; on Windows <c>ERROR_INVALID_FUNCTION</c> or <c>ERROR_NOT_SUPPORTED</c>, or any file system but
    /// NTFS, even when the flush succeeds) is logged once per process, not thrown.
    /// </summary>
    /// <exception cref="IOException">The folder could not be opened or flushed.</exception>
    public static void SyncFolder(string folder) => SyncFolder(folder, faults: null);

    internal static void SyncFolder(string folder, IStorageFaults? faults)
    {
        faults?.Before(StorageStep.SyncFolder, folder);
        if (OperatingSystem.IsWindows())
        {
            SyncFolderWindows(folder, faults);
            return;
        }

        if (_folderSyncUnavailable)
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
    /// Windows: opens <paramref name="folder"/> (<c>CreateFileW</c> with <c>GENERIC_WRITE</c>, every share mode and
    /// <c>FILE_FLAG_BACKUP_SEMANTICS</c>, by literal name) and flushes it with <c>FlushFileBuffers</c>, so its entries are
    /// on disk. Only NTFS is known to do that: elsewhere (ReFS, FAT, exFAT, a file system Windows can't name) a flush can
    /// succeed without it, so there, as on a file system that can't flush folders (<c>ERROR_INVALID_FUNCTION</c>,
    /// <c>ERROR_NOT_SUPPORTED</c>), the first folder is logged once per process, not thrown. Any other failure, opening
    /// included, is an <see cref="IOException"/>, on every file system.
    /// </summary>
    private static void SyncFolderWindows(string folder, IStorageFaults? faults)
    {
        using var handle = Windows.CreateFileW(RegularFile.Literal(Path.GetFullPath(folder)), Windows.GENERIC_WRITE,
            Windows.FILE_SHARE_READ | Windows.FILE_SHARE_WRITE | Windows.FILE_SHARE_DELETE, IntPtr.Zero, Windows.OPEN_EXISTING,
            Windows.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var opened = Marshal.GetLastPInvokeError();
            throw new IOException($"Could not open {folder} to flush it to disk: {Marshal.GetPInvokeErrorMessage(opened)} (error {opened}).",
                Windows.HResultOf(opened));
        }

        var error = faults != null ? faults.FlushFolder(folder, handle, Windows.Flush) : CallNative(Windows.Flush, handle);
        if (error != 0)
        {
            if (error is not (Windows.ERROR_INVALID_FUNCTION or Windows.ERROR_NOT_SUPPORTED))
                throw new IOException($"Could not flush {folder} to disk: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).", Windows.HResultOf(error));
            if (Interlocked.Exchange(ref _folderSyncWarned, 1) == 0)
            {
                Logger.Warning("The file system of {Folder} can't flush folders to disk ({Error}, error {Code}); a power loss may bring back a file's previous version there",
                    folder, Marshal.GetPInvokeErrorMessage(error), error);
            }
            return;
        }

        // Once warned, nothing more is logged, so the file system needn't be asked again.
        if (Volatile.Read(ref _folderSyncWarned) != 0)
            return;
        var fileSystem = faults?.FileSystemName(folder) ?? Windows.FileSystemOf(handle);
        if (string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            return;
        if (Interlocked.Exchange(ref _folderSyncWarned, 1) == 0)
        {
            Logger.Warning("The file system of {Folder} ({FileSystem}, not NTFS) can't flush folders to disk reliably; a power loss may bring back a file's previous version there",
                folder, fileSystem);
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

    private static class Windows
    {
        public const int MOVEFILE_REPLACE_EXISTING = 0x1;
        public const int MOVEFILE_WRITE_THROUGH = 0x8;

        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint FILE_SHARE_DELETE = 0x4;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        public const int ERROR_INVALID_FUNCTION = 1;
        public const int ERROR_FILE_NOT_FOUND = 2;
        public const int ERROR_PATH_NOT_FOUND = 3;
        public const int ERROR_ACCESS_DENIED = 5;
        public const int ERROR_NOT_SUPPORTED = 50;
        public const int ERROR_FILENAME_EXCED_RANGE = 206;
        public const int ERROR_OPERATION_ABORTED = 995;

        /// <summary><c>MAX_PATH + 1</c>, the longest file system name <c>GetVolumeInformationByHandleW</c> returns, with its terminator.</summary>
        private const int FileSystemNameLength = 261;

        /// <summary>The native folder flush, the one <see cref="IStorageFaults.FlushFolder"/> is handed.</summary>
        public static readonly Func<SafeFileHandle, bool> Flush = FlushFileBuffers;

        /// <summary>
        /// The name of the file system <paramref name="handle"/> is on (<c>NTFS</c>, <c>ReFS</c>, <c>FAT32</c>…), or what
        /// stands for it when Windows can't tell (<c>unknown (error N)</c>).
        /// </summary>
        public static string FileSystemOf(SafeFileHandle handle)
        {
            var name = new char[FileSystemNameLength];
            if (!GetVolumeInformationByHandleW(handle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, name, (uint)name.Length))
                return $"unknown (error {Marshal.GetLastPInvokeError()})";
            var end = Array.IndexOf(name, '\0');
            return new string(name, 0, end < 0 ? name.Length : end);
        }

        /// <summary>The <c>HRESULT</c> of a Win32 error, as .NET's own I/O exceptions carry it.</summary>
        public static int HResultOf(int error) => unchecked((int)0x80070000) | (error & 0xFFFF);

        /// <summary>The exception <see cref="File.Move(string, string, bool)"/> throws for <paramref name="error"/>.</summary>
        public static Exception ExceptionFor(int error, string what)
        {
            var message = $"{what}: {Marshal.GetPInvokeErrorMessage(error)} (error {error}).";
            return error switch
            {
                ERROR_FILE_NOT_FOUND => new FileNotFoundException(message),
                ERROR_PATH_NOT_FOUND => new DirectoryNotFoundException(message),
                ERROR_ACCESS_DENIED => new UnauthorizedAccessException(message),
                ERROR_FILENAME_EXCED_RANGE => new PathTooLongException(message),
                ERROR_OPERATION_ABORTED => new OperationCanceledException(message),
                _ => new IOException(message, HResultOf(error)),
            };
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileExW(string existingFileName, string newFileName, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushFileBuffers(SafeFileHandle file);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, IntPtr volumeName, uint volumeNameSize,
            IntPtr serialNumber, IntPtr maximumComponentLength, IntPtr fileSystemFlags, [Out] char[] fileSystemName, uint fileSystemNameSize);
    }
}
