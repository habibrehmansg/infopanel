using System.Diagnostics;
using System.Text;
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
}
