using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Spec;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;
using Engine = InfoPanel.Tests.Relay.RelayToolRulesTests.Engine;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// <c>update_item</c> with a design (docs/2.0/relay.md, "Designs"): the design names what it was built on
/// (<c>basedOn</c>: the document's tag, its revision and the item's id). Another tag or item ends the call with an error
/// result, never <c>stale</c>; the design is set at <c>basedOn.rev</c> beside the call's other changes in one commit and
/// one undo step, its origin kept; a change to the item's design since that revision answers <c>stale</c>, nothing
/// changed and nothing rebuilt; a move meanwhile doesn't conflict.
/// </summary>
public sealed class RelayUpdateDesignTests
{
    private const string Package = """{"package":"example.widget","version":"1.0.0","note":"a design the service produced"}""";
    private const string Newer = """{"package":"example.widget","version":"1.1.0"}""";

    private static string IdOf(string text) => BlobStore.IdOf(Encoding.UTF8.GetBytes(text));

    private static Workspace WorkspaceOf(EngineFactory factory) => factory.Services.GetRequiredService<Workspace>();

    private static ProfileDocument P(Workspace workspace) => workspace.Find(workspace.List().Single(e => e.Name == "P").Id).Document!;

    /// <summary>Adds a sourced item named Gauge by a design; its id.</summary>
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

    /// <summary>A restyle: a new source and layout, built on <paramref name="basedOn"/>.</summary>
    private static JsonObject Restyle(JsonObject basedOn)
    {
        var design = RelayDesignTests.Design(IdOf(Package), IdOf(Newer));
        design["source"]!["data"]!["colour"] = "#ff0000";
        design["layout"] = new JsonObject { ["w"] = 200, ["h"] = 60 };
        design.Remove("origin");
        design["basedOn"] = basedOn;
        return design;
    }

    private static JsonObject BasedOn(ProfileDocument document, string item, long? rev = null, string? instance = null) => new()
    {
        ["instance"] = instance ?? document.Instance,
        ["rev"] = rev ?? document.Rev,
        ["item"] = item,
    };

    private static JsonObject Update(RelayRig rig, string id, JsonObject arguments, JsonObject design)
    {
        var call = rig.Call(id, "update_item", arguments);
        call["design"] = design;
        call["files"] = new JsonArray(Newer);
        return call;
    }

    private static JsonNode? Source(ProfileDocument document, string id) =>
        JsonSerializer.SerializeToNode(document.Read().Document.Items.Single(i => i.Id == id), SpecJson.Options)!["source"];

    [Fact]
    public async Task ADesign_IsSetBesideTheCallsOtherChanges_InOneCommitAndOneUndoStep_ItsOriginKept()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var before = Source(document, gauge)!.DeepClone();
        var rev = document.Rev;

