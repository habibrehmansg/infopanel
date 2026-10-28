using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace InfoPanel.Import;

/// <summary>
/// An entry of an archive that passed the <see cref="ArchiveReader"/> guards. <see cref="Segments"/> is the name split
/// on <c>/</c> and <c>\</c> (1.x on Windows wrote <c>\</c>); a directory entry's name ends with a separator.
/// </summary>
public sealed class ArchiveEntry
{
    internal ArchiveEntry(ZipArchiveEntry zip, string name, string[] segments, bool isDirectory, int index)
    {
        Zip = zip;
        Name = name;
        Segments = segments;
        IsDirectory = isDirectory;
        Index = index;
    }

    internal ZipArchiveEntry Zip { get; }

    /// <summary>The entry's name exactly as stored.</summary>
    public string Name { get; }

    /// <summary>The name's segments, split on both separators; a directory entry's last (empty) segment left out.</summary>
    public IReadOnlyList<string> Segments { get; }

    public bool IsDirectory { get; }

    /// <summary>The entry's position in the archive's central directory.</summary>
    public int Index { get; }

    /// <summary>The size the entry's header claims. Never trusted: reads count the bytes they actually get.</summary>
    public long DeclaredLength => Zip.Length;
}

/// <summary>
/// The guarded zip reader both archive kinds share (a 1.x <c>.infopanel</c> and the 2.0 profile archive). Opening
/// checks the container and every entry's name before any entry is read:
/// <list type="bullet">
/// <item>not a zip: <c>archive.zip</c>; more entries than <see cref="ImportLimits.Entries"/>, declared by the end
/// records or found by walking the central directory's records before .NET reads them, or a record over 4,096 bytes:
/// <c>archive.entries</c>;</item>
/// <item>a name over <see cref="ImportLimits.EntryNameBytes"/> UTF-8 bytes, with NUL or <c>:</c>, rooted (<c>/</c>,
/// <c>\</c>), or with an empty (other than a directory's trailing one), <c>.</c> or <c>..</c> segment:
/// <c>archive.name</c>;</item>
/// <item>two entries with exactly the same name: <c>archive.duplicate</c>; a symbolic link (Unix mode <c>0xA000</c> in
/// the external attributes): <c>archive.link</c>; an encrypted entry: <c>archive.encrypted</c>.</item>
/// </list>
/// Reading counts every decompressed byte against the request's <see cref="ImportBudget"/> (<c>archive.expanded</c>);
/// an entry whose method can't be read is <c>archive.method</c>, and data that doesn't decompress <c>archive.zip</c>.
/// Every refusal is an <see cref="ImportRefusal"/>. The archive kinds add their own name rules on top.
/// </summary>
public sealed class ArchiveReader : IDisposable
{
    /// <summary>Unix file type bits in the high half of a zip entry's external attributes, and a symbolic link's type.</summary>
    private const int UnixTypeMask = 0xF000, UnixSymlink = 0xA000;

    /// <summary>
    /// The largest central directory record an entry may take, checked on every record before .NET reads the directory
    /// (and, times the entry limit, on the declared directory size): 46 fixed bytes, a name of at most 1 KiB, and room
    /// for extra fields and a comment.
    /// </summary>
    private const long CentralRecordBytes = 4096;

    private readonly ZipArchive _zip;
    private readonly ImportBudget _budget;

    private ArchiveReader(ZipArchive zip, IReadOnlyList<ArchiveEntry> entries, ImportBudget budget)
    {
        _zip = zip;
        Entries = entries;
        _budget = budget;
    }

