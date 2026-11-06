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
/// <c>update_items</c> (docs/2.0/mcp.md; docs/2.0/relay.md, "Changing items"): 1 to 50 entries, each as <c>update_item</c>
/// changes one (without <c>profile</c>), in one commit and one undo step, all or nothing, an entry's refusal named
/// <c>items/‹i›</c>. From elsewhere an entry may bring a design (<c>designs</c>, with its own <c>basedOn</c>) or, without
/// one, what a read found (the call frame's <c>basedOn</c> list): the op that sets that item's design is based on
/// <c>basedOn.rev</c>, a conflict on it <c>stale</c>.
/// </summary>
public sealed class RelayUpdateItemsTests
{
    private const string Package = """{"package":"example.widget","version":"1.0.0","note":"a design the service produced"}""";
    private const string Newer = """{"package":"example.widget","version":"1.1.0"}""";

    private static string IdOf(string text) => BlobStore.IdOf(Encoding.UTF8.GetBytes(text));

    private static Workspace WorkspaceOf(EngineFactory factory) => factory.Services.GetRequiredService<Workspace>();

    private static ProfileDocument P(Workspace workspace) => workspace.Find(workspace.List().Single(e => e.Name == "P").Id).Document!;

    /// <summary>The test engine's profile P (<c>a</c> at 10, <c>b</c> at 100) gains <c>c</c> to <c>f</c> and a group <c>card</c> holding <c>x1</c>.</summary>
    private static void Seed(ProfileDocument document) =>
        TestDocs.Commit(document,
            TestDocs.Insert(TestDocs.Scene("c", 200, 10)), TestDocs.Insert(TestDocs.Scene("d", 300, 10)),
            TestDocs.Insert(TestDocs.Scene("e", 10, 100)), TestDocs.Insert(TestDocs.Scene("f", 100, 100)),
            TestDocs.Insert(TestDocs.Group("card", TestDocs.Scene("x1"))));

