using InfoPanel.Panels.Devices;
using InfoPanel.Panels.Jl;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Jl;

/// <summary>
/// A tester's run on a real Jungle Leopard / Hongtai panel, skipped unless <c>INFOPANEL_PANEL_HW=jl</c>: the
/// panel named by <c>INFOPANEL_PANEL_CANDIDATE</c> is probed and opened through the engine's own device seam (its serial
/// port found by the USB device's location), shown colour bars, then a gradient, at brightness 100 then 50, idle a while
/// (keep-alives), and stopped with close; every transfer is recorded to <c>INFOPANEL_PANEL_TRACE</c> (a file) through
/// the kit's recorder, so the trace can become a vector.
/// </summary>
public sealed class JlHardwareTests
{
    private static byte[] Jpeg(int width, int height, int quality, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
            draw(canvas);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    [Fact]
    public void AConnectedPanel_StartsShowsBarsAndAGradient_IdlesAndStops_Recorded()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_PANEL_HW") == JlDriver.FamilyId,
            "A real JL panel: set INFOPANEL_PANEL_HW=jl (and INFOPANEL_PANEL_TRACE=<file> to record it).");
        var named = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_CANDIDATE"); // vvvv:pppp@bus:address:port
        Assert.SkipWhen(string.IsNullOrEmpty(named), "Name the panel: INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port (as the engine's log lists it).");
        var logger = Serilog.Core.Logger.None;
        // The engine's seam as the panel host makes it: libusb when it loads (the serial port's finder reads the device's
        // serial through it on Windows), serial ports through System.IO.Ports.
        using var usb = LibUsb.LoadAndLog(LibUsb.DefaultCandidates(), logger) is { } library ? LibUsbBackend.Create(library, out _) : null;
        IPanelDevices engine = new EnginePanelDevices(usb, null, new SerialPorts(logger), logger);
        var trace = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_TRACE");
        var devices = string.IsNullOrEmpty(trace) ? engine : new RecordingPanelDevices(engine, trace);
        var parts = named!.Split('@', ':');
        var candidate = new PanelCandidate(Convert.ToUInt16(parts[0], 16), Convert.ToUInt16(parts[1], 16), byte.Parse(parts[2]), byte.Parse(parts[3]), parts[4]);
        var driver = new JlDriver(devices);
        var stop = TestContext.Current.CancellationToken;

        var probe = driver.Probe(candidate, stop);
        Assert.True(probe.Mine, $"{probe.Status}: {probe.Message}");
        var open = driver.Open(candidate, probe.Identity!, new PanelOpenOptions(100), stop);
        Assert.True(open.Session != null, $"{open.Status}: {open.Message}");
        using var session = open.Session!;
        try
        {
            var caps = session.Capabilities;
            SKColor[] colours = [SKColors.White, SKColors.Yellow, SKColors.Cyan, SKColors.Lime, SKColors.Magenta, SKColors.Red, SKColors.Blue, SKColors.Black];
            var bars = Jpeg(caps.Width, caps.Height, caps.JpegQuality, canvas =>
            {
                using var paint = new SKPaint();
                for (var i = 0; i < colours.Length; i++)
                {
                    paint.Color = colours[i];
                    canvas.DrawRect(caps.Width * i / colours.Length, 0, caps.Width / colours.Length + 1, caps.Height, paint);
                }
            });
            var gradient = Jpeg(caps.Width, caps.Height, caps.JpegQuality, canvas =>
            {
                using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(caps.Width, 0), [SKColors.Black, SKColors.White], SKShaderTileMode.Clamp) };
                canvas.DrawRect(0, 0, caps.Width, caps.Height, paint);
            });

            for (var frame = 0; frame < 30; frame++) // two seconds of each at 15 fps, 1.4's default rate
            {
                if (frame == 15)
                    Assert.Equal(PanelDeviceStatus.Ok, session.SetBrightness(50)); // the light; the engine dims the frame too
                var bytes = frame < 15 ? bars : gradient;
                Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(bytes, PanelFrameFormat.Jpeg, frame + 1, false), stop).Status);
                Thread.Sleep(66);
            }
            for (var idle = 0; idle < 3; idle++) // the picture held by keep-alives, a second apart
            {
                Thread.Sleep(1000);
                Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(gradient, PanelFrameFormat.Jpeg, 30, repeat: true), stop).Status);
            }
        }
        finally
        {
            session.Close(screenOff: true, TimeSpan.FromSeconds(3)); // also after a failed frame or a cancel
        }
    }
}
