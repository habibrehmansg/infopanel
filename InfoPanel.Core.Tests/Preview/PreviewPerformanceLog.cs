using InfoPanel.Core.Tests.Golden;
using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using SkiaSharp;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// Not a check: logs how long the preview path takes for the Wide bar example document (1920×480) at scale 1 and 2: a
/// runtime tick, drawing the snapshot, its bounds, the hash and the PNG encoders, to the test output and
/// <c>TestResults/preview-performance.txt</c>. It never fails on timings.
/// </summary>
public class PreviewPerformanceLog
{
    private const int Warmup = 5;
    private const int Runs = 30;

    [Fact]
    public void WideBar_RenderAndEncodeTimes()
    {
        var (_, bytes) = Workspace.ExampleDocuments().Single(e => e.Name == "wide-bar.document.json");
        using var json = System.Text.Json.JsonDocument.Parse(bytes);
        using var mock = new MockSensorSource(42);
        var hub = new SensorHub([mock]);
        var history = new SensorHistory(hub, TimeProvider.System, hub.GetBackfill);
        using var runtime = new SceneRuntime(new SceneRuntimeOptions { Inputs = new SensorSceneInputs(hub, history) });
        runtime.Load(json.RootElement);
        using var first = runtime.Snapshot(DateTimeOffset.UtcNow);
        var (profileWidth, profileHeight) = (first.Width, first.Height);
        var elapsed = 0.0;

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Wide bar {profileWidth}x{profileHeight}, {Runs} runs after {Warmup} warm-up, {DateTime.UtcNow:u}, {Environment.OSVersion}, {Environment.ProcessorCount} cores");

        foreach (var scale in new[] { 1f, 2f })
        {
            var (width, height) = FrameScale.PixelSize(scale, profileWidth, profileHeight);
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);

            double ticking = 0, render = 0, bounds = 0, hash = 0;
            for (var i = 0; i < Warmup + Runs; i++)
            {
                var start = Stopwatch.GetTimestamp();
                using var snapshot = runtime.Tick(elapsed += 1.0 / 30, DateTimeOffset.UtcNow);
                var ticked = Stopwatch.GetTimestamp();
                canvas.Clear(SKColors.Transparent);
                snapshot.Draw(canvas, scale);
                canvas.Flush();
                var drawn = Stopwatch.GetTimestamp();
                _ = snapshot.Bounds;
                var measured = Stopwatch.GetTimestamp();
                XxHash3.HashToUInt64(bitmap.GetPixelSpan());
                var hashed = Stopwatch.GetTimestamp();

                if (i >= Warmup)
                {
                    ticking += Stopwatch.GetElapsedTime(start, ticked).TotalMilliseconds;
                    render += Stopwatch.GetElapsedTime(ticked, drawn).TotalMilliseconds;
                    bounds += Stopwatch.GetElapsedTime(drawn, measured).TotalMilliseconds;
                    hash += Stopwatch.GetElapsedTime(measured, hashed).TotalMilliseconds;
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"scale {scale} ({width}x{height}): tick {ticking / Runs:F2} ms, draw {render / Runs:F2} ms, bounds {bounds / Runs:F2} ms, hash {hash / Runs:F2} ms");

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
