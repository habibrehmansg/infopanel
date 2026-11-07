using Xunit;
using System.Diagnostics;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Usb;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Core.Tests.Panels.BeadaPanel;

/// <summary>
/// The probe on a real BeadaPanel, opt-in (<c>INFOPANEL_PANEL_TESTS=1</c>; skipped otherwise, as the sensor and window
/// tests are): identify, start, colour bars with a corner marker for ten seconds, a gradient, brightness 100 then 50,
/// stop with the screen off. Each step's result is written to the test output and, when <c>INFOPANEL_PANEL_LOG</c>
/// names a file, appended to it. Run nothing else on the panel meanwhile (another holder makes it <c>Busy</c>).
/// </summary>
public class BeadaPanelHardwareTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("INFOPANEL_PANEL_TESTS") == "1";

    [Fact]
    public void The_probe_draws_bars_and_a_gradient_and_stops_with_the_screen_off()
    {
        Assert.SkipUnless(Enabled, "Set INFOPANEL_PANEL_TESTS=1 with a BeadaPanel connected.");
        var log = new ProbeLog(Environment.GetEnvironmentVariable("INFOPANEL_PANEL_LOG"));
        log.Line($"probe started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        using var logger = new LoggerConfiguration().WriteTo.Sink(log).CreateLogger();
        var library = LibUsb.LoadAndLog(LibUsb.DefaultCandidates(), logger);
        Assert.NotNull(library);
        using var backend = LibUsbBackend.Create(library, out var why);
        Assert.True(backend is not null, why);

        var panels = backend.ListDevices().Where(d => d.VendorId == BeadaPanelProtocol.VendorId && d.ProductId == BeadaPanelProtocol.ProductId).ToList();
        log.Line($"BeadaPanel devices listed: {panels.Count} ({string.Join(", ", panels)})");
        var id = Assert.Single(panels);

        var identity = BeadaPanelSession.Identify(backend, id);
        log.Line($"identify: {identity.Status}, error {identity.Error}, model code {identity.ModelCode}");
        Assert.Equal(UsbStatus.Ok, identity.Status);
        var info = identity.Info!;
        log.Line($"info: firmware {info.Firmware}, PanelLink {info.PanelLinkVersion}, StatusLink {info.StatusLinkVersion}, platform {info.Platform}, " +
                 $"model {info.Model.Id} (code {info.Model.Code}, {info.Model.Width} x {info.Model.Height}), reported {info.ResolutionX} x {info.ResolutionY}, " +
                 $"storage {info.StorageKb} KB, brightness {info.CurrentBrightness} / {info.MaxBrightness}");
        log.Line($"serial: panel '{info.Serial}', USB descriptor '{identity.UsbSerial}'");
        log.Line($"stream: {(info.WriteThrough ? "write-through" : "reported size")}, {info.Width} x {info.Height}, format '{BeadaPanelProtocol.FormatString(info)}'");

        var start = BeadaPanelSession.Start(backend, id, 100, TimeProvider.System, CancellationToken.None);
        log.Line($"start: {start.Status} {start.Message}");
        Assert.Equal(UsbStatus.Ok, start.Status);
        using var session = start.Session!;
        try
        {
            var bars = Bars(session.Width, session.Height);
            Show(session, bars, TimeSpan.FromSeconds(10), log, "colour bars");

            var gradient = Gradient(session.Width, session.Height);
            Show(session, gradient, TimeSpan.FromSeconds(3), log, "gradient at brightness 100");
            Assert.Equal(UsbStatus.Ok, session.SetBrightness(50));
            Show(session, gradient, TimeSpan.FromSeconds(3), log, "gradient at brightness 50");
        }
        finally
        {
            session.Stop();
            log.Line("stopped: backlight 0, black frame, stream end");
        }
    }

    private static void Show(BeadaPanelSession session, byte[] frame, TimeSpan duration, ProbeLog log, string what)
    {
        var watch = Stopwatch.StartNew();
        var writes = 0;
        double slowest = 0, total = 0;
        while (watch.Elapsed < duration)
        {
            var one = Stopwatch.StartNew();
            var status = session.WriteFrame(frame);
            var ms = one.Elapsed.TotalMilliseconds;
            Assert.Equal(UsbStatus.Ok, status);
            writes++;
            total += ms;
            slowest = Math.Max(slowest, ms);
            Thread.Sleep(200);
        }
        log.Line($"{what}: {writes} writes of {frame.Length} bytes over {duration.TotalSeconds:0} s, mean {total / writes:0.0} ms, slowest {slowest:0.0} ms");
    }

    /// <summary>
    /// Colour bars along the long side, from the origin: red, green, blue, white, each labelled; a white corner marker
    /// with "TOP LEFT" at pixel (0, 0). Drawn by Skia into RGB565 (R in the top five bits, little-endian), as 1.4 drew.
    /// </summary>
    private static byte[] Bars(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb565, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            (SKColor Colour, string Name)[] bars = [(SKColors.Red, "1 RED"), (new SKColor(0, 255, 0), "2 GREEN"), (SKColors.Blue, "3 BLUE"), (SKColors.White, "4 WHITE")];
            var across = width >= height;
            var length = (across ? width : height) / (float)bars.Length;
            using var font = new SKFont(SKTypeface.Default, Math.Min(width, height) / 8f);
            using var paint = new SKPaint();
            for (var i = 0; i < bars.Length; i++)
            {
                var rect = across ? new SKRect(i * length, 0, (i + 1) * length, height) : new SKRect(0, i * length, width, (i + 1) * length);
                paint.Color = bars[i].Colour;
                canvas.DrawRect(rect, paint);
                paint.Color = bars[i].Name.EndsWith("WHITE", StringComparison.Ordinal) ? SKColors.Black : SKColors.White;
                canvas.DrawText(bars[i].Name, rect.Left + 12, rect.MidY, font, paint);
            }
            var marker = Math.Min(width, height) / 4f;
            paint.Color = SKColors.White;
            canvas.DrawRect(0, 0, marker * 2, marker, paint);
            paint.Color = SKColors.Black;
            using var small = new SKFont(SKTypeface.Default, marker / 3f);
            canvas.DrawText("TOP LEFT", 8, marker * 0.6f, small, paint);
        }
        return Bytes(bitmap);
    }

    /// <summary>A gradient from red at the origin through green to blue along the long side, dark to light across.</summary>
    private static byte[] Gradient(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgb565, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            var across = width >= height;
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), across ? new SKPoint(width, 0) : new SKPoint(0, height),
                    [SKColors.Red, new SKColor(0, 255, 0), SKColors.Blue], SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, width, height, paint);
        }
        return Bytes(bitmap);
    }

    private static byte[] Bytes(SKBitmap bitmap)
    {
        Assert.Equal(bitmap.Width * 2, bitmap.RowBytes);
        return bitmap.GetPixelSpan().ToArray();
    }

    private sealed class ProbeLog(string? path) : Serilog.Core.ILogEventSink
    {
        private readonly Lock _lock = new();

        public void Line(string text)
        {
            lock (_lock)
            {
                TestContext.Current.TestOutputHelper?.WriteLine(text);
                if (!string.IsNullOrEmpty(path))
                    File.AppendAllText(path, text + Environment.NewLine);
            }
        }

        public void Emit(Serilog.Events.LogEvent logEvent) => Line(logEvent.RenderMessage());
    }
}
