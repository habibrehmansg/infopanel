using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.HwInfo;

/// <summary>One open, read-only view of a shared-memory mapping.</summary>
public interface IHwInfoMemory : IDisposable
{
    /// <summary>The bytes the view can read.</summary>
    long Length { get; }

    /// <summary>Copies <paramref name="destination"/>'s length of bytes from <paramref name="offset"/>, within <see cref="Length"/>.</summary>
    void Read(long offset, Span<byte> destination);
}

/// <summary>Opens HWiNFO's mappings by name: the real ones on Windows, a recording's or a test's elsewhere.</summary>
public interface IHwInfoMemoryOpener
{
    /// <summary>
    /// The mapping called <paramref name="name"/>, opened for reading, or null when there is none. Throws
    /// <see cref="UnauthorizedAccessException"/> when one exists that this user may not read.
    /// </summary>
    IHwInfoMemory? TryOpen(string name);
}

/// <summary>Windows' named mappings, opened read-only by name (no privilege needed, only the mapping's permissions).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHwInfoMemoryOpener : IHwInfoMemoryOpener
{
    public IHwInfoMemory? TryOpen(string name)
    {
        MemoryMappedFile? file = null;
        try
        {
            file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            var view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new View(file, view);
        }
        catch (FileNotFoundException)
        {
            file?.Dispose();
            return null;
        }
        catch
        {
            file?.Dispose();
            throw;
        }
    }

    private sealed class View(MemoryMappedFile file, MemoryMappedViewAccessor view) : IHwInfoMemory
    {
        public long Length => view.Capacity;

        public void Read(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (offset > Length - destination.Length)
                throw new ArgumentOutOfRangeException(nameof(offset), "The read ends past the view.");
            view.SafeMemoryMappedViewHandle.ReadSpan((ulong)(view.PointerOffset + offset), destination);
        }

        public void Dispose()
        {
            view.Dispose();
            file.Dispose();
        }
    }
}
