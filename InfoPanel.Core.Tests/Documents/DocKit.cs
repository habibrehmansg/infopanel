using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>Saved generations kept in memory.</summary>
internal sealed class MemoryPersistence : IDocumentPersistence
{
    public List<Generation> Saved { get; } = [];

    public Action<Generation>? Before;

    public void Save(Generation generation)
    {
        Before?.Invoke(generation);
        lock (Saved) Saved.Add(generation);
    }
}

/// <summary>Documents, items and commits for the document tests, as JSON (what a client sends), with a pinned clock.</summary>
internal static class DocKit
{
    public const string Instance = "engine-instance-1";

    public static readonly DateTimeOffset Start = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    public static JsonObject Rect(string fill = "#ff0000", double w = 20, double h = 10) => new()
    {
        ["format"] = "infopanel.scene/1",
        ["nodes"] = new JsonArray(new JsonObject { ["kind"] = "rect", ["w"] = w, ["h"] = h, ["fill"] = fill }),
    };

    /// <summary>A scene that breaks a rule: its rect reads an input it doesn't declare (<c>ref.input</c>).</summary>
    public static JsonObject InvalidScene() => JsonNode.Parse(
        """{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": {"op": "input", "name": "v"}, "h": 10, "fill": "#ffffff"}]}""")!.AsObject();

    /// <summary>A scene of a newer format: kept as sent and drawn as a placeholder.</summary>
    public static JsonObject FutureScene() => JsonNode.Parse("""{"format": "infopanel.scene/2", "nodes": []}""")!.AsObject();

    /// <summary>A scene with a number input <c>v</c> drawn as a bar.</summary>
    public static JsonObject InputScene() => JsonNode.Parse(
        """{"format": "infopanel.scene/1", "inputs": {"v": {"type": "number"}}, "nodes": [{"kind": "rect", "w": {"op": "input", "name": "v"}, "h": 10, "fill": "#00ff00"}]}""")!.AsObject();

    public static JsonObject Scene(string id, double x = 0, double y = 0, JsonObject? scene = null, JsonObject? extra = null)
    {
        var item = new JsonObject
        {
            ["kind"] = "scene",
            ["id"] = id,
            ["placement"] = new JsonObject { ["x"] = x, ["y"] = y },
            ["scene"] = scene ?? Rect(),
        };
        if (extra is not null)
            foreach (var (k, v) in extra) item[k] = v?.DeepClone();
        return item;
    }

    public static JsonObject Group(string id, params JsonObject[] children) => new()
    {
        ["kind"] = "group",
        ["id"] = id,
        ["placement"] = new JsonObject { ["x"] = 0, ["y"] = 0 },
        ["children"] = new JsonArray(children.Select(c => (JsonNode)c).ToArray()),
    };

    public static JsonElement Doc(params JsonObject[] items) => Element(new JsonObject
    {
        ["format"] = "infopanel.document/1",
        ["id"] = "8a1d4a52-0c1f-4f0b-9d55-2a8f5c2f1e01",
        ["profile"] = new JsonObject { ["name"] = "Test", ["width"] = 200, ["height"] = 100, ["background"] = "#000000" },
        ["items"] = new JsonArray(items.Select(c => (JsonNode)c).ToArray()),
    });

    public static ProfileDocumentOptions Options(ManualTimeProvider? time = null, HistoryBudget? budget = null, IDocumentPersistence? persistence = null,
        int steps = 100, long bytes = 64L * 1024 * 1024) => new()
    {
        Instance = Instance,
        Time = time ?? new ManualTimeProvider(Start),
        HistoryBudget = budget ?? new HistoryBudget(HistoryBudget.DefaultMaxBytes),
        Persistence = persistence ?? new MemoryPersistence(),
        HistorySteps = steps,
        HistoryBytes = bytes,
    };

