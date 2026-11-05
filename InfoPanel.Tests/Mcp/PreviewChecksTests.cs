using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Images;
using InfoPanel.Mcp.Tools;
using InfoPanel.Output;
using InfoPanel.Scenes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// <c>render_preview</c>'s four checks on raw-scene profiles: each one tripped and not tripped, what they skip said per
/// item (Codex plan-1 #9), their budgets, and their thresholds read live from the text folder.
/// </summary>
public sealed class PreviewChecksTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Scene(params JsonObject[] nodes) => new()
    {
        ["format"] = "infopanel.scene/1",
        ["nodes"] = new JsonArray([.. nodes]),
    };

    private static JsonObject Rect(double w, double h, string fill, double x = 0, double y = 0) =>
        new() { ["kind"] = "rect", ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h, ["fill"] = fill };

    private static JsonObject Text(string text, string fill, double size = 16, int weight = 400, JsonObject? more = null)
    {
        var node = new JsonObject
        {
            ["kind"] = "text",
            ["x"] = 0,
            ["y"] = 0,
            ["text"] = text,
            ["font"] = new JsonObject { ["family"] = "Inter", ["size"] = size, ["weight"] = weight },
            ["fill"] = fill,
        };
        foreach (var (k, v) in more ?? [])
            node[k] = v?.DeepClone();
        return node;
    }

    private static JsonObject Item(string name, double x, double y, params JsonObject[] nodes) =>
        TestDocs.Scene(name.ToLowerInvariant(), x, y, Scene(nodes), new JsonObject { ["name"] = name });

    private static JsonObject Doc(int width, int height, string background, params JsonObject[] items)
    {
        var doc = TestDocs.Doc("Checks " + Guid.NewGuid().ToString("N")[..6], width, height, items);
        doc["profile"]!["background"] = background;
        return doc;
    }

    /// <summary>The checks' text for <paramref name="document"/>, through the tool (one call: the picture and the checks).</summary>
    private static async Task<string> Checks(WebApplicationFactory<Program> factory, JsonObject document)
    {
        var id = TestDocs.Create(factory.Services.GetRequiredService<Workspace>(), document);
        using var json = JsonDocument.Parse($$"""{"profile":"{{id}}","width":300}""");
        var result = await factory.Services.GetRequiredService<McpToolHost>().CallAsync("render_preview", json.RootElement.Clone(), Ct);
        Assert.False(result["isError"]!.GetValue<bool>());
        var content = result["content"]!.AsArray();
        Assert.Single(content, c => c!["type"]!.GetValue<string>() == "image");
        return string.Join("\n", content.Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
    }

    private static string LineAbout(string checks, string item) =>
        checks.Split('\n').FirstOrDefault(l => l.Contains($"\"{item}\"")) ?? "";

    /// <summary>render_preview of profile <paramref name="id"/>: its text and its picture.</summary>
    private static async Task<(string Text, byte[] Png)> Preview(WebApplicationFactory<Program> factory, string id)
    {
        using var json = JsonDocument.Parse($$"""{"profile":"{{id}}","width":300}""");
        var result = await factory.Services.GetRequiredService<McpToolHost>().CallAsync("render_preview", json.RootElement.Clone(), Ct);
        Assert.False(result["isError"]!.GetValue<bool>());
        var content = result["content"]!.AsArray();
        var png = Convert.FromBase64String(content.Single(c => c!["type"]!.GetValue<string>() == "image")!["data"]!.GetValue<string>());
        return (string.Join("\n", content.Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>())), png);
    }

    /// <summary>A sink that keeps a profile's loop running and ignores its frames.</summary>
    private sealed class Watcher(string profileId) : IFrameSink
    {
        public string ProfileId { get; } = profileId;

        public FrameRequest Request { get; } = new(1, 30, IncludeBounds: false);

        public void OnFrame(RenderedFrame frame)
        {
        }
    }

    /// <summary>The blob store, with every read held until <paramref name="gate"/> opens: media stays loading.</summary>
    private sealed class HeldStore(BlobStore blobs, ManualResetEventSlim gate) : IMediaAssetStore
    {
        private readonly BlobMediaStore _inner = new(blobs);

        public byte[]? Read(string hash)
        {
            gate.Wait();
            return _inner.Read(hash);
        }
    }

    [Fact]
    public async Task TextThatDoesntFitItsBox_IsAProblem_ATextThatFitsIsnt()
    {
        using var factory = new EngineFactory();
        var long_ = "The quick brown fox jumps over the lazy dog";
        var checks = await Checks(factory, Doc(800, 400, "#000000",
            Item("Cut", 10, 10, Text(long_, "#ffffff", more: new JsonObject { ["w"] = 100, ["overflow"] = "ellipsis" })),
            Item("Clipped", 10, 60, Text(long_, "#ffffff", more: new JsonObject { ["w"] = 100, ["h"] = 24, ["overflow"] = "clip" })),
            Item("Shrunk", 10, 110, Text(long_, "#ffffff", more: new JsonObject { ["w"] = 60, ["h"] = 20, ["fit"] = new JsonObject { ["min"] = 12 } })),
            Item("Fine", 10, 160, Text("CPU", "#ffffff", more: new JsonObject { ["w"] = 200 }))));
        Assert.Contains("ends with", LineAbout(checks, "Cut"));
        Assert.Contains("clipped", LineAbout(checks, "Clipped"));
        Assert.Contains("fit minimum", LineAbout(checks, "Shrunk"));
        Assert.Contains("Make the box", LineAbout(checks, "Cut"));
        Assert.Equal("", LineAbout(checks, "Fine"));
    }

    [Fact]
    public async Task InkPastTheCanvas_ByMoreThanTheTolerance_IsAProblem()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 200, "#000000",
            Item("Over", 353, 10, Rect(50, 20, "#ff0000")), // 3 px past the right edge
            Item("Edge", 10, 181, Rect(50, 20, "#00ff00")))); // 1 px past the bottom: within 1 px
        Assert.Contains("3 px past the canvas's right edge", LineAbout(checks, "Over"));
        Assert.Equal("", LineAbout(checks, "Edge"));
    }

    [Fact]
    public async Task OverlappingBoxes_AreAProblem_UnlessTheContainerIsDrawnBelow()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(600, 300, "#000000",
            Item("A", 10, 10, Rect(100, 50, "#ff0000")),
            Item("B", 60, 30, Rect(100, 50, "#00ff00")), // 30 % of the smaller
            Item("Card", 200, 10, Rect(200, 100, "#202020")),
            Item("Content", 220, 30, Rect(50, 20, "#ffffff")), // inside the card, drawn above it
            Item("Under", 20, 120, Rect(40, 20, "#ffffff")),
            Item("Lid", 10, 110, Rect(100, 60, "#303030")), // contains Under, drawn above it
            Item("C", 300, 200, Rect(100, 50, "#ff0000")),
            Item("D", 395, 200, Rect(100, 50, "#00ff00")))); // 5 %: under the 10 % threshold
        var ab = LineAbout(checks, "A");
        Assert.Contains("\"B\"", ab);
        Assert.Contains("their boxes; the drawn shapes may not", ab);
        Assert.Equal("", LineAbout(checks, "Card"));
        Assert.Equal("", LineAbout(checks, "Content"));
        Assert.Contains("\"Lid\"", LineAbout(checks, "Under"));
        Assert.Equal("", LineAbout(checks, "C"));
    }

    /// <summary>
    /// A background item drawn first, its box the whole canvas, whose glyphs don't reach its edges: the items above it
    /// in its box aren't overlaps (it holds them by its box and is drawn below), though its ink box doesn't hold them.
    /// </summary>
    [Fact]
    public async Task ABackgroundDrawnFirst_HoldsTheItemsInItsBox_WhereverItsInkReaches()
    {
        using var factory = new EngineFactory();
        var backdrop = TestDocs.Scene("backdrop", 0, 0, Scene(Rect(360, 140, "#003300", x: 20, y: 30)),
            new JsonObject { ["name"] = "Backdrop", ["layout"] = new JsonObject { ["w"] = 400, ["h"] = 200 } });
        var checks = await Checks(factory, Doc(400, 200, "#000000",
            backdrop, // drawn 20–380 × 30–170
            Item("Status", 0, 160, Rect(400, 40, "#00ff41")), // 22 % of its ink box, past its sides
            Item("Corner", 5, 5, Rect(40, 40, "#ffffff")))); // 23 %, past its top left
        Assert.Equal("", LineAbout(checks, "Backdrop"));
        Assert.Contains("The checks found nothing", checks);
    }

    /// <summary>
    /// An item drawn first whose drawing covers a quarter of its box (not a card or a backdrop) holds the next item's
    /// box in its own, yet their drawings collide (30 % of the smaller): still an overlap (Codex r1 #1).
    /// </summary>
    [Fact]
    public async Task ASparseItemDrawnFirst_HoldingTheOthersBox_StillOverlapsWhereTheyDraw()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(600, 300, "#000000",
            TestDocs.Scene("a", 10, 10, Scene(Rect(100, 50, "#ff0000")),
                new JsonObject { ["name"] = "A", ["layout"] = new JsonObject { ["w"] = 200, ["h"] = 100 } }),
            Item("B", 60, 30, Rect(100, 50, "#00ff00"))));
        Assert.Contains("\"B\"", LineAbout(checks, "A"));
        Assert.Contains("30 % of the smaller", LineAbout(checks, "A"));
    }

    /// <summary>
    /// A ring item (a ring in a group clipped to its box and turned 135°) inside a card drawn first: the card holds it
    /// by their boxes, though the turned square around the ring reaches past the card. Card and ring as reported, but for
    /// the ring's `visible` (shown once its sensor has a reading: a test engine has none yet).
    /// </summary>
    [Fact]
    public async Task ARingInsideACardDrawnFirst_IsntAnOverlap()
    {
        using var factory = new EngineFactory();
        var card = JsonNode.Parse("""
            {"format": "infopanel.scene/1", "nodes": [{"fill": "#251a45ff", "h": 272, "id": "shape", "kind": "rect", "r": 24, "stroke": {"paint": "#f2d58f55", "width": 2}, "w": 245}]}
            """)!.AsObject();
        var ring = JsonNode.Parse("""
            {"format": "infopanel.scene/1", "inputs": {"v": {"label": "Value", "type": "number"}}, "nodes": [{"children": [{"cx": {"args": [{"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, 1], "op": "add"}, "cy": {"args": [{"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, 1], "op": "add"}, "fill": "#b8a1ff38", "inner": {"args": [{"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, {"args": [{"args": [14, 0], "op": "max"}, {"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}], "op": "min"}], "op": "sub"}, "kind": "arc", "r": {"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, "sweep": 270}, {"cx": {"args": [{"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, 1], "op": "add"}, "cy": {"args": [{"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, 1], "op": "add"}, "fill": "#ff9fd0ff", "id": "value", "inner": {"args": [{"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, {"args": [{"args": [14, 0], "op": "max"}, {"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}], "op": "min"}], "op": "sub"}, "kind": "arc", "r": {"args": [{"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, 1], "op": "sub"}, "sweep": {"name": "sweep", "op": "value"}, "visible": {"args": [{"name": "pct", "op": "value"}, 0], "op": "gt"}}], "clip": {"h": 196, "kind": "rect", "w": 196}, "id": "ring", "kind": "group", "transform": [{"angle": 135, "cx": {"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, "cy": {"args": [{"args": [196, 2], "op": "div"}], "op": "floor"}, "type": "rotate"}]}], "state": {"shown": {"init": {"args": [{"args": [{"args": [{"count": 1, "fn": "last", "input": "v", "op": "history"}, 0, 100], "op": "norm"}, 0, 1], "op": "clamp"}, 100], "op": "mul"}, "next": {"args": [{"name": "shown", "op": "state"}, {"args": [{"args": [{"args": [{"args": [{"args": [{"count": 1, "fn": "last", "input": "v", "op": "history"}, 0, 100], "op": "norm"}, 0, 1], "op": "clamp"}, 100], "op": "mul"}, {"name": "shown", "op": "state"}], "op": "sub"}, {"args": [1, {"args": [{"args": [{"args": [{"op": "dt"}], "op": "neg"}, 0.3], "op": "div"}], "op": "exp"}], "op": "sub"}], "op": "mul"}], "op": "add"}}}, "values": {"cnt": {"count": 1, "fn": "count", "input": "v", "op": "history"}, "pct": {"args": [{"args": [{"name": "shown", "op": "state"}], "op": "floor"}, 0, 100], "op": "clamp"}, "sweep": {"args": [{"args": [{"name": "pct", "op": "value"}, 270], "op": "mul"}, 100], "op": "div"}}}
            """)!.AsObject();
        var checks = await Checks(factory, Doc(800, 480, "#160f2b",
            TestDocs.Scene("card", 16, 16, card, new JsonObject { ["name"] = "CPU card", ["layout"] = new JsonObject { ["w"] = 245, ["h"] = 272 } }),
            TestDocs.Scene("ring", 41, 40, ring, new JsonObject
            {
                ["name"] = "CPU busy ring",
                ["layout"] = new JsonObject { ["w"] = 196, ["h"] = 196 },
                ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = "plugin:/infopanel.macos/cpu/load" } },
            })));
        Assert.Equal("", LineAbout(checks, "CPU card"));
        Assert.Equal("", LineAbout(checks, "CPU busy ring"));
    }

    /// <summary>
    /// A glow drawn as a blurred copy of the text under it (12 px: it reaches 36 px) and one drawn as a glow effect:
    /// neither is the text's extent (not past the canvas, not overlapping the items it reaches) nor text to read (the
    /// copy's contrast isn't measured); the text itself is measured against what is under it, and a dim one still fails.
    /// </summary>
    [Fact]
    public async Task AGlow_IsNotTheTextsExtent_NorItsContrast()
    {
        using var factory = new EngineFactory();
        JsonObject Glowing(string text, string fill, string glow) => new()
        {
            ["kind"] = "group",
            ["children"] = new JsonArray(
                new JsonObject
                {
                    ["kind"] = "group",
                    ["effects"] = new JsonArray(new JsonObject { ["type"] = "blur", ["sigma"] = 12 }),
                    ["children"] = new JsonArray(Text(text, glow, 40, 800)),
                },
                Text(text, fill, 40, 800)),
        };
        var checks = await Checks(factory, Doc(600, 300, "#000000",
            Item("Hero", 32, 60, Glowing("87", "#00ff41", "#00ff4180")), // 4 px of glow past the left edge
            Item("Aura", 300, 60, new JsonObject
            {
                ["kind"] = "group",
                ["effects"] = new JsonArray(new JsonObject { ["type"] = "glow", ["sigma"] = 12, ["color"] = "#00ff41" }),
                ["children"] = new JsonArray(Text("87", "#00ff41", 40, 800)),
            }),
            Item("Bar", 0, 30, Rect(600, 20, "#00ff41")), // 10 px above both texts: in their glows
            Item("Dim", 32, 200, Glowing("87", "#333333", "#ffffff80"))));
        Assert.Equal("", LineAbout(checks, "Hero"));
        Assert.Equal("", LineAbout(checks, "Aura"));
        var dim = LineAbout(checks, "Dim");
        Assert.Contains("contrast", dim);
        Assert.Contains("#333333 on #000000", dim);
        Assert.Single(checks.Split('\n'), l => l.Contains("\"Dim\""));
    }

    /// <summary>
    /// A lone label in a blurred group, with no sharp copy over it, is text to read: its contrast is still measured
    /// (Codex r1 #2: about 1.29:1 on black), and so is one whose sharp copy is drawn elsewhere; a shadow drawn as a
    /// blurred copy a few pixels off, under its sharp text, isn't.
    /// </summary>
    [Fact]
    public async Task ABlurredLabel_WithNoSharpCopyOverIt_KeepsItsContrastCheck()
    {
        using var factory = new EngineFactory();
        JsonObject Blurred(JsonObject text) => new()
        {
            ["kind"] = "group",
            ["effects"] = new JsonArray(new JsonObject { ["type"] = "blur", ["sigma"] = 0.1 }),
            ["children"] = new JsonArray(text),
        };
        var checks = await Checks(factory, Doc(400, 300, "#000000",
            Item("Soft", 20, 20, Blurred(Text("CPU", "#202020", 24))),
            Item("Apart", 20, 80, Blurred(Text("CPU", "#202020", 24)), Text("CPU", "#ffffff", 24, more: new JsonObject { ["y"] = 40 })),
            Item("Shadowed", 20, 200, Blurred(Text("CPU", "#000000", 24, more: new JsonObject { ["x"] = 3, ["y"] = 3 })), Text("CPU", "#ffffff", 24))));
        var soft = LineAbout(checks, "Soft");
        Assert.Contains("contrast", soft);
        Assert.Contains("#202020 on #000000", soft);
        Assert.Contains("#202020 on #000000", LineAbout(checks, "Apart"));
        Assert.Equal("", LineAbout(checks, "Shadowed"));
    }

    /// <summary>
    /// A dim blurred label with a sharp white copy of it drawn 40 px to its right, their lines crossing: the copy isn't
    /// over it (a shadow sits a few pixels off, not 40), so the label is text to read, about 1.29:1 on black (Codex r2 #2).
    /// </summary>
    [Fact]
    public async Task ABlurredLabel_WithItsSharpCopyFarOff_KeepsItsContrastCheck()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 200, "#000000",
            Item("Shifted", 20, 20, new JsonObject
            {
                ["kind"] = "group",
                ["effects"] = new JsonArray(new JsonObject { ["type"] = "blur", ["sigma"] = 0.1 }),
                ["children"] = new JsonArray(Text("CPU", "#202020", 24)),
            }, Text("CPU", "#ffffff", 24, more: new JsonObject { ["x"] = 40 }))));
        var shifted = LineAbout(checks, "Shifted");
        Assert.Contains("1.3:1", shifted);
        Assert.Contains("#202020 on #000000", shifted);
    }

    [Fact]
    public async Task LowContrast_ComposedWithOpacity_AgainstWhatIsUnderTheTextWithoutText()
    {
        using var factory = new EngineFactory();
        var stripes = Enumerable.Range(0, 70).Select(i => Rect(3, 80, "#000000", x: i * 5)).ToArray();
        var checks = await Checks(factory, Doc(800, 600, "#ffffff",
            Item("Grey3", 10, 10, Text("MMMM", "#949494", 40, 800)), // 3:1 on white
            Item("Grey7", 10, 80, Text("MMMM", "#595959", 40, 800)), // 7:1 on white: its own glyphs aren't the background
            Item("Faded", 10, 150, new JsonObject
            {
                ["kind"] = "group",
                ["opacity"] = 0.3,
                ["children"] = new JsonArray(Text("MMMM", "#000000", 40, 800)), // black at 30 %: about 2:1
            }),
            Item("Striped", 400, 10, [.. stripes, Text("MMMM", "#ffffff", 40, 800)]), // black 60 % passes, light grey 40 % fails
            Item("Rainbow", 400, 150, Text("MMMM", "#000000", 40, 800, new JsonObject
            {
                ["fill"] = new JsonObject
                {
                    ["type"] = "linear", ["x1"] = 0, ["y1"] = 0, ["x2"] = 100, ["y2"] = 0,
                    ["stops"] = new JsonArray(new JsonObject { ["at"] = 0, ["color"] = "#ff0000" }, new JsonObject { ["at"] = 1, ["color"] = "#0000ff" }),
                },
            }))));
        Assert.Contains("contrast", LineAbout(checks, "Grey3"));
        Assert.Equal("", LineAbout(checks, "Grey7"));
        Assert.Contains("contrast", LineAbout(checks, "Faded"));
        Assert.Contains("contrast", LineAbout(checks, "Striped"));
        var rainbow = LineAbout(checks, "Rainbow");
        Assert.Contains("gradient", rainbow);
        Assert.True(checks.IndexOf(rainbow, StringComparison.Ordinal) > checks.IndexOf("Not checked", StringComparison.Ordinal), checks);
    }

    [Fact]
    public async Task WhiteTextOnAWhiteCard_IsOneToOne()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 200, "#000000",
            Item("Card", 10, 10, Rect(300, 80, "#ffffff"), Text("Hello", "#ffffff", 40, 700, new JsonObject { ["x"] = 10, ["y"] = 10 }))));
        Assert.Contains("1:1", LineAbout(checks, "Card"));
    }

    /// <summary>
    /// Codex p2 #4: white text in a multiply group on black is drawn black on black, but its colour and opacity read
    /// 21:1. The checks have no model of a group's blending, so such text is listed as not checked, saying why.
    /// </summary>
    [Fact]
    public async Task TextInAGroupDrawnWithABlendMode_IsNotCheckedForContrast()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 200, "#000000",
            Item("Multiplied", 10, 10, new JsonObject
            {
                ["kind"] = "group",
                ["blend"] = "multiply",
                ["children"] = new JsonArray(Text("MMMM", "#ffffff", 40, 800)),
            }),
            Item("Plain", 10, 100, new JsonObject
            {
                ["kind"] = "group",
                ["children"] = new JsonArray(Text("MMMM", "#202020", 40, 800)),
            })));
        var multiplied = LineAbout(checks, "Multiplied");
        Assert.Contains("blend", multiplied);
        Assert.True(checks.IndexOf(multiplied, StringComparison.Ordinal) > checks.IndexOf("Not checked", StringComparison.Ordinal), checks);
        Assert.Contains("contrast", LineAbout(checks, "Plain")); // a group drawn normally is still checked
    }

    /// <summary>
    /// Codex p2 #5: end-aligned text in a wide box sits at its right end, over a white bar there: white on white, 1:1.
    /// The sampled area is where the lines are drawn, not the box's unused width on the left (black, which passed it).
    /// </summary>
    [Fact]
    public async Task AlignedText_IsSampledWhereItsLinesAre_NotOverItsBoxsUnusedWidth()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 200, "#000000",
            Item("EndAligned", 10, 10, Rect(60, 40, "#ffffff", x: 240), Text("CPU", "#ffffff", 20, 700, new JsonObject { ["w"] = 300, ["align"] = "end" }))));
        Assert.Contains("1:1", LineAbout(checks, "EndAligned"));
    }

    /// <summary>
    /// Codex p2 #5, line by line: centred black text on white whose short second line has black bars on both sides, in
    /// the box around both lines but under neither. Each line is sampled where it is drawn: nothing to report.
    /// </summary>
    [Fact]
    public async Task CentredLines_AreSampledLineByLine_NotOverTheGapsBesideAShortLine()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 200, "#ffffff",
            Item("Centred", 10, 10,
                Rect(120, 24, "#000000", x: 0, y: 24),
                Rect(120, 24, "#000000", x: 180, y: 24),
                Text("WWWWWWWWWWWWWW\nW", "#000000", 20, 400, new JsonObject { ["w"] = 300, ["align"] = "center" }))));
        Assert.Equal("", LineAbout(checks, "Centred"));
    }

    /// <summary>
    /// Codex p2 #6: a change after the live loop's last tick, previewed before its next one. The picture and the checks
    /// come from one generation, the document's newest: an item removed since is in neither, one added since in both.
    /// </summary>
    [Fact]
    public async Task APreviewBeforeTheLiveLoopsNextTick_ShowsAndChecksTheDocumentAsItIsNow()
    {
        using var engine = new EngineFactory();
        using var factory = engine.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new RenderServiceOptions(AutoTick: false))));
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var render = factory.Services.GetRequiredService<RenderService>();
        var id = TestDocs.Create(workspace, Doc(400, 200, "#000000", Item("Gone", 380, 10, Rect(50, 20, "#ff0000"))));
        var document = workspace.Find(id).Document!;
        var sink = new Watcher(id);
        Assert.True(render.Attach(sink, document));
        try
        {
            render.Tick(id); // the live loop's tick: Gone past the right edge
            using (var live = render.LatestSnapshot(id))
                Assert.NotNull(live);
            TestDocs.Commit(document, TestDocs.Remove("gone"),
                TestDocs.Insert(TestDocs.Scene("new", 10, 190, Scene(Rect(50, 20, "#00ff00")), new JsonObject { ["name"] = "New" })));

            var (checks, png) = await Preview(factory, id);
            Assert.Equal("", LineAbout(checks, "Gone"));
            Assert.Contains("past the canvas's bottom edge", LineAbout(checks, "New"));
            using var picture = SKBitmap.Decode(png);
            var scale = picture.Width / 400.0;
            var gone = picture.GetPixel((int)(390 * scale), (int)(20 * scale));
            var added = picture.GetPixel((int)(30 * scale), (int)(195 * scale));
            Assert.False(gone.Red > 200 && gone.Green < 50, $"the removed item is drawn: {gone}");
            Assert.True(added.Green > 200 && added.Red < 50, $"the added item isn't drawn: {added}");
        }
        finally
        {
            render.Detach(sink);
        }
    }

    /// <summary>
    /// Triage p2, suggested: a picture still loading when the preview is drawn (here: its read never finishes) is said
    /// under the checks, so the AI previews again rather than reading the placeholder as its design. No waiting.
    /// </summary>
    [Fact]
    public async Task APictureStillLoading_IsSaidUnderTheChecks()
    {
        using var engine = new EngineFactory();
        var gate = new ManualResetEventSlim(false);
        using var factory = engine.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(sp =>
            new MediaSampleCache(new HeldStore(sp.GetRequiredService<Workspace>().Blobs, gate),
                new MediaSampleCacheOptions { Limits = sp.GetRequiredService<Workspace>().Limits }))));
        try
        {
            var workspace = factory.Services.GetRequiredService<Workspace>();
            var svg = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"><circle cx="10" cy="10" r="9" fill="#00aaff"/></svg>""");
            var asset = workspace.PutBlob(svg).Info.Id;
            var checks = await Checks(factory, Doc(400, 200, "#000000",
                Item("Logo", 10, 10, new JsonObject { ["kind"] = "media", ["w"] = 20, ["h"] = 20, ["source"] = new JsonObject { ["asset"] = asset } })));
            Assert.Contains("\"Logo\" (logo): its picture is still loading; preview again", checks);
            Assert.True(checks.IndexOf("still loading", StringComparison.Ordinal) > checks.IndexOf("Checks", StringComparison.Ordinal), checks);
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// Codex p2-2 #2: a preview goes through the process budget as a live tick does. With the budget exhausted, the live
    /// loop draws the item as a budget placeholder; a change after its last tick, previewed before its next one, is
    /// still drawn as one (the picture and the checks of the newest generation, as row 6 keeps), and so is a preview
    /// with no loop.
    /// </summary>
    [Fact]
    public async Task OverTheProcessBudget_APreviewBeforeTheLiveLoopsNextTick_DrawsTheBudgetPlaceholder()
    {
        var budget = new SceneProcessBudget(0); // exhausted: nothing more is admitted
        using var engine = new EngineFactory();
        using var factory = engine.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddSingleton(new RenderServiceOptions(AutoTick: false));
            var original = s.Last(d => d.ServiceType == typeof(SceneRuntimeOptions)).ImplementationFactory!;
            s.AddSingleton(sp =>
            {
                var o = (SceneRuntimeOptions)original(sp);
                return new SceneRuntimeOptions
                {
                    Inputs = o.Inputs, Media = o.Media, Providers = o.Providers, Limits = o.Limits, Support = o.Support,
                    LocalZone = o.LocalZone, Locale = o.Locale, Logger = o.Logger, LayoutsPerTick = o.LayoutsPerTick,
                    ItemBudgetMs = o.ItemBudgetMs, SuspendFor = o.SuspendFor, ProcessBudget = budget,
                };
            });
        }));
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var render = factory.Services.GetRequiredService<RenderService>();
        Assert.Same(budget, render.SceneOptions.ProcessBudget);
        var id = TestDocs.Create(workspace, Doc(400, 200, "#000000", Item("Shown", 10, 10, Rect(50, 20, "#ff0000"))));
        var document = workspace.Find(id).Document!;

        // The budget's verdict on what a preview shows: its codes for the item, and the revision it drew.
        (string[] Codes, long Rev) Shown() => render.Inspect(document, (snapshot, generation) =>
            (snapshot.Diagnostics.Where(d => d.ItemId == "shown").Select(d => d.Code).ToArray(), generation.Rev));
        void AssertPlaceholder(byte[] png)
        {
            using var picture = SKBitmap.Decode(png);
            var scale = picture.Width / 400.0;
            var pixel = picture.GetPixel((int)(30 * scale), (int)(20 * scale));
            Assert.False(pixel.Red > 200 && pixel.Green < 50, $"the item over the budget is drawn: {pixel}");
        }

        // No loop: a preview is admitted as a tick would be.
        AssertPlaceholder((await Preview(factory, id)).Png);
        Assert.Contains("budget.process", Shown().Codes);

        var sink = new Watcher(id);
        Assert.True(render.Attach(sink, document));
        try
        {
            render.Tick(id); // the live loop's tick: the item over the budget
            using (var live = render.LatestSnapshot(id))
                Assert.Contains(live!.Diagnostics, d => d.ItemId == "shown" && d.Code == "budget.process");
            TestDocs.Commit(document, TestDocs.Attrs("shown", name: "Renamed"));

            AssertPlaceholder((await Preview(factory, id)).Png);
            var (codes, rev) = Shown();
            Assert.Equal(document.Current.Rev, rev); // the newest generation, as row 6 keeps
            Assert.Contains("budget.process", codes);
            Assert.Equal(0, budget.Used);
        }
        finally
        {
            render.Detach(sink);
        }
    }

    [Fact]
    public async Task PastThePairAndPixelBudgets_TheRestIsListedAsNotChecked()
    {
        using var factory = new EngineFactory();
        // 150 items: 11,175 pairs, past the 10,000 compared.
        var many = Enumerable.Range(0, 150).Select(i => TestDocs.Scene($"dot{i}", 10 + i % 15 * 20, 10 + i / 15 * 20, Scene(Rect(4, 4, "#ffffff")))).ToArray();
        var pairs = await Checks(factory, Doc(400, 400, "#000000", many));
        Assert.Contains("Not checked", pairs);
        Assert.Contains("10,000 item pairs", pairs);

        // Two texts each drawn over about 2.4 M pixels: the second is past the 4 M sampled.
        var pixels = await Checks(factory, Doc(2000, 2000, "#000000",
            Item("Huge1", 0, 0, Text("WWWW", "#ffffff", 900, 900)),
            Item("Huge2", 0, 900, Text("WWWW", "#ffffff", 900, 900))));
        var lines = pixels.Split('\n');
        Assert.Contains(lines, l => l.Contains("\"Huge2\"") && l.Contains("4,000,000 pixels"));
        Assert.DoesNotContain(lines, l => l.Contains("\"Huge1\"") && l.Contains("4,000,000 pixels"));
    }

    [Fact]
    public async Task ACleanProfile_SaysWhatTheChecksCover_NeverNoProblemsFound()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 200, "#000000", Item("Hello", 10, 10, Text("Hello", "#ffffff", 24))));
        Assert.Contains("The checks found nothing (they cover fit, the canvas edge, box overlap and contrast)", checks);
        Assert.DoesNotContain("No problems found", checks);
        Assert.DoesNotContain("Not checked", checks);
    }
}
