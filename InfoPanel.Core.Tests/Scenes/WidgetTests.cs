using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Widgets the step-1 renderer never had, as reference documents (<c>Scenes/Reference/widgets/</c>): a segmented gauge
/// (segments light up with a smoothed value), a peak-hold bar (a peak marker held, then falling, in elapsed time) and a
/// sparkline card (history, name and value). They are drawn by the scene runtime unchanged. Each is ticked from 0 to 6 s
/// with one input sequence: 10 until 0.5 s, 85 until 2 s, then 30, and a history that gains a sample every 0.25 s.
/// Goldens (<c>Golden/scene/{os}/widget-{name}-t{ms}.png</c>) are taken at 60 ticks a second; the same times at 30 ticks
/// a second must match them, and the state the frames show is checked against its definition in elapsed time.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Golden")]
public sealed class WidgetTests(ITestOutputHelper output)
{
    public static string ReferenceDir { get; } = Path.Combine(TestPaths.ProjectDirectory, "Scenes", "Reference", "widgets");

    private static readonly double[] Times = [0, 0.5, 1, 2.5, 3.5, 6];

    public static TheoryData<string> Names() => new(Directory.EnumerateFiles(ReferenceDir, "*.document.json")
        .Select(f => Path.GetFileName(f)[..^".document.json".Length]).Order(StringComparer.Ordinal));

    private static double Level(double t) => t < 0.5 ? 10 : t < 2 ? 85 : 30;

    /// <summary>80 older samples of a wave, then one sample of <see cref="Level"/> every 0.25 s up to <paramref name="t"/>.</summary>
    private static double[] History(double t) =>
    [
        .. Enumerable.Range(0, 80).Select(k => 40 + 20 * Math.Sin(k / 5.0)),
        .. Enumerable.Range(0, (int)Math.Floor(t * 4 + 1e-9) + 1).Select(k => Level(k / 4.0)),
    ];

    private static JsonElement Document(string name) => SceneKit.Load(Path.Combine(ReferenceDir, name + ".document.json"));

    /// <summary>Ticks the document at <paramref name="fps"/> from 0 to the last of <see cref="Times"/>; the frames at those times.</summary>
    private static Dictionary<double, SKBitmap> Run(string name, int fps)
    {
        var inputs = new FakeSceneInputs();
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        var check = runtime.Load(Document(name));
        Assert.Equal(CheckStatus.Valid, check.Status);
        var frames = new Dictionary<double, SKBitmap>();
        var ticks = (int)Math.Round(Times[^1] * fps);
        for (var k = 0; k <= ticks; k++)
        {
            var t = (double)k / fps;
            inputs.Number("test:v", Level(t), "%");
            inputs.Histories["test:v"] = History(t);
            using var snapshot = runtime.Tick(t, SceneKit.Now);
            Assert.All(snapshot.Bounds, b => Assert.Null(b.Problem));
            if (Times.FirstOrDefault(x => Math.Abs(x - t) < 1e-9, -1) is var at && at >= 0) frames[at] = snapshot.Render();
        }
        Assert.Equal(Times.Length, frames.Count);
        return frames;
    }

