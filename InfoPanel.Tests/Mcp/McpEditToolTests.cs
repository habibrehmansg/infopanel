using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Mcp.Tools;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
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

    /// <summary>A <paramref name="width"/>×<paramref name="height"/> picture SkiaSharp encodes as <paramref name="format"/>.</summary>
    internal static byte[] Encoded(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(0x22, 0x88, 0xcc));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] Picture(string format) => format switch
    {
        "PNG" => Encoded(SKEncodedImageFormat.Png, 40, 20),
        "JPEG" => Encoded(SKEncodedImageFormat.Jpeg, 40, 20),
        "WebP" => Encoded(SKEncodedImageFormat.Webp, 40, 20),
        "GIF" => File.ReadAllBytes(Path.Combine(InfoPanel.Tests.Editing.LiveEngine.RepoRoot(), "InfoPanel.Core.Tests", "Fixtures", "assets", "blink.gif")),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static int BlobFiles(EngineFactory factory) =>
        Directory.Exists(Workspace(factory).Blobs.Paths.BlobsDirectory)
            ? Directory.EnumerateFiles(Workspace(factory).Blobs.Paths.BlobsDirectory, "*", SearchOption.AllDirectories).Count()
            : 0;

    [Theory]
    [InlineData("PNG", 40, 20)]
    [InlineData("JPEG", 40, 20)]
    [InlineData("WebP", 40, 20)]
    [InlineData("GIF", 48, 32)]
    public async Task AddAsset_StoresAPictureGivenAsBase64_ItsBytesAsGiven_AndAnswersItsFormatIdAndSize(string format, int width, int height)
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var bytes = Picture(format);
        var (text, error) = await Call(host, "add_asset", new JsonObject { ["data"] = Convert.ToBase64String(bytes) });
        Assert.False(error, text);
        var id = BlobStore.IdOf(bytes);
        Assert.Equal(
            $"Stored the {format} picture as {id} ({width}×{height}, {bytes.Length} bytes). Show it with a media node in a scene: " +
            $"{{\"kind\":\"media\",\"x\":0,\"y\":0,\"w\":{width},\"h\":{height},\"source\":{{\"asset\":\"{id}\"}}}}", text);
        Assert.Equal(bytes.Length, Workspace(factory).Blobs.StoredSize(id));
        Assert.Equal(bytes, Workspace(factory).Blobs.ReadHashed(id));
    }

    /// <summary>The answer to <c>svg</c>, word for word: the service reads its first sentence.</summary>
    [Fact]
    public async Task AddAsset_AnswersAnSvg_WordForWord()
    {
        using var factory = new EngineFactory();
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="32" viewBox="0 0 64 32"><rect width="64" height="32" fill="#ff8800"/></svg>""";
        var (text, error) = await Call(Host(factory), "add_asset", new JsonObject { ["svg"] = svg });
        Assert.False(error, text);
        var id = BlobStore.IdOf(Encoding.UTF8.GetBytes(svg));
        Assert.Equal(
            $"Stored the SVG as {id} (64×32, 134 bytes). Show it with a media node in a scene: " +
            $"{{\"kind\":\"media\",\"x\":0,\"y\":0,\"w\":64,\"h\":32,\"source\":{{\"asset\":\"{id}\"}}}}", text);
    }

    [Fact]
    public async Task AddAsset_TakesAnSvgAsBase64Too_AndBase64WithWhitespace_AnsweringAsForSvg()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="32" viewBox="0 0 64 32"><rect width="64" height="32" fill="#ff8800"/></svg>""";
        var bytes = Encoding.UTF8.GetBytes(svg);
        var (text, error) = await Call(host, "add_asset", new JsonObject { ["data"] = Convert.ToBase64String(bytes, Base64FormattingOptions.InsertLineBreaks) + "\n" });
        Assert.False(error, text);
        var id = BlobStore.IdOf(bytes);
        Assert.StartsWith($"Stored the SVG as {id} (64×32, {bytes.Length} bytes). ", text);
        Assert.Equal(bytes, Workspace(factory).Blobs.ReadHashed(id));
    }

    [Fact]
    public async Task AddAsset_RefusesAVideo_BothOrNeither_BadBase64_AndBytesThatArentAPicture_StoringNothing()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        const string svg = """<svg xmlns="http://www.w3.org/2000/svg" width="8" height="8" viewBox="0 0 8 8"><rect width="8" height="8" fill="#ff8800"/></svg>""";
        var png = Picture("PNG");
        var video = VideoFixture.Read("clip-h264.mp4");
        var html = Encoding.UTF8.GetBytes("<!doctype html><html><body><img src=\"x.png\"></body></html>");
        var before = BlobFiles(factory);

        foreach (var (arguments, expected) in new (JsonObject, string)[]
        {
            (new JsonObject { ["data"] = Convert.ToBase64String(video) }, "add_asset stores pictures, not videos (media.format). Nothing was stored."),
            (new JsonObject { ["svg"] = svg, ["data"] = Convert.ToBase64String(png) }, "add_asset takes svg or data, not both (arguments). Nothing was stored."),
            (new JsonObject(), "add_asset needs svg (an SVG's text) or data (a picture file's bytes in base64) (arguments)."),
            (new JsonObject { ["data"] = "%%%" }, "data must be base64 (arguments). Nothing was stored."),
            (new JsonObject { ["data"] = Convert.ToBase64String(png).Replace('+', '-').Replace('/', '_').TrimEnd('=') + "-_" }, "data must be base64 (arguments). Nothing was stored."),
        })
        {
            var (text, error) = await Call(host, "add_asset", arguments);
            Assert.True(error, $"{arguments.ToJsonString()[..Math.Min(80, arguments.ToJsonString().Length)]}: {text}");
            Assert.Equal(expected, text);
        }

        var (page, pageError) = await Call(host, "add_asset", new JsonObject { ["data"] = Convert.ToBase64String(html) });
        Assert.True(pageError, page);
        Assert.StartsWith("That isn't a picture InfoPanel can show: media.", page);
        Assert.EndsWith(". Nothing was stored.", page);
        Assert.DoesNotContain("get_design_guide", page);

        Assert.Equal(before, BlobFiles(factory));
        foreach (var bytes in new[] { Encoding.UTF8.GetBytes(svg), png, video, html })
            Assert.Null(Workspace(factory).Blobs.StoredSize(BlobStore.IdOf(bytes)));
    }

    [Fact]
    public void AddAsset_ListsSvgAndData_NeitherRequired_DataUpToAFileOf6MiBInBase64()
    {
        using var factory = new EngineFactory();
        var tool = Host(factory).All.Single(t => t.Name == "add_asset");
        Assert.Equal("Add a picture", tool.Title);
        var schema = tool.InputSchema;
        Assert.Empty(schema["required"]!.AsArray());
        Assert.Equal(["svg", "data"], schema["properties"]!.AsObject().Select(p => p.Key).ToArray());
        Assert.Equal((6 * 1024 * 1024 + 2) / 3 * 4, schema["properties"]!["data"]!["maxLength"]!.GetValue<int>()); // 6 MiB as base64
    }

    [Fact]
    public async Task ShowOnDesktop_TurnsTheWindowOnAndOff_KeepingItsOtherSettings()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Desk");
        TestDocs.Commit(document, TestDocs.Profile(new JsonObject { ["window"] = new JsonObject { ["enabled"] = false, ["level"] = "desktop", ["x"] = 12 } }));
        document.Save();

        var (text, error) = await Call(host, "show_on_desktop", new JsonObject { ["profile"] = "Desk" });
        Assert.False(error, text);
        var window = document.Read().Document.Profile.Window!;
        Assert.Equal((true, "desktop", 12), (window.Enabled, window.Level, window.X!.Value));
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

    // Groups through the tools: add_items' group entry (children), update_item's group and index.

    /// <summary>The items' tree as names (an item without one by its id), a group's children in brackets.</summary>
    private static string Tree(IEnumerable<Item> items) =>
        string.Join(", ", items.Select(i => (i.Name ?? i.Id) + (i is GroupItem g ? $"[{Tree(g.Children)}]" : "")));

    private static string Tree(ProfileDocument document) => Tree(document.Read().Document.Items);

    private static Item Named(ProfileDocument document, string name) => Flatten(document.Read().Document.Items).Single(i => i.Name == name);

    private static JsonObject Child(string name, double x, double y) => new() { ["name"] = name, ["scene"] = TestDocs.Rect(), ["x"] = x, ["y"] = y };

    /// <summary><paramref name="count"/> scene children, named <c>Child 0</c> on.</summary>
    private static JsonArray Children(int count) => new([.. Enumerable.Range(0, count).Select(i => (JsonNode)Child($"Child {i}", i, 0))]);

    [Fact]
    public async Task AddItems_AGroupEntry_IsInsertedWithItsChildren_InOneCommit_AndOneUndoStepTakesItBack()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Pane");
        var before = document.Rev;

        var (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Pane",
            ["items"] = new JsonArray(
                new JsonObject { ["name"] = "Background", ["scene"] = TestDocs.Rect(), ["width"] = 400, ["height"] = 200 },
                new JsonObject
                {
                    ["name"] = "CPU",
                    ["x"] = 20,
                    ["y"] = 30,
                    ["children"] = new JsonArray(Child("CPU card", 0, 0), Child("CPU title", 16, 12), Child("CPU temperature", 16, 48)),
                }),
        });
        Assert.False(error, text);
        Assert.Equal(before + 1, document.Rev); // one commit
        Assert.Equal("a, b, Background, CPU[CPU card, CPU title, CPU temperature]", Tree(document));
        var group = Assert.IsType<GroupItem>(Named(document, "CPU"));
        Assert.StartsWith("ai-", group.Id);
        Assert.Equal((20d, 30d), (group.Placement.X, group.Placement.Y));
        // The children's x, y are as given: from the group's x, y, as the document stores them.
        Assert.Equal([(0d, 0d), (16d, 12d), (16d, 48d)], group.Children.Select(c => (c.Placement.X, c.Placement.Y)));
        Assert.All(group.Children, c => Assert.StartsWith("ai-", c.Id));
        Assert.Contains($"\"CPU\" ({group.Id}, a group of 3: \"CPU card\" ({group.Children[0].Id})", text);
        Assert.StartsWith("Added 2 items to Pane in one change", text);
        Assert.Contains(SavedItems(document), i => i.Id == group.Children[2].Id);

        // get_profile lists it nested, its children's x, y as stored.
        var (summary, summaryError) = await Call(host, "get_profile", new JsonObject { ["profile"] = "Pane" });
        Assert.False(summaryError, summary);
        var listed = JsonNode.Parse(summary)!["items"]!.AsArray().Single(i => i!["name"]?.GetValue<string>() == "CPU")!;
        Assert.Equal("group", listed["kind"]!.GetValue<string>());
        Assert.Equal(["CPU card", "CPU title", "CPU temperature"], listed["children"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal(48, listed["children"]![2]!["y"]!.GetValue<double>());

        // One undo step takes the whole call back.
        Assert.NotNull(document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev }).Result);
        Assert.Equal("a, b", Tree(document));
    }

    [Fact]
    public async Task AddItems_AnEmptyGroup_AndAGroupIntoAnExistingGroup_AtAnIndex()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Nest", 400, 200, TestDocs.Group("card", TestDocs.Scene("x1"), TestDocs.Scene("x2"))));
        var document = workspace.Find(id).Document!;

        var (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Nest",
            ["items"] = new JsonArray(
                new JsonObject { ["name"] = "Empty", ["children"] = new JsonArray() },
                new JsonObject { ["name"] = "Inner", ["group"] = "card", ["index"] = 1, ["x"] = 5, ["children"] = new JsonArray(Child("Dot", 1, 2)) }),
        });
        Assert.False(error, text);
        Assert.Equal("card[x1, Inner[Dot], x2], Empty[]", Tree(document));
        Assert.Contains("\"Empty\" (", text);
        Assert.Contains("an empty group", text);
        Assert.Equal((0d, 0d), (Named(document, "Empty").Placement.X, Named(document, "Empty").Placement.Y)); // 0 when absent
        Assert.Equal(5d, Named(document, "Inner").Placement.X);
    }

    [Fact]
    public async Task AddItems_AGroupWhoseChildrenWouldPassEightLevels_IsRefused_ByTheEngine()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        // Groups seven deep: an item in the deepest is at level 8, a group there may hold nothing.
        var deepest = TestDocs.Group("g7", TestDocs.Scene("leaf"));
        for (var level = 6; level >= 1; level--)
            deepest = TestDocs.Group($"g{level}", deepest);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Deep", 400, 200, deepest));
        var document = workspace.Find(id).Document!;
        var before = document.Rev;

        var (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Deep",
            ["items"] = new JsonArray(new JsonObject { ["name"] = "Too deep", ["group"] = "g7", ["children"] = new JsonArray(Child("Dot", 0, 0)) }),
        });
        Assert.True(error, text);
        Assert.Contains("limit.itemDepth", text);
        Assert.Contains("items/0 limit.itemDepth", text);
        Assert.Equal(before, document.Rev);

        // An empty group there is within the limit.
        (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Deep",
            ["items"] = new JsonArray(new JsonObject { ["name"] = "Just fits", ["group"] = "g7", ["children"] = new JsonArray() }),
        });
        Assert.False(error, text);
    }

    [Fact]
    public async Task AddItems_CountsAGroupsChildren_FiftyAddAndFiftyOneAreRefused_BeforeAnything()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Cap");
        var before = document.Rev;

        // A group of 50 children is 51 items: refused, though the list holds 2 entries and the group 50 children.
        var (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Cap",
            ["items"] = new JsonArray(new JsonObject { ["name"] = "Big", ["children"] = Children(50) }),
        });
        Assert.True(error, text);
        Assert.Contains("add_items takes at most 50 items in a call, a group's children counted: these are 51. Send the rest in another call.", text);
        Assert.Contains("(arguments)", text);
        Assert.Equal(before, document.Rev);

        // A group of 49 children and one more item: 51 too.
        (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Cap",
            ["items"] = new JsonArray(new JsonObject { ["name"] = "Big", ["children"] = Children(49) }, Child("One more", 0, 0)),
        });
        Assert.True(error, text);
        Assert.Contains("these are 51", text);
        Assert.Equal(before, document.Rev);

        // A group of 48 and one more item: 50, added.
        (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Cap",
            ["items"] = new JsonArray(new JsonObject { ["name"] = "Big", ["children"] = Children(48) }, Child("One more", 0, 0)),
        });
        Assert.False(error, text);
        Assert.Equal(before + 1, document.Rev);
        Assert.Equal(48, Assert.IsType<GroupItem>(Named(document, "Big")).Children.Count);
    }

    public static TheoryData<string, string, string> BadGroupEntries() => new()
    {
        // A group has no scene, box or bindings: its children have them.
        { "group scene", """{"name":"G","scene":{"format":"infopanel.scene/1","nodes":[]},"children":[]}""", "item 1 is a group (children): give scene, width, height and bindings to its children." },
        { "group width", """{"name":"G","width":10,"children":[]}""", "item 1 is a group (children)" },
        { "group height", """{"name":"G","height":10,"children":[]}""", "item 1 is a group (children)" },
        { "group bindings", """{"name":"G","bindings":{"v":"k"},"children":[]}""", "item 1 is a group (children)" },
        // A child is an item as add_items takes one, without group, index or children.
        { "child children", """{"name":"G","children":[{"scene":{"format":"infopanel.scene/1","nodes":[]},"children":[]}]}""", "items/1/children/0 can't take these arguments (arguments): 'children' isn't one of its arguments" },
        { "child group", """{"name":"G","children":[{"scene":{"format":"infopanel.scene/1","nodes":[]},"group":"a"}]}""", "items/1/children/0 can't take these arguments (arguments): 'group' isn't one of its arguments" },
        { "child index", """{"name":"G","children":[{"scene":{"format":"infopanel.scene/1","nodes":[]},"index":0}]}""", "items/1/children/0 can't take these arguments (arguments): 'index' isn't one of its arguments" },
        { "child no scene", """{"name":"G","children":[{"name":"c"}]}""", "items/1/children/0 can't take these arguments (arguments): 'scene' is required" },
        { "children not a list", """{"name":"G","children":{}}""", "'children' must be a list of JSON objects" },
        // An entry is a scene item or a group.
        { "neither", """{"name":"N","x":4}""", "item 1 needs scene, or children for a group." },
    };

    [Theory]
    [MemberData(nameof(BadGroupEntries))]
    public async Task AddItems_ABadGroupEntry_IsRefusedByItsPath_AndNothingIsAdded(string which, string entry, string expected)
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Bad");
        var before = document.Rev;
        var (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Bad",
            ["items"] = new JsonArray(Child("Fine", 0, 0), JsonNode.Parse(entry)),
        });
        Assert.True(error, $"{which}: {text}");
        Assert.Contains(expected, text);
        Assert.Contains("Nothing was changed.", text);
        Assert.Equal(before, document.Rev);
    }

    [Fact]
    public async Task AddItems_AChildsSceneTheEngineRefuses_IsNamedByItsPath()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Path");
        var before = document.Rev;
        var bad = Child("Bad", 0, 0);
        bad["scene"] = TestDocs.InvalidScene();
        var (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Path",
            ["items"] = new JsonArray(Child("Fine", 0, 0), new JsonObject { ["name"] = "G", ["children"] = new JsonArray(Child("c0", 0, 0), Child("c1", 0, 0), bad) }),
        });
        Assert.True(error, text);
        Assert.Contains("items/1/children/2/scene/nodes/0/w ref.input", text);
        Assert.DoesNotContain("/ops/", text);
        Assert.Equal(before, document.Rev);
    }

    [Fact]
    public async Task UpdateItem_MovesAnItem_IntoAGroup_KeepingItsNumbers_OrPlacedByTheXAndYGiven_InOneCommit()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Move", 400, 200,
            TestDocs.Scene("a", 10, 20), TestDocs.Scene("b", 30, 40), TestDocs.Group("card", TestDocs.Scene("x1"), TestDocs.Scene("x2")), TestDocs.Group("other")));
        var document = workspace.Find(id).Document!;

        // Into a group: at its end, the numbers kept.
        var before = document.Rev;
        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = "Move", ["item"] = "a", ["group"] = "card" });
        Assert.False(error, text);
        Assert.Equal(before + 1, document.Rev);
        Assert.Equal("b, card[x1, x2, a], other[]", Tree(document));
        Assert.Equal((10d, 20d), (ItemOf(document, "a").Placement.X, ItemOf(document, "a").Placement.Y));

        // With x, y and an index: one commit, placed by the numbers given, in the new parent.
        before = document.Rev;
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "b", ["group"] = "card", ["index"] = 0, ["x"] = 3, ["y"] = 4 });
        Assert.False(error, text);
        Assert.Equal(before + 1, document.Rev);
        Assert.Equal("card[b, x1, x2, a], other[]", Tree(document));
        Assert.Equal((3d, 4d), (ItemOf(document, "b").Placement.X, ItemOf(document, "b").Placement.Y));

        // Between groups, and out to the top level ("").
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "x1", ["group"] = "other" });
        Assert.False(error, text);
        Assert.Equal("card[b, x2, a], other[x1]", Tree(document));
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "a", ["group"] = "" });
        Assert.False(error, text);
        Assert.Equal("card[b, x2], other[x1], a", Tree(document));
        Assert.Equal((10d, 20d), (ItemOf(document, "a").Placement.X, ItemOf(document, "a").Placement.Y));

        // A group into a group, its children with it.
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "other", ["group"] = "card", ["index"] = 1 });
        Assert.False(error, text);
        Assert.Equal("card[b, other[x1], x2], a", Tree(document));

        // One undo step takes one move back.
        Assert.NotNull(document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev }).Result);
        Assert.Equal("card[b, x2], other[x1], a", Tree(document));
    }

    [Fact]
    public async Task UpdateItem_IndexAlone_ReordersWithinItsParent_AtTheTopLevelAndInAGroup()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Order", 400, 200,
            TestDocs.Scene("a"), TestDocs.Scene("b"), TestDocs.Scene("c"), TestDocs.Group("card", TestDocs.Scene("x1"), TestDocs.Scene("x2"), TestDocs.Scene("x3"))));
        var document = workspace.Find(id).Document!;

        // On top: past the last, the end.
        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "a", ["index"] = 2000 });
        Assert.False(error, text);
        Assert.Equal("b, c, card[x1, x2, x3], a", Tree(document));
        // To the back: 0.
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "card", ["index"] = 0 });
        Assert.False(error, text);
        Assert.Equal("card[x1, x2, x3], b, c, a", Tree(document));
        // Within a group: it stays in it.
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "x1", ["index"] = 2000 });
        Assert.False(error, text);
        Assert.Equal("card[x2, x3, x1], b, c, a", Tree(document));
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "x3", ["index"] = 0 });
        Assert.False(error, text);
        Assert.Equal("card[x3, x2, x1], b, c, a", Tree(document));

        // Naming its own parent without an index changes nothing (no reorder by accident), at the top level too.
        var before = document.Rev;
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "x3", ["group"] = "card" });
        Assert.False(error, text);
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "b", ["group"] = "" });
        Assert.False(error, text);
        Assert.Equal(before, document.Rev);
        Assert.Equal("card[x3, x2, x1], b, c, a", Tree(document));
        Assert.Contains("already was as asked", text);
    }

    [Fact]
    public async Task UpdateItem_IntoAnItemThatIsntAGroup_OrIntoItself_IsRefused_AndChangesNothing()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Cycle", 400, 200,
            TestDocs.Scene("a"), TestDocs.Group("outer", TestDocs.Group("inner", TestDocs.Scene("x1")))));
        var document = workspace.Find(id).Document!;
        var before = document.Rev;

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "outer", ["group"] = "a", ["x"] = 5 });
        Assert.True(error, text);
        Assert.Contains("a isn't a group (notAGroup): an item goes into a group or the top level.", text);

        foreach (var into in new[] { "outer", "inner" })
        {
            (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "outer", ["group"] = into, ["x"] = 5 });
            Assert.True(error, text);
            Assert.Contains("outer can't go into itself or a group inside it (op.cycle): nothing was changed.", text);
        }
        Assert.Equal(before, document.Rev);
        Assert.Equal("a, outer[inner[x1]]", Tree(document));
        Assert.Equal(0d, ItemOf(document, "outer").Placement.X);

        // A group's group and index change it; its scene still doesn't.
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "inner", ["group"] = "", ["index"] = 0 });
        Assert.False(error, text);
        Assert.Equal("inner[x1], a, outer[]", Tree(document));
        (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "inner" });
        Assert.True(error, text);
        Assert.Contains("group, index", text); // "nothing to change" names them
    }

    /// <summary>Another client's commit inserting <c>c</c> at the front of group <c>g</c>, at the tool's first write.</summary>
    private static Action<string> InsertsAtTheFrontOfG(ProfileDocument document, Action wrote) => step =>
    {
        if (step != "write")
            return;
        wrote();
        TestDocs.Commit(document, new JsonObject { ["op"] = "insert", ["parentId"] = "g", ["index"] = 0, ["item"] = TestDocs.Scene("c") });
    };

    /// <summary>
    /// Codex MC2 r1 E1: <c>g[a,b]</c>; update_item names a's own group without an index while another client would insert
    /// <c>c</c> at the front just before the tool's write. Naming the current parent without an index is no move: the call
    /// writes nothing (no commit, no undo step), so it can't put <c>a</c> back at a place read before the insert.
    /// </summary>
    [Fact]
    public async Task UpdateItem_NamingItsOwnGroupWithoutAnIndex_WritesNothing_AndMakesNoUndoStep()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Front", 400, 200, TestDocs.Group("g", TestDocs.Scene("a"), TestDocs.Scene("b"))));
        var document = workspace.Find(id).Document!;
        var before = document.Rev;
        Assert.False(document.CanUndo);
        var writes = 0;
        host.StepHook = InsertsAtTheFrontOfG(document, () => writes++);

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "a", ["group"] = "g" });
        Assert.False(error, text);
        Assert.Contains("already was as asked: nothing changed.", text);
        Assert.Equal(0, writes);
        Assert.Equal(before, document.Rev);
        Assert.False(document.CanUndo);
        Assert.Equal("g[a, b]", Tree(document));
    }

    /// <summary>
    /// Codex MC2 r1 E1, the same sequence with a change beside the group: the change is written, and the sibling inserted
    /// at the front meanwhile stays at the front (<c>g[c,A,b]</c>, not <c>g[A,c,b]</c>); one undo step takes the rename back.
    /// </summary>
    [Fact]
    public async Task UpdateItem_NamingItsOwnGroupWithoutAnIndex_KeepsASiblingInsertedAtTheFrontMeanwhile()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Front", 400, 200, TestDocs.Group("g", TestDocs.Scene("a"), TestDocs.Scene("b"))));
        var document = workspace.Find(id).Document!;
        var writes = 0;
        host.StepHook = InsertsAtTheFrontOfG(document, () => writes++);

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "a", ["group"] = "g", ["name"] = "A" });
        Assert.False(error, text);
        Assert.Equal(1, writes);
        Assert.Equal("g[c, A, b]", Tree(document));

        Assert.NotNull(document.Undo(new HistoryRequest { Instance = document.Instance, BaseRev = document.Rev }).Result);
        Assert.Equal("g[c, a, b]", Tree(document));
    }

    /// <summary>
    /// Codex MC2 r1 E2: a malformed binding (not a key or {key, stat}) in a group's child, or in an entry, is refused by
    /// its path, so the caller can tell which item it is; nothing is changed.
    /// </summary>
    [Fact]
    public async Task AddItems_AMalformedBinding_InAChildOrAnEntry_IsNamedByItsPath()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var document = Seed(factory, "Bind");
        var before = document.Rev;

        var (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Bind",
            ["items"] = new JsonArray(new JsonObject
            {
                ["name"] = "G",
                ["children"] = new JsonArray(new JsonObject { ["scene"] = TestDocs.InputScene(), ["bindings"] = new JsonObject { ["v"] = 5 } }),
            }),
        });
        Assert.True(error, text);
        Assert.Contains("items/0/children/0/bindings/v", text);
        Assert.Contains("(arguments)", text);
        Assert.Contains("Nothing was changed.", text);
        Assert.Equal(before, document.Rev);

        (text, error) = await Call(host, "add_items", new JsonObject
        {
            ["profile"] = "Bind",
            ["items"] = new JsonArray(Child("Fine", 0, 0), new JsonObject { ["scene"] = TestDocs.InputScene(), ["bindings"] = new JsonObject { ["v"] = true } }),
        });
        Assert.True(error, text);
        Assert.Contains("items/1/bindings/v", text);
        Assert.Equal(before, document.Rev);
    }

    /// <summary>
    /// Codex MC2e r2 F1: <c>g[a,b]</c>; update_item names a's own group without an index (no ops to write) and the profile
    /// is deleted after the tool read it: the call is <c>notFound</c>, never "nothing changed" on a profile that is gone.
    /// </summary>
    [Fact]
    public async Task UpdateItem_WithNothingToWrite_OnAProfileDeletedAfterTheRead_IsNotFound()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Gone", 400, 200, TestDocs.Group("g", TestDocs.Scene("a"), TestDocs.Scene("b"))));
        var writes = 0;
        host.StepHook = step =>
        {
            if (step == "read")
                Assert.True(workspace.DeleteWithItsTag(id));
            else if (step == "write")
                writes++;
        };

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "a", ["group"] = "g" });
        Assert.True(error, text);
        Assert.Contains("(notFound)", text);
        Assert.Contains(id, text);
        Assert.DoesNotContain("already was as asked", text);
        Assert.Equal(0, writes);
        Assert.Null(workspace.Find(id).Document);
    }

    /// <summary>
    /// Codex MC2e r2 F1, the profile deleted and created again under its id after the read: the new document is another
    /// profile, so the call is <c>notFound</c> and the new one is left as it is.
    /// </summary>
    [Fact]
    public async Task UpdateItem_WithNothingToWrite_OnAProfileReplacedAfterTheRead_IsNotFound()
    {
        using var factory = new EngineFactory();
        var host = Host(factory);
        var workspace = Workspace(factory);
        var id = TestDocs.Create(workspace, TestDocs.Doc("Swapped", 400, 200, TestDocs.Group("g", TestDocs.Scene("a"), TestDocs.Scene("b"))));
        var writes = 0;
        host.StepHook = step =>
        {
            if (step == "read")
            {
                Assert.True(workspace.DeleteWithItsTag(id));
                var again = TestDocs.Doc("Swapped", 400, 200, TestDocs.Group("g", TestDocs.Scene("a"), TestDocs.Scene("b")));
                again["id"] = id;
                Assert.NotNull(workspace.Create(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id, ["document"] = again }.ToJsonString())).Entry);
            }
            else if (step == "write")
                writes++;
        };

        var (text, error) = await Call(host, "update_item", new JsonObject { ["profile"] = id, ["item"] = "a", ["group"] = "g" });
        Assert.True(error, text);
        Assert.Contains("(notFound)", text);
        Assert.Contains(id, text);
        Assert.DoesNotContain("already was as asked", text);
        Assert.Equal(0, writes);
        var replaced = workspace.Find(id).Document!;
        Assert.Equal("g[a, b]", Tree(replaced));
        Assert.False(replaced.CanUndo);
    }
}
