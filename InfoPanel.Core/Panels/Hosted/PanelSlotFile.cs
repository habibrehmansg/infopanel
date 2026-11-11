using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using InfoPanel.Panels.Sdk;
using Serilog;

namespace InfoPanel.Panels.Hosted;

/// <summary>
/// A hosted session's two frame slots, one file mapped by the engine (to write) and its host (to read), made in the
/// link's slot folder when the session starts (private to the user) and removed when it ends; leftovers are swept at
/// start. Layout, little-endian: the header page (4096 bytes): <c>IPSLOTS1</c>, the layout version (1), the slot count
/// (2), the slot size and the data's offset (4096); at 64 + 32 × k, slot k's length, format and frame id. Slot k's bytes
/// start at 4096 + k × the slot size rounded up to 4096. A slot is the host's from the <c>write</c> that names it until
/// its answer: the engine writes only the other (<see cref="HostedPanelDriver"/>), so neither side locks.
/// </summary>
public sealed unsafe class PanelSlotFile : IDisposable
{
    public const string Extension = ".slots";
    public const int Version = 1;
    public const int Slots = 2;
    public const int HeaderBytes = 4096;
    private const int SlotHeaders = 64;
    private const int SlotHeaderBytes = 32;
    private static ReadOnlySpan<byte> Magic => "IPSLOTS1"u8;

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly bool _owner;
    private bool _disposed;

    private PanelSlotFile(string path, MemoryMappedFile map, MemoryMappedViewAccessor view, int slotSize, bool owner)
    {
        Path = path;
        _map = map;
        _view = view;
        SlotSize = slotSize;
        _owner = owner;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + view.PointerOffset;
    }

    public string Path { get; }

    /// <summary>The most bytes a frame in a slot may be.</summary>
    public int SlotSize { get; }

    private static long Stride(int slotSize) => (slotSize + 4095L) / 4096 * 4096;

    private static long LengthOf(int slotSize) => HeaderBytes + Slots * Stride(slotSize);

    /// <summary>The engine's: a new slot file of <paramref name="slotSize"/> bytes a slot in <paramref name="directory"/>, owner read and write only.</summary>
    public static PanelSlotFile Create(string directory, int slotSize)
    {
        var path = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + Extension);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite | FileShare.Delete,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try
        {
            var length = LengthOf(slotSize);
            stream.SetLength(length);
            var map = MemoryMappedFile.CreateFromFile(stream, null, length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            var file = new PanelSlotFile(path, map, map.CreateViewAccessor(0, length, MemoryMappedFileAccess.ReadWrite), slotSize, owner: true);
            var header = new Span<byte>(file._base, HeaderBytes);
            Magic.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], Version);
            BinaryPrimitives.WriteInt32LittleEndian(header[12..], Slots);
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], slotSize);
            BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeaderBytes);
            return file;
        }
        catch
        {
            stream.Dispose();
            TryDelete(path);
            throw;
        }
    }

    /// <summary>The host's: the engine's slot file at <paramref name="path"/>, read only, its header checked.</summary>
    /// <exception cref="InvalidDataException">It isn't a slot file of this layout.</exception>
    public static PanelSlotFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var length = stream.Length;
            if (length < HeaderBytes)
                throw new InvalidDataException("The slot file is shorter than its header.");
            var map = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            var view = map.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            var header = new byte[24];
            view.ReadArray(0, header, 0, header.Length);
            var slotSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            if (!header.AsSpan(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != Version
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != Slots || slotSize <= 0
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20)) != HeaderBytes || length < LengthOf(slotSize))
            {
                view.Dispose();
                map.Dispose();
                throw new InvalidDataException("The slot file's header isn't this layout's.");
            }
            return new PanelSlotFile(path, map, view, slotSize, owner: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private Span<byte> Header(int slot) => new(_base + SlotHeaders + slot * SlotHeaderBytes, SlotHeaderBytes);

    private Span<byte> Data(int slot) => new(_base + HeaderBytes + slot * Stride(SlotSize), SlotSize);

    /// <summary>The engine's: <paramref name="bytes"/> into <paramref name="slot"/>, then its length, format and frame id.</summary>
    public void Write(int slot, ReadOnlySpan<byte> bytes, PanelFrameFormat format, long frameId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (bytes.Length > SlotSize)
            throw new ArgumentException($"A frame of {bytes.Length} bytes is over the slot's {SlotSize}.", nameof(bytes));
        bytes.CopyTo(Data(slot));
        var header = Header(slot);
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], (int)format);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], frameId);
    }

    /// <summary>The host's: <paramref name="slot"/>'s frame, when it is <paramref name="frameId"/> and within the slot; a view of the mapping, no copy.</summary>
    public bool TryRead(int slot, long frameId, out ReadOnlySpan<byte> bytes, out PanelFrameFormat format)
    {
        bytes = default;
        format = default;
        if (_disposed || slot is < 0 or >= Slots)
            return false;
        var header = Header(slot);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        var kind = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (BinaryPrimitives.ReadInt64LittleEndian(header[8..]) != frameId || length <= 0 || length > SlotSize || !Enum.IsDefined((PanelFrameFormat)kind))
            return false;
        format = (PanelFrameFormat)kind;
        bytes = Data(slot)[..length];
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
        if (_owner)
            TryDelete(Path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // swept at the next start
        }
    }

    /// <summary>Removes the slot files a run before left in <paramref name="directory"/> (regular files named <c>*.slots</c>).</summary>
    public static void Sweep(string directory, ILogger logger)
    {
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*" + Extension))
                if (file.LinkTarget == null)
                    TryDelete(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning("The panel slot folder {Folder:l} couldn't be swept: {Error:l}", directory, ex.Message);
        }
    }
}
