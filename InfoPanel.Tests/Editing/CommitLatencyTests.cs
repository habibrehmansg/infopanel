using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Editing;

/// <summary>
/// A drag on the reference document of spec/docs/limits.md (1920 × 480, 30 items) through the engine on real Kestrel:
/// the round trip of each <c>setPlacement</c> commit over loopback HTTP, and the time from sending it to the first
/// <c>/ws</c> frame of its revision (scale 1, 60 fps). Prints p50/p95/p99; asserts only generous bounds, since these are
/// timings on a shared machine. Measure with
/// <c>dotnet test InfoPanel.slnx -c Release --filter Category=Performance --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Performance")]
public sealed class CommitLatencyTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_drag_on_the_reference_document_commits_and_reaches_the_preview_quickly()
    {
        using var engine = new EngineFactory();
        using var live = LiveEngine.Start(engine);
        using var api = live.Client();
        var id = await CreateAsync(api, ReferenceDocument(), Ct);
        var instance = await LiveEngine.Tag(api, id, Ct);
        var rev = (await api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct)).GetProperty("rev").GetInt64();
        await using var socket = await live.Subscribe(id, Ct, scale: 1, maxFps: 60);
        await socket.FirstFrameAt(rev, 0, TimeSpan.FromSeconds(10), Ct);

        const int warmup = 20, samples = 200;
        List<double> commit = [], frame = [];
        for (var i = 0; i < warmup + samples; i++)
        {
            // One gesture, a placement per pointer move, based on the revision the client last read.
            var bytes = LiveEngine.Commit(instance, rev, "gesture:latency:1", Place($"i{i % 30}", 100 + i % 50, 40 + i % 20, baseRev: rev));
            var t0 = Stopwatch.GetTimestamp();
            using (var response = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct))
            {
                var t1 = Stopwatch.GetTimestamp();
                var body = await LiveEngine.Body(response, Ct);
                Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
                rev = body.GetProperty("rev").GetInt64();
                if (i >= warmup)
                    commit.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            }

            // The frame of exactly this revision, after the request was sent; none labelled with a revision still to come.
            var shown = await socket.FirstFrameAt(rev, t0, TimeSpan.FromSeconds(10), Ct);
            Assert.True(socket.FrameRevs.Max() == rev, $"a frame labelled rev {socket.FrameRevs.Max()} while the document was at {rev}");
            if (i >= warmup)
                frame.Add(Stopwatch.GetElapsedTime(t0, shown).TotalMilliseconds);
        }

#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        foreach (var (name, list) in new[] { ("setPlacement commit round trip", commit), ("commit to its first frame", frame) })
        {
            var (p50, p95, p99) = LiveEngine.Percentiles(list);
            output.WriteLine($"{build} {name}: p50 {p50:0.00} ms, p95 {p95:0.00} ms, p99 {p99:0.00} ms");
        }

        // Generous: a round trip is well under a millisecond's work on the engine's side, and a frame waits for the next
        // tick at 60 fps and a 1920 × 480 PNG.
        Assert.True(LiveEngine.Percentiles(commit).P95 < 100, "setPlacement commit round trip p95");
        Assert.True(LiveEngine.Percentiles(frame).P95 < 1000, "commit to frame p95");
    }

    /// <summary>
    /// The reference document of spec/docs/limits.md, as the scene runtime's performance tests build it: donuts, a graph,
    /// a clock, bars, histograms, sensor text, text layout, a table, peak holds, gauges and shapes, from spec/examples.
    /// </summary>
    internal static JsonObject ReferenceDocument()
    {
        var examples = Path.Combine(LiveEngine.RepoRoot(), "spec", "examples", "scene", "valid");
        var items = new List<JsonObject>();
        void Add(string example, double x, double y, double? w = null, double? h = null)
        {
            var n = items.Count;
            var scene = JsonNode.Parse(File.ReadAllText(Path.Combine(examples, example)))!.AsObject();
            var extra = new JsonObject();
            if (w is not null || h is not null)
            {
                var layout = new JsonObject();
                if (w is { } ww) layout["w"] = ww;
                if (h is { } hh) layout["h"] = hh;
                extra["layout"] = layout;
            }
            if (scene["inputs"] is JsonObject inputs)
            {
                var bindings = new JsonObject();
                foreach (var (name, _) in inputs) bindings[name] = new JsonObject { ["key"] = $"perf:{n % 6}:{name}" };
                extra["bindings"] = bindings;
            }
            items.Add(Scene($"i{n}", x, y, scene, extra));
        }
        for (var i = 0; i < 4; i++) Add("donut.json", 20 + i * 140, 20);
        Add("graph.json", 600, 20);
        Add("clock.json", 880, 20);
        for (var i = 0; i < 6; i++) Add("bar.json", 1120, 20 + i * 30, 240, 20);
        for (var i = 0; i < 2; i++) Add("histogram.json", 1400 + i * 260, 20, 240, 80);
        for (var i = 0; i < 8; i++) Add("sensor-text.json", 20 + i * 180, 300);
        Add("text-layout.json", 1400, 120);
        Add("table.json", 1620, 120);
        for (var i = 0; i < 2; i++) Add("peak-hold.json", 1100 + i * 40, 230);
        for (var i = 0; i < 2; i++) Add("gauge-sequence.json", 600 + i * 150, 120);
        for (var i = 0; i < 2; i++) Add("shapes.json", 900 + i * 220, 250);
        Assert.Equal(30, items.Count);
        var doc = Doc("Wide bar", 1920, 480, [.. items]);
        doc["profile"]!["background"] = "#0b1220ff";
        return doc;
    }
}
