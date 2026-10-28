using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using InfoPanel.Import;

namespace InfoPanel.Core.Tests.Import;

/// <summary>Builds archives for the import tests, hostile ones included, by writing them and patching their headers.</summary>
internal static class ZipKit
{
    /// <summary>An archive of <paramref name="entries"/> (name, bytes), in order; duplicates and odd names written as given.</summary>
    public static byte[] Zip(params (string Name, byte[] Bytes)[] entries) =>
        Zip(CompressionLevel.Optimal, entries);

    public static byte[] Zip(CompressionLevel level, params (string Name, byte[] Bytes)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name, level).Open();
                stream.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    public static (string, byte[]) Text(string name, string text) => (name, Encoding.UTF8.GetBytes(text));

    /// <summary>An archive with one entry whose external attributes are <paramref name="attributes"/>.</summary>
    public static byte[] WithAttributes(string name, byte[] bytes, int attributes)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(name);
            entry.ExternalAttributes = attributes;
            using var stream = entry.Open();
            stream.Write(bytes);
        }
        return buffer.ToArray();
    }

    /// <summary>The offsets of each entry's central directory record, in order.</summary>
    private static List<int> CentralRecords(byte[] zip)
    {
        var end = LastIndexOf(zip, 0x06054b50);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        var result = new List<int>();
        for (var i = 0; i < count; i++)
        {
            result.Add(at);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(at + 32));
            at += 46 + nameLength + extraLength + commentLength;
        }
        return result;
    }

    private static int LastIndexOf(byte[] zip, uint signature)
    {
        for (var i = zip.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == signature)
                return i;
        throw new InvalidDataException("No end record.");
    }

    /// <summary>
    /// Patches entry <paramref name="index"/> in both its central and local headers: a 16-bit field at
    /// <paramref name="centralOffset"/> in the central record and <paramref name="localOffset"/> in the local one.
    /// </summary>
    private static void Patch16(byte[] zip, int index, int centralOffset, int localOffset, Func<ushort, ushort> change)
    {
        var central = CentralRecords(zip)[index];
        var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(central + 42));
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(central + centralOffset), change(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(central + centralOffset))));
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(local + localOffset), change(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + localOffset))));
    }

    private static void Patch32(byte[] zip, int index, int centralOffset, int localOffset, uint value)
    {
        var central = CentralRecords(zip)[index];
        var local = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(central + 42));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + centralOffset), value);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(local + localOffset), value);
    }

    /// <summary>Sets the encrypted flag (general purpose bit 0) of entry <paramref name="index"/>.</summary>
    public static byte[] Encrypted(byte[] zip, int index = 0)
    {
        var copy = (byte[])zip.Clone();
        Patch16(copy, index, 8, 6, flags => (ushort)(flags | 1));
        return copy;
    }

    /// <summary>Sets the compression method of entry <paramref name="index"/> (12 is bzip2, which .NET can't read).</summary>
    public static byte[] WithMethod(byte[] zip, ushort method, int index = 0)
    {
        var copy = (byte[])zip.Clone();
        Patch16(copy, index, 10, 8, _ => method);
        return copy;
    }

    /// <summary>Makes entry <paramref name="index"/>'s headers claim <paramref name="size"/> uncompressed bytes.</summary>
    public static byte[] ClaimingSize(byte[] zip, uint size, int index = 0)
    {
        var copy = (byte[])zip.Clone();
        Patch32(copy, index, 24, 22, size);
        return copy;
    }

    /// <summary>Makes the end record claim <paramref name="entries"/> entries (on this disk and in total).</summary>
    public static byte[] ClaimingEntries(byte[] zip, ushort entries)
    {
        var copy = (byte[])zip.Clone();
        var end = LastIndexOf(copy, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(end + 8), entries);
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(end + 10), entries);
        return copy;
    }

    /// <summary>Makes the end record claim a central directory of <paramref name="bytes"/> bytes.</summary>
    public static byte[] ClaimingDirectorySize(byte[] zip, uint bytes)
    {
        var copy = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(LastIndexOf(copy, 0x06054b50) + 12), bytes);
        return copy;
    }

    /// <summary>
    /// Inserts a Zip64 end record and its locator before the end record. The Zip64 record declares
    /// <paramref name="entries"/> entries and the directory's true size and offset. The end record keeps its own fields,
    /// except a sentinel: <paramref name="sentinelOffset"/> sets its directory offset to <c>0xFFFFFFFF</c>, and
    /// <paramref name="sentinelDisk"/> both its disk numbers to <c>0xFFFF</c>.
    /// </summary>
    public static byte[] WithZip64(byte[] zip, ulong entries, bool sentinelOffset = false, bool sentinelDisk = false)
    {
        var end = LastIndexOf(zip, 0x06054b50);
        var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 12));
        var directoryAt = BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));

        var record = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(record, 0x06064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(12), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), 45);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), entries);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(40), directorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(48), directoryAt);

        var locator = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)end);
        BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);

        var endRecord = zip[end..];
        if (sentinelOffset)
            BinaryPrimitives.WriteUInt32LittleEndian(endRecord.AsSpan(16), 0xFFFFFFFF);
        if (sentinelDisk)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(4), 0xFFFF);
            BinaryPrimitives.WriteUInt16LittleEndian(endRecord.AsSpan(6), 0xFFFF);
        }
        return [.. zip.AsSpan(0, end), .. record, .. locator, .. endRecord];
    }

    public static ArchiveReader Open(byte[] zip, ImportLimits? limits = null, ImportBudget? budget = null)
    {
        limits ??= ImportLimits.Default;
        return ArchiveReader.Open(new MemoryStream(zip), limits, budget ?? new ImportBudget(limits.ExpandedBytes));
    }

    /// <summary>The code of the refusal <paramref name="act"/> throws.</summary>
    public static ImportRefusal Refused(Action act) => Xunit.Assert.Throws<ImportRefusal>(act);
}
