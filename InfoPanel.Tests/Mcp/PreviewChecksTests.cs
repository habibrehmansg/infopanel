using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Images;
using InfoPanel.Mcp.Tools;
using InfoPanel.Output;
using InfoPanel.Scenes;
using InfoPanel.Service;
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
                ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = "infopanel:/macos/cpu/load" } },
            })));
        Assert.Equal("", LineAbout(checks, "CPU card"));
        Assert.Equal("", LineAbout(checks, "CPU busy ring"));
    }

    /// <summary>
    /// A ring drawn as an SVG path: a circle of radius 48 about (50, 50) stroked 4 wide (default miter join), its stroke
    /// just touching (0, 0)–(100, 100), in a group with <paramref name="transform"/>.
    /// </summary>
    private static JsonObject RingIn(string transform) => JsonNode.Parse($$$"""
        {"kind": "group", "transform": {{{transform}}}, "children": [
          {"kind": "path", "d": "M2 50A48 48 0 1 1 98 50A48 48 0 1 1 2 50Z", "stroke": {"paint": "#ffffff", "width": 4}}]}
        """)!.AsObject();

    /// <summary>
    /// A ring scaled 1.2 at the canvas's left edge, its stroke touching its 120 × 120 box: not past the canvas (a miter
    /// reach all round the smooth ring once put it about 7 px past the left edge).
    /// </summary>
    [Fact]
    public async Task AScaledRingTouchingTheCanvasEdge_IsntPastIt()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 300, "#000000",
            TestDocs.Scene("ring", 0, 0, Scene(RingIn("""[{"type": "scale", "x": 1.2, "y": 1.2}]""")),
                new JsonObject { ["name"] = "Ring", ["layout"] = new JsonObject { ["w"] = 120, ["h"] = 120 } })));
        Assert.Equal("", LineAbout(checks, "Ring"));
        Assert.Contains("The checks found nothing", checks);
    }

    /// <summary>
    /// A card drawn first, a ring scaled 1.2 and turned 135° set 4 px inside its edges, and a header text above the card:
    /// the card holds the ring and the ring doesn't reach the header (its turned box, boxed again, once reached both).
    /// </summary>
    [Fact]
    public async Task ATurnedRingInsideItsCard_IsHeld_AndDoesntReachTheHeaderAbove()
    {
        using var factory = new EngineFactory();
        var checks = await Checks(factory, Doc(400, 300, "#000000",
            Item("Card", 40, 60, Rect(240, 140, "#202020")),
            TestDocs.Scene("ring", 44, 64, Scene(RingIn("""[{"type": "scale", "x": 1.2, "y": 1.2}, {"type": "rotate", "angle": 135, "cx": 50, "cy": 50}]""")),
                new JsonObject { ["name"] = "Ring" }),
            Item("Header", 44, 20, Text("CPU", "#ffffff"))));
        Assert.Equal("", LineAbout(checks, "Card"));
        Assert.Equal("", LineAbout(checks, "Ring"));
        Assert.Equal("", LineAbout(checks, "Header"));
        Assert.Contains("The checks found nothing", checks);
    }

    /// <summary>
    /// A chip set on its card's top edge, half above it: the same overlap whether the two are at the top level or in one
    /// group (the group at the card's place, their x, y from it). Grouping items doesn't clear what their drawings do.
    /// </summary>
    [Fact]
    public async Task AChipStraddlingItsCardsEdge_IsTheSameOverlap_FlatOrInAGroup()
    {
        using var factory = new EngineFactory();
        JsonObject Card(double x, double y) => Item("Card", x, y, Rect(200, 100, "#202020"));
        JsonObject Chip(double x, double y) => Item("Chip", x, y, Rect(60, 20, "#ff9fd0"));
        var flat = await Checks(factory, Doc(600, 300, "#000000", Card(100, 100), Chip(120, 90)));
        var pane = TestDocs.Group("pane", Card(0, 0), Chip(20, -10));
        pane["name"] = "Pane";
        pane["placement"] = new JsonObject { ["x"] = 100, ["y"] = 100 };
        var grouped = await Checks(factory, Doc(600, 300, "#000000", pane));

        var line = LineAbout(flat, "Card");
        Assert.Contains("\"Chip\"", line);
        Assert.Contains("50 % of the smaller", line);
        Assert.Equal(line, LineAbout(grouped, "Card"));
        Assert.Equal("", LineAbout(grouped, "Pane"));
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

    [Fact]
    public async Task FindingsOfOneKindOnOneItem_AreOneLine_WithTheirCount()
    {
        using var factory = new EngineFactory();
        // Twelve labels each drawn past a box too narrow for it, in each of two items.
        JsonObject[] Labels(string prefix) => [.. Enumerable.Range(0, 12).Select(i =>
            Text($"{prefix}{i * 10}", "#ffffff", more: new JsonObject { ["x"] = i * 30, ["y"] = 0, ["w"] = 6 }))];
        static JsonObject Gradient(string text, double x) => Text(text, "#000000", 40, 800, new JsonObject
        {
            ["x"] = x,
            ["fill"] = new JsonObject
            {
                ["type"] = "linear", ["x1"] = 0, ["y1"] = 0, ["x2"] = 100, ["y2"] = 0,
                ["stops"] = new JsonArray(new JsonObject { ["at"] = 0, ["color"] = "#ff0000" }, new JsonObject { ["at"] = 1, ["color"] = "#0000ff" }),
            },
        });
        var checks = await Checks(factory, Doc(800, 600, "#000000",
            Item("Gauge", 10, 10, Labels("")),
            Item("Dial", 10, 60, Labels("d")),
            // Contrasts that differ, the lowest (#101010) neither first nor last.
            Item("Dim", 10, 120, [.. new[] { "#303030", "#202020", "#101010", "#282828", "#202020" }.Select((fill, i) =>
                Text(((char)('A' + i)).ToString(), fill, 40, 800, new JsonObject { ["x"] = i * 60 }))]),
            Item("Rainbow", 10, 200, Gradient("a", 0), Gradient("b", 100), Gradient("c", 200)),
            Item("Single", 10, 300, Text("Alone in a box", "#ffffff", more: new JsonObject { ["w"] = 20, ["overflow"] = "ellipsis" })),
            // Two kinds on one item: two texts drawn past their boxes, two that end with "…".
            Item("Mixed", 10, 400,
                Text("m1", "#ffffff", more: new JsonObject { ["w"] = 6 }),
                Text("m2", "#ffffff", more: new JsonObject { ["x"] = 40, ["w"] = 6 }),
                Text("Much too long", "#ffffff", more: new JsonObject { ["x"] = 100, ["w"] = 20, ["overflow"] = "ellipsis" }),
                Text("Far too long", "#ffffff", more: new JsonObject { ["x"] = 200, ["w"] = 20, ["overflow"] = "ellipsis" })),
            // Four items each straddling the bottom edge of one drawn before them: half of each in its box. Of those, P0
            // is drawn below P1 and P2, which overlap it and each other.
            Item("Wide", 400, 300, Rect(200, 40, "#404040")),
            Item("P0", 400, 320, Rect(30, 40, "#808080")),
            Item("P1", 415, 320, Rect(30, 40, "#808080")),
            Item("P2", 422, 320, Rect(30, 40, "#808080")),
            Item("P3", 550, 320, Rect(30, 40, "#808080"))));
        var lines = checks.Split('\n');

        // One line per item and kind, saying the count and quoting three.
        var gauge = Assert.Single(lines, l => l.Contains("\"Gauge\""));
        Assert.Contains("\"Gauge\" (gauge): 12 texts are drawn past their boxes (\"0\", \"10\", \"20\" and 9 more). Make the box larger or the text smaller.", gauge);
        var dial = Assert.Single(lines, l => l.Contains("\"Dial\""));
        Assert.Contains("12 texts are drawn past their boxes", dial);
        Assert.Contains("(\"d0\", \"d10\", \"d20\" and 9 more)", dial);
        var dim = Assert.Single(lines, l => l.Contains("\"Dim\""));
        Assert.Contains("5 texts (\"A\", \"B\", \"C\" and 2 more) have contrast as low as 1.1:1 against what is under them (#101010 on #000000)", dim);
        var rainbow = Assert.Single(lines, l => l.Contains("\"Rainbow\""));
        Assert.Contains("3 texts \"a\", \"b\" and \"c\" (filled with a gradient or a composed paint: their contrast isn't checked)", rainbow);
        Assert.True(checks.IndexOf(rainbow, StringComparison.Ordinal) > checks.IndexOf("Not checked", StringComparison.Ordinal), checks);

        // Overlaps grouped by the item drawn below, the upper items' own overlaps on their own lines.
        Assert.Contains("\"P0\" (p0) and 2 items overlap (their boxes; the drawn shapes may not): \"P1\" (p1) 50 % and \"P2\" (p2) 27 % of the smaller.",
            Assert.Single(lines, l => l.Contains("\"P0\" (p0) and")));
        Assert.Contains("\"P1\" (p1) and \"P2\" (p2) overlap (their boxes; the drawn shapes may not): 77 % of the smaller.",
            Assert.Single(lines, l => l.Contains("\"P1\" (p1) and")));
        Assert.DoesNotContain(lines, l => l.Contains("\"P2\" (p2) and") || l.Contains("\"P3\" (p3) and"));
        var wide = Assert.Single(lines, l => l.Contains("\"Wide\""));
        Assert.Contains("\"Wide\" (wide) and 4 items overlap (their boxes; the drawn shapes may not): \"P0\" (p0) 50 %, \"P1\" (p1) 50 %, \"P2\" (p2) 50 % and 1 more of the smaller.", wide);

        // One line per kind on one item.
        var mixed = lines.Where(l => l.Contains("\"Mixed\"")).ToList();
        Assert.Equal(2, mixed.Count);
        Assert.Contains(mixed, l => l.Contains("\"Mixed\" (mixed): 2 texts are drawn past their boxes (\"m1\" and \"m2\"). Make the box larger or the text smaller."));
        Assert.Contains(mixed, l => l.Contains("\"Mixed\" (mixed): 2 texts end with \"…\": they don't fit their boxes (\"Much too long\" and \"Far too long\"). Make the box wider or the text smaller."));

        // A single finding keeps its own line.
        Assert.Contains("its text \"Alone in a box\" ends with \"…\": it doesn't fit its box. Make the box wider or the text smaller.",
            Assert.Single(lines, l => l.Contains("\"Single\"")));
        Assert.Equal(40, global::InfoPanel.Mcp.PreviewCheckReport.MaxLinesShown);
    }

    /// <summary>A busy profile: sixty small rectangles of many colours, so its picture is several kilobytes.</summary>
    private static JsonObject Busy(string name) => TestDocs.Doc(name, 400, 200, [.. Enumerable.Range(0, 60).Select(i =>
        TestDocs.Scene($"r{i}", i * 37 % 380, i * 53 % 180, TestDocs.Rect($"#{i * 40503 & 0xffffff:x6}", 15 + i % 7, 9 + i % 5)))]);

    /// <summary>render_preview through the tool host as a call from elsewhere with <paramref name="maxChars"/> (null: none).</summary>
    private static async Task<ToolResult> Run(WebApplicationFactory<Program> factory, string arguments, int? maxChars)
    {
        using var json = JsonDocument.Parse(arguments);
        using var cutoff = new RelayCutoff(TimeProvider.System);
        var call = McpCall.Elsewhere(new RelayCallContext("e1", maxChars, cutoff));
        return await factory.Services.GetRequiredService<McpToolHost>().RunAsync("render_preview", json.RootElement.Clone(), call, Ct);
    }

    private static string Picture(ToolResult result) => result.Content.Single(c => c!["type"]!.GetValue<string>() == "image")!["data"]!.GetValue<string>();

    private static string Words(ToolResult result) =>
        string.Join("\n", result.Content.Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));

    private static int Characters(ToolResult result) => result.Content.Sum(c => (c!["text"] ?? c["data"])!.GetValue<string>().Length);

    [Fact]
    public async Task APictureOverWhatTheAiAppTakes_IsDrawnNarrower_AndSaysSo()
    {
        using var factory = new EngineFactory();
        TestDocs.Create(factory.Services.GetRequiredService<Workspace>(), Busy("Busy"));

        // Without a limit: the picture as asked.
        var whole = await Run(factory, """{"profile":"Busy","width":400}""", null);
        var picture = Picture(whole);
        using (var png = SKBitmap.Decode(Convert.FromBase64String(picture)))
            Assert.Equal(400, png.Width);
        Assert.StartsWith("Busy: a 400×200 picture of the 400×200 profile.\n", Words(whole));

        // With room for it all: as without a limit.
        var roomy = await Run(factory, """{"profile":"Busy","width":400}""", Characters(whole) + 100);
        Assert.Equal(picture, Picture(roomy));
        Assert.Equal(Words(whole), Words(roomy));

        // Half the picture's room: a picture all the same, narrower, saying so, the whole answer within the limit.
        var maxChars = Words(whole).Length + picture.Length / 2;
        var held = await Run(factory, """{"profile":"Busy","width":400}""", maxChars);
        Assert.False(held.IsError, Words(held));
        Assert.True(Characters(held) <= maxChars, $"{Characters(held)} characters, more than {maxChars}");
        using var smaller = SKBitmap.Decode(Convert.FromBase64String(Picture(held)));
        Assert.InRange(smaller.Width, Thumbnail.MinWidth, 399);
        Assert.StartsWith($"Busy: a {smaller.Width}×{smaller.Height} picture (drawn smaller than the 400 asked, to fit what this AI app takes) of the 400×200 profile.\n",
            Words(held));
        Assert.Contains("Checks (heuristics", Words(held)); // the checks whole

        // No width asked: the profile's own, said as such.
        var own = await Run(factory, """{"profile":"Busy"}""", maxChars);
        Assert.Contains("(drawn smaller than 400 wide, to fit what this AI app takes)", Words(own));
    }

    [Fact]
    public async Task WhenEvenTheLeastWidthDoesntFit_ThePictureIsRefusedAsBefore()
    {
        using var factory = new EngineFactory();
        TestDocs.Create(factory.Services.GetRequiredService<Workspace>(), Busy("Busy"));
        var refused = await Run(factory, """{"profile":"Busy","width":400}""", McpToolHost.TextRoom + 20);
        Assert.True(refused.IsError);
        Assert.Equal("maxChars", refused.Outcome);
        Assert.DoesNotContain(refused.Content, c => c!["type"]!.GetValue<string>() == "image");
        Assert.Contains("Ask for a smaller one: render_preview with a smaller width.", Words(refused));
        Assert.Contains($"({McpToolHost.TextRoom + 20} in all)", Words(refused)); // the limit, named
    }

    /// <summary>
    /// A profile so tall its picture's height is capped: asked 1200 wide it is drawn 100 wide, and a narrower picture is
    /// narrower than that, not than the 1200 asked, so each one drawn again is smaller than the last.
    /// </summary>
    [Fact]
    public async Task APictureUnderTheHeightCap_IsDrawnNarrowerThanTheWidthItWasDrawn()
    {
        using var factory = new EngineFactory();
        TestDocs.Create(factory.Services.GetRequiredService<Workspace>(), TestDocs.Doc("Tall", 400, 16384, [.. Enumerable.Range(0, 120).Select(i =>
            TestDocs.Scene($"r{i}", i * 37 % 300, i * 136, TestDocs.Rect($"#{i * 40503 & 0xffffff:x6}", 40 + i * 13 % 60, 60 + i % 70)))]));

        var whole = await Run(factory, """{"profile":"Tall","width":1200}""", null);
        using (var png = SKBitmap.Decode(Convert.FromBase64String(Picture(whole))))
            Assert.Equal((100, 4096), (png.Width, png.Height));

        // Room for half the picture: a picture this tall and thin gets smaller about as its width does, not its area,
        // so the tries after the first are measured by the width; a few of them fit it.
        var maxChars = Words(whole).Length + Picture(whole).Length / 2;
        var held = await Run(factory, """{"profile":"Tall","width":1200}""", maxChars);
        Assert.False(held.IsError, Words(held));
        Assert.True(Characters(held) <= maxChars, $"{Characters(held)} characters, more than {maxChars}");
        using var smaller = SKBitmap.Decode(Convert.FromBase64String(Picture(held)));
        Assert.InRange(smaller.Width, Thumbnail.MinWidth, 99);
        Assert.StartsWith($"Tall: a {smaller.Width}×{smaller.Height} picture (drawn smaller than the 1200 asked, to fit what this AI app takes) of the 400×16384 profile.\n",
            Words(held));
    }

    /// <summary>A clock that moves on by <paramref name="step"/> each time it is read, from half past a second.</summary>
    private sealed class SteppingTime(TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, 500, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            lock (this)
            {
                var now = _now;
                _now += step;
                return now;
            }
        }
    }

    /// <summary>
    /// The picture drawn narrower is the tick the checks measured: a rectangle moved by the clock's milliseconds, on a
    /// clock that moves 300 ms each time it is read, so a second tick would draw it 60 px or more elsewhere.
    /// </summary>
    [Fact]
    public async Task APictureDrawnNarrower_IsTheTickTheChecksMeasured()
    {
        using var factory = new EngineFactory();
        var workspace = factory.Services.GetRequiredService<Workspace>();
        var moving = new JsonObject
        {
            ["kind"] = "rect",
            ["x"] = new JsonObject { ["op"] = "mul", ["args"] = new JsonArray(new JsonObject { ["op"] = "time", ["field"] = "millisecond" }, -0.2) },
            ["y"] = 0, ["w"] = 300, ["h"] = 50, ["fill"] = "#ff0000",
        };
        TestDocs.Create(workspace, TestDocs.Doc("Moving", 400, 200, [TestDocs.Scene("moving", 0, 0, Scene(moving), new JsonObject { ["name"] = "Moving" })]));
        await using var render = new RenderService(workspace, factory.Services.GetRequiredService<SceneRuntimeOptions>(), new SteppingTime(TimeSpan.FromMilliseconds(300)),
            new RenderServiceOptions(AutoTick: false));
        var tool = new RenderPreviewTool(workspace, render, factory.Services.GetRequiredService<McpToolHost>().Text);

        async Task<ToolResult> Call(int? maxChars)
        {
            using var json = JsonDocument.Parse("""{"profile":"Moving","width":400}""");
            using var cutoff = new RelayCutoff(TimeProvider.System);
            return await tool.CallAsync(ToolArguments.Check(tool.Name, tool.InputSchema, json.RootElement).For(McpCall.Elsewhere(new RelayCallContext("e1", maxChars, cutoff))), Ct);
        }

        var whole = await Call(null);
        var held = await Call(Words(whole).Length + Picture(whole).Length / 2);
        var words = Words(held);
        Assert.Contains("drawn smaller than the 400 asked", words);
        var past = System.Text.RegularExpressions.Regex.Match(words, "\"Moving\" \\(moving\\): its drawing reaches ([0-9.]+) px past the canvas's left edge");
        Assert.True(past.Success, words);
        var left = double.Parse(past.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        // The rectangle's right edge in the picture is where the checks' tick put it: 300 − left, at the picture's scale.
        using var png = SKBitmap.Decode(Convert.FromBase64String(Picture(held)));
        var scale = png.Width / 400.0;
        var row = (int)(25 * scale);
        var right = Enumerable.Range(0, png.Width).Last(x => png.GetPixel(x, row) is { Red: > 200, Green: < 60, Blue: < 60 }) + 1;
        Assert.InRange(right, (300 - left) * scale - 2, (300 - left) * scale + 2);
    }
}
