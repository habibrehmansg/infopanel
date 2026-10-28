using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Topten.RichTextKit;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Parity of the scene runtime with the step-1 renderer: each reference document in <c>Scenes/Reference/v1/</c> is a
/// golden case of <see cref="GoldenTests"/> expressed as an <c>infopanel.document/1</c>. It is rendered by the scene runtime
/// with the sensor values, history, fixture images and clock the step-1 golden used (<see cref="GoldenScene"/>), as a
/// one-off snapshot (state at init, elapsed 0, as the step-1 goldens draw values at their target), at the case's scale,
/// and compared with the step-1 golden <c>Golden/{os}/{case}.png</c> under <see cref="GoldenImage"/>'s tolerance.
/// A case listed in <see cref="Accepted"/> has its own bound and reason.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Golden")]
public sealed class V1ParityTests(ITestOutputHelper output)
{
    public static string ReferenceDir { get; } = Path.Combine(TestPaths.ProjectDirectory, "Scenes", "Reference", "v1");

    /// <summary>
    /// A case that differs from the step-1 golden for known reasons: at most <see cref="MaxFraction"/> of its pixels may
    /// differ from the golden, and everywhere outside <see cref="Areas"/> (profile pixels, where the reasons apply) it
    /// must match within the standard tolerance: the golden, or with <see cref="Unhinted"/> (glyph hinting is one of the
    /// reasons) the step-1 renderer drawing the case with unhinted glyphs.
    /// </summary>
    private sealed record Deviation(double MaxFraction, string Reason, bool Unhinted = false, params SKRectI[] Areas);

    private const string Hinting = "glyphs unhinted";
    private const string RotatedChart = "rotated chart drawn as rotated shapes, not a rotated raster";
    private const string Resampled = "raster image resized with linear filtering, not Mitchell";
    private const string Placeholder = "the format's missing-media placeholder";
    private const string HaloCut = "a scrolling text's halo cut at its box before the blur, not only after it";
    private const string FractionalRows = "rows at fractional offsets, not truncated to whole pixels";
    private const string TableGlow = "the glow drawn once for the table, not once per cell";

    private static SKRectI Box(int x, int y, int w, int h) => SKRectI.Create(x, y, w, h);

    /// <summary>The table's data rows: everything below its header row (checked row by row by <see cref="TableRowProblems"/>).</summary>
    private static readonly SKRectI TableRows = Box(0, 26, 260, 104);

    /// <summary>
    /// The composite's charts (bar, line graph, donut) in profile pixels, each with the most pixels of its scale-2 frame,
    /// reduced to scale 1, that may differ from the step-1 scale-1 golden by more than <see cref="ChartChannelTolerance"/>
    /// (<see cref="ChartProblems"/>): measured 3, 0 and 41 (the donut is also a rotated chart).
    /// </summary>
    private static readonly (string Name, SKRectI Area, int MaxDiffering)[] CompositeCharts =
    [
        ("bar", Box(20, 96, 440, 14), 10),
        ("graph", Box(20, 124, 300, 96), 10),
        ("donut", Box(360, 128, 88, 88), 80),
    ];

    /// <summary>
    /// Anti-aliasing at scale 2 reduced to scale 1 isn't scale 1's anti-aliasing (up to 19 apart on the graph's edges, 38
    /// on the bar's ends, 60 on the donut's), so the charts are compared at this channel tolerance; content one pixel off
    /// at scale 2 differs by 100 or more along every edge it moves.
    /// </summary>
    private const int ChartChannelTolerance = 24;

    private static readonly Dictionary<string, Deviation> Accepted = new()
    {
        ["calendar"] = new(0.085, Hinting, Unhinted: true),
        ["clock"] = new(0.087, Hinting, Unhinted: true),
        ["group-nested-hidden-child"] = new(0.03, Hinting, Unhinted: true),
        ["sensor-thresholds-precision-unit"] = new(0.045, Hinting, Unhinted: true),
        ["text-center-box"] = new(0.071, Hinting, Unhinted: true),
        ["text-glow"] = new(0.246, Hinting, Unhinted: true),
        ["text-glow-hard-light"] = new(0.19, Hinting, Unhinted: true),
        ["text-glow-overlay"] = new(0.039, Hinting, Unhinted: true),
        ["text-glow-plus"] = new(0.088, Hinting, Unhinted: true),
        ["text-glow-screen"] = new(0.096, Hinting, Unhinted: true),
        ["text-glow-soft-light"] = new(0.028, Hinting, Unhinted: true),
        // The halo within 3 sigma of the box's right side (the text's start is at the left side at elapsed 0).
        ["text-glow-marquee"] = new(0.194, Hinting + "; " + HaloCut, Unhinted: true, Box(180, 16, 24, 41)),
        ["text-left"] = new(0.078, Hinting, Unhinted: true),
        ["text-marquee-t0"] = new(0.059, Hinting, Unhinted: true),
        ["text-right-auto"] = new(0.094, Hinting, Unhinted: true),
        ["text-rotated-30"] = new(0.029, Hinting, Unhinted: true),
        ["table"] = new(0.153, Hinting + "; " + FractionalRows, Unhinted: true, TableRows),
        ["table-missing-column"] = new(0.111, Hinting + "; " + FractionalRows, Unhinted: true, TableRows),
        ["table-rows-0"] = new(0.07, Hinting, Unhinted: true),
        ["table-single-line-no-ellipsis"] = new(0.089, Hinting + "; " + FractionalRows, Unhinted: true, TableRows),
        // The data rows and the lines their cells run into below them (checked line by line by the wrapped-cell test).
        ["table-wrap-off"] = new(0.121, Hinting + "; " + FractionalRows, Unhinted: true, TableRows),
        // The glow reaches the whole frame; without it the case is the table case (A_glowing_table_differs_only_by_its_glow).
        ["table-glow"] = new(0.252, Hinting + "; " + FractionalRows + "; " + TableGlow, Unhinted: true, Box(0, 0, 260, 130)),
        ["donut-span270-rotation90"] = new(0.024, RotatedChart, Areas: Box(20, 20, 120, 120)),
        ["composite-scale1"] = new(0.019, Hinting + "; " + RotatedChart, Unhinted: true, Box(360, 128, 88, 88)),
        ["composite-scale2"] = new(0.031, Hinting + "; " + RotatedChart + "; charts drawn at full resolution, not a 1x raster scaled up", Unhinted: true,
            [.. CompositeCharts.Select(c => c.Area)]),
        ["image-png"] = new(0.024, Resampled, Areas: Box(116, 10, 48, 80)),
        ["image-gif-frame0"] = new(0.049, Resampled, Areas: Box(10, 10, 96, 64)),
        ["image-missing"] = new(0.51, Placeholder, Areas: [Box(10, 10, 100, 100), Box(130, 10, 120, 60)]),
        ["http-image-placeholder"] = new(0.597, Placeholder, Areas: Box(10, 10, 100, 100)),
    };