    /// <summary>Adds a sourced item named Gauge by a design (its file <see cref="Package"/>); its id.</summary>
    private static async Task<string> AddGaugeAsync(RelayRig rig, RelayPeer peer, ProfileDocument document)
    {
        var add = rig.Call("add", "add_item", new JsonObject { ["profile"] = "P", ["x"] = 5, ["y"] = 6, ["name"] = "Gauge" });
        add["design"] = RelayDesignTests.Design(IdOf(Package));
        add["files"] = new JsonArray(Package);
        await peer.SendAsync(add);
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());
        return document.Read().Document.Items.Single(i => i.Name == "Gauge").Id;
    }

    private static JsonObject BasedOn(ProfileDocument document, string item) => new()
    {
        ["instance"] = document.Instance,
        ["rev"] = document.Rev,
        ["item"] = item,
    };

    /// <summary>A restyle: a new source and layout (its file <see cref="Newer"/>), built on <paramref name="basedOn"/>.</summary>
    private static JsonObject Restyle(JsonObject basedOn)
    {
        var design = RelayDesignTests.Design(IdOf(Package), IdOf(Newer));
        design["source"]!["data"]!["colour"] = "#ff0000";
        design["layout"] = new JsonObject { ["w"] = 200, ["h"] = 60 };
        design.Remove("origin");
        design["basedOn"] = basedOn;
        return design;
    }

    private static JsonObject Updates(RelayRig rig, string id, JsonArray items, JsonArray? designs = null, JsonArray? basedOn = null,
        IEnumerable<string>? files = null)
    {
        var call = rig.Call(id, "update_items", new JsonObject { ["profile"] = "P", ["items"] = items });
        if (designs is not null)
            call["designs"] = designs;
        if (basedOn is not null)
            call["basedOn"] = basedOn;
        if (files is not null)
            call["files"] = new JsonArray([.. files.Select(f => (JsonNode)JsonValue.Create(f))]);
        return call;
    }

    private static async Task<(string Text, bool IsError)> ResultAsync(RelayPeer peer)
    {
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    private static Item ItemOf(ProfileDocument document, string id) => ItemNames.Flatten(document.Read().Document.Items).Single(i => i.Id == id);

    private static JsonNode Stored(ProfileDocument document, string id) => JsonSerializer.SerializeToNode(ItemOf(document, id), SpecJson.Options)!;

    /// <summary>The item as stored, without its revision (an undo step is a new one).</summary>
    private static JsonNode Content(ProfileDocument document, string id)
    {
        var item = Stored(document, id).AsObject();
        item.Remove("rev");
        return item;
    }

    private static void UndoOnce(ProfileDocument document) =>
        Assert.NotNull(document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev }).Result);

    [Fact]
    public async Task SixMoves_AreOneCommit_AndOneUndoStepBringsAllSixBack()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        Seed(document);
        string[] ids = ["a", "b", "c", "d", "e", "f"];
        var before = ids.Select(id => (ItemOf(document, id).Placement.X, ItemOf(document, id).Placement.Y)).ToList();
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var items = new JsonArray([.. ids.Select((id, i) => (JsonNode)new JsonObject { ["item"] = id, ["x"] = 20 + i * 60, ["y"] = 150 })]);
        await peer.SendAsync(Updates(rig, "u", items));
        var (text, error) = await ResultAsync(peer);
        Assert.False(error, text);
        Assert.StartsWith("Changed 6 items in P in one change: a, b, c, d, e, f.", text);

        Assert.Equal(rev + 1, document.Rev);
        Assert.Equal(ids.Select((_, i) => (20d + i * 60, 150d)), ids.Select(id => (ItemOf(document, id).Placement.X, ItemOf(document, id).Placement.Y)));

        UndoOnce(document);
        Assert.Equal(before, ids.Select(id => (ItemOf(document, id).Placement.X, ItemOf(document, id).Placement.Y)));
    }

    /// <summary>
    /// Mixed entries in one commit, in order: a restyle by a design (its origin kept), a raw resize based on a read, a move,
    /// a rename and a move into a group at an index. One undo step takes all five back.
    /// </summary>
    [Fact]
    public async Task MixedEntries_AreOneCommit_TheDesignsOriginKept_AndOneUndoStepTakesThemAllBack()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        Seed(document);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var gaugeBefore = Content(document, gauge);
        var rev = document.Rev;

        var design = Restyle(BasedOn(document, gauge));
        JsonArray items =
        [
            new JsonObject { ["item"] = gauge, ["x"] = 50 },
            new JsonObject { ["item"] = "a", ["width"] = 300, ["height"] = 40 },
            new JsonObject { ["item"] = "b", ["x"] = 123 },
            new JsonObject { ["item"] = "c", ["name"] = "Renamed" },
            new JsonObject { ["item"] = "d", ["group"] = "card", ["index"] = 0 },
        ];
        await peer.SendAsync(Updates(rig, "u", items, [design.DeepClone(), null, null, null, null], [null, BasedOn(document, "a"), null, null, null], [Newer]));
        var (text, error) = await ResultAsync(peer);
        Assert.False(error, text);
        Assert.StartsWith($"Changed 5 items in P in one change: \"Gauge\" ({gauge}), a, b, c, d.", text);

        Assert.Equal(rev + 1, document.Rev);
        var stored = Stored(document, gauge);
        Assert.True(JsonNode.DeepEquals(design["source"], stored["source"]), stored["source"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(design["layout"], stored["layout"]), stored["layout"]?.ToJsonString());
        Assert.Equal("local", stored["origin"]!["kind"]!.GetValue<string>());
        Assert.Equal(50d, ItemOf(document, gauge).Placement.X);
        Assert.Equal("""{"w":300,"h":40}""", Stored(document, "a")["layout"]!.ToJsonString());
        Assert.Equal(123d, ItemOf(document, "b").Placement.X);
        Assert.Equal("Renamed", ItemOf(document, "c").Name);
        Assert.Equal(["d", "x1"], Assert.IsType<GroupItem>(ItemOf(document, "card")).Children.Select(c => c.Id));
        Assert.True(workspace.Blobs.Exists(IdOf(Newer)));

        UndoOnce(document);
        Assert.True(JsonNode.DeepEquals(gaugeBefore, Content(document, gauge)), $"{gaugeBefore.ToJsonString()} became {Content(document, gauge).ToJsonString()}");
        Assert.Null(Stored(document, "a")["layout"]);
        Assert.Equal(100d, ItemOf(document, "b").Placement.X);
        Assert.Null(ItemOf(document, "c").Name);
        Assert.Contains(document.Read().Document.Items, i => i.Id == "d");
        Assert.Equal(["x1"], Assert.IsType<GroupItem>(ItemOf(document, "card")).Children.Select(c => c.Id));
    }

    [Fact]
    public async Task TheAnswer_NamesTheItemsChanged_ThenThoseAlreadyAsAsked()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        var rev = document.Rev;

        async Task<string> CallAsync(JsonArray items)
        {
            using var json = JsonDocument.Parse(new JsonObject { ["profile"] = "P", ["items"] = items }.ToJsonString());
            var result = await engine.Host.CallAsync("update_items", json.RootElement.Clone(), Ct);
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            Assert.False(result["isError"]!.GetValue<bool>(), text);
            return text;
        }

        // From this computer too: a is already at 10.
        Assert.StartsWith("Changed 1 item in P in one change: b; already as asked: a.",
            await CallAsync([new JsonObject { ["item"] = "a", ["x"] = 10 }, new JsonObject { ["item"] = "b", ["x"] = 5 }]));
        Assert.Equal(rev + 1, document.Rev);
        Assert.Equal("Nothing changed: every item already was as asked.",
            await CallAsync([new JsonObject { ["item"] = "a", ["x"] = 10 }, new JsonObject { ["item"] = "b", ["x"] = 5 }]));
        Assert.Equal(rev + 1, document.Rev);
    }

    public static TheoryData<string> Refusals() => ["notFound", "cycle", "nothing", "duplicate", "scene"];

    /// <summary>
    /// One refused entry among three refuses the call whole, named by its place: an item that isn't there, a group moved
    /// into itself, an entry with nothing to change, an item named twice, or a scene the engine refuses (its path
    /// <c>items/2/…</c> though the entries before it made four ops). The revision stays and the design's file isn't stored.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task OneRefusedEntry_RefusesThemAll_NamedByItsPlace_NothingChangedOrStored(string which)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        Seed(document);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var written = engine.Written();
        var rev = document.Rev;

        JsonArray items =
        [
            new JsonObject { ["item"] = gauge, ["x"] = 50 },
            new JsonObject { ["item"] = "a", ["x"] = 1, ["name"] = "A" },
            which switch
            {
                "notFound" => new JsonObject { ["item"] = "zz", ["x"] = 1 },
                "cycle" => new JsonObject { ["item"] = "card", ["group"] = "card" },
                "nothing" => new JsonObject { ["item"] = "b" },
                "duplicate" => new JsonObject { ["item"] = "a", ["y"] = 5 },
                _ => new JsonObject { ["item"] = "b", ["scene"] = TestDocs.InvalidScene() },
            },
        ];
        await peer.SendAsync(Updates(rig, "u", items, [Restyle(BasedOn(document, gauge)), null, null], files: [Newer]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, $"{which}: {text}");
        Assert.Contains(which switch
        {
            "notFound" => "items/2: There's no item zz in P (notFound).",
            "cycle" => "items/2: card can't go into itself or a group inside it (op.cycle)",
            "nothing" => "item 2 has nothing to change",
            "duplicate" => "items/2 names the same item as items/1",
            _ => "items/2/scene/nodes/0/w",
        }, text);
        if (which != "scene")
            Assert.EndsWith("nothing was changed.", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(rev, document.Rev);
        Assert.Equal(written, engine.Written());
        Assert.False(workspace.Blobs.Exists(IdOf(Newer)), "the design's file was stored");
    }

    [Fact]
    public async Task TheFiftyFirst_IsRefused_AndNoneAtAll()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var written = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        await peer.SendAsync(Updates(rig, "many", new JsonArray([.. Enumerable.Range(0, 51).Select(i => (JsonNode)new JsonObject { ["item"] = "a", ["x"] = i })])));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(arguments)", text);
        Assert.Contains("1 to 50, not 51", text);

        await peer.SendAsync(Updates(rig, "none", []));
        (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(arguments)", text);
        Assert.Equal(written, engine.Written());
    }

    /// <summary>
    /// Designs one per entry (a wrong count is the frame's mistake: <c>malformed</c>); a design without <c>basedOn</c>; a
    /// <c>basedOn</c> list on <c>add_items</c>, or an object on <c>update_items</c>: each refused, nothing stored or changed.
    /// </summary>
    [Fact]
    public async Task DesignsAndBasedOn_NotAsTheToolTakesThem_AreRefused_NothingStored()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var written = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        JsonArray Two() => [new JsonObject { ["item"] = "a", ["x"] = 1 }, new JsonObject { ["item"] = "b", ["x"] = 2 }];

        await peer.SendAsync(Updates(rig, "count", Two(), [Restyle(BasedOn(document, "a"))], files: [Package, Newer]));
        Assert.Equal("""{"type":"error","code":"malformed","id":"count"}""", (await peer.NextAsync(Ct)).ToJsonString());

        var unbased = Restyle(BasedOn(document, "a"));
        unbased.Remove("basedOn");
        await peer.SendAsync(Updates(rig, "unbased", Two(), [unbased, null], files: [Package, Newer]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("items/0", text);
        Assert.Contains("(design)", text);

        var add = rig.Call("add", "add_items", new JsonObject { ["profile"] = "P", ["items"] = new JsonArray(new JsonObject { ["scene"] = TestDocs.Rect() }) });
        add["basedOn"] = new JsonArray(BasedOn(document, "a"));
        await peer.SendAsync(add);
        (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(design)", text);

        var single = Updates(rig, "object", Two());
        single["basedOn"] = BasedOn(document, "a");
        await peer.SendAsync(single);
        (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(design)", text);

        Assert.False(workspace.Blobs.Exists(IdOf(Newer)));
        Assert.Equal(written, engine.Written());
    }

    public static TheoryData<string> StaleEntries() => ["design", "raw"];

    /// <summary>
    /// The item's design changed since the read an entry was built on (a design's <c>basedOn</c>, or a raw resize's in
    /// the frame's list): <c>stale</c> at once, nothing changed, nothing rebuilt, the design's file not stored.
    /// </summary>
    [Theory]
    [MemberData(nameof(StaleEntries))]
    public async Task ADesignChangedMeanwhile_IsStale_NothingChanged(string which)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var target = which == "design" ? gauge : "a";
        var basedOn = BasedOn(document, target);

        // Meanwhile the item's design changes (another client's new scene and box).
        TestDocs.Commit(document, new JsonObject
        {
            ["op"] = "setDesign", ["id"] = target, ["baseRev"] = document.Rev,
            ["layout"] = new JsonObject { ["w"] = 300, ["h"] = 40 }, ["scene"] = TestDocs.InputScene(),
        });
        var written = engine.Written();
        var designed = Stored(document, target).DeepClone();
        var retries = 0;
        engine.Host.StepHook = step =>
        {
            if (step == "retry")
                Interlocked.Increment(ref retries);
        };

        JsonArray items = [which == "design" ? new JsonObject { ["item"] = gauge } : new JsonObject { ["item"] = "a", ["width"] = 90 }, new JsonObject { ["item"] = "b", ["x"] = 5 }];
        var call = which == "design"
            ? Updates(rig, "u", items, [Restyle(basedOn), null], files: [Package, Newer])
            : Updates(rig, "u", items, basedOn: [basedOn, null]);
        await peer.SendAsync(call);
        Assert.Equal("""{"type":"stale","id":"u"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Equal(written, engine.Written());
        Assert.True(JsonNode.DeepEquals(designed, Stored(document, target)));
        Assert.Equal(0, retries);
        Assert.False(workspace.Blobs.Exists(IdOf(Newer)), "the design's file was stored");
    }

    /// <summary>A move and a rename since the read don't conflict with the entries' designs: every entry lands.</summary>
    [Fact]
    public async Task AMoveMeanwhile_IsNotStale()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var (onGauge, onA) = (BasedOn(document, gauge), BasedOn(document, "a"));
        TestDocs.Commit(document, TestDocs.Place(gauge, 77, 7, document.Rev), TestDocs.Attrs("a", name: "Moved"));
        var rev = document.Rev;

        var design = Restyle(onGauge);
        JsonArray items = [new JsonObject { ["item"] = gauge }, new JsonObject { ["item"] = "a", ["width"] = 90 }, new JsonObject { ["item"] = "b", ["x"] = 5 }];
        await peer.SendAsync(Updates(rig, "u", items, [design.DeepClone(), null, null], [null, onA, null], [Newer]));
        var (text, error) = await ResultAsync(peer);
        Assert.False(error, text);
        Assert.Equal(rev + 1, document.Rev);
        Assert.Equal(77d, ItemOf(document, gauge).Placement.X);
        Assert.True(JsonNode.DeepEquals(design["source"], Stored(document, gauge)["source"]));
        Assert.Equal(("Moved", 90d), (ItemOf(document, "a").Name, Stored(document, "a")["layout"]!["w"]!.GetValue<double>()));
        Assert.Equal(5d, ItemOf(document, "b").Placement.X);
    }

    private static string Replace(Workspace workspace, string id)
    {
        Assert.True(workspace.DeleteWithItsTag(id));
        var again = TestDocs.Doc("P", 400, 200, TestDocs.Scene("a", 10, 10), TestDocs.Scene("b", 100, 10));
        again["id"] = id;
        Assert.NotNull(workspace.Create(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["document"] = again }.ToJsonString())).Entry);
        return workspace.Find(id).Document!.Instance;
    }

    /// <summary>
    /// The profile replaced (deleted and created again under its id) since the read an entry was built on: the error
    /// <c>basedOn</c> as the call starts, named by the entry, never <c>stale</c>. Replaced after that check, before the
    /// commit: <c>notFound</c>. Either way the new profile is left as it is.
    /// </summary>
    [Fact]
    public async Task AProfileReplaced_BeforeTheCall_IsBasedOn_AndBeforeTheCommit_IsNotFound()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var id = document.Id;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        JsonArray Items() => [new JsonObject { ["item"] = "b", ["x"] = 5 }, new JsonObject { ["item"] = "a", ["width"] = 90 }];

        var basedOn = BasedOn(document, "a");
        Replace(workspace, id);
        var written = engine.Written();
        var call = Updates(rig, "before", Items(), basedOn: [null, basedOn]);
        call["arguments"]!["profile"] = id;
        await peer.SendAsync(call);
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.StartsWith("items/1: The profile was replaced or the item changed since this change was prepared; nothing was changed.", text);
        Assert.Equal(written, engine.Written());

        // Based on the new one, replaced again between the check and the commit.
        basedOn = BasedOn(workspace.Find(id).Document!, "a");
        string? instance = null;
        engine.Host.StepHook = step =>
        {
            if (step == "write" && instance is null)
                instance = Replace(workspace, id);
        };
        call = Updates(rig, "between", Items(), basedOn: [null, basedOn]);
        call["arguments"]!["profile"] = id;
        await peer.SendAsync(call);
        (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(notFound)", text);
        Assert.NotNull(instance);
        var now = workspace.Find(id).Document!;
        Assert.Equal(instance, now.Instance);
        Assert.Equal(100d, ItemOf(now, "b").Placement.X);
        Assert.Null(Stored(now, "a")["layout"]);
    }

    /// <summary>
    /// An entry's scene that shows a web image, beside another entry's design and its file, refuses the call
    /// (<c>webImage</c>) before anything is stored.
    /// </summary>
    [Fact]
    public async Task AWebImageBesideADesign_IsRefused_BeforeTheDesignsFileIsStored()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var written = engine.Written();

        JsonArray items =
        [
            new JsonObject { ["item"] = gauge },
            new JsonObject { ["item"] = "b", ["scene"] = RelayToolRulesTests.WebImageScene("http://192.168.1.10/cam.jpg") },
        ];
        await peer.SendAsync(Updates(rig, "u", items, [Restyle(BasedOn(document, gauge)), null], files: [Newer]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(webImage)", text);
        Assert.False(workspace.Blobs.Exists(IdOf(Newer)), "the design's file was stored");
        Assert.Equal(written, engine.Written());
    }
}
