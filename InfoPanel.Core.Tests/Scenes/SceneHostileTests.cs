using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Images;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// Every example renders or is refused without harm, and content at (or crafted against) the limits stays within time
/// and memory bounds: the item is skipped with a placeholder and a diagnostic, the loop goes on, the process survives.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class SceneHostileTests
{
    private static readonly string ExamplesDir = SceneKit.ExamplesDir;

    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateFiles(Path.Combine(ExamplesDir, folder), "*.json").Select(f => Path.GetRelativePath(ExamplesDir, f)).Order(StringComparer.Ordinal);

    public static TheoryData<string> ValidScenes() => new(Files("scene/valid"));
    public static TheoryData<string> InvalidScenes() => new(Files("scene/invalid").Concat(Files("scene/unsupported")));
    public static TheoryData<string> Documents() => new(Files("document/valid").Concat(Files("document/placeholder")));
    public static TheoryData<string> BadDocuments() => new(Files("document/invalid").Concat(Files("document/unsupported")));

    [Theory]
    [MemberData(nameof(ValidScenes))]
    public void Every_valid_example_scene_ticks_draws_and_measures(string rel)
    {
        var inputs = new FakeSceneInputs();
        using var media = SceneKit.SyncMedia();
        using var runtime = new SceneRuntime(SceneKit.Options(inputs, media));
        runtime.Load(SceneKit.Doc(SceneKit.Example(rel), 300, 240, 10, 10, 200, 120));
        for (var t = 0.0; t < 1; t += 0.25)
        {
            using var s = runtime.Tick(t, SceneKit.Now);
            Assert.DoesNotContain(s.Diagnostics, d => d.Code.StartsWith("budget", StringComparison.Ordinal) || d.Code == "runtime.error");
            Assert.Null(s.Bounds.Single().Problem);
            s.Render(1).Dispose();
            s.Render(2).Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(InvalidScenes))]
    public void Every_invalid_or_unsupported_example_scene_is_a_placeholder(string rel)
    {
        var scene = SceneKit.Example(rel);
        var doc = SceneKit.Doc(scene, 200, 200);
        if (!scene.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String)
        {
            // A document's schema checks each scene for a string format (spec/docs/validation.md), so one without is the
            // document's error, at the scene's path as the example gives it in the scene (missing: the scene itself).
            var refused = Assert.Throws<SceneDocumentException>(() => SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options()));
            Assert.Equal(("schema", "/items/0/scene" + ExpectedPath(rel)), (Assert.Single(refused.Check.Errors).Code, refused.Check.Errors[0].Path));
            return;
        }
        if (ExpectedCode(rel) == "json")
        {
            // Not JSON as the formats read it (a number that rounds to infinity): the document holding it isn't either.
            var refused = Assert.Throws<SceneDocumentException>(() => SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options()));
            Assert.Equal("json", Assert.Single(refused.Check.Errors).Code);
            return;
        }
        using var s = SceneRuntime.RenderOnce(doc, SceneKit.Now, SceneKit.Options(limits: ExpectedLimits(rel)));
        Assert.NotNull(s.Bounds.Single().Problem);
        s.Render().Dispose();
    }

    /// <summary>The runtime assumes validated input but must not hang or overflow on what validation refuses.</summary>
    [Theory]
    [MemberData(nameof(InvalidScenes))]
    public void Invalid_example_scenes_compiled_without_validation_evaluate_within_bounds(string rel)
    {
        Scene scene;
        try
        {
            scene = SpecJson.Read<Scene>(SceneKit.Example(rel));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return; // not readable as a scene at all: nothing reaches the runtime
        }
        var watch = Stopwatch.StartNew();
        var compiled = CompiledScene.Compile(scene, SceneCost.Zero, SpecLimits.Default);
        var rt = new ItemRuntime("x");
        rt.Use(compiled);
        var inputs = compiled.InputNames.Select(_ => InputSnap.Unbound).ToArray();
        var evaluator = new SceneEvaluator(compiled, rt, new TickServices(), inputs, SceneClock.At(0, SceneKit.Now), 100, 100,
            new ItemDiagnostics("x"), 40_000);
        try
        {
            var nodes = evaluator.Run(advance: true);
            using var bmp = new SkiaSharp.SKBitmap(200, 200);
            using var canvas = new SkiaSharp.SKCanvas(bmp);
            SceneDrawer.Draw(canvas, nodes);
            _ = SceneBounds.Measure(nodes);
        }
        catch (BudgetExceeded)
        {
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{rel} took {watch.Elapsed}");
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_valid_or_placeholder_example_document_renders(string rel)
    {
        using var media = SceneKit.SyncMedia();
        using var s = SceneRuntime.RenderOnce(SceneKit.Example(rel), SceneKit.Now, SceneKit.Options(media: media));
        s.Render().Dispose();
        Assert.Equal(s.Bounds.Count, s.Bounds.Select(b => b.Id).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(BadDocuments))]
    public void Invalid_and_unsupported_example_documents_are_refused(string rel)
    {
        using var runtime = new SceneRuntime(SceneKit.Options());
        using var strict = new SceneRuntime(SceneKit.Options(limits: ExpectedLimits(rel)));
        var e = Assert.Throws<SceneDocumentException>(() => strict.Load(SceneKit.Example(rel)));
        Assert.NotEqual(CheckStatus.Valid, e.Check.Status);
    }

    [Theory]
    [InlineData("too-large.png")]
    [InlineData("too-wide.png")]
    [InlineData("truncated.png")]
    [InlineData("frames.gif")]
    [InlineData("external.svg")]
    [InlineData("script.svg")]
    [InlineData("dtd.svg")]
    [InlineData("data-uri.svg")]
    [InlineData("event-attribute.svg")]
    [InlineData("style-url.svg")]
    [InlineData("physical-units.svg")]
    [InlineData("not-media.txt")]
    public void Hostile_media_is_inspected_before_decoding_and_drawn_as_missing(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(ExamplesDir, "media", "invalid", file));
        var hash = "sha256:" + new string('a', 64);
        var store = new FolderAssets { Override = h => h == hash ? bytes : null };
        var limits = file is "frames.gif" ? SpecLimits.Default.With(new Dictionary<string, double> { ["mediaFrames"] = 2 }) : SpecLimits.Default;
        using var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true, Limits = limits });
        var scene = SceneKit.Json($"{{'format':'infopanel.scene/1','nodes':[{{'kind':'media','w':40,'h':40,'source':{{'asset':'{hash}'}}}}]}}");
        using var s = SceneRuntime.RenderOnce(SceneKit.Doc(scene, 60, 60), SceneKit.Now, SceneKit.Options(media: media));
        Assert.Contains(s.Diagnostics, d => d.Code == "media.missing");
        Assert.Equal(0, s.SampleCount);
        Assert.Equal(0, media.Bytes);
        s.Render().Dispose();
    }

    /// <summary>An invalid or unsupported example's expected code (spec/examples/expected.json).</summary>
    private static string? ExpectedCode(string rel) =>
        (string?)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, "expected.json")))![rel.Replace('\\', '/')]?["code"];

    /// <summary>An invalid or unsupported example's expected path (spec/examples/expected.json).</summary>
    private static string ExpectedPath(string rel) =>
        (string)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, "expected.json")))![rel.Replace('\\', '/')]!["path"]!;

    /// <summary>The limits an example tightens to stay small (spec/examples/expected.json).</summary>
    private static SpecLimits ExpectedLimits(string rel)
    {
        var expected = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, "expected.json")))!;
        return expected[rel.Replace('\\', '/')]?["limits"] is System.Text.Json.Nodes.JsonObject limits
            ? SpecLimits.Default.With(limits.ToDictionary(p => p.Key, p => p.Value!.GetValue<double>()))
            : SpecLimits.Default;
    }

    // ---- synthetic worst cases ------------------------------------------------------------------------------

    private static string Scene(string nodes, string extra = "") => $"{{\"format\":\"infopanel.scene/1\"{extra},\"nodes\":[{nodes}]}}";

    private static (TickSnapshot Snapshot, TimeSpan Tick, TimeSpan Draw) Time(string sceneJson, int width = 800, int height = 600, SceneRuntimeOptions? options = null, int ticks = 2)
    {
        var scene = JsonDocument.Parse(sceneJson, SpecJson.DocumentOptions).RootElement.Clone();
        var check = SceneValidator.Check(scene);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Take(3).Select(e => $"{e.Code} {e.Path} {e.Message}")));
        var runtime = new SceneRuntime(options ?? SceneKit.Options());
        runtime.Load(SceneKit.Doc(scene, width, height));
        var tick = Stopwatch.StartNew();
        TickSnapshot s = null!;
        for (var i = 0; i < ticks; i++)
        {
            s?.Dispose();
            s = runtime.Tick(i * 0.1, SceneKit.Now);
        }
        tick.Stop();
        var draw = Stopwatch.StartNew();
        s.Render().Dispose();
        draw.Stop();
        runtime.Dispose();
        return (s, tick.Elapsed, draw.Elapsed);
    }

    [Fact]
    public void The_most_nodes_nested_deepest_stay_within_a_frame()
    {
        // 2,000 nodes: 23 nested groups, the rest rectangles at the deepest level.
        var sb = new StringBuilder();
        for (var i = 0; i < 23; i++) sb.Append("{\"kind\":\"group\",\"transform\":[{\"type\":\"rotate\",\"angle\":1}],\"children\":[");
        sb.Append(string.Join(",", Enumerable.Range(0, 1977).Select(i => $"{{\"kind\":\"rect\",\"x\":{i % 80 * 10},\"y\":{i / 80 * 20},\"w\":8,\"h\":16,\"fill\":\"#ff0000\"}}")));
        for (var i = 0; i < 23; i++) sb.Append("]}");
        var (s, tick, draw) = Time(Scene(sb.ToString()));
        using (s) Assert.Null(s.Bounds.Single().Problem);
        Assert.True(tick < TimeSpan.FromSeconds(1), $"tick {tick}");
        Assert.True(draw < TimeSpan.FromSeconds(1), $"draw {draw}");
    }

    [Fact]
    public void The_most_expression_work_stays_within_a_tick()
    {
        // 256 values in a chain, 77 operations each (about 19,700 units).
        var values = new StringBuilder();
        for (var i = 0; i < 256; i++)
        {
            var r = i == 0 ? "1" : $"{{\"op\":\"value\",\"name\":\"v{i - 1}\"}}";
            var sin = $"{{\"op\":\"sin\",\"args\":[{r}]}}";
            var block = $"{{\"op\":\"add\",\"args\":[{string.Join(",", Enumerable.Repeat(sin, 15))}]}}";
            var e = $"{{\"op\":\"add\",\"args\":[{block},{block},{string.Join(",", Enumerable.Repeat(sin, 7))}]}}";
            values.Append(i == 0 ? "" : ",").Append($"\"v{i}\":{e}");
        }
        var json = Scene("{\"kind\":\"rect\",\"w\":{\"op\":\"value\",\"name\":\"v255\"},\"h\":10,\"fill\":\"#ffffff\"}", $",\"values\":{{{values}}}");
        var check = SceneValidator.Check(JsonDocument.Parse(json, SpecJson.DocumentOptions).RootElement);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Take(3).Select(e => $"{e.Code} {e.Path}")));
        Assert.InRange(check.Cost.Work, 15_000, 20_000);
        var (s, tick, _) = Time(json, ticks: 10);
        using (s) Assert.Null(s.Bounds.Single().Problem);
        Assert.True(tick < TimeSpan.FromMilliseconds(500), $"10 ticks took {tick}");
    }

    [Fact]
    public void The_longest_largest_text_is_bounded()
    {
        var text = new string('W', 4096);
        var nodes = $"{{\"kind\":\"text\",\"w\":20,\"wrap\":\"word\",\"font\":{{\"family\":\"Inter\",\"size\":2048}},\"fill\":\"#ffffff\",\"text\":\"{text}\"}},"
            + $"{{\"kind\":\"text\",\"y\":100,\"font\":{{\"family\":\"Inter\",\"size\":2048}},\"fill\":\"#ffffff\",\"text\":\"{text}\"}},"
            + $"{{\"kind\":\"text\",\"w\":300,\"h\":40,\"fit\":{{\"min\":1}},\"wrap\":\"word\",\"font\":{{\"family\":\"Inter\",\"size\":2048}},\"fill\":\"#ffffff\",\"text\":\"{string.Join(' ', Enumerable.Repeat("word", 800))}\"}}";
        var (s, tick, draw) = Time(Scene(nodes), 1920, 480);
        using (s) Assert.Null(s.Bounds.Single().Problem);
        Assert.True(tick < TimeSpan.FromSeconds(10), $"tick {tick}");
        Assert.True(draw < TimeSpan.FromSeconds(5), $"draw {draw}");
    }

    [Fact]
    public void A_dash_too_fine_for_its_outline_is_over_budget()
    {
        var points = string.Join(",", Enumerable.Range(0, 8192).Select(i => $"[{i % 2 * 1000},{i}]"));
        var (s, tick, draw) = Time(Scene($"{{\"kind\":\"polyline\",\"points\":[{points}],\"stroke\":{{\"paint\":\"#ffffff\",\"width\":1,\"dash\":[0.001,0.001]}}}}"));
        using (s)
        {
            Assert.Equal("budget", s.Bounds.Single().Problem);
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.dash");
        }
        Assert.True(tick + draw < TimeSpan.FromSeconds(2), $"{tick} + {draw}");
    }

    [Fact]
    public void The_most_effects_on_a_full_canvas_are_bounded()
    {
        // Seven sigma-64 blurs (cost 9 each, 63 of 64) over a 4096 × 4096 canvas (16 Mpx, the frame cap).
        var groups = string.Join(",", Enumerable.Range(0, 7).Select(_ =>
            "{\"kind\":\"group\",\"effects\":[{\"type\":\"blur\",\"sigma\":64}],\"children\":[{\"kind\":\"rect\",\"w\":4096,\"h\":4096,\"fill\":\"#ffffff80\"}]}"));
        var (s, _, draw) = Time(Scene(groups), 4096, 4096);
        s.Dispose();
        Assert.True(draw < TimeSpan.FromSeconds(30), $"draw {draw}");
    }

    [Fact]
    public void Changing_tables_are_bounded_by_the_text_budget()
    {
        var columns = string.Join(",", Enumerable.Range(0, 16).Select(i => $"{{\"column\":{i},\"w\":40}}"));
        var tables = string.Join(",", Enumerable.Range(0, 8).Select(i =>
            $"{{\"kind\":\"table\",\"input\":\"t\",\"y\":{i * 10},\"rows\":100,\"columns\":[{columns}],\"font\":{{\"family\":\"Inter\",\"size\":8}},\"fill\":\"#ffffff\"}}"));
        var inputs = new FakeSceneInputs();
        var counter = 0;
        void Fill() => inputs.Readings["test:t"] = new SceneReading
        {
            Status = SceneInputStatus.Live,
            Table = new SceneTable(Enumerable.Range(0, 16).Select(c => "c" + c).ToArray(),
                Enumerable.Range(0, 100).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, 16).Select(c => $"{r}:{c}:{counter}").ToArray()).ToArray()),
        };
        Fill();
        var json = Scene(tables, ",\"inputs\":{\"t\":{\"type\":\"table\"}}");
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(SceneKit.Doc(JsonDocument.Parse(json).RootElement, 800, 600));
        var watch = Stopwatch.StartNew();
        using (var s = runtime.Tick(0, SceneKit.Now))
        {
            Assert.Equal("budget", s.Bounds.Single().Problem);
            Assert.Contains(s.Diagnostics, d => d.Code == "budget.text");
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"tick {watch.Elapsed}");
    }

    [Fact]
    public void The_most_series_samples_draw_within_a_frame()
    {
        var inputs = new FakeSceneInputs();
        inputs.Number("test:v", 1);
        inputs.Histories["test:v"] = Enumerable.Range(0, 4096).Select(i => (double)(i * 7919 % 100)).ToArray();
        var series = string.Join(",", Enumerable.Range(0, 32).Select(i =>
            $"{{\"kind\":\"series\",\"input\":\"v\",\"y\":{i * 15},\"w\":1920,\"h\":15,\"pitch\":0.25,\"min\":0,\"max\":100,\"mode\":\"{(i % 3 == 0 ? "bars" : i % 3 == 1 ? "area" : "line")}\",\"fill\":\"#22c55e\",\"stroke\":{{\"paint\":\"#ffffff\",\"width\":1}}}}"));
        var (s, tick, draw) = Time(Scene(series, ",\"inputs\":{\"v\":{\"type\":\"number\"}}"), 1920, 480, SceneKit.Options(inputs));
        s.Dispose();
        Assert.True(tick < TimeSpan.FromSeconds(2), $"tick {tick}");
        Assert.True(draw < TimeSpan.FromSeconds(3), $"draw {draw}");
    }

    [Fact]
    public void Extreme_numbers_draw_nothing_harmful()
    {
        var nodes = string.Join(",",
            "{\"kind\":\"rect\",\"x\":1e308,\"y\":-1e308,\"w\":1e308,\"h\":1e308,\"r\":1e308,\"fill\":\"#ffffff\",\"stroke\":{\"paint\":\"#ff0000\",\"width\":1e30}}",
            "{\"kind\":\"ellipse\",\"cx\":{\"op\":\"div\",\"args\":[1,0]},\"rx\":{\"op\":\"pow\",\"args\":[10,400]},\"ry\":5,\"fill\":\"#ffffff\"}",
            "{\"kind\":\"arc\",\"r\":1e20,\"inner\":-5,\"sweep\":1e9,\"stroke\":{\"paint\":\"#ffffff\",\"width\":3,\"dash\":[1e-30,1e30]}}",
            "{\"kind\":\"group\",\"transform\":[{\"type\":\"scale\",\"x\":1e-30,\"y\":1e30},{\"type\":\"skew\",\"x\":89,\"y\":-89}],\"effects\":[{\"type\":\"shadow\",\"dx\":1e30,\"dy\":-1e30,\"sigma\":1e9,\"color\":\"#000000\"}],\"children\":[{\"kind\":\"rect\",\"w\":10,\"h\":10,\"fill\":\"#ffffff\"}]}",
            "{\"kind\":\"text\",\"x\":1e300,\"letterSpacing\":1e30,\"lineHeight\":1e30,\"font\":{\"family\":\"Inter\",\"size\":1e9},\"fill\":\"#ffffff\",\"text\":\"x\"}");
        var (s, tick, draw) = Time(Scene(nodes));
        using (s) _ = s.Bounds;
        Assert.True(tick + draw < TimeSpan.FromSeconds(5), $"{tick} + {draw}");
    }

    [Fact]
    public void A_slow_item_is_suspended_and_the_loop_goes_on()
    {
        var options = new SceneRuntimeOptions { ItemBudgetMs = 0.000001, LocalZone = TimeZoneInfo.Utc };
        using var runtime = new SceneRuntime(options);
        runtime.Load(SceneKit.Doc(SceneKit.Example("scene/valid/shapes.json"), 220, 180));
        for (var i = 0; i < 3; i++) runtime.Tick(i * 0.1, SceneKit.Now).Dispose();
        using var s = runtime.Tick(0.4, SceneKit.Now);
        Assert.Equal("budget", s.Bounds.Single().Problem);
        Assert.Contains(s.Diagnostics, d => d.Code == "budget.time");
        s.Render().Dispose();
    }
}
