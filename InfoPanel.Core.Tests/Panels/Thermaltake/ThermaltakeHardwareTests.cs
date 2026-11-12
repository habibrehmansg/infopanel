using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermaltake;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermaltake;

/// <summary>
/// A tester's run on a real Thermaltake or ASRock panel (§2.2 (9)), skipped unless <c>INFOPANEL_PANEL_HW=thermaltake</c>:
/// the panel is probed and opened through the engine's device seam, shown colour bars, then a gradient, and stopped with
/// realtime display off; every transfer is recorded to <c>INFOPANEL_PANEL_TRACE</c> (a file) through the kit's recorder,
/// so the trace can become a vector. The engine's seam over the OS's HID comes with the engine side of the device seam;
/// until then the test skips there too.
/// </summary>
public sealed class ThermaltakeHardwareTests
{
    /// <summary>The engine's own device seam, once it has one (found by type, so this test needs no change when it lands).</summary>
    private static IPanelDevices? EngineDevices()
    {
        var type = typeof(InfoPanel.Panels.PanelTransform).Assembly.GetTypes()
            .FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && typeof(IPanelDevices).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null);
        return type == null ? null : (IPanelDevices)Activator.CreateInstance(type)!;
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
        var engine = EngineDevices();
        Assert.SkipWhen(engine == null, "The engine has no device seam over the OS's HID yet.");
        var named = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_CANDIDATE"); // vvvv:pppp@bus:address:port
        Assert.SkipWhen(string.IsNullOrEmpty(named), "Name the panel: INFOPANEL_PANEL_CANDIDATE=vvvv:pppp@bus:address:port (as the engine's log lists it).");
        var trace = Environment.GetEnvironmentVariable("INFOPANEL_PANEL_TRACE");
        var devices = string.IsNullOrEmpty(trace) ? engine! : new RecordingPanelDevices(engine!, trace);
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