using InfoPanel.Panels.Devices;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermaltake;
using InfoPanel.Panels.Usb;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermaltake;

/// <summary>
/// A tester's run on a real Thermaltake or ASRock panel (§2.2 (9)), skipped unless <c>INFOPANEL_PANEL_HW=thermaltake</c>:
/// the panel is probed and opened through the engine's device seam, shown colour bars, then a gradient, and stopped with
/// realtime display off; every transfer is recorded to <c>INFOPANEL_PANEL_TRACE</c> (a file) through the kit's recorder,
/// so the trace can become a vector. The seam is the one the panel host gives a package's drivers: the engine's
/// <see cref="EnginePanelDevices"/> over HID (and libusb and serial, as the host wires them). Without the opt-in it skips;
/// opted in, a run whose setup can't complete (no panel named, no seam) fails.
/// </summary>
public sealed class ThermaltakeHardwareTests
{
    /// <summary>The engine's own device seam, built as the panel host builds it (libusb only when <paramref name="usb"/> and it loads).</summary>
    internal static EnginePanelDevices EngineDevices(Serilog.ILogger logger, out IUsbBackend? backend, bool usb = true)
    {
        backend = null;
        if (usb && LibUsb.LoadAndLog(LibUsb.DefaultCandidates(), logger) is { } library)
            backend = LibUsbBackend.Create(library, out _);
        return new EnginePanelDevices(backend, new HidSharpSystem(logger), new SerialPorts(logger), logger);
    }

    [Fact]
    public void TheHardwareTest_BuildsThePanelHostsSeam_WithoutTouchingADevice()
    {
        // No device is touched: the seam is only built (without libusb), as the hardware run builds it.
        Assert.IsType<EnginePanelDevices>(EngineDevices(Serilog.Core.Logger.None, out var backend, usb: false));
        Assert.Null(backend);
    }

    private static byte[] Jpeg(int width, int height, int quality, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
            draw(canvas);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    [Fact]
    public void AConnectedPanel_StartsShowsBarsAndAGradient_AndStops_Recorded()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("INFOPANEL_PANEL_HW") == ThermaltakeDriver.FamilyId,
            "A real Thermaltake or ASRock panel: set INFOPANEL_PANEL_HW=thermaltake (and INFOPANEL_PANEL_TRACE=<file> to record it).");
        // Opted in: a setup that can't complete is a failure, not a skip (the tester asked for a hardware run).
        var engine = EngineDevices(Serilog.Log.Logger, out var backend);
        using var usb = backend; // null when libusb isn't there (a HID family needs none)
        var named = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_CANDIDATE"); // vvvv:pppp@bus:address:port
        Assert.False(string.IsNullOrEmpty(named), "INFOPANEL_PANEL_HW=thermaltake is set, but no panel is named: INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port (as the engine's log lists it).");
        var trace = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_TRACE");
        IPanelDevices devices = string.IsNullOrEmpty(trace) ? engine : new RecordingPanelDevices(engine, trace);
        var parts = named!.Split('@', ':');
        var candidate = new PanelCandidate(Convert.ToUInt16(parts[0], 16), Convert.ToUInt16(parts[1], 16), byte.Parse(parts[2]), byte.Parse(parts[3]), parts[4]);
        var driver = new ThermaltakeDriver(devices);
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
                var bytes = frame < 15 ? bars : gradient;
                Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(bytes, PanelFrameFormat.Jpeg, frame + 1, false), stop).Status);
                Thread.Sleep(133);
            }
        }
        finally
        {
            session.Close(screenOff: true, TimeSpan.FromSeconds(3)); // also after a failed frame or a cancel, so the panel doesn't stay in realtime display
        }
    }
}