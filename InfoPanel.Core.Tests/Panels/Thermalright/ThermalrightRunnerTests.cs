using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Output;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermalright;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The Thermalright driver run by the engine in process (§2.2 (7)): the manager lists the device (the engine's fake USB
/// backend), offers it to the driver, which probes and opens it over the kit's fake devices, and the runner turns a
/// rendered frame into the session's format through the engine's transform (as far as it goes before the engine's
/// family formats: JPEG and little-endian RGB565) and writes it; stopping lets the device go with nothing sent.
/// </summary>
public sealed class ThermalrightRunnerTests : IAsyncDisposable
{
    private readonly FakeUsbBackend _backend = new();
    private readonly FakePanelDevices _devices = new();
    private readonly KeptLog _log = new();
    private PanelManager? _manager;
    private volatile PanelSink? _sink;
    private long _frameId;

    public async ValueTask DisposeAsync()
    {
        if (_manager != null)
            await _manager.DisposeAsync();
    }

    private PanelManager Manager(IPanelDriver driver)
    {
        var host = new PanelHost(_ => true, sink =>
        {
            _sink = (PanelSink)sink;
            return true;
        }, _ => { }, _ => { }, _ => { }, () => 60);
        _manager = new PanelManager(_backend, PanelStore.InMemory(), host, _backend.Clock, _log.Logger,
            new PanelRunnerOptions { KeepAlive = Timeout.InfiniteTimeSpan }, drivers: [driver]);
        _manager.Start(passes: false);
        return _manager;
    }

    private (FakePanelDevice Device, ThermalrightDriver Driver) Plug(ushort vendor, ushort product, params PanelTransport[] transports)
    {
        _backend.Add(new FakeUsbDevice(new UsbDeviceId(vendor, product, 0, 7, "0-7")));
        var device = _devices.Add(new PanelCandidate(vendor, product, 0, 7, "0-7"), transports);
        return (device, new ThermalrightDriver(_devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() });
    }

    private async Task Until(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what}\nThe log:\n{_log}");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A new 8 × 2 frame, steel blue (or <paramref name="color"/>), to the panel's sink, as the render loop hands one over.</summary>
    private void Deliver(SKColor? color = null)
    {
        using var pixels = new SKBitmap(new SKImageInfo(8, 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        pixels.Erase(color ?? SKColors.SteelBlue);
        Deliver(pixels);
    }

    /// <summary><paramref name="pixels"/> as a new frame to the panel's sink.</summary>
    private void Deliver(SKBitmap pixels)
    {
        if (_sink is not { } sink)
            return;
        var frameId = Interlocked.Increment(ref _frameId);
        using var image = SKImage.FromBitmap(pixels);
        var frame = new RenderedFrame(image)
        {
            ProfileId = "p-1",
            Rev = 1,
            FrameId = frameId,
            Tick = frameId,
            Scale = 1,
            PixelWidth = pixels.Width,
            PixelHeight = pixels.Height,
            ProfileWidth = pixels.Width,
            ProfileHeight = pixels.Height,
            Bounds = [],
            ContentHash = (ulong)frameId,
        };
        sink.OnFrame(frame);
        frame.Close();
    }

    private async Task<PanelStatus> Run(PanelManager manager, string model, int brightness = 100, string? familySettings = null)
    {
        await manager.RunPassAsync();
        var found = Assert.Single(await manager.ListAsync());
        Assert.Equal((ThermalrightCatalog.FamilyId, model), (found.Saved.Family, found.Saved.Model));
        await manager.UpdateAsync(found.Saved.Id, s => s with { Enabled = true, ProfileId = "p-1", Brightness = brightness, FamilySettings = familySettings });
        await Until(async () => (await manager.ListAsync())[0].State == "running", "the panel never ran");
        return found;
    }

    [Fact]
    public async Task A_ChiZhu_Jpeg_Panel_GetsTheEnginesJpeg_InItsHeader_AndItsStopSendsNothing()
    {
        var (device, driver) = Plug(0x87AD, 0x70DB);
        var reply = new byte[48];
        System.Text.Encoding.ASCII.GetBytes("SSCRM-V1").CopyTo(reply, 4);
        (reply[24], reply[28]) = (4, 1); // the Hyper Vision, 480 × 480
        device.Usb.Reply(reply); // the probe's
        device.Usb.Reply(reply); // the start's
        var manager = Manager(driver);
        await Run(manager, "hypervision");

        PanelTraceEntry? frame = null;
        await Until(() =>
        {
            Deliver();
            frame = device.Trace.Entries.FirstOrDefault(e => e.Op == PanelTraceOp.UsbWrite && e.Data.Length > ChiZhuProtocol.HeaderSize); // past the two inits
            return Task.FromResult(frame != null);
        }, "no frame was written");
        var data = frame!.Data;
        var length = BitConverter.ToInt32(data, 60);
        Assert.Equal((2, 480, 480, 2, 64 + length), (BitConverter.ToInt32(data, 4), BitConverter.ToInt32(data, 8), BitConverter.ToInt32(data, 12), BitConverter.ToInt32(data, 56), data.Length));
        using (var decoded = SKBitmap.Decode(data.AsSpan(64).ToArray()))
        {
            Assert.Equal((480, 480), (decoded.Width, decoded.Height));
            var centre = decoded.GetPixel(240, 240);
            Assert.True(Math.Abs(centre.Red - SKColors.SteelBlue.Red) <= 6 && Math.Abs(centre.Blue - SKColors.SteelBlue.Blue) <= 6, $"the centre is {centre}");
        }

        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));
        var last = device.Trace.Entries[^1];
        Assert.Equal("h2 close usb if0", PanelTrace.FormatLine(last));
        Assert.Equal(0, device.CallsAfterDispose);
    }

    [Fact]
    public async Task A_Hid_Rgb565_Panel_GetsTheEnginesRgb565Le_TruncatedAs1_4_InPackets()
    {
        var (device, driver) = Plug(0x0416, 0x5302, PanelTransport.Hid);
        device.Hid.MaxOutputReportLength = 513;
        device.Hid.MaxInputReportLength = 513;
        var reply = new byte[64];
        (reply[1], reply[2], reply[3], reply[4], reply[13]) = (0xDA, 0xDB, 0xDC, 0xDD, 1);
        (reply[5], reply[6]) = (0, 58); // SUB 0, PM 58: the Frozen Warframe SE, 240 × 320, RGB565 little-endian
        device.Hid.Reply(reply); // the probe's
        device.Hid.Reply(reply); // the start's
        var manager = Manager(driver);
        await Run(manager, "frozenwarframe");

        var writes = 0;
        await Until(() =>
        {
            Deliver();
            writes = device.Trace.Entries.Count(e => e.Op == PanelTraceOp.HidWrite);
            return Task.FromResult(writes >= 2 + 301);
        }, "no whole frame was written");
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        var reports = device.Trace.Entries.Where(e => e.Op == PanelTraceOp.HidWrite).Skip(2).Take(301).Select(e => e.Data).ToList();
        var header = reports[0];
        Assert.Equal((1, 240, 320, 240 * 320 * 2), ((int)header[1 + 6], (int)BitConverter.ToUInt16(header, 1 + 8), (int)BitConverter.ToUInt16(header, 1 + 10), BitConverter.ToInt32(header, 1 + 16)));
        var pixels = header.AsSpan(1 + 20).ToArray().Concat(reports.Skip(1).SelectMany(r => r.Skip(1))).Take(240 * 320 * 2).ToArray();
        // The centre pixel, RGB565 little-endian by truncation as 1.4 converted (:350-385): steel blue (70, 130, 180) is
        // red 8, green 32, blue 22, 0x4416, low byte first (Skia's rounded conversion would make red 9).
        var at = (160 * 240 + 120) * 2;
        Assert.Equal((0x16, 0x44), (pixels[at], pixels[at + 1]));
        Assert.Equal("h2 close hid", PanelTrace.FormatLine(device.Trace.Entries[^1]));
    }

    private static byte[] ChiZhuReply(string identifier, byte pm, byte sub)
    {
        var reply = new byte[48];
        (reply[0], reply[1], reply[2], reply[3]) = (0x12, 0x34, 0x56, 0x78);
        System.Text.Encoding.ASCII.GetBytes(identifier).CopyTo(reply, 4);
        (reply[24], reply[28]) = (pm, sub);
        return reply;
    }

    private static byte[] LyReply()
    {
        var reply = new byte[TrofeoBulkProtocol.ReplySize];
        (reply[0], reply[1], reply[8], reply[20]) = (0x03, 0xFF, 0x01, 1); // byte 20 = 1: the 9.16"
        BitConverter.GetBytes(1920).CopyTo(reply, 24);
        BitConverter.GetBytes(480).CopyTo(reply, 28);
        return reply;
    }

    /// <summary>The first ChiZhu frame written (past the probe's and the start's inits), and its pixels after the 64-byte header.</summary>
    private async Task<byte[]> ChiZhuFrame(FakePanelDevice device, Action deliver)
    {
        PanelTraceEntry? frame = null;
        await Until(() =>
        {
            deliver();
            frame = device.Trace.Entries.FirstOrDefault(e => e.Op == PanelTraceOp.UsbWrite && e.Data.Length > ChiZhuProtocol.HeaderSize);
            return Task.FromResult(frame != null);
        }, "no frame was written");
        return frame!.Data.AsSpan(ChiZhuProtocol.HeaderSize).ToArray();
    }

    /// <summary>An LY frame's JPEG, put back together from its 512-byte sub-packets (each a 16-byte header and up to 496 bytes).</summary>
    private static byte[] LyJpeg(IEnumerable<byte[]> writes)
    {
        var frame = writes.SelectMany(w => w).ToArray();
        var length = BitConverter.ToInt32(frame, 2);
        var jpeg = new List<byte>(length);
        for (var at = 0; jpeg.Count < length; at += TrofeoBulkProtocol.SubPacketSize)
            jpeg.AddRange(frame.AsSpan(at + TrofeoBulkProtocol.SubHeaderSize, BitConverter.ToUInt16(frame, at + 6)).ToArray());
        return [.. jpeg];
    }

    /// <summary>The first LY frame written: its transfers after the init up to its acknowledgement, as one JPEG.</summary>
    private async Task<byte[]> LyFrame(FakePanelDevice device, Action deliver)
    {
        List<byte[]>? writes = null;
        await Until(() =>
        {
            deliver();
            var entries = device.Trace.Entries;
            var reads = entries.Select((e, i) => (e, i)).Where(x => x.e.Op == PanelTraceOp.UsbRead).Select(x => x.i).ToList();
            if (reads.Count >= 3) // the probe's and the start's replies, then the frame's acknowledgement
                writes = entries.Take(reads[2]).Skip(reads[1] + 1).Where(e => e.Op == PanelTraceOp.UsbWrite).Select(e => e.Data).ToList();
            return Task.FromResult(writes != null);
        }, "no frame was acknowledged");
        return LyJpeg(writes!);
    }

    [Fact]
    public async Task A_ChiZhu_Rgb565Be_Panel_GetsTheEnginesBigEndianRgb565_TruncatedAs1_4()
    {
        // PM 32: the ChiZhu Vision 320 × 320, RGB565 big-endian; the engine converts by truncation, as 1.4 did (:350-385).
        var (device, driver) = Plug(0x87AD, 0x70DB);
        var reply = ChiZhuReply("", 32, 0);
        device.Usb.Reply(reply);
        device.Usb.Reply(reply);
        var manager = Manager(driver);
        await Run(manager, "chizhuvision320x320");

        var pixels = await ChiZhuFrame(device, () => Deliver());
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        // The centre pixel (the 8 × 2 frame fits 320 × 80 of it, letterboxed): 0x4416, high byte first.
        Assert.Equal(320 * 320 * 2, pixels.Length);
        var at = (160 * 320 + 160) * 2;
        Assert.Equal((0x44, 0x16), (pixels[at], pixels[at + 1]));
    }

    [Fact]
    public async Task A_Panel_DimmedByTheEngine_GetsItsFrameAtItsBrightness()
    {
        // These panels have no light of their own: at 50 % the engine dims every channel by half before the encode.
        var (device, driver) = Plug(0x87AD, 0x70DB);
        var reply = ChiZhuReply("", 32, 0);
        device.Usb.Reply(reply);
        device.Usb.Reply(reply);
        var manager = Manager(driver);
        await Run(manager, "chizhuvision320x320", brightness: 50);

        var pixels = await ChiZhuFrame(device, () => Deliver(new SKColor(200, 100, 40)));
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        // The centre pixel at (100, 50, 20), truncated: red 12, green 12, blue 2, high byte first.
        var at = (160 * 320 + 160) * 2;
        var word = pixels[at] << 8 | pixels[at + 1];
        Assert.InRange(word >> 11, 11, 12);
        Assert.InRange((word >> 5) & 0x3F, 12, 13);
        Assert.InRange(word & 0x1F, 2, 3);
    }

    [Fact]
    public async Task A_6_67_Panels_DisplayMask_IsDrawnOverItsFrame_ByTheEngine()
    {
        // The Wonder Vision 360 with its mask rounded on every corner: the engine draws the PNG over the frame before
        // the encode, so the panel's JPEG shows the mask where it is opaque and the frame elsewhere.
        var (device, driver) = Plug(0x87AD, 0x70DB);
        var reply = ChiZhuReply("SSCRM-V3", 0, 1);
        device.Usb.Reply(reply);
        device.Usb.Reply(reply);
        var manager = Manager(driver);
        await Run(manager, "wondervision360", familySettings: """{"displayMask":"RoundedAll"}""");

        using var white = new SKBitmap(new SKImageInfo(160, 72, SKColorType.Rgba8888, SKAlphaType.Premul)); // the panel's shape: no letterbox
        white.Erase(SKColors.White);
        var jpeg = await ChiZhuFrame(device, () => Deliver(white));
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal((1600, 720), (decoded.Width, decoded.Height));
        using var stream = ThermalrightFrames.OpenMask(ThermalrightMask.RoundedAll, 0)!;
        using var mask = SKBitmap.Decode(stream);
        Assert.Equal((1600, 720), (mask.Width, mask.Height));
        // A pixel deep inside the mask (a 17 × 17 square around it opaque), so the JPEG's blocks there are the mask's alone.
        bool Inside(int cx, int cy) => cx >= 8 && cy >= 8 && cx < mask.Width - 8 && cy < mask.Height - 8
            && Enumerable.Range(-8, 17).All(dy => Enumerable.Range(-8, 17).All(dx => mask.GetPixel(cx + dx, cy + dy).Alpha == 255));
        var (x, y) = Enumerable.Range(8, 200).SelectMany(k => new[] { (k, k), (1599 - k, k), (k, 719 - k), (1599 - k, 719 - k) }).First(p => Inside(p.Item1, p.Item2));
        var masked = decoded.GetPixel(x, y);
        var drawn = mask.GetPixel(x, y);
        Assert.True(Math.Abs(masked.Red - drawn.Red) <= 12 && Math.Abs(masked.Green - drawn.Green) <= 12 && Math.Abs(masked.Blue - drawn.Blue) <= 12,
            $"at ({x}, {y}) the frame is {masked}, the mask {drawn}");
        Assert.True(decoded.GetPixel(800, 360).Red >= 245, "the centre is the frame's white");
    }

    [Fact]
    public async Task A_6_67_Panels_DisplayMask_FollowsARotationChange_MidSession()
    {
        // The panel turned to 90° while it runs (the session kept): the next frame gets the 90° overlay, 1.4's portrait
        // mask as 1.4 drew it, clipped to the landscape frame, in place of the 0° one.
        var (device, driver) = Plug(0x87AD, 0x70DB);
        var reply = ChiZhuReply("SSCRM-V3", 0, 1);
        device.Usb.Reply(reply);
        device.Usb.Reply(reply);
        var manager = Manager(driver);
        var panel = await Run(manager, "wondervision360", familySettings: """{"displayMask":"RoundedAll"}""");
        // What 1.4 drew at each rotation: the 0° PNG, and the 90° one (portrait) at (0, 0) clipped to the landscape frame.
        using var png0 = ThermalrightFrames.OpenMask(ThermalrightMask.RoundedAll, 0)!;
        using var at0 = SKBitmap.Decode(png0);
        using var at90 = new SKBitmap(new SKImageInfo(1600, 720, SKColorType.Rgba8888, SKAlphaType.Premul));
        at90.Erase(SKColors.Transparent);
        using (var png90 = ThermalrightFrames.OpenMask(ThermalrightMask.RoundedAll, 90)!)
        using (var portraitMask = SKBitmap.Decode(png90))
        using (var canvas = new SKCanvas(at90))
            canvas.DrawBitmap(portraitMask, 0, 0);
        // A pixel deep inside each mask (a 17 × 17 square around it opaque) where the other is clear (the same square
        // transparent), so the JPEG's blocks there are the one mask's or the frame's alone.
        static bool All(SKBitmap mask, int cx, int cy, Func<byte, bool> alpha) =>
            Enumerable.Range(-8, 17).All(dy => Enumerable.Range(-8, 17).All(dx => alpha(mask.GetPixel(cx + dx, cy + dy).Alpha)));
        static (int X, int Y) Only(SKBitmap mask, SKBitmap other) =>
            Enumerable.Range(0, (1592 - 8) / 4).SelectMany(i => Enumerable.Range(0, (712 - 8) / 4).Select(j => (X: 8 + i * 4, Y: 8 + j * 4)))
                .First(p => All(mask, p.X, p.Y, a => a == 255) && All(other, p.X, p.Y, a => a == 0));
        var only0 = Only(at0, at90);
        var only90 = Only(at90, at0);
        static bool Near(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) <= 12 && Math.Abs(a.Green - b.Green) <= 12 && Math.Abs(a.Blue - b.Blue) <= 12;

        // At 0°: a landscape white frame, the 0° mask over it.
        using var white = new SKBitmap(new SKImageInfo(160, 72, SKColorType.Rgba8888, SKAlphaType.Premul));
        white.Erase(SKColors.White);
        using (var first = SKBitmap.Decode(await ChiZhuFrame(device, () => Deliver(white))))
        {
            Assert.True(Near(first.GetPixel(only0.X, only0.Y), at0.GetPixel(only0.X, only0.Y)), $"at 0° ({only0}) the 0° mask isn't drawn");
            Assert.True(first.GetPixel(only90.X, only90.Y).Red >= 245, $"at 0° ({only90}) is the frame's white");
        }

        // Turned to 90°: a portrait grey frame (turned, it fills the panel), so the first frame whose centre is grey is
        // one rendered at 90°.
        await manager.UpdateAsync(panel.Saved.Id, s => s with { Rotation = PanelRotation.Rotate90 });
        var grey = new SKColor(160, 160, 160);
        using var portrait = new SKBitmap(new SKImageInfo(72, 160, SKColorType.Rgba8888, SKAlphaType.Premul));
        portrait.Erase(grey);
        SKBitmap? turned = null;
        await Until(() =>
        {
            Deliver(portrait);
            foreach (var write in device.Trace.Entries.Where(e => e.Op == PanelTraceOp.UsbWrite && e.Data.Length > ChiZhuProtocol.HeaderSize).Reverse())
            {
                var decoded = SKBitmap.Decode(write.Data.AsSpan(ChiZhuProtocol.HeaderSize).ToArray());
                if (Near(decoded.GetPixel(800, 360), grey))
                {
                    turned = decoded;
                    return Task.FromResult(true);
                }
                decoded.Dispose();
                break; // only the newest frame is looked at
            }
            return Task.FromResult(false);
        }, "no frame was written at 90°");
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));
        using (turned)
        {
            Assert.True(Near(turned!.GetPixel(only90.X, only90.Y), at90.GetPixel(only90.X, only90.Y)),
                $"at 90° ({only90}) the frame is {turned.GetPixel(only90.X, only90.Y)}, the 90° mask {at90.GetPixel(only90.X, only90.Y)}");
            Assert.True(Near(turned.GetPixel(only0.X, only0.Y), grey), $"at 90° ({only0}) the 0° mask is gone: {turned.GetPixel(only0.X, only0.Y)}");
        }
    }

    [Fact]
    public async Task A_9_16_Panels_FlickerFix_SendsTheTop462Rows_ByTheEngine()
    {
        // The flicker fix's crop: the engine renders at 480 rows and encodes the top 462 (:177-185).
        var (device, driver) = Plug(0x0416, 0x5408);
        device.Usb.Respond(_ => LyReply()); // the inits' replies and each frame's acknowledgement
        var manager = Manager(driver);
        await Run(manager, "trofeovision916", familySettings: """{"flickerFix":true}""");

        var jpeg = await LyFrame(device, () => Deliver());
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal((1920, 462), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task A_Jpeg_OverTrofeoBulksCap_IsEncodedAgainLower_ByTheEngine_AndSent()
    {
        // The 230,000-byte cap (:1129): a frame whose JPEG at quality 95 is over it is encoded again 5 lower at a time,
        // and the first that fits is sent. The frame is noise in a band of rows as tall as makes q95 and q90 too big and
        // q50 not; the JPEG sent is the one at the first quality from 95 down, 5 at a time, that fits.
        var (device, driver) = Plug(0x0416, 0x5408);
        device.Usb.Respond(_ => LyReply());
        var manager = Manager(driver);
        await Run(manager, "trofeovision916");

        const int cap = ThermalrightFrames.TrofeoBulkMaxJpegBytes;
        using var frame = new SKBitmap(new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rgba = new byte[1920 * 480 * 4];
        new Random(7).NextBytes(rgba);
        var expected = 0;
        for (var rows = 16; rows <= 480 && expected == 0; rows += 16)
        {
            var trial = (byte[])rgba.Clone();
            for (var i = 0; i < trial.Length; i++)
                trial[i] = i % 4 == 3 ? (byte)255 : i / (1920 * 4) < rows ? trial[i] : (byte)128;
            var sizes = Enumerable.Range(0, 10).Select(step => PanelJpeg.Encode(trial, 1920, 480, ThermalrightFrames.DefaultJpegQuality - step * ThermalrightFrames.JpegQualityStep).Length).ToList();
            if (sizes[0] > cap && sizes[1] > cap && sizes[^1] <= cap)
                (expected, rgba) = (sizes.First(s => s <= cap), trial);
        }
        Assert.True(expected > 0, "no band of noise is over the cap at q95 and q90 and under it at q50");
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, frame.GetPixels(), rgba.Length);

        var jpeg = await LyFrame(device, () => Deliver(frame));
        Assert.True(await manager.StopAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(expected, jpeg.Length);
        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal((1920, 480), (decoded.Width, decoded.Height));
    }
}
