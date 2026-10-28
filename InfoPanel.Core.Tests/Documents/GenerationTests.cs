using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Documents;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Generations: their size (<c>documentBytes</c>) is exactly the stored document (its canonical form), measured as
/// validation measures; the scene runtime loads one checked with other limits or support as its own check would; a JSON
/// load takes the document's own <c>rev</c>; and a chain of JSON loads keeps only the latest scenes.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class GenerationTests
{
    /// <summary>A stored document whose text isn't what the engine writes: whitespace, escapes, non-ASCII, opaque members.</summary>
    private const string Stored = """
        {
          "format" : "infopanel.document/1", "id" : "8a1d4a52-0c1f-4f0b-9d55-2a8f5c2f1e01", "rev" : 7,
          "profile" : { "name" : "Café ✓", "width" : 200, "height" : 100, "editor" : { "z" : 1.50, "a" : [ 1 , 2 ] } },
          "items" : [
            { "kind" : "scene", "id" : "a", "name" : "<b> & ü", "placement" : { "x" : 1.0, "y" : 2 },
              "layout" : { "w" : 40, "h" : 10 }, "origin" : { "from" : "élsewhere" },
              "source" : { "ref" : "opaque", "deps" : [ ] },
              "scene" : { "format" : "infopanel.scene/1", "nodes" : [ { "kind" : "rect", "w" : 2E1, "h" : 10, "fill" : "#ff0000" } ] } },
            { "kind" : "group", "id" : "g", "placement" : { "x" : 0, "y" : 0 }, "origin" : { "n" : 1 },
              "children" : [ { "kind" : "group", "id" : "empty", "placement" : { "x" : 0, "y" : 0 }, "children" : [ ] } ] }
          ]
        }
        """;

    /// <summary>The document as the store writes it: its canonical form.</summary>
    private static long Serialized(Generation g) => Workspace.Serialize(g).LongLength;

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, SpecJson.DocumentOptions);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Validation_and_the_generation_measure_a_scene_alike()
    {
        // Whitespace, escapes, members out of order and long number spellings are how the scene was sent, not its size:
        // both measure its canonical form, so a scene at sceneBytes exactly passes and one byte less refuses it.
        var escaped = "\\" + "u00e9";
        var text = $$"""
            {
              "nodes" : [ { "kind" : "rect", "w" : 2E1, "h" : 10.000, "fill" : "#ff0000" },
                          { "fill" : "#ffffff", "text" : "Caf{{escaped}} é ✓ \/", "kind" : "text", "font" : { "size" : 24, "family" : "Inter" } } ],
              "format" : "infopanel.scene/1"
            }
            """;
        var scene = Parse(text);
        var size = GenerationContent.SizeOf(scene);
        Assert.Equal(Encoding.UTF8.GetByteCount(global::InfoPanel.Spec.CanonicalJson.Write(scene)), size);
        Assert.True(size < Encoding.UTF8.GetByteCount(text) - 50, $"{size} bytes measured, {Encoding.UTF8.GetByteCount(text)} sent");

        var at = SceneValidator.Check(scene, SpecLimits.Default with { SceneBytes = (int)size });
        Assert.True(at.Status == CheckStatus.Valid, string.Join("; ", at.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
        var under = SceneValidator.Check(scene, SpecLimits.Default with { SceneBytes = (int)size - 1 });
        var error = Assert.Single(under.Errors);
        Assert.Equal(("limit.sceneBytes", ""), (error.Code, error.Path));
        Assert.Contains($"is {size} bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_size_is_exactly_the_serialized_document_after_every_change()
    {
        using var doc = Open(Parse(Stored));
        void Same() => Assert.Equal(Serialized(doc.Current), doc.Current.Content.Bytes);
        Same();

        doc.Ok(Commit(Insert(Scene("n", 3, 4, extra: new JsonObject { ["name"] = "Ünïcode ✓ <&>" }), "g")));
        Same();
        doc.Ok(Commit(Insert(Group("h", Scene("h1"), Scene("h2"), Group("h3")), "empty", 0)));
        Same();
        doc.Ok(Commit(Design("a", Rect("#00ff00"), source: new JsonObject { ["ref"] = "other" }), Attrs("a", name: "é")));
        Same();
        doc.Ok(Commit(Move("h1", null, 0), Move("a", "g", 9), Profile(new JsonObject { ["name"] = "Ω", ["guides"] = new JsonArray(new JsonObject { ["axis"] = "x", ["at"] = 3.5 }) })));
        Same();
        doc.Ok(Commit(Remove("h"), Remove("n"), Bind("a", "v", "test:ü")));
        Same();
        doc.Undo();
        Same();
        doc.Redo();
        Same();
        doc.Revert();
        Same();
        Assert.Equal(Serialized(doc.Saved), doc.Saved.Content.Bytes);
    }

    [Fact]
    public void A_commit_may_reach_documentBytes_exactly_and_not_one_byte_more()
    {
        JsonObject Insert() => DocKit.Insert(Scene("added", 1, 1, Rect("#ffffff"), new JsonObject { ["name"] = "Ünïcode ✓ <&>", ["origin"] = new JsonObject { ["from"] = "x" } }), "g");
        long size;
        using (var probe = Open(Parse(Stored)))
        {
            probe.Ok(Commit(Insert()));
            size = Serialized(probe.Current);
        }

        foreach (var (limit, fits) in new[] { (size, true), (size - 1, false) })
        {
            using var doc = Open(Parse(Stored), new ProfileDocumentOptions
            {
                Instance = DocKit.Instance, Time = new ManualTimeProvider(Start), HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes),
                Limits = SpecLimits.Default with { DocumentBytes = (int)limit },
            });
            var outcome = doc.Commit(Commit(Insert()));
            Assert.True(fits ? outcome.Result is not null : outcome.Invalid is not null, $"limit {limit}: {Describe(outcome)}");
            if (!fits) Assert.Equal("limit.documentBytes", Assert.Single(outcome.Invalid!.Errors).Code);
        }
    }

    /// <summary>
    /// documentBytes measures what is stored: the document without its revision metadata (the <c>rev</c> of the document,
    /// of <c>profile</c> and of every item, groups' children included), so a copy of an API document, revisions and all,
    /// passes at the size it will be stored at. A <c>rev</c> inside an opaque member is content and is measured.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_document_is_measured_without_its_revisions_at_documentBytes_exactly_and_not_one_byte_more(bool withRevisions)
    {
        // Already in the canonical form, so its length is its size, whatever the writer does.
        const string canonical = """{"format":"infopanel.document/1","id":"8a1d4a52-0c1f-4f0b-9d55-2a8f5c2f1e01","items":[{"id":"a","kind":"scene","origin":{"from":"x","rev":6},"placement":{"x":1,"y":2},"scene":{"format":"infopanel.scene/1","nodes":[{"fill":"#ff0000","h":10,"kind":"rect","w":20}]}},{"children":[{"children":[],"id":"empty","kind":"group","placement":{"x":0,"y":0}}],"id":"g","kind":"group","placement":{"x":0,"y":0}}],"profile":{"editor":{"rev":5},"height":100,"name":"Café","width":200}}""";
        long stored = Encoding.UTF8.GetByteCount(canonical);

        var sent = JsonNode.Parse(canonical)!.AsObject();
        if (withRevisions)
        {
            sent["rev"] = 123_456_789;
            sent["profile"]!["rev"] = 123_456_788;
            sent["items"]![0]!["rev"] = 123_456_787;
            sent["items"]![1]!["rev"] = 123_456_786;
            sent["items"]![1]!["children"]![0]!["rev"] = 123_456_785;
        }

        using (var doc = Open(Element(sent)))
            Assert.Equal(stored, Serialized(doc.Current));
        foreach (var (limit, fits) in new[] { (stored, true), (stored - 1, false) })
        {
            var check = DocumentValidator.Check(Element(sent), SpecLimits.Default with { DocumentBytes = (int)limit }, EngineSupport.All);
            Assert.True(fits == (check.Status == CheckStatus.Valid), $"limit {limit}: {check.Status} {string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Message}"))}");
            if (!fits) Assert.Equal("limit.documentBytes", Assert.Single(check.Errors).Code);
        }
    }

    /// <summary>
    /// A body within jsonDepth can still make a document deeper than it: the document nests what an op carries under the
    /// groups it lands in. Seven nested groups put a scene item's scene at level 18 of the document but at level 5 of an
    /// insert's body, so an unsupported scene (kept as sent) holding n nested arrays makes the document 18 + n deep.
    /// </summary>
    [Fact]
    public void A_commit_may_nest_the_document_to_jsonDepth_exactly_and_not_one_level_more()
    {
        var chain = Group("d7");
        for (var i = 6; i >= 1; i--) chain = Group($"d{i}", chain);
        var saved = new MemoryPersistence();
        using var doc = Open(Doc(chain), Options(persistence: saved));
        var limits = SpecLimits.Default with { DocumentNodes = 19_999 };

        // The last good generation saves, serializes and loads in a runtime with other limits.
        void Usable(int depth)
        {
            Assert.Equal(depth, doc.Current.Content.JsonDepth);
            var bytes = Workspace.Serialize(doc.Current);
            Assert.Equal(depth, SpecJson.Depth(bytes));
            Assert.Equal(bytes.LongLength, doc.Current.Content.Bytes);
            doc.Save();
            Assert.Same(doc.Current, saved.Saved[^1]);
            using var runtime = new SceneRuntime(SceneKit.Options(limits: limits));
            runtime.Load(doc.Current);
            using var snapshot = runtime.Snapshot(SceneKit.Now);
            Assert.Equal(doc.Rev, snapshot.Rev);
        }
        Usable(16);

        // The body itself is held to jsonDepth: 252 arrays put the body at 257 levels.
        var deepBody = doc.Commit(Encoding.UTF8.GetBytes(InsertDeep("body-too-deep", null, "deep", 252)));
        Assert.True(deepBody.Invalid is not null, Describe(deepBody));
        Assert.Equal(("limit.jsonDepth", ""), (Assert.Single(deepBody.Invalid.Errors).Code, deepBody.Invalid.Errors[0].Path));

        foreach (var arrays in new[] { 239, 244 })
        {
            var before = doc.Current;
            var outcome = doc.Commit(Encoding.UTF8.GetBytes(InsertDeep($"too-deep-{arrays}", "d7", "deep", arrays)));
            Assert.True(outcome.Invalid is not null, Describe(outcome));
            var e = Assert.Single(outcome.Invalid.Errors);
            Assert.Equal(("limit.jsonDepth", ""), (e.Code, e.Path));
            Assert.Same(before, doc.Current);
            Assert.Equal(0, doc.HistorySize.Undo);
            Usable(16);
        }

        doc.Ok(ParseBody(InsertDeep("deepest", "d7", "deep", 238)));
        Assert.Equal(["deep:unsupported"], doc.Current.Problems.Select(p => $"{p.Id}:{p.Problem}"));
        Usable(256);

        // An item that fits at the top level is refused where a move would take it.
        doc.Ok(ParseBody(InsertDeep("top", null, "top", 239)));
        Usable(256);
        var moved = doc.Invalid(Commit(Move("top", "d7", 0)));
        Assert.Equal(("limit.jsonDepth", ""), (Assert.Single(moved.Errors).Code, moved.Errors[0].Path));
        doc.Undo();
        doc.Redo();
        Usable(256);
    }

    private static string InsertDeep(string opId, string? parent, string id, int arrays)
    {
        var parentId = parent is null ? "" : $"\"parentId\": \"{parent}\", ";
        var nested = new string('[', arrays) + new string(']', arrays);
        return "{\"opId\": \"" + opId + "\", \"instance\": \"" + DocKit.Instance + "\", \"baseRev\": 0, \"ops\": [{\"op\": \"insert\", " + parentId
            + "\"item\": {\"kind\": \"scene\", \"id\": \"" + id + "\", \"placement\": {\"x\": 0, \"y\": 0}, "
            + "\"scene\": {\"format\": \"infopanel.scene/2\", \"x\": " + nested + "}}}]}";
    }

    private static JsonElement ParseBody(string json)
    {
        using var doc = JsonDocument.Parse(json, SpecJson.DocumentOptions);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void A_generation_checked_with_other_limits_is_checked_again_with_the_runtimes()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("a", 12, 12)));
        var limits = SpecLimits.Default with { DocumentNodes = 1 };

        using var runtime = new SceneRuntime(SceneKit.Options(limits: limits));
        runtime.Load(doc.Current);
        using var own = new SceneRuntime(SceneKit.Options(limits: limits));
        own.Load(SpecJson.Write(doc.Current.Document));

        using var snapshot = runtime.Snapshot(SceneKit.Now);
        using var expected = own.Snapshot(SceneKit.Now);
        string Problems(TickSnapshot s) => string.Join(" ", s.Bounds.OrderBy(b => b.Id, StringComparer.Ordinal).Select(b => $"{b.Id}:{b.Problem}"));
        Assert.Equal("a: b:budget c:budget g:", Problems(snapshot));
        Assert.Equal(Problems(expected), Problems(snapshot));
        Assert.Equal(doc.Rev, snapshot.Rev);
    }

    [Fact]
    public void A_json_load_takes_the_documents_own_rev_however_it_is_spelled()
    {
        var json = JsonNode.Parse(Doc(Scene("a")).GetRawText())!;
        using var runtime = new SceneRuntime(SceneKit.Options());
        foreach (var spelling in new[] { "42", "42.0", "4.2e1" })
        {
            json["rev"] = JsonNode.Parse(spelling);
            runtime.Load(Element(json));
            using var snapshot = runtime.Snapshot(SceneKit.Now);
            Assert.Equal(42, snapshot.Rev);
        }
        json["rev"] = "42";
        Assert.Throws<SceneDocumentException>(() => runtime.Load(Element(json)));
    }

    [Fact]
    public void A_chain_of_json_loads_keeps_only_the_latest_scenes()
    {
        using var runtime = new SceneRuntime(SceneKit.Options());
        var first = LoadAndWatch(runtime);
        foreach (var fill in new[] { "#00ff00", "#0000ff", "#ffffff" }) runtime.Load(Doc(Scene("a", scene: Rect(fill))));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(first.IsAlive, "the first load's scenes are still reachable");
    }

    /// <summary>Loads a document and returns a weak reference to the scenes the runtime keeps from it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndWatch(SceneRuntime runtime)
    {
        runtime.Load(Doc(Scene("a", scene: Rect("#ff0000"))));
        return new WeakReference(typeof(SceneRuntime).GetField("_scenes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime));
    }
}
