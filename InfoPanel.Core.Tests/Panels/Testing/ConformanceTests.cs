using InfoPanel.Core.Tests.Editing;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Example;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Virtual;
using Xunit;
using V = InfoPanel.Core.Tests.Panels.BeadaPanel.BeadaPanelVectors;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// The kit's conformance suite (<see cref="Conformance"/>): the drivers this repository has pass it (the built-in
/// BeadaPanel driver, unchanged, over the kit's fakes through an adapter of the engine's USB backend; the virtual panel,
/// which reaches no device; the guide's sample driver), and each of its checks fails a driver that breaks its rule.
/// </summary>
public sealed class ConformanceTests
{
    private static readonly PanelCandidate BeadaPanelAt = new(0x4E58, 0x1001, 1, 4, "1-2");
    private static readonly PanelCandidate ExampleAt = new(0x1209, 0x000a, 1, 5, "1-3");
    private static readonly PanelCandidate MisbehavingAt = new(0x1209, 0x000b, 1, 6, "1-4");

    /// <summary>
    /// A BeadaPanel as a fake seam device: its interface 0 with the stream's endpoint 0x01, the status endpoints 0x02 and
    /// 0x82, its USB serial, and its info reply answered to GetPanelInfo; the driver over <see cref="SeamUsbBackend"/>.
    /// </summary>
    private static ConformanceSetup BeadaPanel(byte[] info)
    {
        var devices = new FakePanelDevices();
        var fake = devices.Add(BeadaPanelAt);
        fake.Descriptor = new PanelUsbDescriptor(0, 0, 0, 0x0100,
        [
            new PanelUsbInterface(0, 0xff, 0, 0,
            [
                new PanelUsbEndpoint(0x01, PanelEndpointType.Bulk, 512),
                new PanelUsbEndpoint(0x02, PanelEndpointType.Bulk, 512),
                new PanelUsbEndpoint(0x82, PanelEndpointType.Bulk, 512),
            ]),
        ]);
        fake.Usb.SerialNumber = "934072005c";
        fake.Usb.Respond(last => last != null && last.Target == "ep02" && last.Data.AsSpan().SequenceEqual(V.GetPanelInfo) ? info : null, 0x82);
        var driver = new BeadaPanelDriver(new SeamUsbBackend(devices, [BeadaPanelAt]), new InstantWaits());
        return new ConformanceSetup(driver, BeadaPanelAt, fake);
    }

    private static ConformanceSetup Example()
    {
        var devices = new FakePanelDevices();
        var fake = devices.Add(ExampleAt);
        return new ConformanceSetup(new ExampleDriver(devices), ExampleAt, fake);
    }

    private static ConformanceSetup Misbehaving(Misbehaviour faults)
    {
        var devices = new FakePanelDevices();
        var fake = devices.Add(MisbehavingAt);
        return new ConformanceSetup(new MisbehavingDriver(devices, faults), MisbehavingAt, fake);
    }

    [Fact]
    public void A_raw_BeadaPanel_passes()
    {
        var failures = Conformance.Check(() => BeadaPanel(V.InfoReply(2, 1, 0, 800, 480)));

        Assert.Empty(failures);
    }

    [Fact]
    public void A_JPEG_BeadaPanel_passes()
    {
        // A T113 (platform 5) with PanelLink 2: its stream is JPEG.
        var failures = Conformance.Check(() => BeadaPanel(V.InfoReply(2, 5, 21, 1920, 480)));

        Assert.Empty(failures);
    }

    [Fact]
    public void The_virtual_panel_passes()
    {
        var failures = Conformance.Check(() => new ConformanceSetup(new VirtualPanelDriver(), new PanelCandidate(0x1209, 0x0001, 1, 7, "1-5")));

        Assert.Empty(failures);
    }

    [Fact]
    public void The_guides_sample_driver_passes()
    {
        var failures = Conformance.Check(Example);

        Assert.Empty(failures);
    }

    [Fact]
    public void A_driver_with_an_overlay_and_a_crop_inside_its_frame_passes()
    {
        var failures = Conformance.Check(() => Misbehaving(new Misbehaviour
        {
            Overlay = MisbehavingDriver.Png(MisbehavingDriver.Width, MisbehavingDriver.Height),
            Crop = new PanelCrop(0, 0, MisbehavingDriver.Width, MisbehavingDriver.Height - 18),
        }));

        Assert.Empty(failures);
    }