    private static void Dispose(Dictionary<double, SKBitmap> frames)
    {
        foreach (var f in frames.Values) f.Dispose();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Goldens_over_elapsed_time(string name)
    {
        var frames = Run(name, 60);
        try
        {
            foreach (var (t, frame) in frames) GoldenImage.Verify($"widget-{name}-t{t * 1000:0}", frame, "scene");
        }
        finally
        {
            Dispose(frames);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Thirty_and_sixty_ticks_a_second_draw_the_same_frames(string name)
    {
        var at60 = Run(name, 60);
        var at30 = Run(name, 30);
        try
        {
            foreach (var t in Times)
            {
                var differing = SceneKit.DifferingPixels(at60[t], at30[t]);
                var total = at60[t].Width * at60[t].Height;
                output.WriteLine($"{name} at {t} s: {differing} of {total} pixels differ by more than 2 between 30 and 60 ticks a second");
                Assert.True(differing <= total * GoldenImage.DifferingPixelFraction, $"{name} at {t} s: {differing} of {total} pixels differ");
            }
        }
        finally
        {
            Dispose(at60);
            Dispose(at30);
        }
    }

    /// <summary>
    /// The peak marker of each bar is where its definition puts it: the highest value, held for <c>hold</c> seconds after
    /// the value first falls below it, then falling at <c>fall</c> of the range a second, never below the value.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void The_peak_marker_holds_then_falls_in_elapsed_time(int fps)
    {
        static double Held(double t, double hold, double fall)
        {
            var value = Level(t) / 100;
            if (t < 0.5) return 0.10;
            if (t < 2) return 0.85;
            return Math.Max(value, 0.85 - fall * Math.Max(0, t - 2 - hold));
        }

        var frames = Run("peak-bar", fps);
        try
        {
            foreach (var t in Times)
            {
                // Bar "level": x 10, 240 wide, marker 3 px, white; bar "slow": y 40, marker 4 px, yellow.
                AssertMarker(frames[t], t, y: 19, left: 10, travel: 237, width: 3, Held(t, 1, 0.5), c => c.Red > 200 && c.Green > 200 && c.Blue > 200);
                AssertMarker(frames[t], t, y: 45, left: 10, travel: 236, width: 4, Held(t, 2, 0.25), c => c.Red > 200 && c.Green > 150 && c.Blue < 120);
            }
        }
        finally
        {
            Dispose(frames);
        }
    }

    private void AssertMarker(SKBitmap frame, double t, int y, int left, int travel, int width, double held, Func<SKColor, bool> isMarker)
    {
        // The marker's centre from the coverage of its colour along the row.
        double sum = 0, weight = 0;
        for (var x = left; x < left + travel + width; x++)
        {
            if (!isMarker(frame.GetPixel(x, y))) continue;
            sum += x + 0.5;
            weight++;
        }
        Assert.True(weight > 0, $"no marker at {t} s on row {y}");
        var centre = sum / weight;
        var expected = left + held * travel + width / 2.0;
        output.WriteLine($"t {t}: marker at {centre:0.00}, expected {expected:0.00} (held {held:0.000})");
        Assert.InRange(centre, expected - 1, expected + 1);
    }

    /// <summary>
    /// The gauges' segments are lit as their smoothed value says: the target the previous tick saw, approached by
    /// exponential smoothing in elapsed time (0.3 s for "load", 0.8 s for "temp"). A segment whose expected opacity is
    /// at least 0.95 is drawn lit and one at most 0.05 unlit.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void Gauge_segments_follow_the_smoothed_value(int fps)
    {
        static double Drawn(double t, double tau)
        {
            if (t < 0.5) return 0.10;
            var at2 = 0.85 + (0.10 - 0.85) * Math.Exp(-(Math.Min(t, 2) - 0.5) / tau);
            return t <= 2 ? at2 : 0.30 + (at2 - 0.30) * Math.Exp(-(t - 2) / tau);
        }

        var frames = Run("segment-gauge", fps);
        try
        {
            foreach (var t in Times)
            {
                // "load": centre (90, 90), r 80, 14 thick, 24 segments from 135° over 270°. "temp": centre (270, 90), r 80, 24 thick, 12 from 180° over 180°.
                Segments(frames[t], t, cx: 90, cy: 90, mid: 73, n: 24, start: 135, sweep: 270, lit: Drawn(t, 0.3) * 24);
                Segments(frames[t], t, cx: 270, cy: 90, mid: 68, n: 12, start: 180, sweep: 180, lit: Drawn(t, 0.8) * 12);
            }
        }
        finally
        {
            Dispose(frames);
        }
    }

    private void Segments(SKBitmap frame, double t, double cx, double cy, double mid, int n, double start, double sweep, double lit)
    {
        var checkedCount = 0;
        for (var i = 0; i < n; i++)
        {
            var opacity = Math.Clamp(lit - i, 0, 1);
            if (opacity is > 0.05 and < 0.95) continue;
            var angle = (start + (i + 0.5) * sweep / n) * Math.PI / 180;
            var p = frame.GetPixel((int)(cx + mid * Math.Cos(angle)), (int)(cy + mid * Math.Sin(angle)));
            // Unlit segments are a faint white over the background; lit ones are saturated.
            var saturation = Math.Max(p.Red, Math.Max(p.Green, p.Blue)) - Math.Min(p.Red, Math.Min(p.Green, p.Blue));
            Assert.True(opacity >= 0.95 ? saturation > 100 : saturation < 20, $"t {t}: segment {i} of {n} (expected opacity {opacity:0.00}) has {p}");
            checkedCount++;
        }
        output.WriteLine($"t {t}: {n} segments, value {lit / n:0.000}, {checkedCount} checked");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Reference_documents_are_valid_and_carry_no_item_sources(string name)
    {
        var document = Document(name);
        var check = DocumentValidator.Check(document, support: EngineSupport.All);
        Assert.Equal(CheckStatus.Valid, check.Status);
        Assert.Empty(check.Items);
        Assert.False(document.GetProperty("profile").TryGetProperty("editor", out _));
        V1ParityTests.AssertNoSources(document.GetProperty("items"));
    }
}
