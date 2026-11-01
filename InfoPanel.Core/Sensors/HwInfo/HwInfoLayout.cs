using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace InfoPanel.Sensors.HwInfo;

/// <summary>How a copy of HWiNFO's shared memory reads: <see cref="HwInfoLayout.ReadHeader"/>.</summary>
public enum HwInfoImageState
{
    /// <summary>Signature <c>HWiS</c> and every count, size and offset within bounds.</summary>
    Ok,

    /// <summary>Signature <c>DEAD</c>: HWiNFO stopped writing (it closed, or its shared memory was turned off).</summary>
    Dead,

    /// <summary>Not an image this reader can trust: an unknown signature, or something out of bounds.</summary>
    Refused,
}

/// <summary>The header of HWiNFO's shared memory (<c>HWiNFO_SENSORS_SHARED_MEM2</c>), as read and checked.</summary>
public readonly record struct HwInfoHeader(
    uint Version,
    uint Revision,
    long PollTime,
    uint SensorOffset,
    uint SensorSize,
    uint SensorCount,
    uint ReadingOffset,
    uint ReadingSize,
    uint ReadingCount,
    uint PollingPeriodMs)
{
    /// <summary>The bytes from the start of the mapping to the end of the last section: what a round copies.</summary>
    public long End => Math.Max((long)SensorOffset + (long)SensorSize * SensorCount, (long)ReadingOffset + (long)ReadingSize * ReadingCount);

    /// <summary>Whether the elements carry HWiNFO's UTF-8 fields (the user's names and the unit).</summary>
    public bool HasUtf8Unit => ReadingSize >= HwInfoLayout.ReadingSizeWithUtf8;
}

/// <summary>One sensor (a device: "CPU [#0]: AMD Ryzen 7 8845HS") in an image.</summary>
public readonly record struct HwInfoSensor(uint Id, uint Instance, string Name);

/// <summary>One reading in an image, its sensor's position and its strings decoded.</summary>
public readonly record struct HwInfoReading(
    uint Type,
    uint SensorIndex,
    uint Id,
    string Name,
    string Unit,
    double Value,
    double Min,
    double Max,
    double Avg);

/// <summary>An image read whole: its header, sensors and the readings whose sensor exists, in HWiNFO's order.</summary>
public sealed record HwInfoSnapshot(HwInfoHeader Header, IReadOnlyList<HwInfoSensor> Sensors, IReadOnlyList<HwInfoReading> Readings, int Skipped);

/// <summary>
/// HWiNFO's shared memory, read by hand from a copy of its bytes (another process's bytes, so every count, size and
/// offset is checked before it is used, and nothing is read through <c>Marshal</c>). The layout, little-endian and
/// packed, is HWiNFO's <c>HWiNFO_SENSORS_SHARED_MEM2</c>, as 1.x read it and as HWiNFO 8.54 writes it (version 2,
/// revision 1):
/// <list type="bullet">
/// <item>header: signature (bytes <c>HWiS</c>; <c>DEAD</c> once HWiNFO stops), version, revision, poll time (64-bit
/// seconds since 1970), sensor section offset, element size and count, reading section offset, element size and count
/// (44 bytes), then, when the sections start at 48 or later, HWiNFO's polling period in milliseconds;</item>
/// <item>sensor element: id, instance, original name (128, ANSI), the user's name (128, ANSI) (264 bytes), then the
/// user's name in UTF-8 (128) (392);</item>
/// <item>reading element: type, sensor index, reading id, original label (128, ANSI), the user's label (128, ANSI),
/// unit (16, ANSI), value, min, max, average (doubles) (316 bytes), then the user's label (128) and the unit (16) in
/// UTF-8 (460).</item>
/// </list>
/// Element sizes come from the header, so a later HWiNFO with longer elements still reads by the known prefix. Names
/// are the original ones (what 1.x stored as a binding's sensor name), read as Windows-1252; the unit is the UTF-8
/// one when the element has it, else Windows-1252.
/// </summary>
public static class HwInfoLayout
{
    /// <summary>The header's known part, without the polling period.</summary>
    public const int HeaderSize = 44;

    /// <summary>The header with the polling period (version 2).</summary>
    public const int HeaderSizeWithPeriod = 48;

    public const int SensorSize = 264;
    public const int SensorSizeWithUtf8 = 392;
    public const int ReadingSize = 316;
    public const int ReadingSizeWithUtf8 = 460;

    public const int StringSize = 128;
    public const int UnitSize = 16;

    public const int MaxSensors = 4096;
    public const int MaxReadings = 65536;
    public const int MaxElementSize = 4096;
    public const long MaxImageBytes = 32L * 1024 * 1024;

    /// <summary>The bytes <c>HWiS</c>.</summary>
    public const uint SignatureLive = 0x53695748;

    /// <summary>
    /// <c>DEAD</c>, as HWiNFO writes the multi-character constant (bytes <c>DAED</c>; what 1.x compared its reversed
    /// bytes with). <see cref="SignatureDeadBytes"/> is the same word spelt as bytes, also taken as dead.
    /// </summary>
    public const uint SignatureDead = 0x44454144;

