using InfoPanel.Panels.Devices;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// A tester's run on a real panel of the package: <c>INFOPANEL_PANEL_HW=ms9132</c> for a Jonsbo DS339 or a
/// VMAX 4.6" (the families asked in the engine's order, jonsbo then vmax), <c>INFOPANEL_PANEL_HW=ds916</c> for a Jonsbo
/// DS916; skipped otherwise. The panel is probed and opened through the engine's device seam, shown colour bars, a
/// gradient and a moving band (as BGR888 pixels, or JPEGs on the DS916), and stopped (an opted-in run whose setup can't
/// complete fails); every transfer is recorded to <c>INFOPANEL_PANEL_TRACE</c> (a file) through the kit's recorder,
/// so the trace can become a vector. <c>INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port</c> names the panel, as
/// the engine's log lists it.
/// </summary>
public sealed class Ms9132HardwareTests
{
    /// <summary>
    /// The engine's own device seam, built as the panel host builds it (PanelHostEntry): libusb when it loads, HID through
    /// HidSharp, serial ports. An opted-in run whose libusb doesn't load fails: the MS9132's frames need it.
    /// </summary>
    private static IPanelDevices EngineDevices(bool needsUsb)
    {
        var logger = Serilog.Core.Logger.None;
        IUsbBackend? usb = null;
        if (LibUsb.LoadAndLog(LibUsb.DefaultCandidates(), logger) is { } library)
            usb = LibUsbBackend.Create(library, out _);
        Assert.True(usb != null || !needsUsb, "libusb didn't load (beside the engine, INFOPANEL_LIBUSB_DIR, or the system's): the MS9132's frames need it.");
        return new EnginePanelDevices(usb, new HidSharpSystem(logger), new SerialPorts(logger), logger);
    }

    private static (IPanelDevices Devices, PanelCandidate Candidate) Panel(bool needsUsb)
    {
        var named = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_CANDIDATE");
        Assert.False(string.IsNullOrEmpty(named), "Name the panel: INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port (as the engine's log lists it).");
        var engine = EngineDevices(needsUsb);
        var trace = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_TRACE");
        var devices = string.IsNullOrEmpty(trace) ? engine : new RecordingPanelDevices(engine, trace);
        var parts = named!.Split('@', ':');
        return (devices, new PanelCandidate(Convert.ToUInt16(parts[0], 16), Convert.ToUInt16(parts[1], 16), byte.Parse(parts[2]), byte.Parse(parts[3]), parts[4]));
    }

    /// <summary>
    /// Eight colour bars, then a gradient, then a white band moving down on black, in the session's first format (BGR888
    /// pixels, or a JPEG of the same picture at the session's quality), a second and a third each at 15 fps; then the stop.
    /// </summary>
    private static void Show(IPanelSession session, CancellationToken stop)
    {
        try
        {
            var caps = session.Capabilities;
            var format = caps.Formats[0];
            byte[][] colours = [[255, 255, 255], [255, 255, 0], [0, 255, 255], [0, 255, 0], [255, 0, 255], [255, 0, 0], [0, 0, 255], [0, 0, 0]];
            for (var frame = 0; frame < 60; frame++)
            {
                var band = (frame - 40) * caps.Height / 20; // the band's top, in the last 20 frames
                Func<int, int, byte[]> rgb = frame < 20 ? (x, _) => colours[x * 8 / caps.Width]
                    : frame < 40 ? (x, _) => [(byte)(x * 255 / caps.Width), (byte)(x * 255 / caps.Width), (byte)(x * 255 / caps.Width)]
                    : (_, y) => y >= band && y < band + Math.Max(1, caps.Height / 10) ? [255, 255, 255] : [0, 0, 0];
                var bytes = format == PanelFrameFormat.Jpeg ? Jpeg(caps.Width, caps.Height, caps.JpegQuality, rgb) : Raw(caps.Width, caps.Height, rgb);
                Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(bytes, format, frame + 1, false), stop).Status);
                Thread.Sleep(66);
            }
        }
        finally
        {
            session.Close(screenOff: true, TimeSpan.FromSeconds(3)); // also after a failed frame or a cancel
        }
    }

    // BGR888 rows of RGB colours.
    private static byte[] Raw(int width, int height, Func<int, int, byte[]> rgb)
    {
        var bytes = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = rgb(x, y);
                var at = (y * width + x) * 3;
                (bytes[at], bytes[at + 1], bytes[at + 2]) = (c[2], c[1], c[0]);
            }
        return bytes;
    }

    // The same picture as a JPEG.
    private static byte[] Jpeg(int width, int height, int quality, Func<int, int, byte[]> rgb)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var c = rgb(x, y);
                bitmap.SetPixel(x, y, new SKColor(c[0], c[1], c[2]));
            }
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    [Fact]
    public void AConnectedMs9132Panel_StartsShowsBarsAndAGradient_AndStops_Recorded()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_PANEL_HW") == "ms9132",
            "A real Jonsbo DS339 or VMAX 4.6\": set INFOPANEL_PANEL_HW=ms9132 (and INFOPANEL_PANEL_TRACE=<file> to record it).");
        var (devices, candidate) = Panel(needsUsb: true);
        var stop = TestContext.Current.CancellationToken;
        foreach (IPanelDriver driver in new IPanelDriver[] { new JonsboDriver(devices), new VmaxDriver(devices) })
        {
            var probe = driver.Probe(candidate, stop);
            Assert.Equal(PanelDeviceStatus.Ok, probe.Status);
            if (!probe.Mine)
                continue;
            // An unknown model is tried as the DS339, as the family's "Try it as…" would.
            var settings = System.Text.Json.JsonDocument.Parse("""{"model": "ds339"}""").RootElement.Clone();
            var open = driver.Open(candidate, probe.Identity!, new PanelOpenOptions(100, FamilySettings: settings), stop);
            Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
            using var session = open.Session!;
            Show(session, stop);
            return;
        }
        Assert.Fail("Neither family took the panel.");
    }

    [Fact]
    public void AConnectedDs916_StartsShowsFrames_AndStops_Recorded()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_PANEL_HW") == "ds916",
            "A real Jonsbo DS916: set INFOPANEL_PANEL_HW=ds916 (and INFOPANEL_PANEL_TRACE=<file> to record it).");
        var (devices, candidate) = Panel(needsUsb: false);
        var stop = TestContext.Current.CancellationToken;
        var driver = new JonsboDriver(devices);
        var probe = driver.Probe(candidate, stop);
        Assert.True(probe.Mine, $"{probe.Status}: {probe.Message}");
        var open = driver.Open(candidate, probe.Identity!, new PanelOpenOptions(100), stop);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        using var session = open.Session!;
        Show(session, stop);
    }
}