    private static IEnumerable<string> Cases() => Directory.EnumerateFiles(ReferenceDir, "*.document.json")
        .Select(f => Path.GetFileName(f)[..^".document.json".Length])
        .Where(n => n != "all-kinds")
        .Order(StringComparer.Ordinal);

    public static TheoryData<string> CaseNames() => new(Cases());

    private static float ScaleOf(string name) => name.EndsWith("scale2", StringComparison.Ordinal) ? 2f : 1f;

    internal static SceneRuntimeOptions Options(GoldenScene scene, MediaSampleCache media) => new()
    {
        Inputs = new SensorSceneInputs(scene.Sensors, scene.History),
        Media = media,
        LocalZone = TimeZoneInfo.Utc,
        Locale = RegionalFormat.Invariant,
    };

    internal static MediaSampleCache FixtureMedia() =>
        new(new FolderAssets(Path.Combine(TestPaths.ProjectDirectory, "Fixtures", "assets")), new MediaSampleCacheOptions { Synchronous = true });

    /// <summary>The reference document's frame, drawn by the scene runtime.</summary>
    private static SKBitmap RenderScene(string name)
    {
        using var scene = new GoldenScene();
        using var media = FixtureMedia();
        var document = SceneKit.Load(Path.Combine(ReferenceDir, name + ".document.json"));
        using var snapshot = SceneRuntime.RenderOnce(document, GoldenScene.UtcNow, Options(scene, media));
        Assert.All(snapshot.Bounds, b => Assert.Null(b.Problem));
        return snapshot.Render(ScaleOf(name));
    }

    /// <summary>The reference document's frame, as <see cref="GoldenImage.Measure"/> reads pixels.</summary>
    private static SKBitmap RenderSceneNormalized(string name)
    {
        using var frame = RenderScene(name);
        return GoldenImage.Normalize(frame);
    }

    /// <summary>
    /// The step-1 renderer's frame of the case with unhinted glyphs: everything else as its golden. Text is laid out by
    /// the same library in both renderers; only its default glyph hinting differs, and it is a process-wide default, set
    /// here only while the Golden collection runs alone.
    /// </summary>
    private static SKBitmap RenderStep1Unhinted(string name)
    {
        var before = TextPaintOptions.Default.Hinting;
        TextPaintOptions.Default.Hinting = SKFontHinting.None;
        try
        {
            using var scene = new GoldenScene();
            var (profile, items) = GoldenTests.Cases[name]();
            return scene.Render(profile, items, ScaleOf(name));
        }
        finally
        {
            TextPaintOptions.Default.Hinting = before;
        }
    }

    private static string GoldenPath(string name) => Path.Combine(GoldenImage.GoldenDirectory, name + ".png");

    /// <summary>
    /// The frame against the golden; against the step-1 renderer with unhinted glyphs; and for an accepted case, outside
    /// its areas against the golden or (<see cref="Deviation.Unhinted"/>) the unhinted step-1 frame.
    /// </summary>
    private static (ImageDiff Golden, ImageDiff Unhinted, ImageDiff? Outside) Compare(string name, bool writeOnFailure = false)
    {
        using var frame = RenderScene(name);
        var golden = GoldenImage.Measure(GoldenPath(name), frame);
        using var unhinted = RenderStep1Unhinted(name);
        var unhintedPath = Path.Combine(GoldenImage.ResultsDirectory, $"v1-parity-{name}.unhinted.png");
        Directory.CreateDirectory(GoldenImage.ResultsDirectory);
        File.WriteAllBytes(unhintedPath, GoldenImage.EncodePng(unhinted));
        var vsUnhinted = GoldenImage.Measure(unhintedPath, frame);
        ImageDiff? rest = null;
        if (Accepted.TryGetValue(name, out var accepted))
        {
            var scale = (int)ScaleOf(name);
            var areas = accepted.Areas.Select(r => new SKRectI(r.Left * scale, r.Top * scale, r.Right * scale, r.Bottom * scale)).ToArray();
            rest = GoldenImage.Measure(accepted.Unhinted ? unhintedPath : GoldenPath(name), frame, areas);
        }
        if (writeOnFailure && golden.Fraction > GoldenImage.DifferingPixelFraction)
            GoldenImage.WriteComparison("v1-parity-" + name, GoldenPath(name), frame);
        return (golden, vsUnhinted, rest);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Matches_the_step1_golden(string name)
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        var (golden, unhinted, rest) = Compare(name, writeOnFailure: true);
        output.WriteLine($"{name}: against the golden {golden}; against step 1 unhinted {unhinted}; outside the accepted areas {rest}");
        var see = $" See TestResults/golden/v1-parity-{name}.actual.png.";
        if (!Accepted.TryGetValue(name, out var accepted))
        {
            Assert.True(golden.Fraction <= GoldenImage.DifferingPixelFraction, $"{name}: {golden}; at most {GoldenImage.DifferingPixelFraction * 100}% may.{see}");
            return;
        }
        Assert.True(golden.Fraction <= accepted.MaxFraction, $"{name}: {golden}; at most {accepted.MaxFraction * 100:0.###}% may ({accepted.Reason}).{see}");
        Assert.True(rest!.Value.Fraction <= GoldenImage.DifferingPixelFraction,
            $"{name} outside its accepted areas, against {(accepted.Unhinted ? "the step-1 renderer with unhinted glyphs" : "the golden")}: {rest}; "
            + $"at most {GoldenImage.DifferingPixelFraction * 100}% may.{see}");
    }

