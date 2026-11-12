using InfoPanel.Panels.Devices;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Turing;
using InfoPanel.Panels.Usb;
using Serilog.Core;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// A tester's run on a real Turing / TURZX or Lian Li panel (§2.2 (9)), skipped unless <c>INFOPANEL_PANEL_HW=turing</c>
/// or <c>lianli</c>: the panel named by <c>INFOPANEL_PANEL_CANDIDATE</c> is probed and opened (a sleeping 3.5" woken by
/// its start) through the engine's device seam, built as the panel host builds it (an opted-in run whose setup can't
/// complete fails), shown colour bars, then a gradient, then a band that changes (a serial
/// panel's sector updates), and stopped with its screen off; every transfer is recorded to <c>INFOPANEL_PANEL_TRACE</c>
/// (a file) through the kit's recorder, so the trace can become a vector.
/// </summary>
public sealed class TuringHardwareTests
{
    /// <summary>
    /// The engine's own device seam, built as the panel host builds it for a package's drivers: USB over
    /// <paramref name="usb"/> (libusb; none: USB answers notSupported), HID, and serial ports.
    /// </summary>
    private static IPanelDevices EngineDevices(IUsbBackend? usb = null) =>
        new EnginePanelDevices(usb, new HidSharpSystem(Logger.None), new SerialPorts(Logger.None), Logger.None);

