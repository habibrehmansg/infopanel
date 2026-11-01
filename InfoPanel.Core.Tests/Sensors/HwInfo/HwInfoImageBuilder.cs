using System.Buffers.Binary;
using System.Text;
using InfoPanel.Sensors.HwInfo;

namespace InfoPanel.Core.Tests.Sensors.HwInfo;

/// <summary>
/// Builds HWiNFO shared-memory images for tests, in the layout HWiNFO 8.54 writes (header 48 bytes with the polling
/// period, sensor elements of 392 and reading elements of 460 bytes, with the UTF-8 fields) or, with
/// <see cref="Utf8"/> false, 1.x's (44, 264, 316). Strings are written as given in Windows-1252 (and UTF-8 in the
/// UTF-8 fields); <see cref="Raw"/> lets a test put any bytes in a field.
/// </summary>
internal sealed class HwInfoImageBuilder
{
    public sealed record Sensor(uint Id, uint Instance, string Name, string? UserName = null);

    public sealed record Reading(uint SensorIndex, uint Id, string Name, string Unit, double Value, double Min, double Max, double Avg, uint Type = 1, string? UserName = null)
    {
        /// <summary>When set, the unit's Windows-1252 field holds these bytes instead.</summary>
        public byte[]? UnitBytes1252 { get; init; }

        /// <summary>When set, the label's Windows-1252 field holds these bytes instead (exactly 128 of them, say, for no NUL).</summary>
        public byte[]? NameBytes { get; init; }
    }

    public List<Sensor> Sensors { get; } = [];
    public List<Reading> Readings { get; } = [];
    public bool Utf8 { get; set; } = true;
    public uint Signature { get; set; } = HwInfoLayout.SignatureLive;
    public long PollTime { get; set; } = 1_793_508_823;
    public uint PollingPeriodMs { get; set; } = 2000;
    public uint Version { get; set; } = 2;
    public uint Revision { get; set; } = 1;
    /// <summary>Overrides of the header's counts, sizes and offsets (written as given; the body follows the real ones).</summary>
    public uint? SensorCountOverride { get; set; }
    public uint? ReadingCountOverride { get; set; }
    public uint? SensorOffsetOverride { get; set; }
    public uint? ReadingOffsetOverride { get; set; }
    public uint? SensorSizeOverride { get; set; }
    public uint? ReadingSizeOverride { get; set; }
    /// <summary>Extra bytes after the readings (a mapping larger than its data, as HWiNFO's is).</summary>
    public int Slack { get; set; }

    public int HeaderSize => Utf8 ? HwInfoLayout.HeaderSizeWithPeriod : HwInfoLayout.HeaderSize;
    public int SensorSize => Utf8 ? HwInfoLayout.SensorSizeWithUtf8 : HwInfoLayout.SensorSize;
    public int ReadingSize => Utf8 ? HwInfoLayout.ReadingSizeWithUtf8 : HwInfoLayout.ReadingSize;

    public HwInfoImageBuilder AddSensor(uint id, uint instance, string name)
    {
        Sensors.Add(new Sensor(id, instance, name));
        return this;
    }

    public HwInfoImageBuilder AddReading(uint sensorIndex, uint id, string name, string unit, double value, double min = 0, double max = 0, double avg = 0)
    {
        Readings.Add(new Reading(sensorIndex, id, name, unit, value, min, max, avg));
        return this;
    }

