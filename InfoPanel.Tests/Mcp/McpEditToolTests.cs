using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Mcp.Tools;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Mcp;

/// <summary>
/// The tools that change profiles (<see cref="McpToolHost"/>), on test engines: each call one commit at the revision it
/// read, with the document's tag; a conflict re-read once; saved only when the profile was clean as the commit applied.
/// </summary>
public sealed class McpEditToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static McpToolHost Host(EngineFactory factory) => factory.Services.GetRequiredService<McpToolHost>();

    private static Workspace Workspace(EngineFactory factory) => factory.Services.GetRequiredService<Workspace>();

    private static async Task<(string Text, bool IsError)> Call(McpToolHost host, string tool, JsonObject arguments)
    {
        using var json = JsonDocument.Parse(arguments.ToJsonString());
        var result = await host.CallAsync(tool, json.RootElement.Clone(), Ct);
        var text = string.Join("\n", result["content"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
        return (text, result["isError"]!.GetValue<bool>());
    }

    /// <summary>A saved 400×200 profile named <paramref name="name"/> with two rectangles, <c>a</c> and <c>b</c>.</summary>
    private static ProfileDocument Seed(EngineFactory factory, string name)
    {
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc(name, 400, 200, TestDocs.Scene("a", 10, 10), TestDocs.Scene("b", 100, 10)));
        var document = workspace.Find(id).Document!;
        Assert.False(document.Dirty);
        return document;
    }

    private static Item ItemOf(ProfileDocument document, string id) =>
        Flatten(document.Read().Document.Items).Single(i => i.Id == id);

    private static IEnumerable<Item> Flatten(IEnumerable<Item> items) =>
        items.SelectMany(i => i is GroupItem g ? new[] { i }.Concat(Flatten(g.Children)) : [i]);

    private static IEnumerable<Item> SavedItems(ProfileDocument document) => Flatten(document.Saved.Document.Items);

    [Fact]
    public async Task CreateProfile_StoresAnEmptyProfile_SavedAtOnce()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var (text, error) = await Call(host, "create_profile", new JsonObject { ["name"] = "Desk", ["width"] = 800, ["height"] = 480, ["background"] = "#101010" });
        Assert.False(error, text);
        var entry = Workspace(factory).List().Single(e => e.Name == "Desk");
        Assert.Contains(entry.Id, text);
        var document = Workspace(factory).Find(entry.Id).Document!;
        Assert.False(document.Dirty);
        Assert.Equal((800, 480, "#101010"), (document.Saved.Document.Profile.Width, document.Saved.Document.Profile.Height, document.Saved.Document.Profile.Background));
        Assert.Empty(document.Saved.Document.Items);

        var (invalid, invalidError) = await Call(host, "create_profile", new JsonObject { ["name"] = "Bad", ["width"] = 800, ["height"] = 480, ["background"] = "blue" });
        Assert.True(invalidError);
        Assert.Contains("background", invalid);
    }

    [Fact]
    public async Task AddItem_IsOneCommitAtTheRevItRead_WithTheDocumentsTag_AndIsSaved()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Add");
        long? read = null;
        host.StepHook = step =>
        {
            if (step == "read")
                read = document.Rev;
        };

        var (text, error) = await Call(host, "add_item", new JsonObject
        {
            ["profile"] = "Add",
            ["scene"] = TestDocs.InputScene(),
            ["x"] = 20,
            ["y"] = 40.5,
            ["width"] = 120,
            ["height"] = 30,
            ["name"] = "CPU bar",
            ["bindings"] = new JsonObject { ["v"] = "plugin:/infopanel.mock/cpu/cpu-load" },
        });
        Assert.False(error, text);
        Assert.Contains("Saved", text);
        Assert.Equal(read + 1, document.Rev); // one commit, one revision
        Assert.False(document.Dirty);
        Assert.True(document.CanUndo);

        var added = (SceneItem)Flatten(document.Read().Document.Items).Single(i => i.Name == "CPU bar");
        Assert.Contains(added.Id, text);
        Assert.Equal((20, 40.5), (added.Placement.X, added.Placement.Y));
        Assert.Equal((120.0, 30.0), (added.Layout!.W!.Value, added.Layout.H!.Value));
        Assert.Equal("plugin:/infopanel.mock/cpu/cpu-load", added.Bindings!["v"].Key);
        Assert.Contains(SavedItems(document), i => i.Id == added.Id);

        // One undo step takes the whole call back.
        var undo = document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev });
        Assert.NotNull(undo.Result);
        Assert.DoesNotContain(Flatten(document.Read().Document.Items), i => i.Id == added.Id);
    }

    [Fact]
    public async Task AddItem_IntoAGroup_AtAnIndex()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Grouped", 400, 200, TestDocs.Group("card", TestDocs.Scene("x1"), TestDocs.Scene("x2"))));
        var (text, error) = await Call(host, "add_item", new JsonObject { ["profile"] = id, ["scene"] = TestDocs.Rect(), ["group"] = "card", ["index"] = 1, ["name"] = "middle" });
        Assert.False(error, text);
        var group = (GroupItem)workspace.Find(id).Document!.Read().Document.Items.Single();
        Assert.Equal(["x1", "middle", "x2"], group.Children.Select(c => c.Name ?? c.Id));
    }

    [Fact]
    public async Task UpdateItem_ChangesEveryPartGiven_InOneCommit_AndKeepsTheRest()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Update");
        var before = document.Rev;

        var (text, error) = await Call(host, "update_item", new JsonObject
        {
            ["profile"] = "Update",
            ["item"] = "a",
            ["x"] = 33,
            ["rotation"] = 15,
            ["width"] = 50,
            ["height"] = 25,
            ["scene"] = TestDocs.InputScene(),
            ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = "plugin:/infopanel.mock/cpu/cpu-load", ["stat"] = "max" } },
            ["name"] = "Load",
            ["visible"] = false,
        });
        Assert.False(error, text);
        Assert.Equal(before + 1, document.Rev);
        var a = (SceneItem)ItemOf(document, "a");
        Assert.Equal((33.0, 10.0, 15.0), (a.Placement.X, a.Placement.Y, a.Placement.Rotation!.Value)); // y kept
        Assert.Equal((50.0, 25.0), (a.Layout!.W!.Value, a.Layout.H!.Value));
        Assert.Equal("number", a.Scene.GetProperty("inputs").GetProperty("v").GetProperty("type").GetString());
        Assert.Equal(("plugin:/infopanel.mock/cpu/cpu-load", "max"), (a.Bindings!["v"].Key, a.Bindings["v"].Stat));
        Assert.Equal(("Load", false), (a.Name, a.Visible));

        // By its new name; only the size: the scene stays.
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Update", ["item"] = "Load", ["width"] = 70 });
        Assert.False(error, text);
        a = (SceneItem)ItemOf(document, "a");
        Assert.Equal((70.0, 25.0), (a.Layout!.W!.Value, a.Layout.H!.Value));
        Assert.Equal("number", a.Scene.GetProperty("inputs").GetProperty("v").GetProperty("type").GetString());

        var (nothing, nothingError) = await Call(host, "update_item", new JsonObject { ["profile"] = "Update", ["item"] = "a" });
        Assert.True(nothingError);
        Assert.Contains("nothing to change", nothing);
        var (missing, missingError) = await Call(host, "update_item", new JsonObject { ["profile"] = "Update", ["item"] = "zz", ["x"] = 1 });
        Assert.True(missingError);
        Assert.Contains("There's no item zz", missing);
    }

    [Fact]
    public async Task RemoveItem_RemovesIt_AndIsUndoable()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Remove");
        var (text, error) = await Call(host, "remove_item", new JsonObject { ["profile"] = "Remove", ["item"] = "b" });
        Assert.False(error, text);
        Assert.DoesNotContain(Flatten(document.Read().Document.Items), i => i.Id == "b");
        Assert.DoesNotContain(SavedItems(document), i => i.Id == "b");
        Assert.NotNull(document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev }).Result);
        Assert.Contains(Flatten(document.Read().Document.Items), i => i.Id == "b");
    }

    [Fact]
    public async Task AnInvalidScene_AnswersItsCodesAndPaths_AndCommitsNothing()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Invalid");
        var before = document.Rev;
        var (text, error) = await Call(host, "add_item", new JsonObject { ["profile"] = "Invalid", ["scene"] = TestDocs.InvalidScene() });
        Assert.True(error);
        Assert.Contains("The engine refused the change", text);
        Assert.Contains("ref.input", text);
        Assert.Contains("scene/nodes/0/w", text);
        Assert.DoesNotContain("/ops/", text);
        Assert.Contains("get_design_guide", text);
        Assert.Equal(before, document.Rev);
    }

    [Fact]
    public async Task ACommitSlippedInBetween_IsRetriedOnce_ThenTheCallIsAnError()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Race");
        var slipped = 0;
        var times = 1;
        host.StepHook = step =>
        {
            // Another client moves the same item between the tool's read and its commit.
            if (step == "read" && slipped < times)
            {
                slipped++;
                TestDocs.Commit(document, TestDocs.Place("a", 200 + slipped, 0));
            }
        };

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Race", ["item"] = "a", ["x"] = 5 });
        Assert.False(error, text);
        Assert.Equal(5, ItemOf(document, "a").Placement.X); // re-read and applied over the other change
        Assert.Equal(1, slipped);

        slipped = 0;
        times = 2;
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Race", ["item"] = "a", ["x"] = 6 });
        Assert.True(error);
        Assert.Contains("Someone changed this profile while I worked. Read it again with get_profile and retry.", text);
        Assert.Equal(2, slipped);
        Assert.Equal(202, ItemOf(document, "a").Placement.X); // the other client's change stands
    }

    [Fact]
    public async Task AProfileWithUnsavedChanges_IsLeftUnsaved_AndTheAnswerSaysSo()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Dirty");
        TestDocs.Commit(document, TestDocs.Attrs("b", name: "editing"));
        Assert.True(document.Dirty);

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Dirty", ["item"] = "a", ["x"] = 77 });
        Assert.False(error, text);
        Assert.Contains("Not saved: Dirty has unsaved changes from the editor; save there.", text);
        Assert.True(document.Dirty);
        Assert.Equal(77, ItemOf(document, "a").Placement.X);
        Assert.Equal(10, SavedItems(document).Single(i => i.Id == "a").Placement.X);
        Assert.Null(SavedItems(document).Single(i => i.Id == "b").Name);
    }

    /// <summary>
    /// Codex plan-1 #3: the tool reads a clean profile; the editor commits another field before the tool's commit, which
    /// merges past it. Clean is decided as the commit applies: nothing is saved (the save would carry the editor's
    /// half-done change), and the answer says so.
    /// </summary>
    [Fact]
    public async Task AnEditorCommitBetweenTheReadAndTheCommit_LeavesItUnsaved()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Between");
        var read = 0L;
        host.StepHook = step =>
        {
            if (step != "read" || read != 0)
                return;
            read = document.Rev;
            Assert.False(document.Dirty);
            TestDocs.Commit(document, TestDocs.Attrs("b", name: "half-done"));
        };

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Between", ["item"] = "a", ["x"] = 55 });
        Assert.False(error, text);
        Assert.Equal(read + 2, document.Rev); // the editor's commit, then the tool's, merged
        Assert.Contains("Not saved: Between has unsaved changes from the editor; save there.", text);
        Assert.True(document.Dirty);
        var saved = SavedItems(document).ToList();
        Assert.Equal(10, saved.Single(i => i.Id == "a").Placement.X);
        Assert.Null(saved.Single(i => i.Id == "b").Name);
    }

    /// <summary>
    /// Codex plan-1 #3: a commit that lands between the tool's commit and its save: the save, guarded by the commit's
    /// revision, is refused; the change stays applied and the answer says "Changed, but not saved", never an error.
    /// </summary>
    [Fact]
    public async Task ACommitBetweenTheToolsCommitAndItsSave_ChangedButNotSaved()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "After");
        host.StepHook = step =>
        {
            if (step == "committed")
                TestDocs.Commit(document, TestDocs.Attrs("b", name: "same moment"));
        };

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "After", ["item"] = "a", ["x"] = 66 });
        Assert.False(error, text);
        Assert.Contains("Changed, but not saved: the editor changed After at the same moment; save there.", text);
        Assert.Equal(66, ItemOf(document, "a").Placement.X);
        Assert.True(document.Dirty);
        var saved = SavedItems(document).ToList();
        Assert.Equal(10, saved.Single(i => i.Id == "a").Placement.X);
        Assert.Null(saved.Single(i => i.Id == "b").Name);
    }

    [Fact]
    public async Task APreviewClient_GetsTheDocEvent_OfTheToolsChange()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Watched");
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = document.Id, scale = 1, maxFps = 30 }, Ct);
        var first = await client.ReceiveTextAsync("doc", Ct);
        var rev = first.GetProperty("rev").GetInt64();

        var (text, error) = await Call(host, "remove_item", new JsonObject { ["profile"] = document.Id, ["item"] = "a" });
        Assert.False(error, text);
        var doc = await client.ReceiveTextAsync("doc", Ct, d => d.GetProperty("rev").GetInt64() > rev);
        Assert.Equal(rev + 1, doc.GetProperty("rev").GetInt64());
    }

    [Fact]
    public async Task AddAsset_StoresAnSvg_AndAnswersItsIdAndSize_OtherTextItsMediaError()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="32" viewBox="0 0 64 32"><rect width="64" height="32" fill="#ff8800"/></svg>""";
        var (text, error) = await Call(host, "add_asset", new JsonObject { ["svg"] = svg });
        Assert.False(error, text);
        var id = BlobStore.IdOf(Encoding.UTF8.GetBytes(svg));
        Assert.Contains(id, text);
        Assert.Contains("64×32", text);
        Assert.Equal(Encoding.UTF8.GetByteCount(svg), Workspace(factory).Blobs.StoredSize(id));

        const string words = "plain words, not a picture";
        var (bad, badError) = await Call(host, "add_asset", new JsonObject { ["svg"] = words });
        Assert.True(badError);
        Assert.Contains("media.", bad);
        Assert.Null(Workspace(factory).Blobs.StoredSize(BlobStore.IdOf(Encoding.UTF8.GetBytes(words)))); // nothing stored
    }

    [Fact]
    public async Task ShowOnDesktop_TurnsTheWindowOnAndOff_KeepingItsOtherSettings()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Desk");
        TestDocs.Commit(document, TestDocs.Profile(new JsonObject { ["window"] = new JsonObject { ["enabled"] = false, ["topmost"] = true, ["x"] = 12 } }));
        document.Save();

        var (text, error) = await Call(host, "show_on_desktop", new JsonObject { ["profile"] = "Desk" });
        Assert.False(error, text);
        var window = document.Read().Document.Profile.Window!;
        Assert.Equal((true, true, 12), (window.Enabled, window.Topmost!.Value, window.X!.Value));
        Assert.False(document.Dirty);

        (text, error) = await Call(host, "show_on_desktop", new JsonObject { ["profile"] = "Desk", ["show"] = false });
        Assert.False(error, text);
        Assert.False(document.Read().Document.Profile.Window!.Enabled);
    }

    [Fact]
    public async Task ListFonts_ListsTheBundledFamily()
    {
        using var factory = new EngineFactory();
        var (text, error) = await Call(Host(factory), "list_fonts", new JsonObject());
        Assert.False(error, text);
        Assert.Contains("Inter", text);
    }

    /// <summary>
    /// Triage p2 #10: every installed family costs the AI context. A <c>query</c> (a case-insensitive part of the name)
    /// narrows the list; the families InfoPanel bundles are marked, and without a query they come first.
    /// </summary>
    [Fact]
    public async Task ListFonts_TakesAQuery_AndMarksTheBundledFamiliesFirst()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var (all, error) = await Call(host, "list_fonts", new JsonObject());
        Assert.False(error, all);
        var families = all.Split('\n').Skip(1).ToList();
        Assert.StartsWith("Inter (bundled", families[0]);
        Assert.Single(families, f => f.Contains("(bundled"));

        var (inter, interError) = await Call(host, "list_fonts", new JsonObject { ["query"] = "iNTe" });
        Assert.False(interError, inter);
        var found = inter.Split('\n').Skip(1).ToList();
        Assert.Contains(found, f => f.StartsWith("Inter (bundled", StringComparison.Ordinal));
        Assert.All(found, f => Assert.Contains("inte", f, StringComparison.OrdinalIgnoreCase));
        Assert.True(found.Count < families.Count || families.Count == found.Count && families.All(f => f.Contains("inte", StringComparison.OrdinalIgnoreCase)));

        var (none, noneError) = await Call(host, "list_fonts", new JsonObject { ["query"] = "no-such-family-zq" });
        Assert.False(noneError, none);
        Assert.Contains("No font family", none);
        Assert.Contains("Inter", none); // what a scene can always use
    }

    /// <summary>
    /// Codex p2 #1: a retry resolves nothing again. The tool found the item named CPU (a); another client renames it
    /// Old and names b CPU meanwhile: the retry removes a, the item it found, never b.
    /// </summary>
    [Fact]
    public async Task ARetry_ActsOnTheItemItFoundFirst_NotOneGivenItsNameMeanwhile()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Pinned");
        TestDocs.Commit(document, TestDocs.Attrs("a", name: "CPU"), TestDocs.Attrs("b", name: "GPU"));
        document.Save();
        var slipped = 0;
        host.StepHook = step =>
        {
            if (step == "read" && slipped++ == 0)
                TestDocs.Commit(document, TestDocs.Attrs("a", name: "Old"), TestDocs.Attrs("b", name: "CPU"));
        };

        var (text, error) = await Call(host, "remove_item", new JsonObject { ["profile"] = "Pinned", ["item"] = "CPU" });
        Assert.False(error, text);
        Assert.Equal(2, slipped); // it conflicted and read again
        var items = Flatten(document.Read().Document.Items).ToList();
        Assert.DoesNotContain(items, i => i.Id == "a");
        Assert.Contains(items, i => i.Id == "b" && i.Name == "CPU");
    }

    /// <summary>Codex p2 #1: the item the tool found is removed meanwhile, and b is given its name: an error, b untouched.</summary>
    [Fact]
    public async Task ARetry_WhoseItemWasRemovedMeanwhile_IsAnError_AndChangesNothingElse()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Vanished");
        TestDocs.Commit(document, TestDocs.Attrs("a", name: "CPU"));
        document.Save();
        var slipped = 0;
        host.StepHook = step =>
        {
            if (step == "read" && slipped++ == 0)
                TestDocs.Commit(document, TestDocs.Remove("a"), TestDocs.Attrs("b", name: "CPU"));
        };

        var (text, error) = await Call(host, "remove_item", new JsonObject { ["profile"] = "Vanished", ["item"] = "CPU" });
        Assert.True(error);
        Assert.Contains("while I worked", text);
        Assert.Contains("\"CPU\" (a)", text);
        Assert.Contains(Flatten(document.Read().Document.Items), i => i.Id == "b" && i.Name == "CPU");
    }

    /// <summary>
    /// Codex p2 #1: profile names aren't resolved again either. The tool found Desk (p); meanwhile p is renamed Old and
    /// q is named Desk: the retry changes p, never q.
    /// </summary>
    [Fact]
    public async Task ARetry_ChangesTheProfileItFoundFirst_NotOneGivenItsNameMeanwhile()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var p = Seed(factory, "Desk");
        var q = Seed(factory, "Other");
        var slipped = 0;
        host.StepHook = step =>
        {
            if (step != "read" || slipped++ != 0)
                return;
            TestDocs.Commit(p, TestDocs.Profile(new JsonObject { ["name"] = "Old" }), TestDocs.Place("a", 1, 1));
            TestDocs.Commit(q, TestDocs.Profile(new JsonObject { ["name"] = "Desk" }));
        };

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Desk", ["item"] = "a", ["x"] = 5 });
        Assert.False(error, text);
        Assert.Equal(2, slipped);
        Assert.Equal(5, ItemOf(p, "a").Placement.X);
        Assert.Equal(10, ItemOf(q, "a").Placement.X);
    }

    /// <summary>
    /// Codex p2 #1: the profile is deleted and created again under its id before the retry: the retry never acts on the
    /// new one; it is an error that names the profile.
    /// </summary>
    [Fact]
    public async Task ARetry_OnAProfileReplacedMeanwhile_IsAnError_NamingIt()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var p = Seed(factory, "Swapped");
        var id = p.Id;
        host.StepHook = step =>
        {
            if (step == "read" && ItemOf(p, "a").Placement.X == 10)
            {
                TestDocs.Commit(p, TestDocs.Place("a", 1, 1)); // the tool's commit conflicts
            }
            else if (step == "retry")
            {
                Assert.True(workspace.DeleteWithItsTag(id));
                var again = TestDocs.Doc("Swapped", 400, 200, TestDocs.Scene("a", 10, 10), TestDocs.Scene("b", 100, 10));
                again["id"] = id;
                Assert.NotNull(workspace.Create(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["document"] = again }.ToJsonString())).Entry);
            }
        };

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "a", ["x"] = 5 });
        Assert.True(error, text);
        Assert.Contains(id, text);
        Assert.Contains("deleted or replaced while I worked", text);
        Assert.Equal(10, ItemOf(workspace.Find(id).Document!, "a").Placement.X);
    }

    /// <summary>
    /// Codex p2 #2: a save the folder refuses (no write access) after the change applied is "Changed, but not saved"
    /// with its reason, never a failure that invites a retry and a second copy.
    /// </summary>
    [Fact]
    public async Task ASaveTheFolderRefuses_IsChangedButNotSaved_NeverAFailure()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Locked");
        var folder = Path.Combine(factory.DataDir, "profiles", document.Id);
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (text, error) = await Call(host, "add_item", new JsonObject { ["profile"] = "Locked", ["scene"] = TestDocs.Rect(), ["name"] = "Once" });
            Assert.False(error, text);
            Assert.Contains("Changed, but not saved: saving failed (", text);
            Assert.Single(Flatten(document.Read().Document.Items), i => i.Name == "Once");
            Assert.True(document.Dirty);
        }
        finally
        {
            File.SetUnixFileMode(folder, mode);
        }
    }

    /// <summary>
    /// Codex p2 #8: on a computer where the engine can't open desktop windows (a test engine has no desktop platform),
    /// show_on_desktop still turns the setting on, and says no window opens instead of claiming one.
    /// </summary>
    [Fact]
    public async Task ShowOnDesktop_WhereNoWindowCanOpen_SaysSo_AndStillTurnsTheSettingOn()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Nowhere");
        var (text, error) = await Call(host, "show_on_desktop", new JsonObject { ["profile"] = "Nowhere" });
        Assert.False(error, text);
        Assert.True(document.Read().Document.Profile.Window!.Enabled);
        Assert.False(document.Dirty);
        Assert.DoesNotContain("in a window of its own", text);
        Assert.Contains("no window opens", text);
        Assert.Contains("can't show desktop windows", text);

        // Where windows open, the answer says it is on the desktop.
        using var platform = new InfoPanel.Tests.Desktop.FakeDesktopPlatform();
        using var engine = new EngineFactory();
        using var shown = engine.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<InfoPanel.Desktop.IDesktopPlatform>(platform)));
        var other = TestDocs.Create(shown.Services.GetRequiredService<Workspace>(), TestDocs.Doc("Somewhere", 400, 200, TestDocs.Scene("a")));
        (text, error) = await Call(shown.Services.GetRequiredService<McpToolHost>(), "show_on_desktop", new JsonObject { ["profile"] = other });
        Assert.False(error, text);
        Assert.Contains("Somewhere is on the desktop, in a window of its own.", text);
    }

    [Fact]
    public async Task ANameOrIdThatIsntThere_AndATargetOfTheWrongKind_AreErrors()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        TestDocs.Create(workspace, TestDocs.Doc("Kinds", 400, 200, TestDocs.Group("card", TestDocs.Scene("x1"))));
        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Kinds", ["item"] = "card", ["scene"] = TestDocs.Rect() });
        Assert.True(error);
        Assert.Contains("group", text);
        (text, error) = await Call(host, "add_item", new JsonObject { ["profile"] = "Kinds", ["scene"] = TestDocs.Rect(), ["group"] = "x1" });
        Assert.True(error);
        Assert.Contains("x1", text);
        (text, error) = await Call(host, "remove_item", new JsonObject { ["profile"] = "Nope", ["item"] = "x1" });
        Assert.True(error);
        Assert.Contains("There's no profile Nope", text);
    }
}