    private static SKBitmap Draw(int width, int height, Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas);
        return bitmap;
    }

    /// <summary>The bitmap in the session's format: a JPEG, RGB565 little-endian truncated, or BGR.</summary>
    private static byte[] Frame(SKBitmap bitmap, PanelCapabilities caps)
    {
        switch (caps.Formats[0])
        {
            case PanelFrameFormat.Jpeg:
                using (var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, caps.JpegQuality))
                    return data.ToArray();
            case PanelFrameFormat.Rgb565LeTruncated:
            {
                var bytes = new byte[bitmap.Width * bitmap.Height * 2];
                for (var i = 0; i < bitmap.Width * bitmap.Height; i++)
                {
                    var c = bitmap.GetPixel(i % bitmap.Width, i / bitmap.Width);
                    var v = ((c.Red >> 3) << 11) | ((c.Green >> 2) << 5) | (c.Blue >> 3);
                    bytes[i * 2] = (byte)v;
                    bytes[(i * 2) + 1] = (byte)(v >> 8);
                }
                return bytes;
            }
            default:
            {
                var bytes = new byte[bitmap.Width * bitmap.Height * 3];
                for (var i = 0; i < bitmap.Width * bitmap.Height; i++)
                {
                    var c = bitmap.GetPixel(i % bitmap.Width, i / bitmap.Width);
                    (bytes[i * 3], bytes[(i * 3) + 1], bytes[(i * 3) + 2]) = (c.Blue, c.Green, c.Red);
                }
                return bytes;
            }
        }
    }

    [Fact]
    public void TheHardwareTest_DrivesThePanelThroughTheEnginesOwnSeam()
    {
        // No device is touched: the seam is only built (without libusb), as the hardware run builds it.
        Assert.IsType<EnginePanelDevices>(EngineDevices());
    }

    [Fact]
    public void AConnectedPanel_StartsShowsBarsAGradientAndChanges_AndStops_Recorded()
    {
        var family = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_HW");
        Assert.SkipUnless(family is TuringDriver.FamilyId or LianLiDriver.FamilyId,
            "A real Turing / TURZX or Lian Li panel: set INFOPANEL_PANEL_HW=turing or lianli (and INFOPANEL_PANEL_TRACE=<file> to record it).");
        // Opted in: a setup that can't complete fails the run rather than skipping it.
        var named = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_CANDIDATE"); // vvvv:pppp@bus:address:port
        Assert.False(string.IsNullOrEmpty(named), "Name the panel: INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port (as the engine's log lists it).");
        // libusb, as the panel host loads it: the USB panels need it, and on Windows the serial port's finder reads the
        // device's serial through it.
        using var usb = LibUsb.LoadAndLog(LibUsb.DefaultCandidates(), Logger.None) is { } library ? LibUsbBackend.Create(library, out _) : null;
        Assert.True(usb != null, "libusb couldn't be loaded (beside the engine, INFOPANEL_LIBUSB_DIR, or the system's), so the engine's seam can't reach USB.");
        var engine = EngineDevices(usb);
        var trace = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_TRACE");
        var devices = string.IsNullOrEmpty(trace) ? engine : new RecordingPanelDevices(engine, trace);
        var parts = named!.Split('@', ':');
        Assert.True(parts.Length == 5, $"INFOPANEL_PANEL_CANDIDATE={named} isn't vvvv:pppp@bus:address:port.");
        var candidate = new PanelCandidate(Convert.ToUInt16(parts[0], 16), Convert.ToUInt16(parts[1], 16), byte.Parse(parts[2]), byte.Parse(parts[3]), parts[4]);
        IPanelDriver driver = family == TuringDriver.FamilyId ? new TuringDriver(devices) : new LianLiDriver(devices);
        var stop = TestContext.Current.CancellationToken;

        var probe = driver.Probe(candidate, stop);
        Assert.True(probe.Mine, $"{probe.Status}: {probe.Message}");
        var open = driver.Open(candidate, probe.Identity!, new PanelOpenOptions(100), stop); // a 3.5" is woken first
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        using var session = open.Session!;
        try
        {
            var caps = session.Capabilities;
            SKColor[] colours = [SKColors.White, SKColors.Yellow, SKColors.Cyan, SKColors.Lime, SKColors.Magenta, SKColors.Red, SKColors.Blue, SKColors.Black];
            using var bars = Draw(caps.Width, caps.Height, canvas =>
            {
                using var paint = new SKPaint();
                for (var i = 0; i < colours.Length; i++)
                {
                    paint.Color = colours[i];
                    canvas.DrawRect(caps.Width * i / colours.Length, 0, caps.Width / colours.Length + 1, caps.Height, paint);
                }
            });
            using var gradient = Draw(caps.Width, caps.Height, canvas =>
            {
                using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(caps.Width, 0), [SKColors.Black, SKColors.White], SKShaderTileMode.Clamp) };
                canvas.DrawRect(0, 0, caps.Width, caps.Height, paint);
            });
            var frames = new List<byte[]> { Frame(bars, caps), Frame(gradient, caps) };
            for (var step = 0; step < 10; step++) // a band moving down the gradient: a few sectors a frame
            {
                using var changed = gradient.Copy();
                using (var canvas = new SKCanvas(changed))
                using (var paint = new SKPaint { Color = SKColors.OrangeRed })
                    canvas.DrawRect(0, caps.Height * step / 10, caps.Width / 4, 24, paint);
                frames.Add(Frame(changed, caps));
            }

            for (var i = 0; i < frames.Count; i++)
            {
                Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(frames[i], caps.Formats[0], i + 1, false), stop).Status);
                // A serial frame's write sends for about 3 s; the engine's keep-alive, a second later, sends the rest.
                for (var more = 0; caps.Formats[0] != PanelFrameFormat.Jpeg && more < 30; more++)
                {
                    Thread.Sleep(1000);
                    var write = session.WriteFrame(new PanelFrameView(frames[i], caps.Formats[0], i + 1, repeat: true), stop);
                    Assert.Equal(PanelDeviceStatus.Ok, write.Status);
                    if (write.DeviceTime == TimeSpan.Zero)
                        break;
                }
                Thread.Sleep(i < 2 ? 2000 : 300);
            }
            Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(30));
            Thread.Sleep(1000);
        }
        finally
        {
            session.Close(screenOff: true, TimeSpan.FromSeconds(3));
        }
    }
}
