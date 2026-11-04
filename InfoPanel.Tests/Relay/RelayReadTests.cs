using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Service;
using InfoPanel.Spec;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;
using Engine = InfoPanel.Tests.Relay.RelayToolRulesTests.Engine;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// The <c>read</c> frame (docs/2.0/relay.md, "Designs"): one item of one profile, answered whole as the document holds it
/// with the ids it resolved, the document's tag and revision, and on request <c>detail</c>, <c>get_profile</c>'s text for the
/// item from the same snapshot (a sourced item's without its scene and source); a profile or item it can't resolve is an
/// error result. It arrives under the call's rules: late (sent over 10 s ago) it is refused unread.
/// </summary>
public sealed class RelayReadTests
{
    private static Workspace WorkspaceOf(EngineFactory factory) => factory.Services.GetRequiredService<Workspace>();

    private static ProfileDocument P(Workspace workspace) => workspace.Find(workspace.List().Single(e => e.Name == "P").Id).Document!;

    private static JsonObject Read(RelayRig rig, string id, string profile, string item, bool detail = false, long? sentAt = null)
    {
        var read = new JsonObject
        {
            ["type"] = "read",
            ["id"] = id,
            ["profile"] = profile,
            ["item"] = item,
            ["sentAt"] = sentAt ?? rig.Clocks.RelayNow,
        };
        if (detail)
            read["detail"] = true;
        return read;
    }