    /// <summary>An accepted deviation that no longer differs is removed instead of carried along.</summary>
    [Theory]
    [MemberData(nameof(AcceptedNames))]
    public void Accepted_deviations_still_differ(string name)
    {
        if (name.Length == 0) return; // none accepted
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        var (golden, _, _) = Compare(name);
        Assert.True(golden.Fraction > GoldenImage.DifferingPixelFraction, $"{name} is now within the standard tolerance ({golden}); remove its deviation.");
    }

    public static TheoryData<string> AcceptedNames() => Accepted.Count == 0 ? new("") : new(Accepted.Keys.Order(StringComparer.Ordinal));

    /// <summary>The parity table in <c>TestResults/golden/v1-parity.md</c>.</summary>
    [Fact]
    public void Writes_the_parity_table()
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        var table = new StringBuilder("| Case | Scale | Against the golden: max channel diff, differing pixels (%) | Against step 1 unhinted | Outside accepted areas | Result |\n|---|---|---|---|---|---|\n");
        static string Cell(ImageDiff d) => $"{d.MaxDelta}, {d.Differing} ({100 * d.Fraction:0.###}%)";
        foreach (var name in Cases())
        {
            var (golden, unhinted, rest) = Compare(name);
            var ok = GoldenImage.DifferingPixelFraction;
            var result = !Accepted.TryGetValue(name, out var a) ? golden.Fraction <= ok ? "within tolerance" : "FAILS"
                : golden.Fraction <= a.MaxFraction && rest!.Value.Fraction <= ok ? $"accepted: {a.Reason}" : "FAILS";
            table.Append(CultureInfo.InvariantCulture,
                $"| {name} | {ScaleOf(name)} | {golden.MaxDelta}, {golden.Differing} / {golden.Total} ({100 * golden.Fraction:0.###}%) | {Cell(unhinted)} | {(rest is { } r ? Cell(r) : "")} | {result} |\n");
        }
        Directory.CreateDirectory(GoldenImage.ResultsDirectory);
        File.WriteAllText(Path.Combine(GoldenImage.ResultsDirectory, "v1-parity.md"), table.ToString());
        output.WriteLine(table.ToString());
    }

    /// <summary>
    /// The rotated chart's reason: with its rotation taken out of both (the scene's rotate transform, the step-1 item's
    /// Rotation), the donut matches the step-1 renderer within the standard tolerance.
    /// </summary>
    [Fact]
    public void A_rotated_chart_differs_only_by_its_rotation()
    {
        const string name = "donut-span270-rotation90";
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(ReferenceDir, name + ".document.json")))!;
        var group = node["items"]![0]!["scene"]!["nodes"]![0]!.AsObject();
        Assert.NotNull(group["transform"]);
        group.Remove("transform");
        using var scene = new GoldenScene();
        using var media = FixtureMedia();
        using var snapshot = SceneRuntime.RenderOnce(JsonSerializer.SerializeToElement(node), GoldenScene.UtcNow, Options(scene, media));
        using var frame = snapshot.Render();

        var (profile, items) = GoldenTests.Cases[name]();
        ((InfoPanel.Models.DonutDisplayItem)items.Single()).Rotation = 0;
        using var step1 = scene.Render(profile, items);
        var path = Path.Combine(GoldenImage.ResultsDirectory, "v1-parity-donut-unrotated.step1.png");
        Directory.CreateDirectory(GoldenImage.ResultsDirectory);
        File.WriteAllBytes(path, GoldenImage.EncodePng(step1));
        var diff = GoldenImage.Measure(path, frame);
        Assert.True(diff.Fraction <= GoldenImage.DifferingPixelFraction, $"unrotated donut: {diff}");
    }

    /// <summary>
    /// The resampling reason: inside the image's box the golden is the image resized with a Mitchell filter (as step 1
    /// resized it) and the scene's frame is the image drawn with linear filtering and mipmaps, both into the same box.
    /// </summary>
    [Theory]
    [InlineData("image-png", "logo.png", 116, 10, 48, 80)]
    [InlineData("image-gif-frame0", "blink.gif", 10, 10, 96, 64)]
    public void A_resized_image_differs_only_by_its_filter(string name, string file, int x, int y, int w, int h)
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        // Decoded as step 1 decoded it: the codec's own pixel format (unpremultiplied), copied to BGRA.
        using var codec = SKCodec.Create(Path.Combine(TestPaths.ProjectDirectory, "Fixtures", "assets", file));
        using var decoded = new SKBitmap(codec.Info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(codec.Info, decoded.GetPixels(), new SKCodecOptions(0)));
        using var source = decoded.Copy(SKColorType.Bgra8888);
        using var premultiplied = SKBitmap.Decode(Path.Combine(TestPaths.ProjectDirectory, "Fixtures", "assets", file));
        using var frame = RenderScene(name);
        var box = SKRectI.Create(x, y, w, h);
        using var golden = SKBitmap.Decode(GoldenPath(name));

        SKBitmap Draw(Action<SKCanvas> image)
        {
            // The case's own frame outside the box, so only the box is compared.
            var bitmap = golden.Copy(SKColorType.Rgba8888);
            using var canvas = new SKCanvas(bitmap);
            canvas.Save();
            canvas.ClipRect(box);
            canvas.Clear(SKColor.Parse(GoldenScene.Background));
            image(canvas);
            canvas.Restore();
            canvas.Flush();
            return bitmap;
        }

        using var mitchell = Draw(c =>
        {
            using var resized = source.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var image = SKImage.FromBitmap(resized);
            c.DrawImage(image, x, y);
        });
        using var linear = Draw(c =>
        {
            using var image = SKImage.FromBitmap(premultiplied);
            using var paint = new SKPaint { IsAntialias = true };
            c.DrawImage(image, SKRect.Create(x, y, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        });

        var against = GoldenImage.Measure(GoldenPath(name), mitchell);
        Assert.True(against.Fraction <= GoldenImage.DifferingPixelFraction, $"{name}, the golden against a Mitchell resize: {against}");
        var linearPath = Path.Combine(GoldenImage.ResultsDirectory, $"v1-parity-{name}.linear.png");
        Directory.CreateDirectory(GoldenImage.ResultsDirectory);
        File.WriteAllBytes(linearPath, GoldenImage.EncodePng(linear));
        var scene = GoldenImage.Measure(linearPath, frame);
        Assert.True(scene.Fraction <= GoldenImage.DifferingPixelFraction, $"{name}, the scene against a linear draw: {scene}");
    }

    /// <summary>
    /// The placeholder reason: each missing image's placeholder fills the box step 1 drew its own placeholder in, and is
    /// drawn there (<see cref="PlaceholderProblems"/>).
    /// </summary>
    [Theory]
    [InlineData("image-missing")]
    [InlineData("http-image-placeholder")]
    public void A_missing_image_draws_the_placeholder_in_the_step1_box(string name)
    {
        using var scene = new GoldenScene();
        using var media = FixtureMedia();
        var document = SceneKit.Load(Path.Combine(ReferenceDir, name + ".document.json"));
        using var snapshot = SceneRuntime.RenderOnce(document, GoldenScene.UtcNow, Options(scene, media));
        var frames = snapshot.Bounds.Select(b => SKRectI.Create((int)b.Frame.X, (int)b.Frame.Y, (int)b.Frame.W, (int)b.Frame.H)).ToArray();
        Assert.Equal(Accepted[name].Areas, frames);
        Assert.Contains(snapshot.Diagnostics, d => d.Code.StartsWith("media", StringComparison.Ordinal));
        using var rendered = snapshot.Render();
        using var frame = GoldenImage.Normalize(rendered);
        Assert.All(Accepted[name].Areas, box => Assert.Empty(PlaceholderProblems(frame, box)));
    }

    /// <summary>The placeholder check fails on a frame without the placeholder.</summary>
    [Fact]
    public void The_placeholder_check_fails_on_a_blank_box()
    {
        using var frame = RenderSceneNormalized("image-missing");
        var box = Accepted["image-missing"].Areas[1];
        Fill(frame, box, SKColor.Parse(GoldenScene.Background));
        Assert.NotEmpty(PlaceholderProblems(frame, box));
        Assert.Empty(PlaceholderProblems(frame, Accepted["image-missing"].Areas[0]));
    }

    /// <summary>
    /// The missing-media placeholder at <paramref name="box"/>, as the engine draws it (the colours of
    /// <c>Placeholders.DrawMissingMedia</c>, composited here over the golden background): a #9CA3AF fill at alpha 0x40,
    /// a one-pixel border and both diagonals at alpha 0xB0, and the background just outside.
    /// </summary>
    internal static List<string> PlaceholderProblems(SKBitmap frame, SKRectI box)
    {
        var background = SKColor.Parse(GoldenScene.Background);
        var grey = new SKColor(0x9C, 0xA3, 0xAF);
        var fill = Over(grey, 0x40, background);
        var line = Over(grey, 0xB0, fill);
        var problems = new List<string>();
        void Expect(int x, int y, SKColor colour, string what)
        {
            var actual = frame.GetPixel(x, y);
            if (Delta(actual, colour) > 3) problems.Add($"{what} at ({x}, {y}) is {actual}, not {colour}");
        }

        int l = box.Left, t = box.Top, r = box.Right - 1, b = box.Bottom - 1, w = box.Width, h = box.Height;
        Expect(l + w / 2, t + h / 4, fill, "fill");
        Expect(l + w / 2, t + 3 * h / 4, fill, "fill");
        Expect(l + w / 4, t + h / 2, fill, "fill");
        Expect(l + 3 * w / 4, t + h / 2, fill, "fill");
        Expect(l, t + h / 3, line, "left border");
        Expect(r, t + h / 3, line, "right border");
        Expect(l + w / 3, t, line, "top border");
        Expect(l + w / 3, b, line, "bottom border");
        Expect(l - 1, t + h / 3, background, "background");
        Expect(r + 1, t + h / 3, background, "background");
        Expect(l + w / 3, t - 1, background, "background");
        Expect(l + w / 3, b + 1, background, "background");
        // Each diagonal crosses the middle row and column: the brightest pixel there is about the line's colour.
        foreach (var (x0, x1) in new[] { (l + 1, l + w / 2), (l + w / 2, r) })
        {
            var brightest = Enumerable.Range(x0, x1 - x0).Max(x => frame.GetPixel(x, t + h / 4).Green);
            if (brightest < line.Green - 24) problems.Add($"no diagonal between x {x0} and {x1} at y {t + h / 4} (brightest green {brightest}, the line is {line.Green})");
        }
        return problems;
    }

    /// <summary>
    /// The table rows' reason, row by row: step 1 put row k (k = 0 is the header) at <c>y + floor(k × line height)</c>, the
    /// runtime at <c>y + k × rowHeight</c> with its glyphs on whole pixels, so row k is step 1's row k (drawn unhinted)
    /// moved down by <c>ceil(y + k × rowHeight) − (y + floor(k × line height))</c> pixels: 0 for the header and 1 for the
    /// data rows here (<paramref name="shifts"/>). Each row and the part below the rows must match that within the standard
    /// tolerance. Tables of one-line cells; cells of several lines are checked line by line
    /// (<see cref="Wrapped_cells_match_step1_line_by_line_at_their_fractional_offsets"/>), and a glowing table without its
    /// glow (<see cref="A_glowing_table_differs_only_by_its_glow"/>).
    /// </summary>
    [Theory]
    [InlineData("table", "0,1,1,1,1")]
    [InlineData("table-missing-column", "0,1,1,1,1")]
    [InlineData("table-rows-0", "0")]
    [InlineData("table-single-line-no-ellipsis", "0,1,1,1,1")]
    public void Table_rows_match_step1_row_by_row_at_their_fractional_offsets(string name, string shifts)
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        var (frame, unhinted, layout) = TableFrames(name);
        using (frame)
        using (unhinted)
        {
            output.WriteLine($"rows at {string.Join(", ", layout.Step1Tops)} in step 1, moved down by {string.Join(", ", layout.Shifts)}");
            Assert.Equal(shifts.Split(',').Select(int.Parse), layout.Shifts);
            var problems = TableRowProblems(frame, unhinted, layout);
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
    }

    /// <summary>The row check fails on missing rows and on a row one pixel off.</summary>
    [Theory]
    [InlineData("table")]
    [InlineData("table-missing-column")]
    [InlineData("table-rows-0")]
    [InlineData("table-single-line-no-ellipsis")]
    public void The_table_row_check_fails_on_missing_or_moved_rows(string name)
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        var (frame, unhinted, layout) = TableFrames(name);
        using (frame)
        using (unhinted)
        {
            var background = SKColor.Parse(GoldenScene.Background);
            var tops = layout.Step1Tops;
            var rows = layout.Shifts.Length;
            SKRectI Band(int k) => SKRectI.Create(0, tops[k] + layout.Shifts[k], frame.Width, tops[k + 1] - tops[k]);

            // Every data row blanked (the header too when there are none).
            var first = rows > 1 ? 1 : 0;
            using var blank = frame.Copy();
            Fill(blank, SKRectI.Create(0, Band(first).Top, blank.Width, blank.Height - Band(first).Top), background);
            Assert.Equal(rows - first, TableRowProblems(blank, unhinted, layout).Count(p => p.StartsWith("row", StringComparison.Ordinal)));

            var last = rows - 1;
            using var lastBlank = frame.Copy();
            Fill(lastBlank, Band(last), background);
            Assert.Single(TableRowProblems(lastBlank, unhinted, layout), p => p.StartsWith($"row {last}", StringComparison.Ordinal));

            var middle = Math.Min(2, last);
            using var moved = frame.Copy();
            Shift(moved, Band(middle), 0, -1, background);
            Assert.Contains(TableRowProblems(moved, unhinted, layout), p => p.StartsWith($"row {middle}", StringComparison.Ordinal));
        }
    }

    private sealed record TableLayout(int[] Step1Tops, int[] Shifts);

    /// <summary>The table's frame, the step-1 renderer's frame of it with unhinted glyphs, and both renderers' row offsets.</summary>
    private static (SKBitmap Frame, SKBitmap Unhinted, TableLayout Layout) TableFrames(string name)
    {
        using var scene = new GoldenScene();
        using var media = FixtureMedia();
        var document = SceneKit.Load(Path.Combine(ReferenceDir, name + ".document.json"));
        using var snapshot = SceneRuntime.RenderOnce(document, GoldenScene.UtcNow, Options(scene, media));
        var (profile, items) = GoldenTests.Cases[name]();
        var table = (InfoPanel.Models.TableSensorDisplayItem)items.Single();
        var rows = table.MaxRows + (table.ShowHeader ? 1 : 0);
        // Step 1's rows are one "A" line apart (its cells can be taller); the runtime's table frame is `rows` rowHeights tall.
        var g = InfoPanel.Drawing.SkiaGraphics.FromEmpty(profile.FontScale); // no canvas, nothing to dispose
        var (_, step1Line) = g.MeasureString("A", table.Font, table.FontStyle, table.FontSize, table.Bold, table.Italic, table.Underline, table.Strikeout);
        var rowHeight = snapshot.Bounds.Single().Frame.H / rows;
        var y = table.Y;
        var tops = Enumerable.Range(0, rows + 1).Select(k => (int)(y + step1Line * k)).ToArray();
        var shifts = Enumerable.Range(0, rows).Select(k => (int)Math.Ceiling(y + k * rowHeight - 1e-3) - tops[k]).ToArray();
        using var rendered = snapshot.Render();
        using var step1 = RenderStep1Unhinted(name);
        return (GoldenImage.Normalize(rendered), GoldenImage.Normalize(step1), new TableLayout(tops, shifts));
    }

    /// <summary>Each row's band of the unhinted step-1 frame against the frame's band moved by its shift, then the rest below.</summary>
    private static List<string> TableRowProblems(SKBitmap frame, SKBitmap unhinted, TableLayout layout)
    {
        var problems = new List<string>();
        var rows = layout.Shifts.Length;
        for (var k = 0; k < rows; k++)
        {
            var band = SKRectI.Create(0, layout.Step1Tops[k], frame.Width, layout.Step1Tops[k + 1] - layout.Step1Tops[k]);
            var diff = Differing(unhinted, band, frame, layout.Shifts[k]);
            if (diff > 0) problems.Add($"row {k} (step 1's y {band.Top}–{band.Bottom}, moved {layout.Shifts[k]}): {diff} pixels differ");
        }
        var shift = layout.Shifts[rows - 1];
        var below = layout.Step1Tops[rows];
        var rest = Differing(unhinted, SKRectI.Create(0, below, frame.Width, frame.Height - below - Math.Max(0, shift)), frame, shift);
        if (rest > 0) problems.Add($"below the rows (step 1's y ≥ {below}, moved {shift}): {rest} pixels differ");
        return problems;
    }

    /// <summary>
    /// The fractional rows' reason for cells of several lines (1.x with Wrap off), which run over the rows below: one cell
    /// at a time (every other data cell empty, so no lines overlap), each of its lines is step 1's line (drawn unhinted)
    /// moved down by its own shift. Step 1 put line j of row k at <c>floor(y + k × line height) + j × line height</c>, the
    /// runtime at <c>y + k × rowHeight + j × line height</c>, each with its glyphs on whole pixels, so line j moves by
    /// <c>ceil(runtime top) − ceil(step 1's top)</c> pixels (for the first line, the row's shift). The header and the rest of
    /// the frame must match unmoved.
    /// </summary>
    [Fact]
    public void Wrapped_cells_match_step1_line_by_line_at_their_fractional_offsets()
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        const string name = "table-wrap-off";
        var full = GoldenScene.NotesTable();
        var checkedLines = 0;
        for (var r = 0; r < full.Rows.Count; r++)
            for (var c = 0; c < full.Columns.Count; c++)
            {
                var only = full.Clone();
                foreach (System.Data.DataRow source in full.Rows) only.ImportRow(source);
                for (var rr = 0; rr < only.Rows.Count; rr++)
                    for (var cc = 0; cc < only.Columns.Count; cc++)
                        if (rr != r || cc != c) only.Rows[rr][cc] = new InfoPanel.Plugins.PluginText("empty", "");
                var (frame, unhinted, lines) = CellFrames(name, only, r);
                using (frame)
                using (unhinted)
                {
                    output.WriteLine($"row {r + 1}, column {c}: lines at step 1's y {string.Join(", ", lines.Select(l => $"{l.Top}–{l.Bottom} moved {l.Shift}"))}");
                    Assert.NotEmpty(lines);
                    var problems = new List<string>();
                    var y = 0;
                    foreach (var (top, bottom, shift) in lines)
                    {
                        var before = Differing(unhinted, SKRectI.Create(0, y, frame.Width, top - y), frame, 0);
                        if (before > 0) problems.Add($"above y {top}: {before} pixels differ");
                        var line = Differing(unhinted, SKRectI.Create(0, top, frame.Width, bottom - top), frame, shift);
                        if (line > 0) problems.Add($"line at {top}–{bottom} (moved {shift}): {line} pixels differ");
                        y = bottom;
                    }
                    var rest = Differing(unhinted, SKRectI.Create(0, y, frame.Width, frame.Height - y - 1), frame, 0);
                    if (rest > 0) problems.Add($"below y {y}: {rest} pixels differ");
                    Assert.True(problems.Count == 0, $"row {r + 1}, column {c}: " + string.Join("; ", problems));
                    checkedLines += lines.Count;
                }
            }
        // Some cells wrap or break: more lines than cells.
        Assert.True(checkedLines > full.Rows.Count * full.Columns.Count, $"{checkedLines} lines");
    }

    /// <summary>
    /// The case drawn with only <paramref name="table"/>'s cells by both renderers, and the bands of the step-1 frame its
    /// data row <paramref name="row"/> (0 first) draws lines in (each with a pixel of margin) with their shifts.
    /// </summary>
    private static (SKBitmap Frame, SKBitmap Unhinted, List<(int Top, int Bottom, int Shift)> Lines) CellFrames(string name, System.Data.DataTable table, int row)
    {
        using var scene = new GoldenScene();
        var (profile, items) = GoldenTests.Cases[name]();
        var item = (InfoPanel.Models.TableSensorDisplayItem)items.Single();
        scene.Sensors.Readings[InfoPanel.Sensors.SensorKey.Plugin(item.PluginSensorId)] = new InfoPanel.Models.SensorReading(table, item.TableFormat, "");
        using var media = FixtureMedia();
        var document = SceneKit.Load(Path.Combine(ReferenceDir, name + ".document.json"));
        using var snapshot = SceneRuntime.RenderOnce(document, GoldenScene.UtcNow, Options(scene, media));
        var g = InfoPanel.Drawing.SkiaGraphics.FromEmpty(profile.FontScale); // no canvas, nothing to dispose
        var (_, step1Line) = g.MeasureString("A", item.Font, item.FontStyle, item.FontSize, item.Bold, item.Italic, item.Underline, item.Strikeout);
        var rows = item.MaxRows + (item.ShowHeader ? 1 : 0);
        var rowHeight = snapshot.Bounds.Single().Frame.H / rows;
        var lineHeight = TextLayoutEngine.Layout(new TextLayoutKey { Runs = [new TextRun("A", "Inter", item.FontSize * profile.FontScale, 400, false)], BaseSize = item.FontSize * profile.FontScale }).Lines[0].Height;
        var k = row + (item.ShowHeader ? 1 : 0);
        var step1Top = (int)(item.Y + step1Line * k);

        using var rendered = snapshot.Render();
        SKBitmap Step1Unhinted()
        {
            var before = TextPaintOptions.Default.Hinting;
            TextPaintOptions.Default.Hinting = SKFontHinting.None;
            try
            {
                using var step1 = scene.Render(profile, items);
                return GoldenImage.Normalize(step1);
            }
            finally
            {
                TextPaintOptions.Default.Hinting = before;
            }
        }
        var unhinted = Step1Unhinted();
        // The same frame with every data cell empty: the header alone.
        var empty = table.Clone();
        foreach (System.Data.DataRow source in table.Rows) empty.Rows.Add(source.ItemArray.Select(_ => (object)new InfoPanel.Plugins.PluginText("empty", "")).ToArray());
        scene.Sensors.Readings[InfoPanel.Sensors.SensorKey.Plugin(item.PluginSensorId)] = new InfoPanel.Models.SensorReading(empty, item.TableFormat, "");
        using var header = Step1Unhinted();

        // The cell's lines: bands where the step-1 frame differs from the header alone, line j the j-th.
        var inked = Enumerable.Range(0, unhinted.Height).Select(y => Enumerable.Range(0, unhinted.Width).Any(x => Delta(unhinted.GetPixel(x, y), header.GetPixel(x, y)) > GoldenImage.ChannelTolerance)).ToArray();
        // Runs of inked rows less than 4 rows apart are one line (an i's dot, an accent).
        var runs = new List<(int Top, int Bottom)>();
        for (var y = 0; y < inked.Length; y++)
        {
            if (!inked[y]) continue;
            var top = y;
            while (y < inked.Length && inked[y]) y++;
            if (runs.Count > 0 && top - runs[^1].Bottom < 4) runs[^1] = (runs[^1].Top, y);
            else runs.Add((top, y));
        }
        var lines = new List<(int, int, int)>();
        foreach (var (top, bottom) in runs)
        {
            var j = lines.Count;
            var shift = (int)Math.Ceiling(item.Y + k * rowHeight + j * lineHeight - 1e-3) - (int)Math.Ceiling(step1Top + j * step1Line - 1e-3);
            lines.Add((Math.Max(0, top - 1), Math.Min(inked.Length - 1, bottom + 1), shift));
        }
        return (GoldenImage.Normalize(rendered), unhinted, lines);
    }

    /// <summary>The glow's reason: with the glow taken out of both, the glowing table is the table case, pixel for pixel.</summary>
    [Fact]
    public void A_glowing_table_differs_only_by_its_glow()
    {
        var node = JsonNode.Parse(File.ReadAllText(Path.Combine(ReferenceDir, "table-glow.document.json")))!;
        var group = node["items"]![0]!["scene"]!["nodes"]![0]!.AsObject();
        Assert.NotNull(group["effects"]);
        group.Remove("effects");
        using var scene = new GoldenScene();
        using var media = FixtureMedia();
        using var snapshot = SceneRuntime.RenderOnce(JsonSerializer.SerializeToElement(node), GoldenScene.UtcNow, Options(scene, media));
        using var frame = snapshot.Render();
        using var table = RenderScene("table");
        Assert.Equal(0, SceneKit.DifferingPixels(table, frame, GoldenImage.ChannelTolerance));

        var (_, glowing) = GoldenTests.Cases["table-glow"]();
        var (_, plain) = GoldenTests.Cases["table"]();
        var a = (InfoPanel.Models.TableSensorDisplayItem)glowing.Single();
        var b = (InfoPanel.Models.TableSensorDisplayItem)plain.Single();
        Assert.True(a.GlowEnabled && !b.GlowEnabled);
        Assert.Equal((b.TableFormat, b.MaxRows, b.RightAlign, b.FontSize, b.Color, b.X, b.Y), (a.TableFormat, a.MaxRows, a.RightAlign, a.FontSize, a.Color, a.X, a.Y));
    }

    /// <summary>
    /// The charts' reason at scale 2: each chart's area of the scale-2 frame, reduced to scale 1 (each 2 × 2 block
    /// averaged), against the step-1 scale-1 golden, an independent full-resolution reference for where every edge is.
    /// </summary>
    [Fact]
    public void Scale2_charts_match_the_step1_scale1_golden_where_they_are_drawn()
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        using var frame = RenderSceneNormalized("composite-scale2");
        using var reference = GoldenImage.Load(GoldenPath("composite-scale1"));
        foreach (var (chart, area, max) in CompositeCharts)
        {
            var differing = ChartDiffering(frame, reference, area);
            output.WriteLine($"{chart}: {differing} of {area.Width * area.Height} pixels differ by more than {ChartChannelTolerance} (at most {max} may)");
            Assert.True(differing <= max, $"{chart}: {differing} pixels of {area} differ by more than {ChartChannelTolerance}; at most {max} may");
        }
    }

    /// <summary>The scale-2 chart check fails when a chart moves by one pixel at scale 2.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void The_scale2_chart_check_fails_on_a_one_pixel_shift(int dx, int dy)
    {
        GoldenImage.RequireGoldenDirectory(GoldenImage.GoldenDirectory);
        using var reference = GoldenImage.Load(GoldenPath("composite-scale1"));
        foreach (var (chart, area, max) in CompositeCharts)
        {
            using var frame = RenderSceneNormalized("composite-scale2");
            Shift(frame, new SKRectI(area.Left * 2, area.Top * 2, area.Right * 2, area.Bottom * 2), dx, dy, SKColor.Parse(GoldenScene.Background));
            var differing = ChartDiffering(frame, reference, area);
            Assert.True(differing > max, $"{chart} moved by ({dx}, {dy}) at scale 2: only {differing} pixels differ (at most {max} may)");
        }
    }

    /// <summary>Pixels of <paramref name="area"/> (scale 1) where the 2 × 2 average of <paramref name="frame2"/> differs from <paramref name="reference"/> by more than <see cref="ChartChannelTolerance"/>.</summary>
    private static int ChartDiffering(SKBitmap frame2, SKBitmap reference, SKRectI area)
    {
        var differing = 0;
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (var j = 0; j < 2; j++)
                    for (var i = 0; i < 2; i++)
                    {
                        var c = frame2.GetPixel(2 * x + i, 2 * y + j);
                        r += c.Red; g += c.Green; b += c.Blue; a += c.Alpha;
                    }
                var average = new SKColor((byte)((r + 2) / 4), (byte)((g + 2) / 4), (byte)((b + 2) / 4), (byte)((a + 2) / 4));
                if (Delta(average, reference.GetPixel(x, y)) > ChartChannelTolerance) differing++;
            }
        return differing;
    }

    /// <summary>Pixels of <paramref name="band"/> in <paramref name="expected"/> that differ by more than the channel tolerance from <paramref name="actual"/> <paramref name="down"/> pixels lower.</summary>
    private static int Differing(SKBitmap expected, SKRectI band, SKBitmap actual, int down)
    {
        var differing = 0;
        for (var y = band.Top; y < band.Bottom; y++)
            for (var x = band.Left; x < band.Right; x++)
            {
                var a = y + down >= 0 && y + down < actual.Height ? actual.GetPixel(x, y + down) : SKColors.Transparent;
                if (Delta(a, expected.GetPixel(x, y)) > GoldenImage.ChannelTolerance) differing++;
            }
        return differing;
    }

    private static int Delta(SKColor a, SKColor b) =>
        Math.Max(Math.Max(Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green)), Math.Max(Math.Abs(a.Blue - b.Blue), Math.Abs(a.Alpha - b.Alpha)));

    /// <summary><paramref name="colour"/> at <paramref name="alpha"/> over the opaque <paramref name="under"/>.</summary>
    private static SKColor Over(SKColor colour, byte alpha, SKColor under)
    {
        static byte Mix(byte top, byte bottom, int a) => (byte)Math.Round((top * a + bottom * (255 - a)) / 255.0);
        return new SKColor(Mix(colour.Red, under.Red, alpha), Mix(colour.Green, under.Green, alpha), Mix(colour.Blue, under.Blue, alpha), 0xFF);
    }

    private static void Fill(SKBitmap bitmap, SKRectI area, SKColor colour)
    {
        for (var y = Math.Max(0, area.Top); y < Math.Min(bitmap.Height, area.Bottom); y++)
            for (var x = Math.Max(0, area.Left); x < Math.Min(bitmap.Width, area.Right); x++)
                bitmap.SetPixel(x, y, colour);
    }

    /// <summary>Moves the content of <paramref name="area"/> by (dx, dy) within it, filling what it uncovers with <paramref name="colour"/>.</summary>
    private static void Shift(SKBitmap bitmap, SKRectI area, int dx, int dy, SKColor colour)
    {
        using var copy = bitmap.Copy();
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                int sx = x - dx, sy = y - dy;
                bitmap.SetPixel(x, y, area.Contains(sx, sy) ? copy.GetPixel(sx, sy) : colour);
            }
    }

    /// <summary>The reference documents are plain documents: valid, and carrying only what the engine reads.</summary>
    [Theory]
    [MemberData(nameof(AllNames))]
    public void Reference_documents_are_valid_and_carry_no_item_sources(string name)
    {
        var document = SceneKit.Load(Path.Combine(ReferenceDir, name + ".document.json"));
        var check = DocumentValidator.Check(document, support: EngineSupport.All);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")
            .Concat(check.Items.Select(i => $"{i.Id}: {i.Problem} {string.Join(", ", i.Errors.Select(e => e.Code + " " + e.Path))}"))));
        Assert.Empty(check.Items);
        Assert.False(document.GetProperty("profile").TryGetProperty("editor", out _));
        AssertNoSources(document.GetProperty("items"));
    }

    public static TheoryData<string> AllNames() => new(Directory.EnumerateFiles(ReferenceDir, "*.document.json")
        .Select(f => Path.GetFileName(f)[..^".document.json".Length]).Order(StringComparer.Ordinal));

    internal static void AssertNoSources(JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
        {
            Assert.False(item.TryGetProperty("source", out _), $"{item.GetProperty("id")} has a source");
            Assert.False(item.TryGetProperty("origin", out _), $"{item.GetProperty("id")} has an origin");
            if (item.TryGetProperty("children", out var children)) AssertNoSources(children);
        }
    }

    /// <summary>
    /// The 1.x all-kinds profile (<c>Fixtures/v1/</c>, every kind) as a reference document: every item renders without a
    /// placeholder problem; its frame is a scene golden (<c>Golden/scene/{os}/v1-all-kinds.png</c>), since the step-1
    /// goldens have no frame of it.
    /// </summary>
    [Fact]
    public void The_all_kinds_profile_renders_every_item()
    {
        using var media = FixtureMedia();
        var document = SceneKit.Load(Path.Combine(ReferenceDir, "all-kinds.document.json"));
        var options = new SceneRuntimeOptions { Inputs = new EveryKeyInputs(), Media = media, LocalZone = TimeZoneInfo.Utc, Locale = RegionalFormat.Invariant };
        using var snapshot = SceneRuntime.RenderOnce(document, GoldenScene.UtcNow, options);
        Assert.True(snapshot.Bounds.Count >= 17);
        Assert.All(snapshot.Bounds, b => Assert.Null(b.Problem));
        using var bitmap = snapshot.Render();
        GoldenImage.Verify("v1-all-kinds", bitmap, "scene");
    }
}

/// <summary>
/// A reading for every key (the 1.x profile's keys are from several sensor sources): a number between 20 and 80 fixed by
/// the key, a history around it, and the step-1 goldens' process table for table inputs. Text inputs read as unavailable,
/// as a numeric sensor's do.
/// </summary>
internal sealed class EveryKeyInputs : ISceneInputSource
{
    private static double Level(string key) => 20 + key.Aggregate(0, (h, c) => (h * 31 + c) % 6007) % 61;

    public SceneReading Read(string key, string inputType) => inputType switch
    {
        "number" => new SceneReading { Status = SceneInputStatus.Live, Now = Level(key), Min = Level(key) - 10, Max = Level(key) + 10, Avg = Level(key), Unit = "%" },
        "table" => new SceneReading
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(["Process", "CPU", "RAM"], [["dotnet", "12.5%", "412 MB"], ["WindowServer", "8.1%", "260 MB"], ["Safari", "3.4%", "1.2 GB"]]),
        },
        _ => SceneReading.Unavailable,
    };

    public IReadOnlyList<double> History(string key) => [.. Enumerable.Range(0, 160).Select(i => Level(key) + 15 * Math.Sin(i / 7.0))];
}
