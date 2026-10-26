using InfoPanel.Core.Tests.Golden;
using InfoPanel.Drawing;
using InfoPanel.Editing;
using InfoPanel.Output;
using InfoPanel.Sensors;
using SkiaSharp;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// Not a check: logs how long the preview path takes for the Wide bar example (1920×480) at scale 1 and 2, to the test
/// output and <c>TestResults/preview-performance.txt</c>. It never fails on timings.
/// </summary>
public class PreviewPerformanceLog
{
    private const int Warmup = 5;
    private const int Runs = 30;

    [Fact]
    public void WideBar_RenderAndEncodeTimes()
    {
        var (profile, items) = ExampleProfiles.WideBar();
        using var mock = new MockSensorSource(42);
        var hub = new SensorHub([mock]);
        var history = new SensorHistory(hub, TimeProvider.System, hub.GetBackfill);
        var renderer = new ProfileRenderer(hub, history, new FakeImageSource(), new FakeAssetLocator(Path.GetTempPath()), TimeProvider.System);
        var state = new ProfileRenderState();

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Wide bar {profile.Width}x{profile.Height}, {Runs} runs after {Warmup} warm-up, {DateTime.UtcNow:u}, {Environment.OSVersion}, {Environment.ProcessorCount} cores");

        foreach (var scale in new[] { 1f, 2f })
        {
            var (width, height) = FrameScale.PixelSize(scale, profile.Width, profile.Height);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);

            double render = 0, bounds = 0, hash = 0;
            for (var i = 0; i < Warmup + Runs; i++)
            {
                state.Tick++;
                var start = Stopwatch.GetTimestamp();
                canvas.Clear(SKColors.Transparent);
                renderer.Draw(canvas, profile, items, state, RenderOptions.Default, scale);
                canvas.Flush();
                var drawn = Stopwatch.GetTimestamp();
                renderer.MeasureBounds(profile, items, state);
                var measured = Stopwatch.GetTimestamp();
                XxHash3.HashToUInt64(bitmap.GetPixelSpan());
                var hashed = Stopwatch.GetTimestamp();

                if (i >= Warmup)
                {
                    render += Stopwatch.GetElapsedTime(start, drawn).TotalMilliseconds;
                    bounds += Stopwatch.GetElapsedTime(drawn, measured).TotalMilliseconds;
                    hash += Stopwatch.GetElapsedTime(measured, hashed).TotalMilliseconds;
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"scale {scale} ({width}x{height}): render {render / Runs:F2} ms, bounds {bounds / Runs:F2} ms, hash {hash / Runs:F2} ms");

            using var pixmap = bitmap.PeekPixels();
            foreach (var (name, options) in new[]
            {
                ("png zlib 1, filter Up (used)", RenderedFrame.PngOptions),
                ("png zlib 1, filter None", new SKPngEncoderOptions(SKPngEncoderFilterFlags.None, 1)),
                ("png zlib 1, filter Sub", new SKPngEncoderOptions(SKPngEncoderFilterFlags.Sub, 1)),
                ("png zlib 1, every filter", new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 1)),
                ("png zlib 6, all filters (default)", new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 6)),
            })
            {
                double encode = 0;
                long size = 0;
                for (var i = 0; i < Warmup + Runs; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    using var data = pixmap.Encode(options) ?? throw new InvalidOperationException("PNG encoding failed");
                    if (i >= Warmup)
                    {
                        encode += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        size = data.Size;
                    }
                }

                report.AppendLine(CultureInfo.InvariantCulture, $"  encode {name}: {encode / Runs:F2} ms, {size / 1024.0:F0} KB");
            }
        }

        var text = report.ToString();
        TestContext.Current.TestOutputHelper?.WriteLine(text);
        try
        {
            var directory = Path.Combine(TestPaths.ProjectDirectory, "TestResults");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "preview-performance.txt"), text);
        }
        catch (IOException)
        {
            // The log is best effort.
        }
    }
}
