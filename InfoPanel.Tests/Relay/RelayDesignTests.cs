using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Mcp.Tools;
using InfoPanel.Service;
using InfoPanel.Spec;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static InfoPanel.Tests.Relay.RelayRig;
using Engine = InfoPanel.Tests.Relay.RelayToolRulesTests.Engine;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// Designs over the relay (docs/2.0/relay.md, "Designs"): a call from elsewhere may bring <c>add_item</c> a design the
/// service produced (its layout, source, scene and origin) with the files its source needs; the engine checks every file
/// and dependency before it writes anything, stores the files under their hashes, each behind the call's cutoff, then
/// inserts the item as given, behind the cutoff too. A refused design leaves nothing stored and nothing changed.
/// </summary>
public sealed class RelayDesignTests
{
    private const string Package = """{"package":"example.widget","version":"1.0.0","note":"a design the service produced"}""";
    private const string Second = """{"package":"example.widget.part","version":"1.0.0"}""";

    private static string IdOf(string text) => BlobStore.IdOf(Encoding.UTF8.GetBytes(text));

    /// <summary>A design whose source needs <paramref name="deps"/>.</summary>
    internal static JsonObject Design(params string[] deps) => new()
    {
        ["layout"] = new JsonObject { ["w"] = 120, ["h"] = 40 },
        ["source"] = new JsonObject
        {
            ["format"] = "example.widget/1",
            ["data"] = new JsonObject { ["colour"] = "#ff8800", ["max"] = 100 },
            ["deps"] = new JsonArray([.. deps.Select(d => (JsonNode)JsonValue.Create(d))]),
        },
        ["scene"] = TestDocs.InputScene(),
        ["origin"] = new JsonObject { ["kind"] = "local" },
    };

    /// <summary>A call frame bringing <paramref name="design"/> and <paramref name="files"/>.</summary>
    private static JsonObject DesignCall(RelayRig rig, string id, string tool, JsonObject arguments, JsonObject? design, IEnumerable<string>? files,
        long? deadline = null, long? sentAt = null)
    {
        var call = rig.Call(id, tool, arguments, sentAt);
        // add_items takes a design per item (DesignCalls' first item), null for its item given as a scene.
        if (design is not null && tool == "add_items")
            call["designs"] = new JsonArray(design, null);
        else if (design is not null)
            call["design"] = design;
        if (files is not null)
            call["files"] = new JsonArray([.. files.Select(f => (JsonNode)JsonValue.Create(f))]);
        if (deadline is { } at)
            call["deadline"] = at;
        return call;
    }

    /// <summary>
    /// Each tool a design comes to, with its arguments on the test engine's profile <c>P</c>: every tool the host takes a
    /// design for must be here (<see cref="EveryDesignTool_HasACutoffTestAtEachWrite"/>), so a design path can't skip the
    /// cutoff at any of its writes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Func<JsonObject>> DesignCalls = new Dictionary<string, Func<JsonObject>>
    {
        ["add_item"] = () => new JsonObject { ["profile"] = "P", ["x"] = 5, ["y"] = 6, ["name"] = "Gauge" },
        ["update_item"] = () => new JsonObject { ["profile"] = "P", ["item"] = "a", ["x"] = 7 },
        ["add_items"] = () => new JsonObject
        {
            ["profile"] = "P",
            ["items"] = new JsonArray(
                new JsonObject { ["x"] = 5, ["y"] = 6, ["name"] = "Gauge" },
                new JsonObject { ["scene"] = TestDocs.InputScene(), ["name"] = "Plain" }),
        },
    };

    /// <summary>A design for <paramref name="tool"/> on <paramref name="document"/>: <c>update_item</c>'s built on item <c>a</c> as it is now.</summary>
    private static JsonObject DesignFor(string tool, ProfileDocument document, params string[] deps)
    {
        var design = Design(deps);
        if (tool == "update_item")
            design["basedOn"] = new JsonObject { ["instance"] = document.Instance, ["rev"] = document.Rev, ["item"] = "a" };
        return design;
    }

    [Fact]
    public void EveryDesignTool_HasACutoffTestAtEachWrite()
    {
        Assert.Equal(McpToolHost.DesignTools.Order(), DesignCalls.Keys.Order());
        Assert.All(McpToolHost.DesignTools, t => Assert.Contains(t, RelayToolRulesTests.EditCalls.Keys));
    }