    [Fact]
    public async Task ARead_AnswersTheItemWhole_WithTheIdsItResolved_TheTagAndTheRevision()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        // By the profile's name and the item's id: the answer names both by id.
        await peer.SendAsync(Read(rig, "r", "P", "b"));
        var answer = await peer.ExpectAsync("item", Ct);
        Assert.Equal("r", answer["id"]!.GetValue<string>());
        Assert.Equal(document.Instance, answer["instance"]!.GetValue<string>());
        Assert.Equal(document.Rev, answer["rev"]!.GetValue<long>());
        Assert.Equal(document.Id, answer["profile"]!.GetValue<string>());
        var held = JsonSerializer.SerializeToNode(document.Read().Document.Items.Single(i => i.Id == "b"), SpecJson.Options);
        Assert.Equal(held?.ToJsonString(), answer["item"]?.ToJsonString());
        Assert.Null(answer["detail"]);
        Assert.Equal(["read"], engine.Called);
    }

    public static TheoryData<string, string, string> Unresolved() => new()
    {
        { "Q", "a", "notFound" },
        { "P", "zz", "notFound" },
    };

    [Theory]
    [MemberData(nameof(Unresolved))]
    public async Task AProfileOrItemItCantResolve_IsAnErrorResult(string profile, string item, string code)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        await peer.SendAsync(Read(rig, "r", profile, item));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains($"({code})", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task TwoItemsOfTheName_AreAmbiguous_AnErrorResult()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();
        await peer.SendAsync(rig.Call("n1", "update_item", new JsonObject { ["profile"] = "P", ["item"] = "a", ["name"] = "Twin" }));
        await peer.ExpectAsync("result", Ct);
        await peer.SendAsync(rig.Call("n2", "update_item", new JsonObject { ["profile"] = "P", ["item"] = "b", ["name"] = "Twin" }));
        await peer.ExpectAsync("result", Ct);

        await peer.SendAsync(Read(rig, "r", "P", "Twin"));
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("(ambiguous)", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(2, document.Current.Document.Items.Count(i => i.Name == "Twin"));
    }

    /// <summary>A read's error is held to <c>maxChars</c> as a call's is: 100 items named alike, each with an AI app's id.</summary>
    [Fact]
    public async Task AReadsError_IsHeldToMaxChars()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var twins = Enumerable.Range(0, 100).Select(i => TestDocs.Scene($"ai-{i:0000000000}", i, extra: new JsonObject { ["name"] = "Twin" })).ToArray();
        TestDocs.Create(workspace, TestDocs.Doc("Twins", 400, 200, twins));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var read = Read(rig, "r", "Twins", "Twin");
        read["maxChars"] = 1000;
        await peer.SendAsync(read);
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = string.Concat(result["content"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()));
        Assert.True(text.Length <= 1000, $"{text.Length} characters: {text}");
        Assert.Contains("(ambiguous)", text);
    }

    [Fact]
    public async Task WithDetail_ItIsGetProfilesTextForTheItem_FromTheSameRevision_ASourcedItemsWithoutSceneAndSource()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var document = P(WorkspaceOf(factory));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        // A sourced item: a design the service produced.
        const string package = """{"package":"example.widget","version":"1.0.0"}""";
        var add = rig.Call("d", "add_item", new JsonObject { ["profile"] = "P", ["x"] = 5, ["y"] = 6, ["name"] = "Gauge" });
        add["design"] = RelayDesignTests.Design(BlobStore.IdOf(Encoding.UTF8.GetBytes(package)));
        add["files"] = new JsonArray(package);
        await peer.SendAsync(add);
        var added = (await peer.ExpectAsync("result", Ct))["result"]!;
        Assert.False(added["isError"]!.GetValue<bool>(), added.ToJsonString());

        await peer.SendAsync(Read(rig, "r", "P", "Gauge", detail: true));
        var answer = await peer.ExpectAsync("item", Ct);
        var item = document.Current.Document.Items.Single(i => i.Name == "Gauge");
        Assert.Equal(item.Id, answer["item"]!["id"]!.GetValue<string>());
        Assert.NotNull(answer["item"]!["source"]);
        var detail = JsonNode.Parse(answer["detail"]!.GetValue<string>())!;
        Assert.Equal(answer["rev"]!.GetValue<long>(), detail["rev"]!.GetValue<long>());
        Assert.Equal(item.Id, detail["item"]!["id"]!.GetValue<string>());
        Assert.Equal("example.widget/1", detail["item"]!["madeWith"]!.GetValue<string>());
        Assert.Null(detail["item"]!["scene"]);
        Assert.Null(detail["item"]!["source"]);

        // An item without a source keeps its scene in the detail, as get_profile gives it.
        await peer.SendAsync(Read(rig, "r2", "P", "a", detail: true));
        var plain = JsonNode.Parse((await peer.ExpectAsync("item", Ct))["detail"]!.GetValue<string>())!;
        Assert.NotNull(plain["item"]!["scene"]);
    }

    [Fact]
    public async Task TheDetail_IsCutByMaxChars()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var read = Read(rig, "r", "P", "a", detail: true);
        read["maxChars"] = 1000;
        await peer.SendAsync(read);
        var answer = await peer.ExpectAsync("item", Ct);
        Assert.True(answer["detail"]!.GetValue<string>().Length <= 1000);
        Assert.NotNull(answer["item"]!["scene"]); // the item itself is whole
    }

    public static TheoryData<string, int> LongDetails() => new()
    {
        { "names", 1000 },
        { "names", 1200 },
        { "bindings", 1000 },
        { "bindings", 5000 },
        { "bindings", 12000 },
        { "both", 1000 },
    };

    /// <summary>
    /// A detail longer than <c>maxChars</c> is shortened by its structure, never cut as text: it still parses, within
    /// <c>maxChars</c>, its ids and revision whole, and says inside itself (<c>cut</c>) that it was shortened. At the
    /// limits: a profile and an item named with 256 characters and a <c>source.ref</c> of 512, beside 50 other items;
    /// 32 bindings (the most an item has) of 32-character names and 512-character keys; both.
    /// </summary>
    [Theory]
    [MemberData(nameof(LongDetails))]
    public async Task ADetailOverMaxChars_IsShortenedByItsStructure_AndStillParses(string which, int maxChars)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var names = which is "names" or "both";
        var extra = new JsonObject();
        if (names)
        {
            extra["name"] = new string('i', 256);
            extra["source"] = new JsonObject { ["ref"] = new string('r', 512) };
        }
        if (which is "bindings" or "both")
        {
            extra["bindings"] = new JsonObject([.. Enumerable.Range(0, 32).Select(i =>
                KeyValuePair.Create($"in{i:00}{new string('x', 28)}", (JsonNode?)new JsonObject { ["key"] = $"plugin:/infopanel.mock/{new string('k', 485)}/{i:000}" }))]);
        }
        JsonObject[] others = [.. Enumerable.Range(0, 50).Select(i => TestDocs.Scene($"other-{i:000}", i))];
        var id = TestDocs.Create(workspace, TestDocs.Doc(names ? new string('n', 256) : "Long", 400, 200, [TestDocs.Scene("long", 1, 2, extra: extra), .. others]));
        var document = workspace.Find(id).Document!;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var read = Read(rig, "r", id, "long", detail: true);
        read["maxChars"] = maxChars;
        await peer.SendAsync(read);
        var answer = await peer.ExpectAsync("item", Ct);
        var text = answer["detail"]!.GetValue<string>();
        Assert.True(text.Length <= maxChars, $"{text.Length} characters: {text}");
        var detail = Assert.IsType<JsonObject>(JsonNode.Parse(text));
        Assert.Equal(id, detail["id"]!.GetValue<string>());
        Assert.Equal(document.Rev, detail["rev"]!.GetValue<long>());
        Assert.Equal("long", detail["item"]!["id"]!.GetValue<string>());
        Assert.Contains($"{maxChars}", detail["cut"]!.GetValue<string>());
        // The item itself is whole.
        Assert.Equal(JsonSerializer.SerializeToNode(document.Read().Document.Items.Single(i => i.Id == "long"), SpecJson.Options)!.ToJsonString(),
            answer["item"]!.ToJsonString());
    }

    public static TheoryData<string> ReadSteps() => ["reading", "read"];

    /// <summary>
    /// A read cancelled once it has begun, before it reads the document (<c>reading</c>) or once it has read it
    /// (<c>read</c>), answers <c>cancelled</c>, not <c>item</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadSteps))]
    public async Task AReadCancelledWhileItRuns_AnswersCancelled(string step)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var steps = new System.Collections.Concurrent.ConcurrentQueue<string>();
        engine.Host.StepHook = s =>
        {
            steps.Enqueue(s);
            if (s != step)
                return;
            paused.TrySetResult();
            release.Wait(Limit, Ct);
        };
        await peer.SendAsync(Read(rig, "r", "P", "a", detail: true));
        await paused.Task.WaitAsync(Limit, Ct);
        await peer.SendAsync(new JsonObject { ["type"] = "cancel", ["id"] = "r" });
        await FlushAsync(peer);
        release.Set();

        Assert.Equal("""{"type":"cancelled","id":"r"}""", (await peer.NextAsync(Ct)).ToJsonString());
        var (id, answer) = await engine.Answers.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        Assert.Equal("r", id);
        Assert.Null(answer.Item);
        // Cancelled before it read, it doesn't read.
        Assert.Equal(step == "reading" ? ["reading"] : ["reading", "read"], steps);
    }

    [Fact]
    public async Task ALateRead_IsRefusedUnread()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        await peer.SendAsync(Read(rig, "late", "P", "a", sentAt: rig.Clocks.RelayNow - 10_001));
        Assert.Equal("""{"type":"late","id":"late"}""", (await peer.NextAsync(Ct)).ToJsonString());
        Assert.Empty(engine.Called);
    }

    [Theory]
    [InlineData("""{"type":"read","id":"r","item":"a","sentAt":1}""")]
    [InlineData("""{"type":"read","id":"r","profile":"P","sentAt":1}""")]
    [InlineData("""{"type":"read","id":"r","profile":"P","item":"a"}""")]
    [InlineData("""{"type":"read","id":"r","profile":"P","item":"a","sentAt":1,"detail":"yes"}""")]
    [InlineData("""{"type":"read","id":"r","profile":1,"item":"a","sentAt":1}""")]
    public void AReadMissingAMember_IsMalformed(string frame)
    {
        var read = Assert.IsType<RelayFrame.Refused>(RelayFrames.Read(Encoding.UTF8.GetBytes(frame)));
        Assert.Equal(("malformed", "r"), (read.Code, read.Id));
    }
}