    /// <summary>The bytes <c>DEAD</c>.</summary>
    public const uint SignatureDeadBytes = 0x44414544;

    // Sensor element.
    private const int SensorId = 0;
    private const int SensorInstance = 4;
    private const int SensorNameOrig = 8;

    // Reading element.
    private const int ReadingType = 0;
    private const int ReadingSensorIndex = 4;
    private const int ReadingId = 8;
    private const int ReadingLabelOrig = 12;
    private const int ReadingUnit = 268;
    private const int ReadingValue = 284;
    private const int ReadingMin = 292;
    private const int ReadingMax = 300;
    private const int ReadingAvg = 308;
    private const int ReadingUtf8Unit = 444;

    /// <summary>
    /// Reads and checks the header of <paramref name="image"/>, a copy of the mapping's first bytes;
    /// <paramref name="mappingLength"/> is the whole mapping's. <see cref="HwInfoImageState.Ok"/> means every section
    /// lies within the mapping and the caps (<see cref="HwInfoHeader.End"/> bytes to copy); <paramref name="problem"/>
    /// says why an image is refused.
    /// </summary>
    public static HwInfoImageState ReadHeader(ReadOnlySpan<byte> image, long mappingLength, out HwInfoHeader header, out string? problem)
    {
        header = default;
        problem = null;
        if (image.Length < 4)
        {
            problem = $"the mapping holds {image.Length} bytes, less than a signature";
            return HwInfoImageState.Refused;
        }

        var signature = BinaryPrimitives.ReadUInt32LittleEndian(image);
        if (signature is SignatureDead or SignatureDeadBytes)
            return HwInfoImageState.Dead;
        if (signature != SignatureLive)
        {
            problem = $"unknown signature 0x{signature:x8}";
            return HwInfoImageState.Refused;
        }

        if (image.Length < HeaderSize)
        {
            problem = $"the mapping holds {image.Length} bytes, less than a header";
            return HwInfoImageState.Refused;
        }

        var sensorOffset = U32(image, 20);
        var readingOffset = U32(image, 32);
        var hasPeriod = sensorOffset >= HeaderSizeWithPeriod && readingOffset >= HeaderSizeWithPeriod && image.Length >= HeaderSizeWithPeriod;
        header = new HwInfoHeader(
            Version: U32(image, 4),
            Revision: U32(image, 8),
            PollTime: BinaryPrimitives.ReadInt64LittleEndian(image[12..]),
            SensorOffset: sensorOffset,
            SensorSize: U32(image, 24),
            SensorCount: U32(image, 28),
            ReadingOffset: readingOffset,
            ReadingSize: U32(image, 36),
            ReadingCount: U32(image, 40),
            PollingPeriodMs: hasPeriod ? U32(image, 44) : 0);

        problem = Check(header, mappingLength);
        return problem == null ? HwInfoImageState.Ok : HwInfoImageState.Refused;
    }

    private static string? Check(in HwInfoHeader h, long mappingLength)
    {
        if (h.SensorCount > MaxSensors)
            return $"{h.SensorCount} sensors, over {MaxSensors}";
        if (h.ReadingCount > MaxReadings)
            return $"{h.ReadingCount} readings, over {MaxReadings}";
        if (h.SensorSize is < SensorSize or > MaxElementSize)
            return $"a sensor element of {h.SensorSize} bytes, not {SensorSize} to {MaxElementSize}";
        if (h.ReadingSize is < ReadingSize or > MaxElementSize)
            return $"a reading element of {h.ReadingSize} bytes, not {ReadingSize} to {MaxElementSize}";
        if (h.SensorOffset < HeaderSize || h.ReadingOffset < HeaderSize)
            return "a section inside the header";
        var end = h.End;
        if (end > MaxImageBytes)
            return $"{end} bytes to copy, over {MaxImageBytes}";
        if (end > mappingLength)
            return $"sections end at byte {end}, past the mapping's {mappingLength}";
        return null;
    }