    private static Workspace WorkspaceOf(EngineFactory factory) => factory.Services.GetRequiredService<Workspace>();

    private static ProfileDocument P(Workspace workspace) => workspace.Find(workspace.List().Single(e => e.Name == "P").Id).Document!;

    private static async Task<(string Text, bool IsError)> ResultAsync(RelayPeer peer)
    {
        var result = (await peer.ExpectAsync("result", Ct))["result"]!;
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]!.GetValue<bool>());
    }

    private static JsonNode? AsSent(JsonElement? element) => element is { } e ? JsonNode.Parse(e.GetRawText()) : null;

    [Fact]
    public async Task ADesign_IsInsertedWithItsLayoutSourceSceneAndOrigin_AsGiven_AndItsFilesAreStoredUnderTheirHashes()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var design = Design(IdOf(Package), IdOf(Second));
        var arguments = DesignCalls["add_item"]();
        arguments["bindings"] = new JsonObject { ["v"] = "plugin:/infopanel.mock/cpu/cpu-load" };
        await peer.SendAsync(DesignCall(rig, "d", "add_item", arguments, design, [Package, Second]));
        var (text, error) = await ResultAsync(peer);
        Assert.False(error, text);
        Assert.Contains("Added \"Gauge\"", text);

        // One commit; the item as the design gave it, with the call's place, name and bindings.
        Assert.Equal(rev + 1, document.Rev);
        var item = Assert.IsType<SceneItem>(document.Current.Document.Items.Single(i => i.Name == "Gauge"));
        Assert.StartsWith("ai-", item.Id);
        Assert.Equal((5d, 6d), (item.Placement.X, item.Placement.Y));
        var stored = JsonSerializer.SerializeToNode(item, SpecJson.Options)!;
        Assert.True(JsonNode.DeepEquals(design["source"], stored["source"]), stored["source"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(design["origin"], stored["origin"]), stored["origin"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(design["layout"], stored["layout"]), stored["layout"]?.ToJsonString());
        Assert.True(JsonNode.DeepEquals(design["scene"], JsonNode.Parse(item.Scene.GetRawText())), item.Scene.GetRawText());
        Assert.Equal("plugin:/infopanel.mock/cpu/cpu-load", item.Bindings!["v"].Key);

        // Each file under its SHA-256, its bytes as sent.
        Assert.Equal(Package, Encoding.UTF8.GetString(workspace.Blobs.ReadVerified(IdOf(Package))!));
        Assert.Equal(Second, Encoding.UTF8.GetString(workspace.Blobs.ReadVerified(IdOf(Second))!));

        // A dep already stored needs no file; the same file again is stored once, as before.
        await peer.SendAsync(DesignCall(rig, "again", "add_item", new JsonObject { ["profile"] = "P", ["name"] = "Again" }, Design(IdOf(Package), IdOf(Second)), [Second]));
        var again = await ResultAsync(peer);
        Assert.False(again.IsError, again.Text);
        Assert.Equal(rev + 2, document.Rev);
    }

    [Fact]
    public async Task SixteenFiles_OneOfThemExactly1MiB_AreTaken()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var files = Enumerable.Range(0, 15).Select(i => $"file {i}").Append(new string('x', 1024 * 1024)).ToList();
        await peer.SendAsync(DesignCall(rig, "d", "add_item", DesignCalls["add_item"](), Design([.. files.Select(IdOf)]), files));
        var (text, error) = await ResultAsync(peer);
        Assert.False(error, text);
        Assert.All(files, f => Assert.True(workspace.Blobs.Exists(IdOf(f)), f[..Math.Min(20, f.Length)]));
    }

    public static TheoryData<string> Refusals() => ["notADep", "seventeen", "overOneMiB", "missingDep", "badDep", "webImage"];

    /// <summary>Each design the engine refuses: what the call sends, and what the refusal must name.</summary>
    private static (JsonObject Design, List<string> Files, string Names) Refused(string which)
    {
        const string invented = "sha256:0000000000000000000000000000000000000000000000000000000000000001";
        switch (which)
        {
            case "notADep":
                return (Design(IdOf(Package)), [Package, Second], IdOf(Second));
            case "seventeen":
            {
                var files = Enumerable.Range(0, 17).Select(i => $"file {i}").ToList();
                return (Design([.. files.Select(IdOf)]), files, "17");
            }
            case "overOneMiB":
            {
                var big = new string('x', 1024 * 1024 + 1);
                return (Design(IdOf(Package), IdOf(big)), [Package, big], "1 MiB");
            }
            case "missingDep":
                return (Design(IdOf(Package), invented), [Package], invented);
            case "badDep":
                return (Design(IdOf(Package), "not-a-blob"), [Package], "not-a-blob");
            default:
            {
                var design = Design(IdOf(Package));
                design["scene"] = RelayToolRulesTests.WebImageScene("https://example.com/a.png");
                return (design, [Package], "(webImage)");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedDesign_StoresNothing_AndChangesNothing(string which)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var (design, files, names) = Refused(which);
        await peer.SendAsync(DesignCall(rig, "d", "add_item", DesignCalls["add_item"](), design, files));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains(names, text);
        Assert.Contains("nothing was stored", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(files, f => Assert.False(workspace.Blobs.Exists(IdOf(f)), $"{which}: {f[..Math.Min(20, f.Length)]} was stored"));
        Assert.Equal(before, engine.Written());
    }

    [Theory]
    [InlineData("layout")]
    [InlineData("group")]
    public async Task ADesignWhoseInsertionTheEngineRefuses_StoresNothing(string which)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        // Every file and dep is right; the item it would insert isn't: a negative box, or a group that isn't there.
        var design = Design(IdOf(Package));
        var arguments = DesignCalls["add_item"]();
        if (which == "layout")
            design["layout"] = new JsonObject { ["w"] = -1 };
        else
            arguments["group"] = "No such group";
        await peer.SendAsync(DesignCall(rig, "d", "add_item", arguments, design, [Package]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.False(workspace.Blobs.Exists(IdOf(Package)), $"{which}: the file was stored");
        Assert.Equal(before, engine.Written());
    }

    [Theory]
    [InlineData("seventeen")]
    [InlineData("overOneMiB")]
    public async Task FilesWithoutADesign_AreRefused_StoringNothing(string which)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var files = which == "seventeen" ? Enumerable.Range(0, 17).Select(i => $"file {i}").ToList() : [new string('x', 1024 * 1024 + 1)];
        var arguments = DesignCalls["add_item"]();
        arguments["scene"] = TestDocs.InputScene();
        await peer.SendAsync(DesignCall(rig, "d", "add_item", arguments, null, files));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(design)", text);
        Assert.Contains("nothing was stored", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(files, f => Assert.False(workspace.Blobs.Exists(IdOf(f))));
        Assert.Equal(before, engine.Written());
    }

    [Fact]
    public async Task ADepStoredWithOtherBytes_IsMissing_AndChangesNothing()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        // A file under the dep's hash whose bytes aren't the dep's: not stored, as a verified read would find it.
        var file = workspace.Blobs.Paths.BlobFile(IdOf(Package));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "other bytes");
        await peer.SendAsync(DesignCall(rig, "d", "add_item", DesignCalls["add_item"](), Design(IdOf(Package)), null));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(missingDep)", text);
        Assert.Equal(rev, document.Rev);
    }

    [Fact]
    public async Task AProfileRenamedWhileItsFilesAreStored_GetsNoItem_NorDoesTheOneNowCalledByItsName()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writes = 0;
        engine.Host.StepHook = step =>
        {
            // The first file.
            if (step != "write" || ++writes != 1)
                return;
            paused.TrySetResult();
            release.Wait(Limit, Ct);
        };

        await peer.SendAsync(DesignCall(rig, "edit", "add_item", DesignCalls["add_item"](), Design(IdOf(Package)), [Package]));
        await paused.Task.WaitAsync(Limit, Ct);
        // P becomes Old, and another profile takes the name P.
        TestDocs.Commit(document, new JsonObject { ["op"] = "profile", ["set"] = new JsonObject { ["name"] = "Old" } });
        var rev = document.Rev;
        var other = workspace.Find(TestDocs.Create(workspace, TestDocs.Doc("P", 400, 200))).Document!;
        var otherRev = other.Rev;
        release.Set();

        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Equal(rev, document.Rev);
        Assert.Equal(otherRev, other.Rev);
        Assert.DoesNotContain(document.Current.Document.Items, i => i.Name == "Gauge");
        Assert.DoesNotContain(other.Current.Document.Items, i => i.Name == "Gauge");
    }

    private static void Rename(ProfileDocument document, string name) =>
        TestDocs.Commit(document, TestDocs.Profile(new JsonObject { ["name"] = name }));

    private static bool HasGauge(ProfileDocument document) => ItemNames.Flatten(document.Current.Document.Items).Any(i => i.Name == "Gauge");

    /// <summary>
    /// The profile is renamed away just after the call found it by its name, while its design's files and deps are checked,
    /// and another profile takes the name: the call is refused before it stores anything, and neither profile gets the
    /// item. The profile found first is carried to the commit, never looked up by its name again.
    /// </summary>
    [Fact]
    public async Task AProfileRenamedWhileTheDesignIsChecked_IsRefused_AndTheOneNowCalledByItsNameGetsNothing()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var p = P(workspace);
        var q = workspace.Find(TestDocs.Create(workspace, TestDocs.Doc("Q", 400, 200))).Document!;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        (long P, long Q)? revs = null;
        engine.Host.StepHook = step =>
        {
            if (step != "preflight")
                return;
            Rename(p, "Old");
            Rename(q, "P");
            revs = (p.Rev, q.Rev);
        };

        await peer.SendAsync(DesignCall(rig, "edit", "add_item", DesignCalls["add_item"](), Design(IdOf(Package)), [Package]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(notFound)", text);
        Assert.Equal(revs, (p.Rev, q.Rev));
        Assert.False(HasGauge(p));
        Assert.False(HasGauge(q));
        Assert.False(workspace.Blobs.Exists(IdOf(Package)), "the file was stored");
    }

    public static TheoryData<string> RenamesWhileTheCallWorks() =>
        ["renamedAtTheCommit", "abaWhileStoring", "abaAtTheCommit", "renamedAtTheRetrysCommit"];

    /// <summary>
    /// The profile the call found by its name is renamed while the call works: away, another profile taking the name, or
    /// away and back (P → Old → P); while the file is stored, just before the commit, or just before the commit of the
    /// retry a conflict brings (the group the item goes into removed at the first read and back at the retry). Each time
    /// the call is refused and no profile gets the item: the commit checks, under the document's lock and on every
    /// attempt, that the profile is still the one found under that name, by the revision of its name, which a rename
    /// back changes too.
    /// </summary>
    [Theory]
    [MemberData(nameof(RenamesWhileTheCallWorks))]
    public async Task AProfileRenamedWhileTheCallWorks_IsRefusedAtTheCommit_OnEveryAttempt(string when)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var p = P(workspace);
        var q = workspace.Find(TestDocs.Create(workspace, TestDocs.Doc("Q", 400, 200))).Document!;
        var arguments = DesignCalls["add_item"]();
        var retry = when == "renamedAtTheRetrysCommit";
        if (retry)
        {
            TestDocs.Commit(p, TestDocs.Insert(TestDocs.Group("G")));
            arguments["group"] = "G";
        }

        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var (reads, writes) = (0, 0);
        (long P, long Q)? revs = null;
        engine.Host.StepHook = step =>
        {
            if (retry && step == "read" && ++reads == 1)
                TestDocs.Commit(p, TestDocs.Remove("G"));
            else if (retry && step == "retry")
                TestDocs.Commit(p, TestDocs.Insert(TestDocs.Group("G")));
            else if (step == "write" && ++writes == (when == "abaWhileStoring" ? 1 : 2))
            {
                Rename(p, "Old");
                Rename(when.StartsWith("aba", StringComparison.Ordinal) ? p : q, "P");
                revs = (p.Rev, q.Rev);
            }
        };

        await peer.SendAsync(DesignCall(rig, "edit", "add_item", arguments, Design(IdOf(Package)), [Package]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, $"{when}: {text}");
        Assert.Contains("(notFound)", text);
        Assert.NotNull(revs);
        Assert.Equal(revs, (p.Rev, q.Rev));
        Assert.False(HasGauge(p), when);
        Assert.False(HasGauge(q), when);
    }

    /// <summary>
    /// The group the item goes into is removed after the call read the profile, before its insertion is checked: the
    /// check's conflict is read again and rebuilt (the group is gone: <c>notFound</c>) before any file is stored, so the
    /// refused call leaves nothing stored and nothing changed.
    /// </summary>
    [Fact]
    public async Task AGroupRemovedAfterTheRead_RefusesTheCall_BeforeAnyFileIsStored()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var p = P(workspace);
        TestDocs.Commit(p, TestDocs.Insert(TestDocs.Group("G")));
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var reads = 0;
        long? rev = null;
        engine.Host.StepHook = step =>
        {
            if (step != "read" || ++reads != 1)
                return;
            TestDocs.Commit(p, TestDocs.Remove("G"));
            rev = p.Rev;
        };

        var arguments = DesignCalls["add_item"]();
        arguments["group"] = "G";
        await peer.SendAsync(DesignCall(rig, "edit", "add_item", arguments, Design(IdOf(Package)), [Package]));
        var (text, error) = await ResultAsync(peer);
        Assert.True(error, text);
        Assert.Contains("(notFound)", text);
        Assert.False(workspace.Blobs.Exists(IdOf(Package)), "the file was stored");
        Assert.Equal(rev, p.Rev);
        Assert.False(HasGauge(p));
    }

    [Fact]
    public async Task ADesignToAToolThatTakesNone_IsRefused_StoringNothing()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var n = 0;
        foreach (var (tool, arguments) in RelayToolRulesTests.EditCalls.Where(c => !McpToolHost.DesignTools.Contains(c.Key)).Select(c => (c.Key, c.Value())))
        {
            // A design stands for the item's scene and box: the arguments don't give them beside it.
            foreach (var member in new[] { "scene", "width", "height" })
                arguments.Remove(member);
            await peer.SendAsync(DesignCall(rig, $"d{n++}", tool, arguments, Design(IdOf(Package)), [Package]));
            var (text, error) = await ResultAsync(peer);
            Assert.True(error, $"{tool}: {text}");
            Assert.Contains("(design)", text);
        }

        Assert.False(workspace.Blobs.Exists(IdOf(Package)));
        Assert.Equal(before, engine.Written());
    }

    [Fact]
    public async Task ADesignOnACallFromThisComputer_IsRefused_StoringNothing()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var before = engine.Written();

        // The local channel's calls carry only arguments: a design or files among them is refused, nothing stored.
        foreach (var given in new[]
        {
            new JsonObject { ["profile"] = "P", ["design"] = Design(IdOf(Package)), ["files"] = new JsonArray(Package) },
            new JsonObject { ["profile"] = "P", ["scene"] = TestDocs.InputScene(), ["files"] = new JsonArray(Package) },
        })
        {
            using var json = JsonDocument.Parse(given.ToJsonString());
            var result = await engine.Host.CallAsync("add_item", json.RootElement.Clone(), Ct);
            Assert.True(result["isError"]!.GetValue<bool>(), result.ToJsonString());
        }

        Assert.False(workspace.Blobs.Exists(IdOf(Package)));
        Assert.Equal(before, engine.Written());
    }

    [Fact]
    public async Task ADesignWithTheItemsSceneOrBoxInItsArguments_IsMalformed_AndNeverRuns()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var before = engine.Written();
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        foreach (var (name, value) in new (string, JsonNode)[] { ("scene", TestDocs.InputScene()), ("width", 10), ("height", 10) })
        {
            var arguments = DesignCalls["add_item"]();
            arguments[name] = value;
            await peer.SendAsync(DesignCall(rig, "m", "add_item", arguments, Design(IdOf(Package)), [Package]));
            Assert.Equal("""{"type":"error","code":"malformed","id":"m"}""", (await peer.NextAsync(Ct)).ToJsonString());
        }

        Assert.Empty(engine.Called);
        Assert.Equal(before, engine.Written());
    }

    public static TheoryData<string, string, int> CutoffsAtEachWrite()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var tool in DesignCalls.Keys)
        {
            foreach (var cutoff in new[] { "cancel", "deadline", "closed", "callsDeadline" })
            {
                // Writes 1 and 2 store the two files, write 3 is the commit.
                for (var write = 1; write <= 3; write++)
                    data.Add(tool, cutoff, write);
            }
        }

        return data;
    }

    /// <summary>
    /// Paused just before its <paramref name="write"/>th write (each file, then the commit), then cut off: the writes
    /// before it were made, it and every later one never are, and the call answers <c>cancelled</c>.
    /// <c>callsDeadline</c> is the call's own <c>deadline</c>, 20 s after it was sent, passing.
    /// </summary>
    [Theory]
    [MemberData(nameof(CutoffsAtEachWrite))]
    public async Task ADesign_CutOffBeforeAnyOfItsWrites_MakesNoneFromThereOn(string tool, string cutoff, int write)
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var writes = 0;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        engine.Host.StepHook = step =>
        {
            if (step != "write" || ++writes != write)
                return;
            paused.TrySetResult();
            release.Wait(Limit, Ct);
        };

        long? deadline = cutoff == "callsDeadline" ? rig.Clocks.RelayNow + 20_000 : null;
        var before = engine.Written();
        await peer.SendAsync(DesignCall(rig, "edit", tool, DesignCalls[tool](), DesignFor(tool, document, IdOf(Package), IdOf(Second)), [Package, Second], deadline));
        await paused.Task.WaitAsync(Limit, Ct);
        switch (cutoff)
        {
            case "cancel":
                await peer.SendAsync(new JsonObject { ["type"] = "cancel", ["id"] = "edit" });
                await FlushAsync(peer);
                break;
            case "deadline":
                rig.Clocks.Advance(TimeSpan.FromSeconds(61));
                break;
            case "callsDeadline":
                rig.Clocks.Advance(TimeSpan.FromSeconds(21));
                break;
            case "closed":
                peer.Abort();
                await rig.WaitForAsync(RelayState.Connecting);
                break;
        }

        release.Set();
        var (id, answer) = await engine.Answers.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        Assert.Equal("edit", id);
        Assert.True(answer.Cancelled, answer.Result.ToJsonString());
        Assert.Equal(write > 1, workspace.Blobs.Exists(IdOf(Package)));
        Assert.Equal(write > 2, workspace.Blobs.Exists(IdOf(Second)));
        Assert.Equal(rev, document.Rev);
        Assert.Equal(before, engine.Written());
        Assert.DoesNotContain(document.Current.Document.Items, i => i.Name == "Gauge" || (i is SceneItem { Source: not null }));
        if (cutoff != "closed")
            Assert.Equal("""{"type":"cancelled","id":"edit"}""", (await peer.NextAsync(Ct)).ToJsonString());
    }

    [Fact]
    public async Task ACallsLaterDeadline_DoesntExtendIt_PastSentAtAnd60s()
    {
        using var factory = new EngineFactory();
        var engine = new Engine(factory);
        var workspace = WorkspaceOf(factory);
        var document = P(workspace);
        var rev = document.Rev;
        await using var rig = await StartAsync(engine.Recording);
        var peer = await rig.ConnectAsync();

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var writes = 0;
        engine.Host.StepHook = step =>
        {
            // The commit: after the one file.
            if (step != "write" || ++writes != 2)
                return;
            paused.TrySetResult();
            release.Wait(Limit, Ct);
        };

        // Sent 5 s before it arrives, with a deadline 10 minutes after that: 55 s, what is left of sentAt + 60 s.
        var sentAt = rig.Clocks.RelayNow - 5000;
        await peer.SendAsync(DesignCall(rig, "edit", "add_item", DesignCalls["add_item"](), Design(IdOf(Package)), [Package], sentAt + 600_000, sentAt));
        await paused.Task.WaitAsync(Limit, Ct);
        rig.Clocks.Advance(TimeSpan.FromSeconds(56));
        release.Set();
        var (_, answer) = await engine.Answers.Reader.ReadAsync(Ct).AsTask().WaitAsync(Limit, Ct);
        Assert.True(answer.Cancelled, answer.Result.ToJsonString());
        Assert.Equal(rev, document.Rev);
    }
}