    /// <summary>Every entry, in central directory order.</summary>
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>Opens and checks <paramref name="stream"/>, which must be seekable; it stays open after disposal.</summary>
    /// <exception cref="ImportRefusal">The container or an entry's name breaks a guard.</exception>
    public static ArchiveReader Open(Stream stream, ImportLimits limits, ImportBudget budget)
    {
        if (!stream.CanSeek || !stream.CanRead)
            throw new ArgumentException("The archive must be a readable, seekable stream.", nameof(stream));

        CheckDirectory(stream, limits);
        stream.Position = 0;

        ZipArchive zip;
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null);
        }
        catch (InvalidDataException ex)
        {
            throw new ImportRefusal("archive.zip", "The file isn't a zip archive: " + ex.Message, inner: ex);
        }

        try
        {
            var entries = Check(zip, limits);
            return new ArchiveReader(zip, entries, budget);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    private static List<ArchiveEntry> Check(ZipArchive zip, ImportLimits limits)
    {
        IReadOnlyList<ZipArchiveEntry> raw;
        try
        {
            raw = zip.Entries;
        }
        catch (InvalidDataException ex)
        {
            throw new ImportRefusal("archive.zip", "The archive's directory can't be read: " + ex.Message, inner: ex);
        }

        if (raw.Count > limits.Entries)
            throw new ImportRefusal("archive.entries", $"The archive has {raw.Count} entries; at most {limits.Entries} are allowed.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ArchiveEntry>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var zipEntry = raw[i];
            var name = zipEntry.FullName;
            if (CheckName(name, limits) is { } problem)
                throw ImportRefusal.Entry("archive.name", name, $"The entry name is refused: {problem}.");
            if (!seen.Add(name))
                throw ImportRefusal.Entry("archive.duplicate", name, "Two entries have this name.");
            if (((zipEntry.ExternalAttributes >>> 16) & UnixTypeMask) == UnixSymlink)
                throw ImportRefusal.Entry("archive.link", name, "The entry is a symbolic link.");
            if (zipEntry.IsEncrypted)
                throw ImportRefusal.Entry("archive.encrypted", name, "The entry is encrypted.");

            var isDirectory = name.EndsWith('/') || name.EndsWith('\\');
            var segments = Split(isDirectory ? name[..^1] : name);
            result.Add(new ArchiveEntry(zipEntry, name, segments, isDirectory, i));
        }
        return result;
    }

    /// <summary>Why an entry name is refused, or null. Shared by both archive kinds.</summary>
    internal static string? CheckName(string name, ImportLimits limits)
    {
        if (name.Length == 0 || name[0] is '/' or '\\')
            return "it is empty or rooted";
        if (Encoding.UTF8.GetByteCount(name) > limits.EntryNameBytes)
            return $"it is longer than {limits.EntryNameBytes} UTF-8 bytes";
        if (name.Contains('\0'))
            return "it holds NUL";
        if (name.Contains(':'))
            return "it holds ':' (a drive or stream name)";

        var body = name.EndsWith('/') || name.EndsWith('\\') ? name[..^1] : name;
        foreach (var segment in Split(body))
        {
            if (segment.Length == 0)
                return "it has an empty segment";
            if (segment is "." or "..")
                return $"it has a '{segment}' segment";
        }
        return null;
    }

    private static string[] Split(string name) => name.Split('/', '\\');

    /// <summary>
    /// The pre-check before .NET reads the central directory into memory. It finds the end record as .NET does, and
    /// reads the Zip64 end record whenever its locator sits right before the end record, whatever the end record's
    /// fields hold, so it never depends on which sentinels make .NET follow it. Every declared count over
    /// <see cref="ImportLimits.Entries"/>, or a declared directory too large for that many records, is
    /// <c>archive.entries</c>. Then it walks the records themselves from each directory start either record declares,
    /// as .NET reads them (46 fixed bytes, while the signature matches), never trusting a declared size: more records
    /// than the limit, or one over <see cref="CentralRecordBytes"/>, is <c>archive.entries</c>; a raw name over
    /// <see cref="ImportLimits.EntryNameBytes"/> is <c>archive.name</c>; a record past the end of the file is
    /// <c>archive.zip</c>. Nothing is allocated per record and no name is read. An end record that can't be found is
    /// left to the zip reader, which refuses the archive.
    /// </summary>
    internal static void CheckDirectory(Stream stream, ImportLimits limits)
    {
        const int EndRecordBytes = 22, LocatorBytes = 20, Zip64RecordBytes = 56;
        // .NET looks back over the longest comment and the signature from the end; a few bytes more only add
        // candidates that are reached when no nearer end record exists, and .NET then finds none.
        const int SearchBytes = EndRecordBytes + 0xFFFF + 64;
        var length = stream.Length;
        if (length < EndRecordBytes)
            return;

        var tailLength = (int)Math.Min(length, SearchBytes);
        var tail = new byte[tailLength];
        var tailAt = length - tailLength;
        stream.Position = tailAt;
        stream.ReadExactly(tail);

        var at = tailLength - EndRecordBytes;
        while (at >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(at)) != 0x06054b50)
            at--;
        if (at < 0)
            return;

        var end = tail.AsSpan(at);
        long first = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]), second = -1;
        var zip64 = false;

        var endAt = tailAt + at;
        if (endAt >= LocatorBytes)
        {
            Span<byte> locator = stackalloc byte[LocatorBytes];
            stream.Position = endAt - LocatorBytes;
            stream.ReadExactly(locator);
            var zip64At = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == 0x07064b50 && length >= Zip64RecordBytes
                && zip64At <= (ulong)(length - Zip64RecordBytes))
            {
                Span<byte> record = stackalloc byte[Zip64RecordBytes];
                stream.Position = (long)zip64At;
                stream.ReadExactly(record);
                if (BinaryPrimitives.ReadUInt32LittleEndian(record) == 0x06064b50)
                {
                    zip64 = true;
                    CheckDeclared(BinaryPrimitives.ReadUInt64LittleEndian(record[24..]), BinaryPrimitives.ReadUInt64LittleEndian(record[32..]),
                        BinaryPrimitives.ReadUInt64LittleEndian(record[40..]), limits);
                    var offset = BinaryPrimitives.ReadUInt64LittleEndian(record[48..]);
                    if (offset < (ulong)length && (long)offset != first)
                        second = (long)offset;
                }
            }
        }

        // The end record's own fields too; with a Zip64 record, a sentinel (0xFFFF, 0xFFFFFFFF) defers to it.
        ulong onDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]), total = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
        ulong directoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
        CheckDeclared(zip64 && onDisk == 0xFFFF ? 0 : onDisk, zip64 && total == 0xFFFF ? 0 : total,
            zip64 && directoryBytes == 0xFFFFFFFF ? 0 : directoryBytes, limits);

        Walk(stream, first, length, limits);
        if (second >= 0)
            Walk(stream, second, length, limits);
    }

    private static void CheckDeclared(ulong entriesOnDisk, ulong entries, ulong directoryBytes, ImportLimits limits)
    {
        var declared = Math.Max(entriesOnDisk, entries);
        if (declared > (ulong)limits.Entries)
            throw new ImportRefusal("archive.entries", $"The archive declares {declared} entries; at most {limits.Entries} are allowed.");
        if (directoryBytes > (ulong)(limits.Entries * CentralRecordBytes))
            throw new ImportRefusal("archive.entries", $"The archive's central directory is {directoryBytes} bytes, too large for {limits.Entries} entries.");
    }

    /// <summary>Walks the central directory records from <paramref name="start"/>; a start outside the file walks nothing.</summary>
    private static void Walk(Stream stream, long start, long length, ImportLimits limits)
    {
        const int FixedBytes = 46;
        Span<byte> header = stackalloc byte[FixedBytes];
        var at = start;
        var count = 0;
        while (at >= 0 && at <= length - FixedBytes)
        {
            stream.Position = at;
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                return;

            if (++count > limits.Entries)
                throw new ImportRefusal("archive.entries", $"The archive's central directory has more than {limits.Entries} entries.");
            int name = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            if (name > limits.EntryNameBytes)
                throw new ImportRefusal("archive.name", $"An entry name is {name} bytes, longer than {limits.EntryNameBytes} UTF-8 bytes.");
            long size = FixedBytes + name + BinaryPrimitives.ReadUInt16LittleEndian(header[30..]) + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (size > CentralRecordBytes)
                throw new ImportRefusal("archive.entries", $"A central directory record is {size} bytes; at most {CentralRecordBytes} are allowed.");
            if (at + size > length)
                throw new ImportRefusal("archive.zip", "A central directory record runs past the end of the file.");
            at += size;
        }
    }

    /// <summary>
    /// Reads <paramref name="entry"/> into memory, counting every decompressed byte against the request's budget. Returns
    /// null when it holds more than <paramref name="maxBytes"/>; reading stops there.
    /// </summary>
    /// <exception cref="ImportRefusal">
    /// <c>archive.expanded</c> (the request's budget), <c>archive.method</c> (the entry can't be opened) or
    /// <c>archive.zip</c> (its data doesn't decompress).
    /// </exception>
    public byte[]? Read(ArchiveEntry entry, long maxBytes)
    {
        using var source = Open(entry);
        try
        {
            return _budget.ReadAll(source, maxBytes);
        }
        catch (ImportBudgetExceededException ex)
        {
            throw Expanded(ex);
        }
        catch (InvalidDataException ex)
        {
            throw ImportRefusal.Entry("archive.zip", entry.Name, "The entry's data can't be decompressed: " + ex.Message, ex);
        }
    }

    private Stream Open(ArchiveEntry entry)
    {
        try
        {
            return entry.Zip.Open();
        }
        catch (InvalidDataException ex)
        {
            throw ImportRefusal.Entry("archive.method", entry.Name, "The entry can't be read (its compression method or header): " + ex.Message, ex);
        }
    }

    private ImportRefusal Expanded(Exception inner) =>
        new("archive.expanded", "The archive decompresses to more than the import limit.", inner: inner);

    public void Dispose() => _zip.Dispose();
}
