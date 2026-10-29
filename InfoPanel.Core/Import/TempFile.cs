using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
    /// start, for <see cref="ArchiveReader"/>. Nothing more than <paramref name="maxBytes"/> plus one byte is read, whether
    /// or not the body states its length, and nothing over <paramref name="maxBytes"/> is written.
    /// </summary>
    /// <exception cref="ImportBodyTooLargeException">The body is longer than <paramref name="maxBytes"/>; the file is gone.</exception>
    public static FileStream Buffer(Stream body, long maxBytes)
    {
        var file = Create();
        try
        {
            var chunk = new byte[(int)Math.Min(81920, maxBytes + 1)];
            long total = 0;
            int read;
            // Never more than the allowance left plus one byte: the extra byte is what tells a body is over.
            while ((read = body.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes + 1 - total))) > 0)
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

    /// <summary>
    /// <see cref="Buffer"/>, reading <paramref name="body"/> asynchronously (a request's body): nothing more than
    /// <paramref name="maxBytes"/> plus one byte is read, and the file is gone when the body is too long.
    /// </summary>
    /// <exception cref="ImportBodyTooLargeException">The body is longer than <paramref name="maxBytes"/>.</exception>
    public static async Task<FileStream> BufferAsync(Stream body, long maxBytes, CancellationToken ct)
    {
        var file = Create();
        try
        {
            var chunk = new byte[(int)Math.Min(81920, maxBytes + 1)];
            long total = 0;
            int read;
            // Never more than the allowance left plus one byte: the extra byte is what tells a body is over.
            while ((read = await body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes + 1 - total)), ct)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new ImportBodyTooLargeException(maxBytes);
                await file.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            await file.FlushAsync(ct);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
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
