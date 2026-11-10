using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Mcp.Tools;
using InfoPanel.Spec;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;
using Engine = InfoPanel.Tests.Relay.RelayToolRulesTests.Engine;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// <c>add_items</c> (docs/2.0/mcp.md; docs/2.0/relay.md, "Designs"): up to 50 items in one change, each as
/// <c>add_item</c> would add it, in one commit and one undo step, all or nothing; from elsewhere, <c>designs</c> brings a
/// design per item in order, <c>null</c> for an item given as a scene, every design checked before anything is stored.
/// </summary>
public sealed class RelayAddItemsTests
{
    private const string Package = """{"package":"example.widget","version":"1.0.0","note":"a design the service produced"}""";
    private const string Second = """{"package":"example.widget.part","version":"1.0.0"}""";

    private static string IdOf(string text) => BlobStore.IdOf(Encoding.UTF8.GetBytes(text));

    private static Workspace WorkspaceOf(EngineFactory factory) => factory.Services.GetRequiredService<Workspace>();

    private static ProfileDocument P(Workspace workspace) => workspace.Find(workspace.List().Single(e => e.Name == "P").Id).Document!;

    /// <summary><paramref name="count"/> scene items, named <c>Item 0</c> on, side by side.</summary>
    private static JsonArray Scenes(int count) =>
        new([.. Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject { ["scene"] = TestDocs.Rect(), ["x"] = i * 4, ["name"] = $"Item {i}" })]);

    private static JsonObject AddItems(RelayRig rig, string id, JsonArray items, JsonArray? designs = null, IEnumerable<string>? files = null)
    {
        var call = rig.Call(id, "add_items", new JsonObject { ["profile"] = "P", ["items"] = items });
        if (designs is not null)
            call["designs"] = designs;
        if (files is not null)
            call["files"] = new JsonArray([.. files.Select(f => (JsonNode)JsonValue.Create(f))]);
        return call;
    }

    private static async Task<(string Text, bool IsError)> ResultAsync(RelayPeer peer)
    {
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    private static List<string> Names(ProfileDocument document) =>
        [.. ItemNames.Flatten(document.Read().Document.Items).Select(i => i.Name ?? "").Where(n => n.StartsWith("Item ", StringComparison.Ordinal))];

    [Fact]
    public async Task FiftyItems_FromThisComputer_AreAddedInOneCommit_AndOneUndoStepTakesThemAllBack()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        var rev = document.Rev;

        using var json = JsonDocument.Parse(new JsonObject { ["profile"] = "P", ["items"] = Scenes(50) }.ToJsonString());
        var result = await engine.Host.CallAsync("add_items", json.RootElement.Clone(), Ct);
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.False(result["isError"]!.GetValue<bool>(), text);
        Assert.Contains("Added 50 items", text);

        // One commit: the 50, in order, each as add_item adds one (a new id, the place and name given).
        Assert.Equal(rev + 1, document.Rev);
        Assert.Equal(Enumerable.Range(0, 50).Select(i => $"Item {i}"), Names(document));
        var added = document.Read().Document.Items.Where(i => i.Name?.StartsWith("Item ", StringComparison.Ordinal) == true).ToList();
        Assert.All(added, i => Assert.StartsWith("ai-", i.Id));
        Assert.Equal(50, added.Select(i => i.Id).Distinct().Count());
        Assert.Equal(49 * 4d, added[^1].Placement.X);

        // One undo step takes all 50 back.
        Assert.NotNull(document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev }).Result);
        Assert.Empty(Names(document));
    }

    [Fact]
    public async Task TheFiftyFirst_IsRefused_AndNothingIsAdded()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var written = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        await peer.SendAsync(AddItems(rig, "many", Scenes(51)));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(arguments)", text);
        Assert.Contains("50", text);
        Assert.Equal(written, engine.Written());

        // None at all is refused too.
        await peer.SendAsync(AddItems(rig, "none", []));
        (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(arguments)", text);
        Assert.Equal(written, engine.Written());
    }

    public static TheoryData<string> BadItems() => ["engine", "arguments", "group", "design"];

    /// <summary>
    /// One bad item among ten refuses the call whole: the engine refusing its scene (the problem's path names the item),
    /// arguments it doesn't take, a group that isn't there, or a design whose dep is missing beside another design's good
    /// file. Nothing is added and no file is stored.
    /// </summary>
    [Theory]
    [MemberData(nameof(BadItems))]
    public async Task OneBadItem_RefusesThemAll_NothingAddedOrStored(string which)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var written = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var items = Scenes(10);
        var bad = items[3]!.AsObject();
        JsonArray? designs = null;
        string[]? files = null;
        switch (which)
        {
            case "engine":
                bad["scene"] = TestDocs.InvalidScene();
                break;
            case "arguments":
                bad["x"] = 1e9;
                break;
            case "group":
                bad["group"] = "No such group";
                break;
            case "design":
                // Item 0 brings a design with its file; item 3's design needs a dep that is neither among the files nor stored.
                items[0]!.AsObject().Remove("scene");
                bad.Remove("scene");
                designs = [RelayDesignTests.Design(IdOf(Package)), null, null, RelayDesignTests.Design(IdOf(Second)), null, null, null, null, null, null];
                files = [Package];
                break;
        }

        await peer.SendAsync(AddItems(rig, "bad", items, designs, files));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, $"{which}: {text}");
        Assert.Contains(which switch
        {
            "engine" => "items/3/scene/nodes/0/w",
            "arguments" => "item 3",
            "group" => "(notFound)",
            _ => "(missingDep)",
        }, text);
        Assert.Equal(written, engine.Written());
        Assert.False(workspace.Blobs.Exists(IdOf(Package)), "a file was stored");
    }

    /// <summary>
    /// An item given as a scene that shows a web image, beside another item's design and its file, refuses the call
    /// (<c>webImage</c>) before anything is stored: the design's file isn't stored and nothing is added.
    /// </summary>
    [Fact]
    public async Task AWebImageBesideADesign_IsRefused_BeforeTheDesignsFileIsStored()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var written = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        JsonArray items =
        [
            new JsonObject { ["name"] = "Item 0" },
            new JsonObject { ["name"] = "Item 1", ["scene"] = RelayToolRulesTests.WebImageScene("http://192.168.1.10/cam.jpg") },
        ];
        await peer.SendAsync(AddItems(rig, "mixed", items, [RelayDesignTests.Design(IdOf(Package)), null], [Package]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(webImage)", text);
        Assert.False(workspace.Blobs.Exists(IdOf(Package)), "a file was stored");
        Assert.Equal(written, engine.Written());
    }

    /// <summary>
    /// From elsewhere, <c>designs</c> holds one design per item in order, <c>null</c> for an item given as a scene: the
    /// designs' items are inserted as <c>add_item</c> inserts one (the design's box, scene, source and origin), the scene
    /// item as given, all in one commit, with every design's files stored under their hashes.
    /// </summary>
    [Fact]
    public async Task Designs_OnePerItem_NullForASceneItem_AreInsertedInOneCommit()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        JsonArray items =
        [
            new JsonObject { ["name"] = "Item 0", ["x"] = 5, ["y"] = 6 },
            new JsonObject { ["name"] = "Item 1", ["scene"] = TestDocs.Rect() },
            new JsonObject { ["name"] = "Item 2", ["x"] = 50, ["bindings"] = new JsonObject { ["v"] = "infopanel:/mock/cpu/cpu-load" } },
        ];
        var first = RelayDesignTests.Design(IdOf(Package));
        var third = RelayDesignTests.Design(IdOf(Package), IdOf(Second));
        await peer.SendAsync(AddItems(rig, "d", items, [first.DeepClone(), null, third.DeepClone()], [Package, Second]));
        var (text, error) = await ResultAsync(peer);
        Assert.False(error, text);

        Assert.Equal(rev + 1, document.Rev);
        Assert.Equal(["Item 0", "Item 1", "Item 2"], Names(document));
        var all = document.Read().Document.Items;
        JsonNode Stored(string name) => JsonSerializer.SerializeToNode(all.Single(i => i.Name == name), SpecJson.Options)!;
        foreach (var (name, design) in new[] { ("Item 0", first), ("Item 2", third) })
        {
            var stored = Stored(name);
            Assert.True(JsonNode.DeepEquals(design["source"], stored["source"]), $"{name}: {stored["source"]?.ToJsonString()}");
            Assert.True(JsonNode.DeepEquals(design["layout"], stored["layout"]), $"{name}: {stored["layout"]?.ToJsonString()}");
            Assert.True(JsonNode.DeepEquals(design["origin"], stored["origin"]), $"{name}: {stored["origin"]?.ToJsonString()}");
            var item = Assert.IsType<SceneItem>(all.Single(i => i.Name == name));
            Assert.True(JsonNode.DeepEquals(design["scene"], JsonNode.Parse(item.Scene.GetRawText())), $"{name}: {item.Scene.GetRawText()}");
        }
        Assert.Equal((5d, 6d), (all.Single(i => i.Name == "Item 0").Placement.X, all.Single(i => i.Name == "Item 0").Placement.Y));
        Assert.Equal("infopanel:/mock/cpu/cpu-load", Assert.IsType<SceneItem>(all.Single(i => i.Name == "Item 2")).Bindings!["v"].Key);
        Assert.Null(Stored("Item 1")["source"]);
        var raw = Assert.IsType<SceneItem>(all.Single(i => i.Name == "Item 1"));
        Assert.True(JsonNode.DeepEquals(TestDocs.Rect(), JsonNode.Parse(raw.Scene.GetRawText())), raw.Scene.GetRawText());
        Assert.Equal(Package, Encoding.UTF8.GetString(workspace.Blobs.ReadVerified(IdOf(Package))!));
        Assert.Equal(Second, Encoding.UTF8.GetString(workspace.Blobs.ReadVerified(IdOf(Second))!));
    }

    /// <summary>
    /// A group entry's children take designs too: one per entry and per child in order, a group's children right after it,
    /// <c>null</c> for the group and for an item given as a scene. Each child's design lands on it, inside the group, in
    /// one commit.
    /// </summary>
    [Fact]
    public async Task Designs_ForAGroupsChildren_FollowTheGroup_InOrder_AndLandOnEachChild()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        JsonArray items =
        [
            new JsonObject { ["name"] = "Item 0", ["scene"] = TestDocs.Rect() },
            new JsonObject
            {
                ["name"] = "Item 1",
                ["x"] = 20,
                ["y"] = 30,
                ["children"] = new JsonArray(
                    new JsonObject { ["name"] = "Item 1.0", ["x"] = 1 },
                    new JsonObject { ["name"] = "Item 1.1", ["scene"] = TestDocs.Rect() },
                    new JsonObject { ["name"] = "Item 1.2", ["x"] = 3, ["bindings"] = new JsonObject { ["v"] = "infopanel:/mock/cpu/cpu-load" } }),
            },
            new JsonObject { ["name"] = "Item 2" },
        ];
        var first = RelayDesignTests.Design(IdOf(Package));
        var third = RelayDesignTests.Design(IdOf(Package), IdOf(Second));
        third["layout"] = new JsonObject { ["w"] = 33, ["h"] = 11 };
        var last = RelayDesignTests.Design(IdOf(Package));
        last["layout"] = new JsonObject { ["w"] = 77, ["h"] = 7 };
        await peer.SendAsync(AddItems(rig, "g", items, [null, null, first.DeepClone(), null, third.DeepClone(), last.DeepClone()], [Package, Second]));
        var (text, error) = await ResultAsync(peer);
        Assert.False(error, text);

        Assert.Equal(rev + 1, document.Rev);
        Assert.Equal(["Item 0", "Item 1", "Item 1.0", "Item 1.1", "Item 1.2", "Item 2"], Names(document));
        var group = Assert.IsType<GroupItem>(document.Read().Document.Items.Single(i => i.Name == "Item 1"));
        Assert.Equal((20d, 30d), (group.Placement.X, group.Placement.Y));
        JsonNode Stored(Item item) => JsonSerializer.SerializeToNode(item, SpecJson.Options)!;
        foreach (var (child, design) in new[] { (group.Children[0], first), (group.Children[2], third) })
        {
            Assert.True(JsonNode.DeepEquals(design["source"], Stored(child)["source"]), $"{child.Name}: {Stored(child)["source"]?.ToJsonString()}");
            Assert.True(JsonNode.DeepEquals(design["layout"], Stored(child)["layout"]), $"{child.Name}: {Stored(child)["layout"]?.ToJsonString()}");
        }
        Assert.Null(Stored(group.Children[1])["source"]);
        Assert.Equal("infopanel:/mock/cpu/cpu-load", Assert.IsType<SceneItem>(group.Children[2]).Bindings!["v"].Key);
        Assert.True(JsonNode.DeepEquals(last["layout"], Stored(document.Read().Document.Items.Single(i => i.Name == "Item 2"))["layout"]));
        Assert.True(workspace.Blobs.Exists(IdOf(Second)));
    }

    /// <summary>
    /// A design for a group refuses the call (<c>design</c>): a group has no scene. Designs counted without a group's
    /// children don't match the items: the frame is <c>malformed</c>. Nothing is stored or changed.
    /// </summary>
    [Fact]
    public async Task ADesignForAGroup_OrDesignsNotCountingItsChildren_AreRefused_NothingStored()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var written = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        JsonArray Items() => [new JsonObject { ["name"] = "Item 0", ["children"] = new JsonArray(new JsonObject { ["name"] = "Item 0.0" }) }];
        await peer.SendAsync(AddItems(rig, "group", Items(), [RelayDesignTests.Design(IdOf(Package)), RelayDesignTests.Design(IdOf(Package))], [Package]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(design)", text);
        Assert.Contains("item 0, a group", text);

        await peer.SendAsync(AddItems(rig, "short", Items(), [RelayDesignTests.Design(IdOf(Package))], [Package]));
        Assert.Equal("""{"type":"error","code":"malformed","id":"short"}""", (await peer.NextAsync(Ct)).ToJsonString());

        Assert.False(workspace.Blobs.Exists(IdOf(Package)));
        Assert.Equal(written, engine.Written());
    }

    /// <summary>
    /// <c>designs</c> belongs to <c>add_items</c> and <c>design</c> to <c>add_item</c> and <c>update_item</c>: either on
    /// the other's tool is an error result, <c>design</c>, nothing stored or changed.
    /// </summary>
    [Fact]
    public async Task ADesignOnAddItems_OrDesignsOnAnotherTool_IsRefused_StoringNothing()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var written = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var single = AddItems(rig, "one", [new JsonObject { ["name"] = "Item 0" }]);
        single["design"] = RelayDesignTests.Design(IdOf(Package));
        single["files"] = new JsonArray(Package);
        await peer.SendAsync(single);
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(design)", text);

        // designs without items to match is the frame's own mistake (malformed); with them, the tool host refuses it.
        var other = rig.Call("other", "add_item", new JsonObject { ["profile"] = "P", ["name"] = "Item 0" });
        other["designs"] = new JsonArray(RelayDesignTests.Design(IdOf(Package)));
        other["files"] = new JsonArray(Package);
        await peer.SendAsync(other);
        Assert.Equal("""{"type":"error","code":"malformed","id":"other"}""", (await peer.NextAsync(Ct)).ToJsonString());
        other = rig.Call("other", "add_item", new JsonObject { ["profile"] = "P", ["name"] = "Item 0", ["items"] = new JsonArray(new JsonObject()) });
        other["designs"] = new JsonArray(RelayDesignTests.Design(IdOf(Package)));
        other["files"] = new JsonArray(Package);
        await peer.SendAsync(other);
        (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(design)", text);

        Assert.False(workspace.Blobs.Exists(IdOf(Package)));
        Assert.Equal(written, engine.Written());
    }

    /// <summary>On this computer, <c>designs</c> isn't an argument: the local channel's calls carry only arguments.</summary>
    [Fact]
    public async Task DesignsAmongTheArgumentsFromThisComputer_AreRefused()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var written = engine.Written();
        var given = new JsonObject
        {
            ["profile"] = "P",
            ["items"] = new JsonArray(new JsonObject { ["name"] = "Item 0" }),
            ["designs"] = new JsonArray(RelayDesignTests.Design(IdOf(Package))),
        };
        using var json = JsonDocument.Parse(given.ToJsonString());
        var result = await engine.Host.CallAsync("add_items", json.RootElement.Clone(), Ct);
        Assert.True(result["isError"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Contains("(arguments)", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(written, engine.Written());
    }
}