    [Fact]
    public void A_fixed_brightness_panel_passes()
    {
        // No brightness range and no dimming in software: the SDK allows it, and the engine never sets its brightness.
        var failures = Conformance.Check(() => Misbehaving(new Misbehaviour { NoBrightness = true }));

        Assert.Empty(failures);
    }

    [Fact]
    public void A_fixed_brightness_panel_whose_start_sends_a_nonce_passes()
    {
        // Its start writes a value of its own each time, so its start at 0 and at 100 differ: nothing brightness has to do with.
        var failures = Conformance.Check(() => Misbehaving(new Misbehaviour { NoBrightness = true, StartNonce = true }));

        Assert.Empty(failures);
    }

    [Fact]
    public void Two_512_byte_queries_on_a_20_by_20_panel_pass()
    {
        // 1,024 bytes in all, more than the 800 bytes of a 20 × 20 frame, but no write draws one.
        var failures = Conformance.Check(() => Misbehaving(new Misbehaviour { Small = true, ProbeQueriesTwice = true }));

        Assert.Empty(failures);
    }

    [Fact]
    public void A_start_of_one_call_that_honours_its_token_passes()
    {
        var failures = Conformance.Check(() => Misbehaving(new Misbehaviour { StartOneCall = true }));

        Assert.Empty(failures);
    }

    [Fact]
    public void The_sample_passes_with_a_slow_trace_callback()
    {
        // Each frame's write is recorded slowly (100 ms in a trace callback): the sample's DeviceTime, measured around the
        // write, holds that time, and the device's own timing of the call has to hold it too.
        var failures = Conformance.Check(() =>
        {
            var setup = Example();
            setup.Fake!.Trace.Added += entry =>
            {
                if (entry.Data.Length >= 320 * 240 * 2)
                    Thread.Sleep(100);
            };
            return setup;
        });

        Assert.Empty(failures);
    }

    [Fact]
    public void Overlays_with_a_palette_ancillary_chunks_or_frames_of_an_animation_pass()
    {
        const int W = MisbehavingDriver.Width, H = MisbehavingDriver.Height;
        // A palette image: gAMA before PLTE, tRNS after it, its image data in two IDATs in a row, tEXt after them.
        var palette = PngOf(
            ("IHDR", Ihdr(W, H, 8, 3)), ("gAMA", [0, 0, 0xB1, 0x8F]), ("PLTE", [0, 0, 0, 255, 255, 255]), ("tRNS", [0, 255]),
            ("IDAT", Pixels(W, H, W)[..20]), ("IDAT", Pixels(W, H, W)[20..]), ("tEXt", "Comment\0mask"u8.ToArray()), ("IEND", []));
        // An animated PNG: acTL and fcTL before the image data, a second frame's fcTL and fdAT after it.
        var animated = PngOf(
            ("IHDR", Ihdr(W, H, 8, 6)), ("acTL", [0, 0, 0, 2, 0, 0, 0, 0]), ("fcTL", FrameControl(0, W, H)), ("IDAT", Pixels(W, H, W * 4)),
            ("fcTL", FrameControl(1, W, H)), ("fdAT", [0, 0, 0, 2, .. Pixels(W, H, W * 4)]), ("IEND", []));

        Assert.Empty(Conformance.Check(() => Misbehaving(new Misbehaviour { Overlay = palette })));
        Assert.Empty(Conformance.Check(() => Misbehaving(new Misbehaviour { Overlay = animated })));
    }

    [Fact]
    public void A_probe_whose_query_is_longer_than_4_KiB_passes()
    {
        var failures = Conformance.Check(() => Misbehaving(new Misbehaviour { ProbeQueriesLong = true }));

        Assert.Empty(failures);
    }

    [Fact]
    public void A_probe_is_held_to_the_queries_its_setup_names()
    {
        ConformanceSetup Named(params byte[][] queries) => Misbehaving(new Misbehaviour { ProbeQueriesLong = true }) with
        {
            ProbeWrite = Conformance.ProbeWrites(queries),
        };

        Assert.Empty(Conformance.Check(() => Named(new byte[4097])));
        Assert.Contains(Conformance.Check(() => Named([0xB0, 0x01])), f => f.Check == Conformance.Probe);
    }

