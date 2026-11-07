using System.Diagnostics;
using InfoPanel.Core.Tests.Panels.Usb;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;
using V = InfoPanel.Core.Tests.Panels.BeadaPanel.BeadaPanelVectors;

namespace InfoPanel.Core.Tests.Panels.BeadaPanel;

/// <summary>
/// JPEG streaming to a BeadaPanel: which panels get it under Auto, the start tag (Panel-Link Rev 1.0 p. 7, table 2-5:
/// type 5, <c>image/jpeg</c>, no size fields, checksum <c>d6 79</c> printed high byte first), frames as one JPEG a write,
/// the stop's black JPEG, the fallback to raw when the tag isn't taken, and raw unchanged when it is chosen.
/// </summary>
public class BeadaPanelJpegTests
{
    private static readonly UsbDeviceId PanelId = new(0x4E58, 0x1001, 1, 4, "1-2");

    /// <summary>The manual's JPEG start tag, worked by hand (and with an independent script): <c>79 d6</c> on the wire.</summary>
    private static byte[] JpegStart => V.PanelLink(5, "image/jpeg", 0x79, 0xD6);

    /// <summary>The existing raw stream's start tag for a 1920 × 480 panel that reports its size (checksum by an independent script).</summary>
    private static byte[] RawStart1920x480 => V.PanelLink(5, "video/x-raw, format=RGB16, width=1920, height=480, framerate=0/1", 0xD3, 0xD7);

    /// <summary>A BeadaPanel 8 as seen: PanelLink 2, platform 5 (T113), model code 21, 1920 × 480.</summary>
    private static byte[] T113Reply => V.InfoReply(2, 5, 21, 1920, 480, serial: "934072005c");

    private static (FakeUsbBackend Backend, FakeUsbDevice Device) Panel(byte[] reply)
    {
        var backend = new FakeUsbBackend();
        var device = backend.Add(new FakeUsbDevice(PanelId) { Reply = reply });
        return (backend, device);
    }

    private static BeadaPanelSession Started(FakeUsbBackend backend, PanelEncoding encoding, int brightness = 50)
    {
        var start = BeadaPanelSession.Start(backend, PanelId, brightness, encoding, backend.Clock, CancellationToken.None);
        Assert.Equal(UsbStatus.Ok, start.Status);
        return start.Session!;
    }

    private static BeadaPanelInfo Info(byte panelLink, byte platform) =>
        BeadaPanelInfo.Parse(V.InfoReply(panelLink, platform, 21, 1920, 480), out _, out _)!;

