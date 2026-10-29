using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Documents;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// The cost of a drag on the reference document of spec/docs/limits.md (30 items): checking and loading it whole (what
/// every commit would cost without generations), a <c>setPlacement</c> commit (the drag budget's 2 ms share) and the
/// scene runtime's load of the generation it makes. Prints p50/p95/p99; asserts the commit budget in Release and loose
/// bounds otherwise. Measure with
/// <c>dotnet test InfoPanel.slnx -c Release --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Performance")]
public sealed class DocumentPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void A_drag_on_the_reference_document_costs_only_what_changed()
    {
        var json = ScenePerformanceTests.ReferenceDocument();
        using var doc = Open(json);
        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(doc.Current);

        List<double> check = [], whole = [], commit = [], load = [];
        for (var i = 0; i < 40; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            DocumentValidator.Check(json, null, EngineSupport.All);
            var t1 = Stopwatch.GetTimestamp();
            using (var fresh = new SceneRuntime(SceneKit.Options())) fresh.Load(json);
            var t2 = Stopwatch.GetTimestamp();
            if (i < 10) continue;
            check.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            whole.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
        }

        const int warmup = 50, samples = 500;
        for (var i = 0; i < warmup + samples; i++)
        {
            // A client's drag: one gesture, a placement per pointer move, based on the revision it last read, as the endpoint receives it.
            var body = Encoding.UTF8.GetBytes(Commit(doc.Rev, $"drag-{i}", "gesture:perf:1", Place($"i{i % 30}", 100 + i % 50, 40 + i % 20, baseRev: doc.Rev)).GetRawText());
            var t0 = Stopwatch.GetTimestamp();
            var outcome = doc.Commit(body);
            var t1 = Stopwatch.GetTimestamp();
            runtime.Load(doc.Current);
            var t2 = Stopwatch.GetTimestamp();
            Assert.NotNull(outcome.Result);
            if (i < warmup) continue;
            commit.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            load.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
        }
        using (var snapshot = runtime.Tick(0, SceneKit.Now)) Assert.Equal(doc.Rev, snapshot.Rev);
        Assert.Equal(1, doc.HistorySize.Undo);

#if DEBUG
        const string build = "Debug";
        const double limit = 20;
#else
        const string build = "Release";
        const double limit = 2;
#endif
        foreach (var (name, list) in new[] { ("whole document check", check), ("whole document load", whole), ("setPlacement commit", commit), ("next-generation load", load) })
        {
            var (p50, p95, p99) = ScenePerformanceTests.Percentiles(list);
            output.WriteLine($"{build} {name}: p50 {p50:0.000} ms, p95 {p95:0.000} ms, p99 {p99:0.000} ms");
        }
        Assert.True(ScenePerformanceTests.Percentiles(commit).P95 <= limit, "setPlacement commit p95");
        Assert.True(ScenePerformanceTests.Percentiles(load).P95 <= limit, "next-generation load p95");
    }

    /// <summary>
    /// A valid scene of 1 MiB (<c>sceneBytes</c>) in the canonical form, sent pretty-printed: 32 polylines of 1,000 points
    /// whose coordinates are whole numbers, short decimals and full-precision doubles (17 digits, the spelling that has to
    /// be converted), and texts of é and ASCII making up the rest (each é escaped as sent: six bytes, two measured).
    /// </summary>
    internal static JsonElement OneMebibyteScene()
    {
        var polylines = new List<JsonNode>();
        for (var p = 0; p < 32; p++)
        {
            var points = new JsonArray();
            for (var i = 0; i < 1_000; i++)
            {
                var x = JsonNode.Parse((i * 1.25).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                var y = JsonNode.Parse((i % 3 == 0 ? i : (p + i) * 0.1 + 0.07).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                points.Add(new JsonArray(x, y));
            }
            polylines.Add(new JsonObject { ["kind"] = "polyline", ["points"] = points, ["stroke"] = new JsonObject { ["paint"] = "#ffffff", ["width"] = 1 } });
        }

        // The most text that keeps the scene within sceneBytes, by bisection on its length, in texts of at most 4,000.
        JsonObject Scene(int length)
        {
            var nodes = new JsonArray();
            foreach (var polyline in polylines) nodes.Add(polyline.DeepClone());
            for (var at = 0; at < length; at += 4_000)
                nodes.Add(new JsonObject
                {
                    ["kind"] = "text", ["y"] = at / 100, ["font"] = new JsonObject { ["family"] = "Inter", ["size"] = 12 }, ["fill"] = "#ffffff",
                    ["text"] = string.Create(Math.Min(4_000, length - at), 0, (span, _) => { for (var i = 0; i < span.Length; i++) span[i] = i % 4 == 0 ? 'é' : 'x'; }),
                });
            return new JsonObject { ["format"] = "infopanel.scene/1", ["nodes"] = nodes };
        }
        int low = 0, high = 1_048_576;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (GenerationContent.SizeOf(JsonSerializer.SerializeToElement(Scene(mid))) <= 1_048_576) low = mid;
            else high = mid - 1;
        }
        var pretty = Scene(low).ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        using var doc = JsonDocument.Parse(pretty, SpecJson.DocumentOptions);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// A sizing benchmark, not a commit's: it times only the canonical measure of a 1 MiB scene (<c>sceneBytes</c>), which
    /// a commit takes once per scene it carries (<see cref="CommitTests.A_commit_measures_each_scene_it_carries_once"/>).
    /// Validating, compiling and applying the scene are not in it; the whole commit has no bound here. The bound, 20 ms at
    /// p95, is the same in either build (measured about 9 ms in Debug and 3 ms in Release).
    /// </summary>
    [Fact]
    public void Sizing_benchmark_a_1_MiB_scene_is_measured_within_20_ms()
    {
        var scene = OneMebibyteScene();
        Assert.InRange(GenerationContent.SizeOf(scene), 1_048_000, 1_048_576);
        const int warmup = 30, samples = 100;
        List<double> times = [];
        for (var i = 0; i < warmup + samples; i++)
        {
            var t0 = Stopwatch.GetTimestamp();
            var measured = ValidationText.CanonicalBytes(scene);
            var t1 = Stopwatch.GetTimestamp();
            Assert.True(measured > 0);
            if (i >= warmup) times.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
        }
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        var (p50, p95, p99) = ScenePerformanceTests.Percentiles(times);
        output.WriteLine($"{build} sizing a 1 MiB scene ({GenerationContent.SizeOf(scene)} bytes measured, {System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(scene).Length} sent): p50 {p50:0.000} ms, p95 {p95:0.000} ms, p99 {p99:0.000} ms");
        Assert.True(p95 < 20, $"sizing p95 {p95:0.000} ms");
    }

    [Fact]
    public void A_design_commit_checks_only_its_own_scene()
    {
        using var doc = Open(ScenePerformanceTests.ReferenceDocument());
        var scene = JsonNode.Parse(SceneKit.Example("scene/valid/donut.json").GetRawText())!.AsObject();
        List<double> times = [];
        for (var i = 0; i < 60; i++)
        {
            scene["nodes"]![0]!["cx"] = 50 + i;
            var body = Commit(Design("i0", scene.DeepClone().AsObject()));
            var t0 = Stopwatch.GetTimestamp();
            Assert.NotNull(doc.Commit(body).Result);
            if (i >= 10) times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        var (p50, p95, p99) = ScenePerformanceTests.Percentiles(times);
        output.WriteLine($"setDesign commit (one donut): p50 {p50:0.000} ms, p95 {p95:0.000} ms, p99 {p99:0.000} ms");
        Assert.True(p95 < 20, "setDesign commit p95");
    }

    /// <summary>
    /// A blob read's hash check: the first read of a file version in a run (a cold store) hashes the bytes it read; a
    /// later one at the same version only reads them. At 100 KiB, 2 MiB and <c>mediaBytes</c> (32 MiB).
    /// </summary>
    [Fact]
    public void A_blob_read_hashes_only_the_first_time_it_meets_a_file()
    {
        using var temp = new InfoPanel.Core.Tests.Persistence.TempDirectory();
        var paths = new InfoPanel.Persistence.DataPaths(temp.Path);
        foreach (var size in new[] { 100 * 1024, 2 * 1024 * 1024, SpecLimits.Default.MediaBytes })
        {
            var content = new byte[size];
            new Random(size).NextBytes(content);
            var id = new BlobStore(paths).Put(content);
            var warm = new BlobStore(paths);
            Assert.NotNull(warm.ReadVerified(id));

            List<double> cold = [], cached = [];
            for (var i = 0; i < 25; i++)
            {
                var store = new BlobStore(paths);
                var t0 = Stopwatch.GetTimestamp();
                Assert.NotNull(store.ReadVerified(id));
                var t1 = Stopwatch.GetTimestamp();
                Assert.NotNull(warm.ReadVerified(id));
                var t2 = Stopwatch.GetTimestamp();
                if (i < 5) continue;
                cold.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                cached.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            }
            Assert.Equal(1, warm.Hashes);
            foreach (var (name, list) in new[] { ("first read (hashed)", cold), ("later read (cached)", cached) })
            {
                var (p50, p95, p99) = ScenePerformanceTests.Percentiles(list);
                output.WriteLine($"blob {size / 1024} KiB {name}: p50 {p50:0.000} ms, p95 {p95:0.000} ms, p99 {p99:0.000} ms");
            }
            Assert.True(ScenePerformanceTests.Percentiles(cold).P95 < 1_000, "first blob read p95");
        }
    }

    /// <summary>
    /// A profile's Save as the store does it, flushes included (on macOS every folder flush is a full flush): at a compact
    /// 30-item document's size (120 KiB) and at 1 MiB, a save that changed the content (it keeps the document it replaces
    /// as a version, and drops the oldest past ten) and one of the same bytes (it keeps none). Commits never wait for it:
    /// Save writes outside the document's lock.
    /// </summary>
    [Fact]
    public void A_save_writes_and_flushes_the_document()
    {
        using var temp = new InfoPanel.Core.Tests.Persistence.TempDirectory();
        var paths = new InfoPanel.Persistence.DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        foreach (var size in new[] { 120 * 1024, 1024 * 1024 })
        {
            var id = Guid.NewGuid().ToString();
            var content = new byte[size];
            new Random(size).NextBytes(content);
            store.Create(id, content);
            foreach (var changed in new[] { true, false })
            {
                List<double> times = [];
                for (var i = 0; i < 50; i++)
                {
                    if (changed)
                        content[i] ^= 1;
                    var t0 = Stopwatch.GetTimestamp();
                    store.Save(id, content);
                    if (i >= 10) times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
                var (p50, p95, p99) = ScenePerformanceTests.Percentiles(times);
                output.WriteLine($"Save {size / 1024} KiB, {(changed ? "keeping a version" : "the same bytes, no version")}: p50 {p50:0.000} ms, p95 {p95:0.000} ms, p99 {p99:0.000} ms");
                Assert.True(p95 < 500, "Save p95");
            }
            Assert.Equal(DocumentStore.VersionsKept, Directory.GetFiles(Path.Combine(paths.ProfileFolder(id), "versions")).Length);
        }
    }
}