        var design = Restyle(BasedOn(document, gauge));
        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge, ["x"] = 50, ["name"] = "Red" }, design));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());

        Assert.Equal(rev + 1, document.Rev);
        var item = Assert.IsType<SceneItem>(document.Read().Document.Items.Single(i => i.Id == gauge));
        var stored = JsonSerializer.SerializeToNode(item, SpecJson.Options)!;
        Assert.True(JsonNode.DeepEquals(design["source"], stored["source"]), stored["source"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(design["layout"], stored["layout"]), stored["layout"]?.ToJsonString());
        Assert.Equal("local", stored["origin"]!["kind"]!.GetValue<string>());
        Assert.Equal(("Red", 50d), (item.Name, item.Placement.X));
        Assert.True(workspace.Blobs.Exists(IdOf(Newer)));

        // One undo step brings back the design, the place and the name together.
        var undone = document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev });
        Assert.NotNull(undone.Result);
        var back = document.Read().Document.Items.Single(i => i.Id == gauge);
        Assert.True(JsonNode.DeepEquals(before, Source(document, gauge)));
        Assert.Equal(("Gauge", 5d), (back.Name, back.Placement.X));
    }

    /// <summary>A design with <c>group</c> and <c>index</c>: the design set, then the move, in one commit and one undo step.</summary>
    [Fact]
    public async Task ADesign_WithAGroupAndIndex_IsSetAndMoved_InOneCommit()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        TestDocs.Commit(document, TestDocs.Insert(TestDocs.Group("card", TestDocs.Scene("x1"))));
        var gauge = await AddGaugeAsync(rig, peer, document);
        var rev = document.Rev;

        var design = Restyle(BasedOn(document, gauge));
        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge, ["group"] = "card", ["index"] = 0 }, design));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());

        Assert.Equal(rev + 1, document.Rev);
        var card = Assert.IsType<GroupItem>(document.Read().Document.Items.Single(i => i.Id == "card"));
        Assert.Equal([gauge, "x1"], card.Children.Select(c => c.Id));
        var stored = JsonSerializer.SerializeToNode(card.Children[0], SpecJson.Options)!;
        Assert.True(JsonNode.DeepEquals(design["source"], stored["source"]), stored["source"]?.ToJsonString());
        Assert.Equal(5d, card.Children[0].Placement.X); // its numbers kept

        // One undo step takes back both.
        Assert.NotNull(document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev }).Result);
        Assert.Contains(document.Read().Document.Items, i => i.Id == gauge);
        Assert.Equal(["x1"], ((GroupItem)document.Read().Document.Items.Single(i => i.Id == "card")).Children.Select(c => c.Id));
    }

    public static TheoryData<string> Elsewhere() => ["tag", "item"];

    [Theory]
    [MemberData(nameof(Elsewhere))]
    public async Task AnotherTagOrItem_EndsTheCallWithTheError_NeverStale_NothingStoredOrChanged(string which)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var written = engine.Written();

        var basedOn = which == "tag" ? BasedOn(document, gauge, instance: "another-tag") : BasedOn(document, "a");
        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge }, Restyle(basedOn)));
        var answer = await peer.NextAsync(Ct);
        Assert.Equal("result", answer["type"]!.GetValue<string>());
        Assert.True(answer["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("The profile was replaced or the item changed since this change was prepared; nothing was changed.",
            answer["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(written, engine.Written());
        Assert.False(workspace.Blobs.Exists(IdOf(Newer)));
    }

    [Fact]
    public async Task AnUpdateWithADesignWithoutBasedOn_IsRefused()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var rev = document.Rev;

        var design = Restyle(BasedOn(document, gauge));
        design.Remove("basedOn");
        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge }, design));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("basedOn", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(rev, document.Rev);
    }

    [Fact]
    public async Task ADesignChangedMeanwhile_IsStale_NothingChanged_NothingRebuilt()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var basedOn = BasedOn(document, gauge);

        // Meanwhile its box changes (its design): the restyle was built on what no longer holds.
        await peer.SendAsync(rig.Call("m", "update_item", new JsonObject { ["profile"] = "P", ["item"] = gauge, ["width"] = 300 }));
        var meanwhile = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.False(meanwhile["isError"]!.GetValue<bool>(), meanwhile.ToJsonString());
        var written = engine.Written();
        var source = Source(document, gauge)!.DeepClone();
        var retries = 0;
        engine.Host.StepHook = step =>
        {
            if (step == "retry")
                Interlocked.Increment(ref retries);
        };

        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge, ["x"] = 50 }, Restyle(basedOn)));
        Assert.Equal("""{"type":"stale","id":"u"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Equal(written, engine.Written());
        Assert.True(JsonNode.DeepEquals(source, Source(document, gauge)));
        Assert.Equal(0, retries);
        // Found by the check before the files are stored: none was.
        Assert.False(WorkspaceOf(factory).Blobs.Exists(IdOf(Newer)), "the file was stored");
    }

    /// <summary>
    /// The item's design changes after the check and the file, just before the commit: the commit's conflict on the
    /// design's op answers <c>stale</c> at once, nothing changed and no retry (the file stays, unreferenced).
    /// </summary>
    [Fact]
    public async Task ADesignChangedJustBeforeTheCommit_IsStale_NothingChanged_NothingRebuilt()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var basedOn = BasedOn(document, gauge);

        (string Written, JsonNode? Source)? meanwhile = null;
        Exception? failed = null;
        var (writes, retries) = (0, 0);
        engine.Host.StepHook = step =>
        {
            if (step == "retry")
                retries++;
            // Write 1 stores the file, write 2 is the commit.
            if (step != "write" || ++writes != 2)
                return;
            try
            {
                TestDocs.Commit(document, new JsonObject
                {
                    ["op"] = "setDesign", ["id"] = gauge, ["baseRev"] = document.Rev,
                    ["layout"] = new JsonObject { ["w"] = 300, ["h"] = 40 }, ["scene"] = TestDocs.InputScene(),
                });
                // A raw scene now: the source is gone.
                meanwhile = (engine.Written(), Source(document, gauge)?.DeepClone());
            }
            catch (Exception ex)
            {
                failed = ex;
            }
        };

        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge, ["x"] = 50 }, Restyle(basedOn)));
        var answer = await peer.NextAsync(Ct);
        Assert.Null(failed);
        Assert.Equal("""{"type":"stale","id":"u"}""", answer.ToJsonString());
        Assert.NotNull(meanwhile);
        Assert.Equal(meanwhile.Value.Written, engine.Written());
        Assert.True(JsonNode.DeepEquals(meanwhile.Value.Source, Source(document, gauge)));
        Assert.Equal(0, retries);
        Assert.Equal(2, writes);
    }

    private static void Rename(ProfileDocument document, string name) =>
        TestDocs.Commit(document, TestDocs.Profile(new JsonObject { ["name"] = name }));

    public static TheoryData<string> Renames() => ["atThePreflight", "whileStoring", "atTheCommit", "abaAtTheCommit"];

    /// <summary>
    /// The profile held as it was found, for <c>update_item</c>'s design as for <c>add_item</c>'s: the profile the call named by
    /// its name is renamed away (another profile taking the name), or away and back, after it was found: as its design is
    /// checked, while its file is stored, or just before the commit. Each time the call is refused (<c>notFound</c>) and no
    /// profile changes: the profile found first is carried to the commit, and every commit attempt checks, under the
    /// document's lock, that its name wasn't changed since.
    /// </summary>
    [Theory]
    [MemberData(nameof(Renames))]
    public async Task AProfileRenamedBetweenThePreflightAndTheCommit_RefusesTheDesign(string when)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var p = P(workspace);
        var q = workspace.Find(TestDocs.Create(workspace, TestDocs.Doc("Q", 400, 200))).Document!;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, p);
        // q holds an item of the same id, so a call that followed the name would find it.
        TestDocs.Commit(q, TestDocs.Insert(TestDocs.Scene(gauge, 1, 1)));
        var basedOn = BasedOn(p, gauge);

        var writes = 0;
        (long P, long Q)? revs = null;
        engine.Host.StepHook = step =>
        {
            var now = when switch
            {
                "atThePreflight" => step == "preflight",
                "whileStoring" => step == "write" && ++writes == 1,
                _ => step == "write" && ++writes == 2,
            };
            if (!now)
                return;
            Rename(p, "Old");
            Rename(when == "abaAtTheCommit" ? p : q, "P");
            revs = (p.Rev, q.Rev);
        };

        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge, ["x"] = 50 }, Restyle(basedOn)));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(result["isError"]!.GetValue<bool>(), $"{when}: {text}");
        Assert.Contains("(notFound)", text);
        Assert.NotNull(revs);
        Assert.Equal(revs, (p.Rev, q.Rev));
        Assert.Equal(5d, p.Read().Document.Items.Single(i => i.Id == gauge).Placement.X);
        Assert.Equal(1d, q.Read().Document.Items.Single(i => i.Id == gauge).Placement.X);
        if (when == "atThePreflight")
            Assert.False(workspace.Blobs.Exists(IdOf(Newer)), "the file was stored");
    }

    [Fact]
    public async Task AMoveMeanwhile_IsNotStale()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        var gauge = await AddGaugeAsync(rig, peer, document);
        var basedOn = BasedOn(document, gauge);

        await peer.SendAsync(rig.Call("m", "update_item", new JsonObject { ["profile"] = "P", ["item"] = gauge, ["x"] = 77, ["name"] = "Moved" }));
        await peer.ExpectAsync("result", Ct);

        var design = Restyle(basedOn);
        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = gauge }, design));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>(), result.ToJsonString());
        var item = document.Read().Document.Items.Single(i => i.Id == gauge);
        Assert.Equal(("Moved", 77d), (item.Name, item.Placement.X));
        Assert.True(JsonNode.DeepEquals(design["source"], Source(document, gauge)));
    }

    /// <summary>
    /// A design beside a change of bindings for an item that shows a web image (one a binding could re-address) is
    /// refused (<c>webImage</c>) before anything is stored: the design's file isn't stored and nothing is changed.
    /// </summary>
    [Fact]
    public async Task ADesignWithBindings_ForAnItemShowingAWebImage_IsRefused_BeforeItsFileIsStored()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        // Shown from this computer, where the rule doesn't apply.
        TestDocs.Commit(document, TestDocs.Design("a", RelayToolRulesTests.WebImageScene("http://192.168.1.10/cam.jpg")));
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var design = RelayDesignTests.Design(IdOf(Newer));
        design.Remove("origin");
        design["basedOn"] = BasedOn(document, "a");
        var bindings = new JsonObject { ["v"] = "plugin:/infopanel.mock/cpu/cpu-load" };
        await peer.SendAsync(Update(rig, "u", new JsonObject { ["profile"] = "P", ["item"] = "a", ["bindings"] = bindings }, design));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Contains("(webImage)", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(workspace.Blobs.Exists(IdOf(Newer)), "the file was stored");
        Assert.Equal(rev, document.Rev);
    }
}