    private static uint U32(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(image[offset..]);

    private static double F64(ReadOnlySpan<byte> image, int offset) => BinaryPrimitives.ReadDoubleLittleEndian(image[offset..]);

    /// <summary>Sensor <paramref name="index"/>'s element. The header must have been checked against the image.</summary>
    public static ReadOnlySpan<byte> SensorElement(ReadOnlySpan<byte> image, in HwInfoHeader h, int index) =>
        image.Slice((int)(h.SensorOffset + (long)h.SensorSize * index), (int)h.SensorSize);

    /// <summary>Reading <paramref name="index"/>'s element. The header must have been checked against the image.</summary>
    public static ReadOnlySpan<byte> ReadingElement(ReadOnlySpan<byte> image, in HwInfoHeader h, int index) =>
        image.Slice((int)(h.ReadingOffset + (long)h.ReadingSize * index), (int)h.ReadingSize);

    public static uint SensorIdOf(ReadOnlySpan<byte> sensor) => U32(sensor, SensorId);

    public static uint SensorInstanceOf(ReadOnlySpan<byte> sensor) => U32(sensor, SensorInstance);

    public static string SensorNameOf(ReadOnlySpan<byte> sensor) => Windows1252(sensor.Slice(SensorNameOrig, StringSize));

    public static uint ReadingTypeOf(ReadOnlySpan<byte> reading) => U32(reading, ReadingType);

    public static uint ReadingSensorIndexOf(ReadOnlySpan<byte> reading) => U32(reading, ReadingSensorIndex);

    public static uint ReadingIdOf(ReadOnlySpan<byte> reading) => U32(reading, ReadingId);

    public static string ReadingNameOf(ReadOnlySpan<byte> reading) => Windows1252(reading.Slice(ReadingLabelOrig, StringSize));

    public static string ReadingUnitOf(ReadOnlySpan<byte> reading) =>
        reading.Length >= ReadingSizeWithUtf8
            ? Utf8(reading.Slice(ReadingUtf8Unit, UnitSize))
            : Windows1252(reading.Slice(ReadingUnit, UnitSize));

    /// <summary>The reading's value and HWiNFO's own minimum, maximum and average.</summary>
    public static (double Value, double Min, double Max, double Avg) ReadingValuesOf(ReadOnlySpan<byte> reading) =>
        (F64(reading, ReadingValue), F64(reading, ReadingMin), F64(reading, ReadingMax), F64(reading, ReadingAvg));

    /// <summary>
    /// The bytes of a reading element that make it part of the catalogue: its type, sensor index, id, original label
    /// and unit (both spellings), not its values or the user's label.
    /// </summary>
    public static void CatalogueBytesOf(ReadOnlySpan<byte> reading, Span<byte> destination, out int written)
    {
        reading[..(ReadingLabelOrig + StringSize)].CopyTo(destination);
        written = ReadingLabelOrig + StringSize;
        reading.Slice(ReadingUnit, UnitSize).CopyTo(destination[written..]);
        written += UnitSize;
        if (reading.Length >= ReadingSizeWithUtf8)
        {
            reading.Slice(ReadingUtf8Unit, UnitSize).CopyTo(destination[written..]);
            written += UnitSize;
        }
    }

    /// <summary>At most this many bytes per reading from <see cref="CatalogueBytesOf"/>.</summary>
    public const int ReadingCatalogueBytes = ReadingLabelOrig + StringSize + UnitSize + UnitSize;

    /// <summary>The bytes of a sensor element that make it part of the catalogue: id, instance and original name.</summary>
    public const int SensorCatalogueBytes = SensorNameOrig + StringSize;

    /// <summary>
    /// Reads a checked image whole: the sensors, then the readings in HWiNFO's order, skipping (and counting) any whose
    /// sensor index is past the sensor list.
    /// </summary>
    public static HwInfoSnapshot Read(ReadOnlySpan<byte> image, in HwInfoHeader h)
    {
        var sensors = new HwInfoSensor[h.SensorCount];
        for (var i = 0; i < sensors.Length; i++)
        {
            var e = SensorElement(image, h, i);
            sensors[i] = new HwInfoSensor(SensorIdOf(e), SensorInstanceOf(e), SensorNameOf(e));
        }

        var readings = new List<HwInfoReading>((int)h.ReadingCount);
        var skipped = 0;
        for (var i = 0; i < h.ReadingCount; i++)
        {
            var e = ReadingElement(image, h, i);
            var sensorIndex = ReadingSensorIndexOf(e);
            if (sensorIndex >= h.SensorCount)
            {
                skipped++;
                continue;
            }

            var (value, min, max, avg) = ReadingValuesOf(e);
            readings.Add(new HwInfoReading(ReadingTypeOf(e), sensorIndex, ReadingIdOf(e), ReadingNameOf(e), ReadingUnitOf(e), value, min, max, avg));
        }

        return new HwInfoSnapshot(h, sensors, readings, skipped);
    }

    /// <summary>A NUL-terminated (or field-long) Windows-1252 string, decoded by hand (no code-page provider needed).</summary>
    public static string Windows1252(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end >= 0)
            field = field[..end];
        if (field.IsEmpty)
            return string.Empty;

        return string.Create(field.Length, field.ToArray(), static (chars, bytes) =>
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[i] = b is >= 0x80 and < 0xA0 ? Cp1252High[b - 0x80] : (char)b;
            }
        });
    }

    /// <summary>A NUL-terminated (or field-long) UTF-8 string; invalid bytes read as U+FFFD.</summary>
    public static string Utf8(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end >= 0)
            field = field[..end];
        return field.IsEmpty ? string.Empty : Encoding.UTF8.GetString(field);
    }

    // Windows-1252's 0x80–0x9F; the five it leaves undefined read as their C1 control, as Windows does.
    private const string Cp1252High =
        "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F" +
        "\u0090‘’“”•–—˜™š›œ\u009DžŸ";
}
