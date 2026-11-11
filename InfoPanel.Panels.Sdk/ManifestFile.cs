using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace InfoPanel.Panels.Sdk;

/// <summary>
/// How a <c>panel.json</c> is read, as the engine reads the files it keeps (its <c>RegularFile</c>, which this SDK can't
/// reference): only a regular file, never a link (the engine's panel reader refuses one), opened without waiting or
/// following a link (on macOS and Linux non-blocking, no terminal, <c>O_NOFOLLOW</c>, the type asked on the open
/// descriptor and refused when it can't be; on Windows by literal name with <c>FILE_FLAG_OPEN_REPARSE_POINT</c>, the
/// handle's reparse tag and type checked), so a FIFO or a device is refused rather than waited on, a link put there
/// after the check is refused on the handle read, and it is read at most to one byte past its limit.
/// </summary>
internal static class ManifestFile
{
    /// <summary>The file's bytes; <see cref="InvalidDataException"/> when it holds more than <paramref name="maxBytes"/>.</summary>
    /// <exception cref="IOException">It isn't a regular file, it is a link, or it can't be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">It may not be read, or it is a folder.</exception>
    public static byte[] ReadAtMost(string path, int maxBytes)
    {
        if (new FileInfo(path).LinkTarget != null)
            throw new IOException($"{path} is a link; only a regular file is read");
        using var stream = new FileStream(Open(path), FileAccess.Read, bufferSize: 0);
        using var content = new MemoryStream();
        var chunk = new byte[16_384];
        int read;
        while ((read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes + 1L - content.Length))) > 0)
        {
            content.Write(chunk, 0, read);
            if (content.Length > maxBytes)
                throw new InvalidDataException($"{path} is larger than {maxBytes} bytes");
        }
        return content.ToArray();
    }

    private static SafeFileHandle Open(string path)
    {
        if (OperatingSystem.IsWindows())
            return OpenWindows(path);
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        int fd;
        do
            fd = Posix.open(path, Posix.O_RDONLY | Posix.O_NONBLOCK | Posix.O_NOCTTY | Posix.O_CLOEXEC | Posix.O_NOFOLLOW);
        while (fd == -1 && Marshal.GetLastPInvokeError() == Posix.EINTR);
        if (fd == -1)
        {
            var errno = Marshal.GetLastPInvokeError();
            var message = $"{path} can't be opened: {Marshal.GetPInvokeErrorMessage(errno)} (errno {errno})";
            throw errno switch
            {
                Posix.ENOENT => new FileNotFoundException(message, path),
                Posix.EACCES or Posix.EPERM => new UnauthorizedAccessException(message),
                _ => new IOException(message), // ELOOP: a link put there since; ENXIO: a FIFO or device with nothing behind it
            };
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            CheckPosixType(Posix.TypeOf(fd), path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// macOS and Linux: refuses what isn't a regular file, by the type asked on the open descriptor. Null (the type can't
    /// be asked: a C library without <c>statx</c>, such as musl before 1.2.5) is refused too: a FIFO opened non-blocking
    /// doesn't wait, but its bytes would be read as the manifest.
    /// </summary>
    internal static void CheckPosixType(int? type, string path)
    {
        switch (type)
        {
            case Posix.S_IFREG:
                return;
            case null:
                throw new IOException($"{path} isn't read: this system can't tell whether it is a regular file (its C library has no statx)");
            case Posix.S_IFDIR:
                throw new UnauthorizedAccessException($"{path} is a folder");
            default:
                throw new IOException($"{path} isn't a regular file (a FIFO, socket or device)");
        }
    }

    /// <summary>
    /// Windows: <paramref name="path"/> opened by its literal name without following a link at it
    /// (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>), so a link put there after <see cref="ReadAtMost"/>'s check is opened as
    /// itself and refused on this handle, the one then read; no second lookup of the path.
    /// </summary>
    internal static SafeFileHandle OpenWindows(string path)
    {
        var full = Path.GetFullPath(path);
        var literal = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var handle = CreateFileW(literal, GENERIC_READ, FILE_SHARE_READ, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | SECURITY_SQOS_PRESENT, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            var message = $"{path} can't be opened: {Marshal.GetPInvokeErrorMessage(error)} (error {error})";
            throw error switch
            {
                ERROR_FILE_NOT_FOUND => new FileNotFoundException(message, path),
                ERROR_PATH_NOT_FOUND => new DirectoryNotFoundException(message),
                ERROR_ACCESS_DENIED => new UnauthorizedAccessException(message), // a folder too: it isn't opened without backup semantics
                _ => new IOException(message),
            };
        }

        try
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var info, Marshal.SizeOf<FileAttributeTag>()))
                throw new IOException($"what kind of file {path} is can't be told (error {Marshal.GetLastPInvokeError()})");
            CheckWindowsHandle((FileAttributes)info.FileAttributes, info.ReparseTag, GetFileType(handle), path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Windows: refuses what the handle opened isn't a regular file, by its attributes, reparse tag and file type. A
    /// reparse point that stands for another name (a symbolic link, a junction: the tags .NET's <c>LinkTarget</c> reads
    /// as a link) is a link; another reparse point (a cloud file's placeholder, a deduplicated file) is the file itself
    /// and reads.
    /// </summary>
    internal static void CheckWindowsHandle(FileAttributes attributes, uint reparseTag, int fileType, string path)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 && (reparseTag & NameSurrogate) != 0)
            throw new IOException($"{path} is a link; only a regular file is read");
        if (fileType != FILE_TYPE_DISK)
            throw new IOException($"{path} isn't a regular file (a device)");
        if ((attributes & FileAttributes.Directory) != 0)
            throw new UnauthorizedAccessException($"{path} is a folder");
    }

    internal const int FILE_TYPE_DISK = 0x0001;
    internal const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C, IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    // The reparse tag's name-surrogate bit (IsReparseTagNameSurrogate): the point stands for another file or folder.
    private const uint NameSurrogate = 0x20000000;

    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 0x1, OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, SECURITY_SQOS_PRESENT = 0x00100000; // anonymous: never impersonated by a pipe
    private const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5;
    private const int FileAttributeTagInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTag
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileAttributeTag info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetFileType(SafeFileHandle handle);

    internal static class Posix
    {
        public const int O_RDONLY = 0;
        public static int O_NONBLOCK => OperatingSystem.IsMacOS() ? 0x0004 : 0x0800;
        public static int O_NOCTTY => OperatingSystem.IsMacOS() ? 0x20000 : 0x0100;
        public static int O_CLOEXEC => OperatingSystem.IsMacOS() ? 0x01000000 : 0x80000;
        public static int O_NOFOLLOW => OperatingSystem.IsMacOS() ? 0x0100
            : RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;

        public const int EPERM = 1, ENOENT = 2, EINTR = 4, EACCES = 13;

        // The file types in st_mode, the same on macOS and Linux.
        public const int S_IFMT = 0xF000, S_IFIFO = 0x1000, S_IFDIR = 0x4000, S_IFREG = 0x8000;

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
                throw new IOException($"what kind of file is open can't be told (errno {Marshal.GetLastPInvokeError()})");
            return BitConverter.ToUInt16(buffer, OperatingSystem.IsMacOS() ? 4 : 28) & S_IFMT;
        }

        private const int AT_EMPTY_PATH = 0x1000;
        private const uint STATX_TYPE = 0x1;

        // Two arguments only: open is variadic, and nothing here creates a file through it (no mode is passed).
        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int fstat(int fd, [Out] byte[] buffer);

        [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
        private static extern int fstat_inode64(int fd, [Out] byte[] buffer);

        [DllImport("libc", SetLastError = true)]
        private static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);
    }
}