    /// <summary>An opaque RGBA frame, a colour per quadrant, encoded as the runner encodes one.</summary>
    private static byte[] JpegFrame(int width, int height, SKColor colour)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (colour.Red, colour.Green, colour.Blue, 255);
        return PanelJpeg.Encode(rgba, width, height, BeadaPanelProtocol.JpegQuality);
    }

    private static void AssertTranscript((long AtMs, byte Endpoint, byte[] Data)[] expected, FakeTransfer[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Endpoint == actual[i].Endpoint, $"transfer {i}: endpoint 0x{actual[i].Endpoint:x2}, expected 0x{expected[i].Endpoint:x2}");
            Assert.True(expected[i].AtMs == actual[i].AtMs, $"transfer {i}: at {actual[i].AtMs} ms, expected {expected[i].AtMs} ms");
            Assert.True(expected[i].Data.AsSpan().SequenceEqual(actual[i].Data), $"transfer {i}: bytes differ (length {actual[i].Data.Length}, expected {expected[i].Data.Length})");
        }
    }

    private static void AssertJpeg(byte[] data)
    {
        Assert.True(data.Length > 4, "a JPEG is more than its markers");
        Assert.Equal([0xFF, 0xD8], data[..2]);
        Assert.Equal([0xFF, 0xD9], data[^2..]);
    }

    /// <summary>
    /// The rule: nothing in the manuals, 1.4.x or NXElec's StreamBridge says which panels decode JPEG (Panel-Link Rev 1.0
    /// p. 7, table 2-5, defines the stream for every panel; Status-Link Rev 1.3 p. 7, table 2-3: no format list or
    /// capability bit in the info reply), so Auto streams JPEG only where it has been seen to work: platform 5 (T113)
    /// with PanelLink 2 (a BeadaPanel 8, 2026-11-07). PanelLink 1 takes the legacy tags, not type 5 (1.4.x
    /// <c>BeadaPanelDeviceTask.cs:154,160</c>).
    /// </summary>
    public static TheoryData<byte, byte, bool, string> SupportRule => new()
    {
        { 2, 5, true, "T113, PanelLink 2: seen drawing JPEG q90, q75 and q50" },
        { 3, 5, true, "T113, a later PanelLink: type 5 tags still exist" },
        { 1, 5, false, "PanelLink 1: legacy tags only (1.4.x)" },
        { 2, 4, false, "V3S: unconfirmed" },
        { 2, 1, false, "i.MX6UL: unconfirmed" },
        { 2, 2, false, "i.MX6ULL: unconfirmed" },
        { 2, 0, false, "platform 0: not in Status-Link's table" },
        { 2, 6, false, "a platform the manual doesn't list" },
    };

    [Theory]
    [MemberData(nameof(SupportRule))]
    public void Auto_takes_jpeg_only_where_it_has_been_seen_to_work(byte panelLink, byte platform, bool jpeg, string source)
    {
        var info = Info(panelLink, platform);
        Assert.True(jpeg == BeadaPanelProtocol.TakesJpeg(info), source);
        Assert.Equal(jpeg, BeadaPanelProtocol.StreamsJpeg(PanelEncoding.Auto, info));
        Assert.True(BeadaPanelProtocol.StreamsJpeg(PanelEncoding.Jpeg, info), "JPEG chosen is JPEG on any panel");
        Assert.False(BeadaPanelProtocol.StreamsJpeg(PanelEncoding.Raw, info), "raw chosen is raw on any panel");
    }

    [Fact]
    public void The_jpeg_start_tag_is_the_manuals_byte_for_byte()
    {
        Assert.Equal(JpegStart, BeadaPanelProtocol.StartJpegStream());
        Assert.Equal(90, BeadaPanelProtocol.JpegQuality);
    }

    [Fact]
    public void Auto_on_a_t113_streams_jpeg_one_write_a_frame_and_stops_with_a_black_jpeg_then_the_end_tag()
    {
        var (backend, device) = Panel(T113Reply);
        var session = Started(backend, PanelEncoding.Auto);
        Assert.True(session.Jpeg);
        Assert.Null(session.JpegRefused);

        var one = JpegFrame(1920, 480, SKColors.Red);
        var two = JpegFrame(1920, 480, SKColors.Blue);
        AssertJpeg(one);
        Assert.Equal(UsbStatus.Ok, session.WriteFrame(one));
        Assert.Equal(UsbStatus.Ok, session.WriteFrame(two));
        session.Stop();

        var t = device.Transfers;
        Assert.Equal(10, t.Length);
        AssertTranscript(
            [(300, 0x02, V.GetPanelInfo), (300, 0x82, T113Reply), (300, 0x02, V.PanelLinkReset), (1300, 0x02, V.SetBacklight(50)),
             (1300, 0x01, JpegStart), (1300, 0x01, one), (1300, 0x01, two), (1300, 0x02, V.SetBacklight(0))],
            t[..8]);

        // The stop's black frame is a JPEG of the panel's size, then the end tag (type 6).
        Assert.Equal(0x01, t[8].Endpoint);
        AssertJpeg(t[8].Data);
        using (var black = SKBitmap.Decode(t[8].Data))
        {
            Assert.Equal((1920, 480), (black.Width, black.Height));
            Assert.All(black.Pixels.Distinct(), p => Assert.True(p.Red <= 2 && p.Green <= 2 && p.Blue <= 2, $"not black: {p}"));
        }
        AssertTranscript([(1300, 0x01, V.EndMediaStream)], t[9..]);

        // Every write at least 2 s (a q50 write once stalled 1.6 s on the panel).
        Assert.All(device.WriteTimeouts, ms => Assert.True(ms >= 2000, $"a write's timeout was {ms} ms"));
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void Raw_chosen_on_a_t113_is_us_raw_transcript_unchanged()
    {
        var (backend, device) = Panel(T113Reply);
        var session = Started(backend, PanelEncoding.Raw);
        Assert.False(session.Jpeg);
        var frame = new byte[session.FrameLength];
        Array.Fill(frame, (byte)0x5A);
        session.WriteFrame(frame);
        session.Stop();

        // The same bytes the start without an encoding (the existing raw stream's, which its transcript tests hold) writes.
        var (backendU, deviceU) = Panel(T113Reply);
        var u = BeadaPanelSession.Start(backendU, PanelId, 50, backendU.Clock, CancellationToken.None).Session!;
        u.WriteFrame(frame);
        u.Stop();

        AssertTranscript(
            [(300, 0x02, V.GetPanelInfo), (300, 0x82, T113Reply), (300, 0x02, V.PanelLinkReset), (1300, 0x02, V.SetBacklight(50)),
             (1300, 0x01, RawStart1920x480), (1300, 0x01, frame), (1300, 0x02, V.SetBacklight(0)), (1300, 0x01, new byte[1920 * 480 * 2]),
             (1300, 0x01, V.EndMediaStream)],
            device.Transfers);
        AssertTranscript([.. deviceU.Transfers.Select(x => (x.AtMs, x.Endpoint, x.Data))], device.Transfers);
    }

    [Fact]
    public void Auto_on_an_unconfirmed_platform_streams_raw()
    {
        // A BeadaPanel 4C on a V3S (platform 4), 800 × 480 as it reports it.
        var reply = V.InfoReply(2, 4, 14, 800, 480);
        var (backend, device) = Panel(reply);
        var session = Started(backend, PanelEncoding.Auto);
        Assert.False(session.Jpeg);
        session.Stop();

        var rawStart = V.PanelLink(5, "video/x-raw, format=RGB16, width=800, height=480, framerate=0/1", 0xC9, 0x16);
        Assert.Equal(rawStart, device.Transfers[4].Data);
        Assert.DoesNotContain(device.Transfers, x => x.Data.AsSpan().SequenceEqual(JpegStart));
        Assert.Equal(new byte[800 * 480 * 2], device.Transfers[^2].Data);
    }

    [Fact]
    public void Jpeg_chosen_on_an_unconfirmed_platform_sends_the_jpeg_start_tag()
    {
        var (backend, device) = Panel(V.InfoReply(2, 4, 14, 800, 480));
        var session = Started(backend, PanelEncoding.Jpeg);
        Assert.True(session.Jpeg);
        session.Stop();
        Assert.Equal(JpegStart, device.Transfers[4].Data);
    }

    [Fact]
    public void A_refused_jpeg_start_falls_back_to_raw_in_the_same_session()
    {
        var (backend, device) = Panel(T113Reply);
        device.OnWrite = write => write.Data.AsSpan().SequenceEqual(JpegStart) ? UsbStatus.Timeout : null;

        var session = Started(backend, PanelEncoding.Auto);

        Assert.False(session.Jpeg);
        Assert.Equal(UsbStatus.Timeout, session.JpegRefused);
        var frame = new byte[session.FrameLength];
        Assert.Equal(UsbStatus.Ok, session.WriteFrame(frame));
        session.Stop();
        // The refused tag, then the start's own recovery (the reset, 1000 ms, the backlight), then the raw stream.
        AssertTranscript(
            [(1300, 0x01, JpegStart), (1300, 0x02, V.PanelLinkReset), (2300, 0x02, V.SetBacklight(50)), (2300, 0x01, RawStart1920x480),
             (2300, 0x01, frame), (2300, 0x02, V.SetBacklight(0)), (2300, 0x01, new byte[1920 * 480 * 2]), (2300, 0x01, V.EndMediaStream)],
            device.Transfers[4..]);
    }

    /// <summary>A panel whose stream is stalled after a tag it didn't take, until its PanelLink is reset: the raw start goes after the reset.</summary>
    [Fact]
    public void After_a_refused_jpeg_start_the_raw_start_follows_a_reset()
    {
        var (backend, device) = Panel(T113Reply);
        var stalled = false;
        device.OnWrite = write =>
        {
            if (write.Data.AsSpan().SequenceEqual(JpegStart))
            {
                stalled = true;
                return UsbStatus.Io;
            }
            if (write.Data.AsSpan().SequenceEqual(V.PanelLinkReset))
                stalled = false;
            return stalled && write.Endpoint == 0x01 ? UsbStatus.Timeout : null;
        };
        var refusals = new List<UsbStatus>();

        var start = BeadaPanelSession.Start(backend, PanelId, 50, PanelEncoding.Auto, backend.Clock, CancellationToken.None, refusals.Add);

        Assert.Equal(UsbStatus.Ok, start.Status);
        Assert.False(start.Session!.Jpeg);
        Assert.Equal(UsbStatus.Io, start.Session.JpegRefused);
        Assert.Equal([UsbStatus.Io], refusals);
        Assert.Equal(UsbStatus.Ok, start.Session.WriteFrame(new byte[start.Session.FrameLength]));
        start.Session.Stop();
    }

    [Fact]
    public void A_reset_not_taken_after_a_refused_jpeg_start_fails_the_start_with_its_reason()
    {
        var (backend, device) = Panel(T113Reply);
        var refused = false;
        device.OnWrite = write =>
        {
            if (write.Data.AsSpan().SequenceEqual(JpegStart))
            {
                refused = true;
                return UsbStatus.Timeout;
            }
            return refused && write.Data.AsSpan().SequenceEqual(V.PanelLinkReset) ? UsbStatus.Timeout : null;
        };
        var refusals = new List<UsbStatus>();

        var start = BeadaPanelSession.Start(backend, PanelId, 50, PanelEncoding.Auto, backend.Clock, CancellationToken.None, refusals.Add);

        Assert.Null(start.Session);
        Assert.Equal(UsbStatus.Timeout, start.Status);
        Assert.Equal("the reset after the JPEG start wasn't taken (Timeout)", start.Message);
        Assert.Equal([UsbStatus.Timeout], refusals); // the refusal is told, though the start failed
        Assert.DoesNotContain(device.Transfers, x => x.Data.AsSpan().SequenceEqual(RawStart1920x480));
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void A_refusal_is_told_when_the_raw_start_then_fails_or_the_start_is_cancelled()
    {
        // Both start tags time out.
        var (backend, device) = Panel(T113Reply);
        device.OnWrite = write => write.Endpoint == 0x01 && write.Data.Length == 270 ? UsbStatus.Timeout : null;
        var refusals = new List<UsbStatus>();
        var start = BeadaPanelSession.Start(backend, PanelId, 50, PanelEncoding.Auto, backend.Clock, CancellationToken.None, refusals.Add);
        Assert.Null(start.Session);
        Assert.Equal(UsbStatus.Timeout, start.Status);
        Assert.Equal([UsbStatus.Timeout], refusals);

        // A stop asked as the JPEG tag is refused.
        var (backend2, device2) = Panel(T113Reply);
        using var stop = new CancellationTokenSource();
        device2.OnWrite = write =>
        {
            if (!write.Data.AsSpan().SequenceEqual(JpegStart))
                return null;
            stop.Cancel();
            return UsbStatus.Timeout;
        };
        var refusals2 = new List<UsbStatus>();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            BeadaPanelSession.Start(backend2, PanelId, 50, PanelEncoding.Auto, backend2.Clock, stop.Token, refusals2.Add));
        Assert.Equal([UsbStatus.Timeout], refusals2);
        Assert.Equal(0, device2.OpenHandles);
    }

    [Fact]
    public void A_panel_gone_at_the_jpeg_start_fails_the_start_without_a_raw_one()
    {
        var (backend, device) = Panel(T113Reply);
        device.UnplugAtWrite = 4; // GetPanelInfo, the reset, the backlight, then the JPEG start tag

        var start = BeadaPanelSession.Start(backend, PanelId, 50, PanelEncoding.Auto, backend.Clock, CancellationToken.None);

        Assert.Null(start.Session);
        Assert.Equal(UsbStatus.NoDevice, start.Status);
        Assert.DoesNotContain(device.Transfers, x => x.Data.AsSpan().SequenceEqual(RawStart1920x480));
        Assert.Equal(0, device.OpenHandles);
    }

    [Fact]
    public void A_jpeg_session_takes_only_a_jpeg_and_a_raw_one_only_a_whole_raw_frame()
    {
        var (backend, _) = Panel(T113Reply);
        var jpeg = Started(backend, PanelEncoding.Auto);
        Assert.Throws<ArgumentException>(() => jpeg.WriteFrame(new byte[jpeg.FrameLength]));
        jpeg.Stop();

        var (rawBackend, _) = Panel(T113Reply);
        var raw = Started(rawBackend, PanelEncoding.Raw);
        Assert.Throws<ArgumentException>(() => raw.WriteFrame(JpegFrame(1920, 480, SKColors.Red)));
        raw.Stop();
    }

    /// <summary>The panel's frame as RGBA for the encoder: the same rotation, fit and black border as the RGB565 frame.</summary>
    [Theory]
    [InlineData(PanelRotation.None)]
    [InlineData(PanelRotation.Rotate90)]
    [InlineData(PanelRotation.Rotate180)]
    [InlineData(PanelRotation.Rotate270)]
    public void The_rgba_frame_has_the_raw_frames_geometry(PanelRotation rotation)
    {
        // 400 × 200: red left half, green right half, a blue band along the top.
        using var frame = PanelTransformTests.Frame(400, 200, (x, y) => y < 40 ? SKColors.Blue : x < 200 ? SKColors.Red : SKColors.Lime);
        using var pixmap = frame.PeekPixels();
        const int w = 480, h = 320;
        var rgb565 = new byte[PanelTransform.TargetLength(w, h, PanelPixelFormat.Rgb565Le)];
        var rgba = new byte[PanelTransform.TargetLength(w, h, PanelPixelFormat.Rgba8888)];
        Assert.Equal(w * h * 4, rgba.Length);

        PanelTransform.Render(pixmap, rgb565, w, h, rotation, PanelPixelFormat.Rgb565Le);
        PanelTransform.Render(pixmap, rgba, w, h, rotation, PanelPixelFormat.Rgba8888);

        var differing = 0;
        for (var i = 0; i < w * h; i++)
        {
            var word = (ushort)(rgb565[i * 2] | rgb565[i * 2 + 1] << 8);
            int r = (word >> 11) * 255 / 31, g = (word >> 5 & 0x3F) * 255 / 63, b = (word & 0x1F) * 255 / 31;
            Assert.Equal(255, rgba[i * 4 + 3]); // opaque, the border black
            if (Math.Abs(r - rgba[i * 4]) > 8 || Math.Abs(g - rgba[i * 4 + 1]) > 8 || Math.Abs(b - rgba[i * 4 + 2]) > 8)
                differing++;
        }
        // Edges blend a little differently at 16 bits; the picture is the same.
        Assert.True(differing < w * h / 100, $"{differing} of {w * h} pixels differ from the RGB565 frame");
    }

    [Fact]
    public void A_jpeg_decodes_to_the_panels_size_and_picture()
    {
        using var frame = PanelTransformTests.Frame(1920, 480, (x, _) => x < 960 ? SKColors.Red : SKColors.Blue);
        using var pixmap = frame.PeekPixels();
        var rgba = new byte[PanelTransform.TargetLength(1920, 480, PanelPixelFormat.Rgba8888)];
        PanelTransform.Render(pixmap, rgba, 1920, 480, PanelRotation.None, PanelPixelFormat.Rgba8888);

        var buffer = Array.Empty<byte>();
        var length = PanelJpeg.Encode(rgba, 1920, 480, 90, ref buffer);
        var jpeg = buffer[..length];
        AssertJpeg(jpeg);
        Assert.Equal(jpeg, PanelJpeg.Encode(rgba, 1920, 480, 90));

        using var decoded = SKBitmap.Decode(jpeg);
        Assert.Equal((1920, 480), (decoded.Width, decoded.Height));
        var left = decoded.GetPixel(400, 240);
        var right = decoded.GetPixel(1500, 240);
        Assert.True(left.Red > 240 && left.Blue < 16, $"left {left}");
        Assert.True(right.Blue > 240 && right.Red < 16, $"right {right}");
    }

    /// <summary>A loose ceiling, not a benchmark: the demo measured 3.6 ms on this Mac.</summary>
    [Fact]
    public void A_1920_by_480_frame_encodes_at_q90_well_within_a_frame()
    {
        var rgba = new byte[1920 * 480 * 4];
        using (var bitmap = new SKBitmap(new SKImageInfo(1920, 480, SKColorType.Rgba8888, SKAlphaType.Opaque)))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1920, 480), [SKColors.Navy, SKColors.Orange], SKShaderTileMode.Clamp);
                using var fill = new SKPaint { Shader = shader };
                canvas.DrawRect(0, 0, 1920, 480, fill);
                using var font = new SKFont(SKTypeface.Default, 48);
                using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
                for (var row = 0; row < 8; row++)
                    canvas.DrawText($"CPU {row * 7 % 100} %  GPU {row * 13 % 100} %  12:3{row}:45  thin text 0123456789", 20, 55 + row * 58, font, ink);
            }
            bitmap.GetPixelSpan().CopyTo(rgba);
        }

        var buffer = Array.Empty<byte>();
        PanelJpeg.Encode(rgba, 1920, 480, 90, ref buffer); // warm-up
        var times = new double[5];
        var length = 0;
        for (var i = 0; i < times.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            length = PanelJpeg.Encode(rgba, 1920, 480, 90, ref buffer);
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        Array.Sort(times);
        TestContext.Current.TestOutputHelper?.WriteLine($"q90 1920 x 480: median {times[2]:0.0} ms (min {times[0]:0.0}, max {times[^1]:0.0}), {length:N0} bytes");
        Assert.True(times[2] < 50, $"a q90 encode took {times[2]:0.0} ms (median of 5)");
    }
}
