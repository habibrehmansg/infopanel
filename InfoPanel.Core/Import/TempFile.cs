using System;
using System.IO;

namespace InfoPanel.Import;

/// <summary>
/// Temporary files for archives on their way in or out: always in the OS temp folder (never the data folder or a 1.x
/// folder), readable and writable by this process only, and deleted when closed.
/// </summary>
public static class TempFile
{
    /// <summary>A new empty file in <see cref="Path.GetTempPath"/>, deleted when the stream is closed.</summary>
    public static FileStream Create()
    {
        var path = Path.Combine(Path.GetTempPath(), "infopanel-" + Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    /// <summary>
    /// Copies <paramref name="body"/> into a new temporary file (<see cref="Create"/>) and returns it positioned at its
    /// start, for <see cref="ArchiveReader"/>. Nothing more than <paramref name="maxBytes"/> plus one buffer is written.
    /// </summary>
    /// <exception cref="ImportBodyTooLargeException">The body is longer than <paramref name="maxBytes"/>; the file is gone.</exception>
    public static FileStream Buffer(Stream body, long maxBytes)
    {
        var file = Create();
        try
        {
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = body.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new ImportBodyTooLargeException(maxBytes);
                file.Write(chunk, 0, read);
            }
            file.Flush();
            file.Position = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }
}

/// <summary>An archive body longer than its limit (the host answers 413); nothing of it was read or stored.</summary>
public sealed class ImportBodyTooLargeException(long maxBytes)
    : Exception($"The body is larger than {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