    public static TheoryData<string, string> Faults => new()
    {
        { nameof(Misbehaviour.ProbeKeepsDevice), Conformance.Probe },
        { nameof(Misbehaviour.ProbeDraws), Conformance.Probe },
        { nameof(Misbehaviour.StartIgnoresCancel), Conformance.Start },
        { "StartOneCallIgnoresCancel", Conformance.Start },
        { nameof(Misbehaviour.CancelledBeforeStartLeaksDevice), Conformance.Start },
        { nameof(Misbehaviour.FailsSecondFormat), Conformance.Formats },
        { nameof(Misbehaviour.FixedStartFailsAtZero), Conformance.Brightness },
        { nameof(Misbehaviour.BrightnessOverflowsAtFull), Conformance.Brightness },
        { nameof(Misbehaviour.BrightnessFailsAt25), Conformance.Brightness },
        { nameof(Misbehaviour.CloseIgnoresBudget), Conformance.CloseBudget },
        { nameof(Misbehaviour.FixedCloseTimeout), Conformance.CloseBudget },
        { nameof(Misbehaviour.WritesWhenSpent), Conformance.CloseBudget },
        { nameof(Misbehaviour.SpendsBudgetThenWrites), Conformance.CloseBudget },
        { nameof(Misbehaviour.GoneAsIo), Conformance.Unplug },
        { nameof(Misbehaviour.ScreenOffIgnoresGone), Conformance.Unplug },
        { nameof(Misbehaviour.WritesAfterClose), Conformance.AfterClose },
        { nameof(Misbehaviour.CloseLeavesTimer), Conformance.AfterClose },
        { nameof(Misbehaviour.ScreenOffWithoutAsk), Conformance.AfterClose },
        { nameof(Misbehaviour.NoDeviceTime), Conformance.DeviceTime },
        { nameof(Misbehaviour.PadsDeviceTime), Conformance.DeviceTime },
        { nameof(Misbehaviour.DeviceTimeIncludesPreparation), Conformance.DeviceTime },
        { "OverlayOfAnotherSize", Conformance.Overlays },
        { "OverlayHeaderOnly", Conformance.Overlays },
        { "OverlayWithABadCrc", Conformance.Overlays },
        { "OverlayWithoutImageData", Conformance.Overlays },
        { "OverlayBitDepthZero", Conformance.Overlays },
        { "OverlayDepthTheColourTypeHasnt", Conformance.Overlays },
        { "OverlayUnknownColourType", Conformance.Overlays },
        { "OverlayUnknownCompression", Conformance.Overlays },
        { "OverlayUnknownFilterMethod", Conformance.Overlays },
        { "OverlayUnknownInterlace", Conformance.Overlays },
        { "OverlayImageDataSplit", Conformance.Overlays },
        { "OverlayPaletteMissing", Conformance.Overlays },
        { "OverlayPaletteAfterImageData", Conformance.Overlays },
        { "OverlayPaletteInGreyscale", Conformance.Overlays },
        { "OverlayPaletteCutShort", Conformance.Overlays },
        { "CropOutsideTheFrame", Conformance.Crop },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void Each_check_fails_the_driver_that_breaks_its_rule(string fault, string check)
    {
        const int W = MisbehavingDriver.Width, H = MisbehavingDriver.Height;
        var faults = fault switch
        {
            nameof(Misbehaviour.ProbeKeepsDevice) => new Misbehaviour { ProbeKeepsDevice = true },
            nameof(Misbehaviour.ProbeDraws) => new Misbehaviour { ProbeDraws = true },
            nameof(Misbehaviour.StartIgnoresCancel) => new Misbehaviour { StartIgnoresCancel = true },
            "StartOneCallIgnoresCancel" => new Misbehaviour { StartOneCall = true, StartIgnoresCancel = true },
            nameof(Misbehaviour.CancelledBeforeStartLeaksDevice) => new Misbehaviour { CancelledBeforeStartLeaksDevice = true },
            nameof(Misbehaviour.FailsSecondFormat) => new Misbehaviour { FailsSecondFormat = true },
            nameof(Misbehaviour.FixedStartFailsAtZero) => new Misbehaviour { NoBrightness = true, FixedStartFailsAtZero = true },
            nameof(Misbehaviour.BrightnessOverflowsAtFull) => new Misbehaviour { BrightnessOverflowsAtFull = true },
            nameof(Misbehaviour.BrightnessFailsAt25) => new Misbehaviour { BrightnessFailsAt25 = true },
            nameof(Misbehaviour.CloseIgnoresBudget) => new Misbehaviour { CloseIgnoresBudget = true },
            nameof(Misbehaviour.FixedCloseTimeout) => new Misbehaviour { FixedCloseTimeout = true },
            nameof(Misbehaviour.WritesWhenSpent) => new Misbehaviour { WritesWhenSpent = true },
            nameof(Misbehaviour.SpendsBudgetThenWrites) => new Misbehaviour { SpendsBudgetThenWrites = true },
            nameof(Misbehaviour.GoneAsIo) => new Misbehaviour { GoneAsIo = true },
            nameof(Misbehaviour.ScreenOffIgnoresGone) => new Misbehaviour { ScreenOffIgnoresGone = true },
            nameof(Misbehaviour.WritesAfterClose) => new Misbehaviour { WritesAfterClose = true },
            nameof(Misbehaviour.CloseLeavesTimer) => new Misbehaviour { CloseLeavesTimer = true },
            nameof(Misbehaviour.ScreenOffWithoutAsk) => new Misbehaviour { ScreenOffWithoutAsk = true },
            nameof(Misbehaviour.NoDeviceTime) => new Misbehaviour { NoDeviceTime = true },
            nameof(Misbehaviour.PadsDeviceTime) => new Misbehaviour { PadsDeviceTime = true },
            nameof(Misbehaviour.DeviceTimeIncludesPreparation) => new Misbehaviour { DeviceTimeIncludesPreparation = true },
            "OverlayOfAnotherSize" => new Misbehaviour { Overlay = MisbehavingDriver.Png(MisbehavingDriver.Height, MisbehavingDriver.Width) },
            "OverlayHeaderOnly" => new Misbehaviour { Overlay = MisbehavingDriver.Png(MisbehavingDriver.Width, MisbehavingDriver.Height)[..24] },
            "OverlayWithABadCrc" => new Misbehaviour { Overlay = Corrupted(MisbehavingDriver.Png(MisbehavingDriver.Width, MisbehavingDriver.Height)) },
            "OverlayWithoutImageData" => new Misbehaviour { Overlay = WithoutIdat(MisbehavingDriver.Png(MisbehavingDriver.Width, MisbehavingDriver.Height)) },
            "OverlayBitDepthZero" => new Misbehaviour { Overlay = Plain(Ihdr(W, H, 0, 6)) },
            "OverlayDepthTheColourTypeHasnt" => new Misbehaviour { Overlay = Plain(Ihdr(W, H, 4, 2)) }, // truecolour takes 8 or 16
            "OverlayUnknownColourType" => new Misbehaviour { Overlay = Plain(Ihdr(W, H, 8, 5)) },
            "OverlayUnknownCompression" => new Misbehaviour { Overlay = Plain(Ihdr(W, H, 8, 6, compression: 1)) },
            "OverlayUnknownFilterMethod" => new Misbehaviour { Overlay = Plain(Ihdr(W, H, 8, 6, filter: 1)) },
            "OverlayUnknownInterlace" => new Misbehaviour { Overlay = Plain(Ihdr(W, H, 8, 6, interlace: 2)) },
            "OverlayImageDataSplit" => new Misbehaviour
            {
                Overlay = PngOf(("IHDR", Ihdr(W, H, 8, 6)), ("IDAT", Pixels(W, H, W * 4)[..20]), ("tEXt", "Comment\0mask"u8.ToArray()),
                    ("IDAT", Pixels(W, H, W * 4)[20..]), ("IEND", [])),
            },
            "OverlayPaletteMissing" => new Misbehaviour { Overlay = PngOf(("IHDR", Ihdr(W, H, 8, 3)), ("IDAT", Pixels(W, H, W)), ("IEND", [])) },
            "OverlayPaletteAfterImageData" => new Misbehaviour
            {
                Overlay = PngOf(("IHDR", Ihdr(W, H, 8, 3)), ("IDAT", Pixels(W, H, W)), ("PLTE", [0, 0, 0]), ("IEND", [])),
            },
            "OverlayPaletteInGreyscale" => new Misbehaviour
            {
                Overlay = PngOf(("IHDR", Ihdr(W, H, 8, 0)), ("PLTE", [0, 0, 0]), ("IDAT", Pixels(W, H, W)), ("IEND", [])),
            },
            "OverlayPaletteCutShort" => new Misbehaviour
            {
                Overlay = PngOf(("IHDR", Ihdr(W, H, 8, 3)), ("PLTE", [0, 0, 0, 255]), ("IDAT", Pixels(W, H, W)), ("IEND", [])),
            },
            "CropOutsideTheFrame" => new Misbehaviour { Crop = new PanelCrop(0, 10, MisbehavingDriver.Width, MisbehavingDriver.Height) },
            _ => throw new ArgumentOutOfRangeException(nameof(fault), fault, null),
        };

        var failures = Conformance.Check(() => Misbehaving(faults));

        Assert.Contains(failures, f => f.Check == check);
    }

    // A whole PNG with one byte of its image data changed, its CRC left as it was.
    private static byte[] Corrupted(byte[] png)
    {
        var copy = png.ToArray();
        copy[^20] ^= 0xFF; // inside the last IDAT, before its CRC and the IEND chunk
        return copy;
    }

    // A whole PNG's chunks but its IDATs: the signature, IHDR (and any others) and IEND, each with its own CRC.
    private static byte[] WithoutIdat(byte[] png)
    {
        var kept = new List<byte>(png[..8]);
        for (var at = 8; at < png.Length;)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            if (!png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8))
                kept.AddRange(png.AsSpan(at, 12 + length).ToArray());
            at += 12 + length;
        }
        return [.. kept];
    }

    // A PNG put together chunk by chunk, each with its own CRC: the signature, then the chunks as given.
    private static byte[] PngOf(params (string Type, byte[] Data)[] chunks)
    {
        var png = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        foreach (var (type, data) in chunks)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
            png.AddRange([.. length, .. typed, .. crc]);
        }
        return [.. png];
    }

    // An IHDR chunk's 13 bytes.
    private static byte[] Ihdr(int width, int height, byte depth, byte colourType, byte compression = 0, byte filter = 0, byte interlace = 0)
    {
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = depth;
        ihdr[9] = colourType;
        ihdr[10] = compression;
        ihdr[11] = filter;
        ihdr[12] = interlace;
        return ihdr;
    }

    // An image's data: `height` rows of `rowBytes` zero bytes, each with its filter byte, compressed as zlib.
    private static byte[] Pixels(int width, int height, int rowBytes)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest))
            zlib.Write(new byte[height * (rowBytes + 1)]);
        return compressed.ToArray();
    }

    // A PNG of one IHDR (as given), its image data and IEND: whole but for what the IHDR says.
    private static byte[] Plain(byte[] ihdr) =>
        PngOf(("IHDR", ihdr), ("IDAT", Pixels(MisbehavingDriver.Width, MisbehavingDriver.Height, MisbehavingDriver.Width * 4)), ("IEND", []));

    // An APNG frame control chunk's 26 bytes: the frame's sequence number, its size at (0, 0), a 1/10 s delay.
    private static byte[] FrameControl(uint sequence, int width, int height)
    {
        var fctl = new byte[26];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fctl, sequence);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), 10);
        return fctl;
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    [Fact]
    public void The_checks_are_named_and_a_failure_says_which()
    {
        Assert.Equal(
            [Conformance.Probe, Conformance.Start, Conformance.Formats, Conformance.Brightness, Conformance.CloseBudget, Conformance.Unplug,
             Conformance.AfterClose, Conformance.DeviceTime, Conformance.Overlays, Conformance.Crop],
            Conformance.Checks);
        Assert.Equal("crop: it is outside", new ConformanceFailure(Conformance.Crop, "it is outside").ToString());
    }

    [Fact]
    public void A_setup_that_throws_is_a_failure_of_each_check_not_an_exception()
    {
        var failures = Conformance.Check(() => throw new InvalidOperationException("no device"));

        Assert.Equal(Conformance.Checks, failures.Select(f => f.Check).Distinct());
        Assert.All(failures, f => Assert.Contains("no device", f.Message));
    }

    [Fact]
    public void The_guides_sample_is_this_file()
    {
        var root = KindManifestTests.RepoRoot();
        var source = File.ReadAllLines(Path.Combine(root, "InfoPanel.Core.Tests", "Panels", "Testing", "ExampleDriver.cs"));
        var begin = Array.IndexOf(source, "// guide:begin");
        var end = Array.IndexOf(source, "// guide:end");
        Assert.True(begin >= 0 && end > begin, "ExampleDriver.cs has its guide markers");
        var sample = string.Join("\n", source[(begin + 1)..end]);

        var guide = File.ReadAllText(Path.Combine(root, "docs", "2.0", "panel-drivers.md")).ReplaceLineEndings("\n");

        Assert.Contains("```csharp\n" + sample + "\n```", guide);
    }
}
