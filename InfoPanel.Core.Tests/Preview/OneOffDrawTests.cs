using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Output;
using InfoPanel.Scenes;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// <see cref="RenderService.InspectOnce"/>, a one-off draw with a runtime of its own: it leaves every loop's videos as they
/// are, and with made-up values for every input it gives an input without a reading one of its type.
/// </summary>
public class OneOffDrawTests
{
    private const int Width = 300, Height = 100;

    /// <summary>The fixture frame drawn at the centre of a 64 × 48 video at (0, 0).</summary>
    private static int VideoFrame(FakeSink.Received frame)
    {
        var i = (24 * Width + 32) * 4;
        return VideoFixtures.FrameOf(new SKColor(frame.Pixels[i], frame.Pixels[i + 1], frame.Pixels[i + 2]));
    }

    [Fact]
    public async Task A_one_off_draw_plays_videos_on_decode_sessions_of_its_own_and_leaves_the_live_ones_as_they_are()
    {
        var clip = VideoFixtures.Read("clip-h264.mp4");
        var hash = InfoPanel.Documents.BlobStore.IdOf(clip);
        var scene = JsonNode.Parse($$$"""
            {"format": "infopanel.scene/1", "requires": ["media.video"],
             "nodes": [{"kind": "media", "playback": "once", "source": {"video": "{{{hash}}}"}}]}
            """)!.AsObject();
        await using var h = new RenderHarness(RenderHarness.Doc(Width, Height, DocKit.Scene("video", 0, 0, scene)));
        var assets = new InfoPanel.Core.Tests.Scenes.MemoryAssets();
        assets.Assets[hash] = clip;
        // As in a render loop: each session decodes on a thread of its own, a few frames ahead of the time asked for.
        using var media = new InfoPanel.Images.MediaSampleCache(assets, new InfoPanel.Images.MediaSampleCacheOptions
        {
            Time = h.Time,
            Video = new InfoPanel.Images.Video.VideoOptions { Decoder = FFmpegForTests.Library },
        });
        var scenes = RenderHarness.Scenes(h.Workspace, h.Sensors, h.History);
        await using var service = new RenderService(h.Workspace, new SceneRuntimeOptions
        {
            Inputs = scenes.Inputs, Media = media, Limits = scenes.Limits, Support = InfoPanel.Spec.EngineSupport.All,
            LocalZone = scenes.LocalZone, Locale = scenes.Locale,
        }, h.Time, new RenderServiceOptions(false));

        var window = new FakeSink(h.ProfileId, new FrameRequest(1, 30));
        Assert.True(service.Attach(window, h.Document));
        int Tick()
        {
            h.Clock.Advance(34);
            service.Tick(h.ProfileId);
            return VideoFrame(window.Frames[^1]);
        }

        // Played once, the live video holds its last frame, 9, from 0.9 s on.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Tick() != 9 && DateTime.UtcNow < deadline)
            Thread.Sleep(2);
        Assert.Equal(9, VideoFrame(window.Frames[^1]));
        Assert.Equal(1, media.VideoSessions);

        // The same video at the same size and playback, drawn once at its time 0 (a document given to draw, or a profile
        // without a loop): on a session of its own while it draws, closed with its runtime.
        foreach (var simulated in new[] { false, true })
        {
            var during = service.InspectOnce(h.Document.Current, simulated, (_, _) => media.VideoSessions);
            Assert.Equal(2, during);
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (media.VideoSessions != 1 && DateTime.UtcNow < deadline)
                Thread.Sleep(2);
            Assert.Equal(1, media.VideoSessions);
        }

        // The live session was never asked for time 0: it holds its frame, and plays on as it was.
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(9, Tick());
            Thread.Sleep(2);
        }
        Assert.Equal(1, media.VideoSessions);
    }

    /// <summary>
    /// A text <c>t</c> drawn in white at (0, 0), its item <paramref name="id"/> at (0, <paramref name="y"/>), bound to
    /// <paramref name="key"/> when one is given.
    /// </summary>
    private static JsonObject TextItem(string id, int y, string? key)
    {
        var scene = JsonNode.Parse("""
            {"format": "infopanel.scene/1", "inputs": {"t": {"type": "text"}},
             "nodes": [{"kind": "text", "font": {"family": "Inter", "size": 12}, "fill": "#ffffff",
                        "text": {"op": "input", "name": "t", "field": "text"}}]}
            """)!.AsObject();
        return DocKit.Scene(id, 0, y, scene, key is null ? null : new JsonObject { ["bindings"] = new JsonObject { ["t"] = new JsonObject { ["key"] = key } } });
    }

    /// <summary>A table <c>procs</c> at (120, 0), its first three columns, no header.</summary>
    private static JsonObject TableItem() => DocKit.Scene("procs", 120, 0, JsonNode.Parse("""
        {"format": "infopanel.scene/1", "inputs": {"p": {"type": "table"}},
         "nodes": [{"kind": "table", "input": "p", "rows": 5, "rowHeight": 16, "header": false,
                    "columns": [{"column": 0, "w": 80}, {"column": 1, "w": 40}, {"column": 2, "w": 40}],
                    "font": {"family": "Inter", "size": 11}, "fill": "#ffffff"}]}
        """)!.AsObject());

    private static Dictionary<string, List<string>> Texts(RenderHarness h, bool simulated, bool everyInput) =>
        h.Service.InspectOnce(h.Document.Current, simulated, (snapshot, _) => snapshot.Texts
            .Where(t => t.Text.Length > 0)
            .GroupBy(t => t.ItemId)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Text).ToList()), everyInput);

    [Fact]
    public async Task With_made_up_values_for_every_input_an_input_without_a_reading_reads_a_made_up_value_of_its_type()
    {
        // An unbound text, a text bound to a key nothing reads, an unbound table, and a text that has a reading.
        await using var h = new RenderHarness(RenderHarness.Doc(Width, Height,
            TextItem("unbound", 0, null),
            TextItem("unread", 20, "plugin:/test/nothing"),
            TableItem(),
            TextItem("read", 40, "plugin:/test/song")));
        h.Sensors.Readings[InfoPanel.Sensors.SensorKey.Plugin("/test/song")] = new InfoPanel.Models.SensorReading("Artist - Title");

        var made = Texts(h, simulated: true, everyInput: true);
        Assert.Equal(["Text"], made["unbound"]);
        Assert.Equal(["Text"], made["unread"]);
        // A few rows of made-up processes, each with its numbers.
        // Five rows of three cells (a table's cells are laid out apart from the text report's words, so they are counted).
        Assert.Equal(15, Cells(h, everyInput: true));
        // A text with a reading is made up as a simulated subscription makes it up.
        Assert.Single(made["read"]);
        Assert.NotEqual("Artist - Title", made["read"][0]);

        // Without the option, as before: made up only where there is a reading; the rest draws nothing.
        var simulatedOnly = Texts(h, simulated: true, everyInput: false);
        Assert.Equal(["read"], simulatedOnly.Keys);
        Assert.Equal(made["read"], simulatedOnly["read"]);
        Assert.Equal(0, Cells(h, everyInput: false));
        var live = Texts(h, simulated: false, everyInput: false);
        Assert.Equal(["read"], live.Keys);
        Assert.Equal(["Artist - Title"], live["read"]);
    }

    /// <summary>The cells the table <c>procs</c> draws in a one-off with made-up values.</summary>
    private static int Cells(RenderHarness h, bool everyInput) =>
        h.Service.InspectOnce(h.Document.Current, simulated: true, (snapshot, _) => snapshot.Texts.Count(t => t.ItemId == "procs"), everyInput);

    /// <summary>
    /// An unbound table reads a table of made-up processes, in System Info's columns, its numbers made up; an unbound
    /// number walks as a key with no reading does, its history full; and a key of an unbound input is never read from the
    /// sensors (a key of a source no plugin may name).
    /// </summary>
    [Fact]
    public void An_unbound_input_reads_a_made_up_value_of_its_type_and_never_the_sensors()
    {
        var asked = new List<string>();
        var inputs = new EveryInputMadeUp(new Asked(asked), new InfoPanel.Core.Tests.Editing.ManualTimeProvider());
        var key = inputs.Bind("procs", "p")!.Value.Key;
        Assert.StartsWith("infopanel:/made-up/", key);
        Assert.NotEqual(key, inputs.Bind("procs", "q")!.Value.Key);
        Assert.NotEqual(key, inputs.Bind("other", "p")!.Value.Key);

        var table = inputs.Read(key, "table");
        Assert.Equal(SceneInputStatus.Live, table.Status);
        Assert.Equal(["Process Name", "Usage", "Utility", "Memory"], table.Table.Columns);
        Assert.Equal(SimulatedText.Processes.Take(8), table.Table.Rows.Select(r => r[0]));
        Assert.All(table.Table.Rows, r => Assert.Matches(@"^\d+(\.\d)?%$", r[1]));
        Assert.All(table.Table.Rows, r => Assert.Matches(@"^\d+(\.\d)? MB$", r[3]));

        var number = inputs.Read(inputs.Bind("gauge", "v")!.Value.Key, "number");
        Assert.Equal(SceneInputStatus.Live, number.Status);
        Assert.InRange(number.Now, SimulatedSceneInputs.NoReadingLow, SimulatedSceneInputs.NoReadingHigh);
        Assert.Equal(SimulatedSceneInputs.Backfill, inputs.History(inputs.Bind("gauge", "v")!.Value.Key).Count);
        Assert.Equal("Text", inputs.Read(inputs.Bind("song", "t")!.Value.Key, "text").Text);

        Assert.Empty(asked);
    }

    /// <summary>Sensors that read nothing and say which keys they were asked for.</summary>
    private sealed class Asked(List<string> asked) : ISceneInputSource
    {
        public SceneReading Read(string key, string inputType)
        {
            asked.Add(key);
            return SceneReading.Unavailable;
        }

        public IReadOnlyList<double> History(string key)
        {
            asked.Add(key);
            return [];
        }
    }
}
