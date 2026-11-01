using InfoPanel.Sensors.HwInfo;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.HwInfo;

/// <summary>Reading HWiNFO's shared memory from images a test builds: both layouts, the signatures, the bounds, the strings.</summary>
public class HwInfoLayoutTests
{
    private static HwInfoImageBuilder TwoSensors(bool utf8 = true)
    {
        var b = new HwInfoImageBuilder { Utf8 = utf8 };
        b.AddSensor(0xF0000300, 0, "CPU [#0]: AMD Ryzen 7 8845HS");
        b.AddSensor(0xF0000301, 1, "Memory Timings");
        b.AddReading(0, 0x1000000, "CPU (Tctl/Tdie)", "°C", 52.125, 33, 90.75, 40.5);
        b.AddReading(1, 0x7000000, "Memory Clock", "MHz", 2800, 2795, 2805, 2800.5);
        b.AddReading(0, 0x2000000, "CPU Core Voltage (SVI3 TFN)", "V", 1.1, 0.9, 1.3, 1.05);
        return b;
    }

    private static (HwInfoImageState State, HwInfoHeader Header, string? Problem) Header(byte[] image, long? length = null)
    {
        var state = HwInfoLayout.ReadHeader(image, length ?? image.Length, out var header, out var problem);
        return (state, header, problem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothElementSizes_Read_TheSame(bool utf8)
    {
        var image = TwoSensors(utf8).Build();
        var (state, h, problem) = Header(image);

        Assert.Equal(HwInfoImageState.Ok, state);
        Assert.Null(problem);
        Assert.Equal((2u, 1u, 1_793_508_823L), (h.Version, h.Revision, h.PollTime));
        Assert.Equal(utf8 ? 392u : 264u, h.SensorSize);
        Assert.Equal(utf8 ? 460u : 316u, h.ReadingSize);
        Assert.Equal(utf8 ? 2000u : 0u, h.PollingPeriodMs);
        Assert.Equal(utf8, h.HasUtf8Unit);
        Assert.Equal(image.Length, h.End);

        var snapshot = HwInfoLayout.Read(image, h);
        Assert.Equal([(0xF0000300u, 0u, "CPU [#0]: AMD Ryzen 7 8845HS"), (0xF0000301u, 1u, "Memory Timings")],
            snapshot.Sensors.Select(s => (s.Id, s.Instance, s.Name)));
        Assert.Equal(
            [
                (0u, 0x1000000u, "CPU (Tctl/Tdie)", "°C", 52.125, 33.0, 90.75, 40.5),
                (1u, 0x7000000u, "Memory Clock", "MHz", 2800.0, 2795.0, 2805.0, 2800.5),
                (0u, 0x2000000u, "CPU Core Voltage (SVI3 TFN)", "V", 1.1, 0.9, 1.3, 1.05),
            ],
            snapshot.Readings.Select(r => (r.SensorIndex, r.Id, r.Name, r.Unit, r.Value, r.Min, r.Max, r.Avg)));
        Assert.Equal(0, snapshot.Skipped);
    }

    [Fact]
    public void LongerElements_FromALaterHWiNFO_ReadByTheirKnownPrefix()
    {
        var b = TwoSensors();
        var normal = b.Build();
        var (_, h, _) = Header(normal);

        // Widen every element by 64 bytes, copying the known part.
        var wide = new byte[48 + (h.SensorSize + 64) * 2 + (h.ReadingSize + 64) * 3];
        normal.AsSpan(0, 48).CopyTo(wide);
        var sensorOffset = 48u;
        var readingOffset = sensorOffset + (h.SensorSize + 64) * 2;
        BitConverter.TryWriteBytes(wide.AsSpan(24), h.SensorSize + 64);
        BitConverter.TryWriteBytes(wide.AsSpan(32), readingOffset);
        BitConverter.TryWriteBytes(wide.AsSpan(36), h.ReadingSize + 64);
        for (var i = 0; i < 2; i++)
            HwInfoLayout.SensorElement(normal, h, i).CopyTo(wide.AsSpan((int)(sensorOffset + (h.SensorSize + 64) * i)));
        for (var i = 0; i < 3; i++)
            HwInfoLayout.ReadingElement(normal, h, i).CopyTo(wide.AsSpan((int)(readingOffset + (h.ReadingSize + 64) * i)));

        var (state, wh, _) = Header(wide);
        Assert.Equal(HwInfoImageState.Ok, state);
        var a = HwInfoLayout.Read(normal, h);
        var w = HwInfoLayout.Read(wide, wh);
        Assert.Equal(a.Sensors, w.Sensors);
        Assert.Equal(a.Readings, w.Readings);
    }

    [Fact]
    public void Signatures_HWiS_IsRead_DEAD_IsDead_InEitherSpelling_AndAnythingElse_IsRefused()
    {
        var b = TwoSensors();
        Assert.Equal(HwInfoImageState.Ok, Header(b.Build()).State);
        Assert.Equal(HwInfoImageState.Dead, Header(b.BuildDead()).State);

        var bytes = b.Build();
        "DEAD"u8.CopyTo(bytes);
        Assert.Equal(HwInfoImageState.Dead, Header(bytes).State);
        // Dead is told from the signature alone, even when nothing else fits.
        Assert.Equal(HwInfoImageState.Dead, Header(bytes[..4]).State);

        "HWiN"u8.CopyTo(bytes);
        var (state, _, problem) = Header(bytes);
        Assert.Equal(HwInfoImageState.Refused, state);
        Assert.Contains("signature", problem);
        Assert.Equal(HwInfoImageState.Refused, Header([]).State);
        Assert.Equal(HwInfoImageState.Refused, Header(b.Build()[..40]).State);
    }

    [Fact]
    public void NoSensors_IsAnEmptyImage()
    {
        var image = new HwInfoImageBuilder().Build();
        var (state, h, _) = Header(image);
        Assert.Equal(HwInfoImageState.Ok, state);
        var snapshot = HwInfoLayout.Read(image, h);
        Assert.Empty(snapshot.Sensors);
        Assert.Empty(snapshot.Readings);
    }

    [Fact]
    public void AReadingPastTheSensors_IsSkipped_NotThrown()
    {
        var b = TwoSensors();
        b.AddReading(2, 0x9000000, "Orphan", "%", 1);
        b.AddReading(uint.MaxValue, 0x9000001, "Far orphan", "%", 1);
        b.AddReading(1, 0x7000001, "After", "MHz", 3);
        var image = b.Build();
        var (_, h, _) = Header(image);

        var snapshot = HwInfoLayout.Read(image, h);
        Assert.Equal(2, snapshot.Skipped);
        Assert.Equal(["CPU (Tctl/Tdie)", "Memory Clock", "CPU Core Voltage (SVI3 TFN)", "After"], snapshot.Readings.Select(r => r.Name));
    }

    public static TheoryData<string> OutOfBounds => new()
    {
        "sensor count past the mapping", "reading count past the mapping", "sensor offset past the mapping",
        "reading offset past the mapping", "offset that wraps", "sensor element too small", "reading element too small",
        "element over 4 KiB", "too many sensors", "too many readings", "section in the header",
    };

    [Theory]
    [MemberData(nameof(OutOfBounds))]
    public void CountsSizesOrOffsets_OutOfBounds_AreRefused(string what)
    {
        var b = TwoSensors();
        switch (what)
        {
            case "sensor count past the mapping": b.SensorCountOverride = 3; b.Readings.Clear(); break;
            case "reading count past the mapping": b.ReadingCountOverride = 4; break;
            case "sensor offset past the mapping": b.SensorOffsetOverride = 10_000; break;
            case "reading offset past the mapping": b.ReadingOffsetOverride = 1_000_000; break;
            case "offset that wraps": b.ReadingOffsetOverride = uint.MaxValue - 100; break;
            case "sensor element too small": b.SensorSizeOverride = 263; break;
            case "reading element too small": b.ReadingSizeOverride = 315; break;
            case "element over 4 KiB": b.ReadingSizeOverride = 4097; break;
            case "too many sensors": b.SensorCountOverride = 4097; break;
            case "too many readings": b.ReadingCountOverride = 65537; break;
            case "section in the header": b.SensorOffsetOverride = 40; break;
        }

        var (state, _, problem) = Header(b.Build());
        Assert.Equal(HwInfoImageState.Refused, state);
        Assert.False(string.IsNullOrEmpty(problem));
    }

    [Fact]
    public void Caps_AtTheirLimits_AreRead_AndTheCopyIsCappedAt32MiB()
    {
        // 4,096 sensors and 65,536 readings of 316 bytes: within every cap (about 21 MiB).
        var b = new HwInfoImageBuilder { Utf8 = false, SensorCountOverride = 4096, ReadingCountOverride = 65536 };
        var header = b.Build()[..44];
        var (state, h, problem) = Header(header, length: 64L * 1024 * 1024);
        Assert.Equal(HwInfoImageState.Ok, state);
        Assert.Null(problem);
        Assert.True(h.End <= HwInfoLayout.MaxImageBytes);

        // 65,536 readings of 1 KiB: 64 MiB to copy, refused even when the mapping is that long.
        b.ReadingSizeOverride = 1024;
        (state, _, problem) = Header(b.Build()[..44], length: 128L * 1024 * 1024);
        Assert.Equal(HwInfoImageState.Refused, state);
        Assert.Contains("copy", problem);
    }

    [Fact]
    public void Strings_WithoutANul_ReadTheWholeField()
    {
        var b = TwoSensors();
        b.Readings[0] = b.Readings[0] with { NameBytes = HwInfoImageBuilder.Raw(128, "", (byte)'x') };
        var image = b.Build();
        var (_, h, _) = Header(image);

        Assert.Equal(new string('x', 128), HwInfoLayout.Read(image, h).Readings[0].Name);
        Assert.Equal("ab", HwInfoLayout.Windows1252("ab\0cd"u8));
        Assert.Equal("abcd", HwInfoLayout.Utf8("abcd"u8));
    }

    [Fact]
    public void DegreesCelsius_ReadsTheSame_In1252_AndInUtf8()
    {
        var utf8 = TwoSensors(utf8: true);
        var ansi = TwoSensors(utf8: false);
        Assert.Equal("°C", Read(utf8).Readings[0].Unit);
        Assert.Equal("°C", Read(ansi).Readings[0].Unit);

        // The UTF-8 unit is the one read when the element has it, whatever the ANSI field says.
        utf8.Readings[0] = utf8.Readings[0] with { UnitBytes1252 = HwInfoImageBuilder.Raw(16, "?C") };
        Assert.Equal("°C", Read(utf8).Readings[0].Unit);

        // Windows-1252's own letters past 0x7F: 0xB0 is °, 0x80 is €, 0xB5 is µ.
        Assert.Equal("°C €µs", HwInfoLayout.Windows1252([0xB0, (byte)'C', (byte)' ', 0x80, 0xB5, (byte)'s']));
        Assert.Equal("°C", HwInfoLayout.Utf8([0xC2, 0xB0, (byte)'C', 0]));
    }

    private static HwInfoSnapshot Read(HwInfoImageBuilder b)
    {
        var image = b.Build();
        var (_, h, _) = Header(image);
        return HwInfoLayout.Read(image, h);
    }
}