    public static ProfileDocument Open(JsonElement doc, ProfileDocumentOptions? options = null)
    {
        var (document, check) = ProfileDocument.Open(doc, options ?? Options());
        Assert.True(document is not null, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
        return document;
    }

    /// <summary>Two scene items <c>a</c> and <c>b</c>, and a group <c>g</c> holding <c>c</c>.</summary>
    public static ProfileDocument Sample(ProfileDocumentOptions? options = null) =>
        Open(Doc(Scene("a", 10, 10), Scene("b", 50, 10), Group("g", Scene("c", 90, 10))), options);

    private static int _ops;

    /// <summary>A commit of this instance based on revision 0: never refused as expired until an <c>opId</c> is forgotten.</summary>
    public static JsonElement Commit(params JsonObject[] ops) => Commit($"op-{Interlocked.Increment(ref _ops)}", null, ops);

    public static JsonElement Commit(string opId, string? coalesceKey, params JsonObject[] ops) => Commit(0, opId, coalesceKey, ops);

    /// <summary>A commit of this instance based on revision <paramref name="baseRev"/>.</summary>
    public static JsonElement Commit(long baseRev, string opId, string? coalesceKey, params JsonObject[] ops)
    {
        var body = new JsonObject
        {
            ["opId"] = opId,
            ["instance"] = Instance,
            ["baseRev"] = baseRev,
            ["ops"] = new JsonArray(ops.Select(o => (JsonNode)o).ToArray()),
        };
        if (coalesceKey is not null) body["coalesceKey"] = coalesceKey;
        return Element(body);
    }

    public static JsonObject Place(string id, double x, double y, long? baseRev = null, double? rotation = null)
    {
        var placement = new JsonObject { ["x"] = x, ["y"] = y };
        if (rotation is not null) placement["rotation"] = rotation;
        return Op(new JsonObject { ["op"] = "setPlacement", ["id"] = id, ["placement"] = placement }, baseRev);
    }

    public static JsonObject Design(string id, JsonObject scene, long? baseRev = null, JsonObject? source = null, JsonObject? layout = null)
    {
        var op = new JsonObject { ["op"] = "setDesign", ["id"] = id, ["scene"] = scene };
        if (source is not null) op["source"] = source;
        if (layout is not null) op["layout"] = layout;
        return Op(op, baseRev);
    }

    public static JsonObject Bind(string id, string input, string key, long? baseRev = null) =>
        Op(new JsonObject { ["op"] = "setBindings", ["id"] = id, ["bindings"] = new JsonObject { [input] = new JsonObject { ["key"] = key } } }, baseRev);

    public static JsonObject Attrs(string id, string? name = null, bool? visible = null, bool? locked = null, long? baseRev = null)
    {
        var set = new JsonObject();
        if (name is not null) set["name"] = name;
        if (visible is not null) set["visible"] = visible;
        if (locked is not null) set["locked"] = locked;
        return Op(new JsonObject { ["op"] = "setAttributes", ["id"] = id, ["set"] = set }, baseRev);
    }

    public static JsonObject Insert(JsonObject item, string? parentId = null, int? index = null)
    {
        var op = new JsonObject { ["op"] = "insert", ["item"] = item };
        if (parentId is not null) op["parentId"] = parentId;
        if (index is not null) op["index"] = index;
        return op;
    }

    public static JsonObject Remove(string id, long? baseRev = null) => Op(new JsonObject { ["op"] = "remove", ["id"] = id }, baseRev);

    public static JsonObject Move(string id, string? parentId, int index, long? baseRev = null, JsonObject? placement = null)
    {
        var op = new JsonObject { ["op"] = "move", ["id"] = id, ["index"] = index };
        if (parentId is not null) op["parentId"] = parentId;
        if (placement is not null) op["placement"] = placement;
        return Op(op, baseRev);
    }

    public static JsonObject Profile(JsonObject set, long? baseRev = null) => Op(new JsonObject { ["op"] = "profile", ["set"] = set }, baseRev);

    private static JsonObject Op(JsonObject op, long? baseRev)
    {
        if (baseRev is not null) op["baseRev"] = baseRev;
        return op;
    }

    public static JsonElement Element(JsonNode node) => JsonSerializer.SerializeToElement(node);

    /// <summary>A commit that must apply.</summary>
    public static CommitResult Ok(this ProfileDocument doc, JsonElement body)
    {
        var outcome = doc.Commit(body);
        Assert.True(outcome.Result is not null, Describe(outcome));
        return outcome.Result;
    }

    /// <summary>An undo at the document's current revision, which must answer 200.</summary>
    public static CommitResult Undo(this ProfileDocument doc) => Done(doc.Undo(Here(doc)));

    public static CommitResult Redo(this ProfileDocument doc) => Done(doc.Redo(Here(doc)));

    public static CommitResult Revert(this ProfileDocument doc) => Done(doc.Revert(Here(doc)));

    /// <summary>A history request of this instance at the document's current revision.</summary>
    public static HistoryRequest Here(ProfileDocument doc) => new() { Instance = Instance, BaseRev = doc.Rev };

    private static CommitResult Done(CommitOutcome outcome)
    {
        Assert.True(outcome.Result is not null, Describe(outcome));
        return outcome.Result;
    }

    public static ConflictProblem Conflict(this ProfileDocument doc, JsonElement body)
    {
        var outcome = doc.Commit(body);
        Assert.True(outcome.Conflict is not null, Describe(outcome));
        return outcome.Conflict;
    }

    public static InvalidProblem Invalid(this ProfileDocument doc, JsonElement body)
    {
        var outcome = doc.Commit(body);
        Assert.True(outcome.Invalid is not null, Describe(outcome));
        return outcome.Invalid;
    }

    public static string Describe(CommitOutcome o) => o.Status + " " + (o.Result is not null ? SpecJson.Write(o.Result).GetRawText()
        : o.Conflict is not null ? SpecJson.Write(o.Conflict).GetRawText() : o.Expired is not null ? SpecJson.Write(o.Expired).GetRawText()
        : SpecJson.Write(o.Invalid!).GetRawText());

    /// <summary>The item by id, anywhere in the tree.</summary>
    public static Item Item(this Generation g, string id) => Find(g.Document.Items, id) ?? throw new KeyNotFoundException(id);

    public static Item? Find(IReadOnlyList<Item> items, string id)
    {
        foreach (var item in items)
        {
            if (item.Id == id) return item;
            if (item is GroupItem g && Find(g.Children, id) is { } found) return found;
        }
        return null;
    }

    /// <summary>The content as canonical JSON text (member order and number spelling don't matter).</summary>
    public static string Canonical<T>(T value) => global::InfoPanel.Scenes.CanonicalJson.Write(SpecJson.Write(value));

    /// <summary>The ids in document order, as "id(parent)".</summary>
    public static string Tree(Generation g)
    {
        var parts = new List<string>();
        void Walk(IReadOnlyList<Item> items, string parent)
        {
            foreach (var i in items)
            {
                parts.Add($"{i.Id}({parent})");
                if (i is GroupItem gi) Walk(gi.Children, i.Id);
            }
        }
        Walk(g.Document.Items, "");
        return string.Join(" ", parts);
    }
}