    public byte[] Build()
    {
        var sensorOffset = HeaderSize;
        var readingOffset = sensorOffset + SensorSize * Sensors.Count;
        var image = new byte[readingOffset + ReadingSize * Readings.Count + Slack];
        var span = image.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Revision);
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], PollTime);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], SensorOffsetOverride ?? (uint)sensorOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], SensorSizeOverride ?? (uint)SensorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], SensorCountOverride ?? (uint)Sensors.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], ReadingOffsetOverride ?? (uint)readingOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], ReadingSizeOverride ?? (uint)ReadingSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], ReadingCountOverride ?? (uint)Readings.Count);
        if (Utf8)
            BinaryPrimitives.WriteUInt32LittleEndian(span[44..], PollingPeriodMs);

        for (var i = 0; i < Sensors.Count; i++)
        {
            var e = span.Slice(sensorOffset + SensorSize * i, SensorSize);
            var s = Sensors[i];
            BinaryPrimitives.WriteUInt32LittleEndian(e, s.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], s.Instance);
            Write1252(e.Slice(8, 128), s.Name);
            Write1252(e.Slice(136, 128), s.UserName ?? s.Name);
            if (Utf8)
                WriteUtf8(e.Slice(264, 128), s.UserName ?? s.Name);
        }

        for (var i = 0; i < Readings.Count; i++)
        {
            var e = span.Slice(readingOffset + ReadingSize * i, ReadingSize);
            var r = Readings[i];
            BinaryPrimitives.WriteUInt32LittleEndian(e, r.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], r.SensorIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], r.Id);
            if (r.NameBytes != null)
                r.NameBytes.CopyTo(e.Slice(12, 128));
            else
                Write1252(e.Slice(12, 128), r.Name);
            Write1252(e.Slice(140, 128), r.UserName ?? r.Name);
            if (r.UnitBytes1252 != null)
                r.UnitBytes1252.CopyTo(e.Slice(268, 16));
            else
                Write1252(e.Slice(268, 16), r.Unit);
            BinaryPrimitives.WriteDoubleLittleEndian(e[284..], r.Value);
            BinaryPrimitives.WriteDoubleLittleEndian(e[292..], r.Min);
            BinaryPrimitives.WriteDoubleLittleEndian(e[300..], r.Max);
            BinaryPrimitives.WriteDoubleLittleEndian(e[308..], r.Avg);
            if (Utf8)
            {
                WriteUtf8(e.Slice(316, 128), r.UserName ?? r.Name);
                WriteUtf8(e.Slice(444, 16), r.Unit);
            }
        }

        return image;
    }

    /// <summary>A DEAD image: HWiNFO's signature after it stopped, the rest as built.</summary>
    public byte[] BuildDead()
    {
        var image = Build();
        BinaryPrimitives.WriteUInt32LittleEndian(image, HwInfoLayout.SignatureDead);
        return image;
    }

    private static void Write1252(Span<byte> field, string text)
    {
        field.Clear();
        for (var i = 0; i < text.Length && i < field.Length; i++)
        {
            var c = text[i];
            field[i] = c switch
            {
                '€' => 0x80,
                < (char)0x100 => (byte)c,
                _ => (byte)'?',
            };
        }
    }

    private static void WriteUtf8(Span<byte> field, string text)
    {
        field.Clear();
        var bytes = Encoding.UTF8.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
    }

    /// <summary>A field's worth of bytes: <paramref name="text"/> in ASCII, then <paramref name="fill"/> to the end.</summary>
    public static byte[] Raw(int length, string text, byte fill = 0)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        Encoding.ASCII.GetBytes(text).AsSpan(0, Math.Min(text.Length, length)).CopyTo(bytes);
        return bytes;
    }
}

/// <summary>
/// Named mappings in memory: <see cref="Set"/> makes or replaces one, <see cref="Remove"/> takes the name away
/// (a view already open keeps reading what it had, as a Windows section stays while a handle holds it).
/// </summary>
internal sealed class FakeHwInfoMemory : IHwInfoMemoryOpener
{
    private sealed class Mapping
    {
        public byte[] Bytes = [];
    }

    private sealed class View(Mapping mapping, FakeHwInfoMemory owner) : IHwInfoMemory
    {
        public long Length => mapping.Bytes.Length;

        public void Read(long offset, Span<byte> destination)
        {
            owner.Reads++;
            owner.BeforeRead?.Invoke(owner.Reads);
            mapping.Bytes.AsSpan((int)offset, destination.Length).CopyTo(destination);
        }

        public void Dispose() => owner.OpenViews--;
    }

    private readonly Dictionary<string, Mapping> _mappings = [];

    public int Opens { get; private set; }
    public int OpenViews { get; private set; }
    public int Reads { get; private set; }
    public List<string> Asked { get; } = [];
    public bool Deny { get; set; }

    /// <summary>The names this user may not open (as <see cref="Deny"/>, for these only).</summary>
    public HashSet<string> Denied { get; } = [];

    /// <summary>Called with <see cref="Reads"/> (this read counted) before each read copies: a test changes a mapping between two reads.</summary>
    public Action<int>? BeforeRead { get; set; }

    /// <summary>Replaces the named mapping's bytes (an open view sees them).</summary>
    public void Set(string name, byte[] bytes)
    {
        if (!_mappings.TryGetValue(name, out var mapping))
            _mappings[name] = mapping = new Mapping();
        mapping.Bytes = bytes;
    }

    /// <summary>The name goes; open views keep their bytes.</summary>
    public void Remove(string name) => _mappings.Remove(name);

    public IHwInfoMemory? TryOpen(string name)
    {
        Asked.Add(name);
        if (Deny || Denied.Contains(name))
            throw new UnauthorizedAccessException("denied");
        if (!_mappings.TryGetValue(name, out var mapping))
            return null;
        Opens++;
        OpenViews++;
        return new View(mapping, this);
    }
}
