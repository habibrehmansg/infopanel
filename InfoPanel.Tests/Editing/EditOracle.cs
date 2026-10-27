using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace InfoPanel.Tests.Editing;

/// <summary>
/// spec/docs/commit.md applied by hand to a document of scene items and groups, independently of the engine: the
/// content after each accepted commit, undo, redo or revert, the field groups each change stamps (by comparing the
/// versions before and after it, the profile's fields included), the conflicts a commit or a history request based on
/// a revision should meet, and the shared undo history with <c>gesture:</c> coalescing. The changes are fed in
/// <c>rev</c> order.
/// </summary>
internal sealed class EditOracle
{
    /// <summary>The field groups of every item (spec/docs/commit.md, "Ops").</summary>
    public static readonly string[] SceneGroups = ["placement", "design", "bindings", "name", "visible", "locked", "position"];

    /// <summary>A group's: every item's, and its membership.</summary>
    public static readonly string[] GroupGroups = [.. SceneGroups, "children"];

    /// <summary>The profile's fields, each its own field group (spec/schemas/document.schema.json, <c>profile</c>).</summary>
    public static readonly string[] ProfileFields = ["name", "width", "height", "background", "statsOverlay", "window", "triggerApps", "guides", "editor"];

    private static readonly string[] DesignMembers = ["layout", "source", "scene", "origin"];

    /// <summary>The undo history's count (spec/docs/limits.md); the byte caps are far off for these documents.</summary>
    private const int MaxSteps = 100;

    private readonly JsonObject _saved;
    private readonly Dictionary<string, Dictionary<string, long>> _changed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _profile = new(StringComparer.Ordinal);
    private readonly List<Step> _undo = [];
    private readonly List<Step> _redo = [];
    private string? _lastKey;

    /// <param name="document">The document as exported (no <c>rev</c> members), saved.</param>
    /// <param name="response">Its <c>documentResponse</c>: the revision and every item's and the profile's <c>rev</c> to start from.</param>
    public EditOracle(JsonObject document, JsonElement response)
    {
        Document = document;
        _saved = (JsonObject)document.DeepClone();
        Rev = response.GetProperty("rev").GetInt64();
        var doc = response.GetProperty("document");
        var profileRev = doc.GetProperty("profile").GetProperty("rev").GetInt64();
        foreach (var field in ProfileFields)
            _profile[field] = profileRev;

        void Read(JsonElement items)
        {
            foreach (var item in items.EnumerateArray())
            {
                var rev = item.GetProperty("rev").GetInt64();
                _changed[item.GetProperty("id").GetString()!] = GroupsOf(item.GetProperty("kind").GetString()!).ToDictionary(g => g, _ => rev);
                if (item.TryGetProperty("children", out var children))
                    Read(children);
            }
        }

        Read(doc.GetProperty("items"));
    }

    public JsonObject Document { get; private set; }

    public long Rev { get; private set; }

    public DocState State => new(Rev, !Equal(Document, _saved), _undo.Count > 0, _redo.Count > 0);

    public long ItemRev(string id) => _changed[id].Values.Max();

    public long ProfileRev => _profile.Values.Max();

    /// <summary>Every item's id, groups' children included, in document order (a group before its children).</summary>
    public IEnumerable<string> ItemIds => Walk(Document).Select(w => Id(w.Item));

    /// <summary>The item's parent (a group's id, or null for the top level); false when it isn't in the document.</summary>
    public bool TryParent(string id, out string? parent)
    {
        parent = null;
        if (Locate(Document, id) is not { } found)
            return false;
        parent = found.Parent;
        return true;
    }

    /// <summary>The ids of the item and everything in it, in document order; empty when it isn't in the document.</summary>
    public List<string> Subtree(string id) => Locate(Document, id) is { } found ? Walk(found.Item).Select(w => Id(w.Item)).ToList() : [];

    /// <summary>The engine's conflicts (<c>conflictProblem.conflicts</c>) in a comparable form.</summary>
    public static List<string> Conflicts(JsonElement problem) =>
        problem.GetProperty("conflicts").EnumerateArray().Select(c => Conflict(
            c.TryGetProperty("op", out var op) ? op.GetInt32() : null,
            c.TryGetProperty("id", out var id) ? id.GetString() : null,
            c.GetProperty("reason").GetString()!,
            c.TryGetProperty("rev", out var rev) ? rev.GetInt64() : null,
            c.TryGetProperty("fields", out var fields) ? fields.EnumerateArray().Select(f => f.GetString()!) : null)).ToList();

    private static string Conflict(int? op, string? id, string reason, long? rev, IEnumerable<string>? fields) =>
        $"op={op?.ToString() ?? "-"} id={id ?? "-"} {reason} rev={rev?.ToString() ?? "-"} fields={string.Join(",", (fields ?? []).Order(StringComparer.Ordinal))}";

    /// <summary>
    /// The commit's ops against the current document: the conflicts it should meet (in op order, field revisions as of
    /// the start of the commit; a <c>remove</c>'s in document order) and, when there are none, the content it produces.
    /// </summary>
    public (List<string> Conflicts, JsonObject After) Evaluate(JsonArray ops)
    {
        var draft = (JsonObject)Document.DeepClone();
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i]!.AsObject();
            var kind = op["op"]!.GetValue<string>();
            long? baseRev = op["baseRev"]?.GetValue<long>();
            var parentId = op["parentId"]?.GetValue<string>();
            int? index = op["index"]?.GetValue<int>();

            if (kind == "profile")
            {
                var set = op["set"]!.AsObject();
                var changedFields = baseRev is { } pb ? set.Select(f => f.Key).Where(f => _profile[f] > pb).ToList() : [];
                if (changedFields.Count > 0)
                {
                    conflicts.Add(Conflict(i, null, "changed", ProfileRev, changedFields));
                    continue;
                }

                foreach (var (field, value) in set)
                    draft["profile"]![field] = value?.DeepClone();
                continue;
            }

            if (kind == "insert")
            {
                var item = (JsonObject)op["item"]!.DeepClone();
                if (ListOf(draft, parentId) is not { } into)
                {
                    conflicts.Add(Conflict(i, parentId, "missing", null, null));
                    continue;
                }

                foreach (var (node, _) in Walk(item))
                    inserted.Add(Id(node));
                InsertAt(into, item, index);
                continue;
            }

            // A missing target, and a move's missing parent: each listed.
            var id = op["id"]!.GetValue<string>();
            var found = Locate(draft, id);
            var parentMissing = kind == "move" && parentId != null && Locate(draft, parentId) is null;
            if (found == null)
                conflicts.Add(Conflict(i, id, "missing", null, null));
            if (parentMissing)
                conflicts.Add(Conflict(i, parentId, "missing", null, null));
            if (found is not { } target || parentMissing)
                continue;

            if (baseRev is { } b)
            {
                if (kind == "remove")
                {
                    // The item and everything in it: one conflict for each with a field group changed after baseRev.
                    var changedInside = Walk(target.Item).Select(w => Id(w.Item)).Where(d => !inserted.Contains(d))
                        .Select(d => (id: d, changed: _changed[d].Where(g => g.Value > b).Select(g => g.Key).ToList()))
                        .Where(x => x.changed.Count > 0)
                        .Select(x => Conflict(i, x.id, "changed", ItemRev(x.id), x.changed))
                        .ToList();
                    if (changedInside.Count > 0)
                    {
                        conflicts.AddRange(changedInside);
                        continue;
                    }
                }
                else if (!inserted.Contains(id))
                {
                    string[] writes = kind switch
                    {
                        "setPlacement" => ["placement"],
                        "setDesign" => ["design"],
                        "setBindings" => ["bindings"],
                        "move" => op["placement"] != null ? ["position", "placement"] : ["position"],
                        _ => throw new NotSupportedException(kind),
                    };
                    var changed = writes.Where(g => _changed[id][g] > b).ToList();
                    if (changed.Count > 0)
                    {
                        conflicts.Add(Conflict(i, id, "changed", ItemRev(id), changed));
                        continue;
                    }
                }
            }

            switch (kind)
            {
                case "setPlacement":
                    target.Item["placement"] = op["placement"]!.DeepClone();
                    break;
                case "setBindings":
                    target.Item["bindings"] = op["bindings"]!.DeepClone();
                    break;
                case "setDesign":
                    target.Item.Remove("layout");
                    target.Item.Remove("source");
                    foreach (var member in DesignMembers)
                        if (op[member] is { } value)
                            target.Item[member] = value.DeepClone();
                    break;
                case "remove":
                    target.List.RemoveAt(target.List.IndexOf(target.Item));
                    foreach (var (node, _) in Walk(target.Item))
                        inserted.Remove(Id(node));
                    break;
                case "move":
                    Assert.True(parentId == null || !Walk(target.Item).Any(w => Id(w.Item) == parentId), "the workload never moves a group into itself");
                    target.List.RemoveAt(target.List.IndexOf(target.Item));
                    if (op["placement"] is { } placement)
                        target.Item["placement"] = placement.DeepClone();
                    InsertAt(ListOf(draft, parentId)!, target.Item, index);
                    break;
                default:
                    throw new NotSupportedException(kind);
            }
        }

        return (conflicts, draft);
    }

    /// <summary>
    /// The conflicts an undo, redo or revert based on <paramref name="baseRev"/> should meet now: items in document
    /// order, then the profile. Only meaningful when <paramref name="baseRev"/> isn't the current revision.
    /// </summary>
    public List<string> HistoryConflicts(long baseRev)
    {
        Assert.NotEqual(Rev, baseRev);
        var conflicts = ItemIds
            .Select(id => (id, changed: _changed[id].Where(g => g.Value > baseRev).Select(g => g.Key).ToList()))
            .Where(x => x.changed.Count > 0)
            .Select(x => Conflict(null, x.id, "changed", ItemRev(x.id), x.changed))
            .ToList();
        var profile = _profile.Where(f => f.Value > baseRev).Select(f => f.Key).ToList();
        if (profile.Count > 0)
            conflicts.Add(Conflict(null, null, "changed", ProfileRev, profile));
        return conflicts;
    }

    /// <summary>Applies an accepted commit that changed the document, as revision <paramref name="rev"/>.</summary>
    public Change Commit(JsonObject after, string? key, long rev, string actor)
    {
        var before = Document;
        var change = Stamp(before, after, rev);
        var merges = key != null && key.StartsWith("gesture:", StringComparison.Ordinal) && key == _lastKey && _undo.Count > 0;
        if (merges)
        {
            _undo[^1] = _undo[^1] with { After = after };
            _redo.Clear();
        }
        else
            Record(new Step(before, after, actor));
        _lastKey = key;
        return change with { Merged = merges };
    }

    /// <summary>An undo that took a step, as revision <paramref name="rev"/>; null when there was nothing to undo.</summary>
    public Change? Undo(long rev)
    {
        _lastKey = null;
        if (_undo.Count == 0)
            return null;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(step with { After = Document });
        return Stamp(Document, step.Before, rev) with { UndoneActor = step.Actor };
    }

    /// <summary>A redo that took a step, as revision <paramref name="rev"/>; null when there was nothing to redo.</summary>
    public Change? Redo(long rev)
    {
        _lastKey = null;
        if (_redo.Count == 0)
            return null;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(step with { Before = Document });
        return Stamp(Document, step.After, rev);
    }

    /// <summary>
    /// A revert that took a step (back to the saved document, one new undoable step, which like any recorded change
    /// leaves nothing to redo), as revision <paramref name="rev"/>; null when the document wasn't dirty.
    /// </summary>
    public Change? Revert(long rev)
    {
        _lastKey = null;
        if (!State.Dirty)
            return null;
        var after = (JsonObject)_saved.DeepClone();
        Record(new Step(Document, after, "revert"));
        return Stamp(Document, after, rev);
    }

    /// <summary>A history request that passed its checks but had no step to take: it still ends any merge.</summary>
    public void NoStep() => _lastKey = null;

    public static bool Equal(JsonNode? a, JsonNode? b) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(a), JsonSerializer.SerializeToElement(b));

    private void Record(Step step)
    {
        _undo.Add(step);
        if (_undo.Count > MaxSteps)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    private Change Stamp(JsonObject before, JsonObject after, long rev)
    {
        var beforeAt = Walk(before).ToDictionary(w => Id(w.Item), StringComparer.Ordinal);
        var afterAt = Walk(after).ToDictionary(w => Id(w.Item), StringComparer.Ordinal);
        var items = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, (a, parentAfter)) in afterAt)
        {
            if (!beforeAt.TryGetValue(id, out var was))
            {
                // Present only after the change: every field group at the new revision.
                _changed[id] = GroupsOf(Kind(a)).ToDictionary(g => g, _ => rev);
                items.Add(id);
                continue;
            }

            foreach (var group in GroupsOf(Kind(a)))
            {
                var differs = group switch
                {
                    "position" => was.Parent != parentAfter || PositionIndex(before, was.Parent, id, after) != PositionIndex(after, parentAfter, id, before),
                    "children" => !ChildIds(was.Item).SetEquals(ChildIds(a)),
                    _ => !Equal(Value(was.Item, group), Value(a, group)),
                };
                if (!differs)
                    continue;
                _changed[id][group] = rev;
                items.Add(id);
            }
        }

        var removed = beforeAt.Keys.Where(id => !afterAt.ContainsKey(id)).ToList();
        foreach (var id in removed)
            _changed.Remove(id);

        var profileChanged = false;
        var profileBefore = before["profile"]!.AsObject();
        var profileAfter = after["profile"]!.AsObject();
        foreach (var field in ProfileFields)
        {
            if (Equal(Member(profileBefore, field), Member(profileAfter, field)))
                continue;
            _profile[field] = rev;
            profileChanged = true;
        }

        Document = after;
        Rev = rev;
        return new Change(items, removed.Order(StringComparer.Ordinal).ToList(), profileChanged, false, null);
    }

    /// <summary>
    /// An item's index among the siblings it has in both versions: the items of <paramref name="parent"/>'s list in
    /// <paramref name="document"/> that are in the same parent's list in <paramref name="other"/> too.
    /// </summary>
    private static int PositionIndex(JsonObject document, string? parent, string id, JsonObject other)
    {
        var otherIds = ListOf(other, parent) is { } list ? list.Select(i => Id(i!.AsObject())).ToHashSet(StringComparer.Ordinal) : [];
        return ListOf(document, parent)!.Select(i => Id(i!.AsObject())).Where(otherIds.Contains).ToList().IndexOf(id);
    }

    private static HashSet<string> ChildIds(JsonObject group) =>
        group["children"]!.AsArray().Select(c => Id(c!.AsObject())).ToHashSet(StringComparer.Ordinal);

    /// <summary>A field group's value: its members as stored, absent ones left out (absent differs from a default).</summary>
    private static JsonObject Value(JsonObject item, string group)
    {
        var value = new JsonObject();
        foreach (var member in group == "design" ? DesignMembers : [group])
            if (item.ContainsKey(member))
                value[member] = item[member]?.DeepClone();
        return value;
    }

    private static JsonObject Member(JsonObject owner, string member) =>
        owner.ContainsKey(member) ? new JsonObject { [member] = owner[member]?.DeepClone() } : new JsonObject();

    private static string[] GroupsOf(string kind) => kind == "group" ? GroupGroups : SceneGroups;

    private static string Kind(JsonObject item) => item["kind"]!.GetValue<string>();

    private static string Id(JsonObject item) => item["id"]!.GetValue<string>();

    /// <summary>Every item in <paramref name="root"/> (a document, or an item and everything in it), a group before its children, with its parent.</summary>
    private static IEnumerable<(JsonObject Item, string? Parent)> Walk(JsonObject root)
    {
        IEnumerable<(JsonObject, string?)> Of(JsonArray list, string? parent)
        {
            foreach (var node in list)
            {
                var item = node!.AsObject();
                yield return (item, parent);
                if (item["children"] is JsonArray children)
                    foreach (var child in Of(children, Id(item)))
                        yield return child;
            }
        }

        if (root.ContainsKey("format"))
            return Of(root["items"]!.AsArray(), null);
        return new[] { (root, (string?)null) }.Concat(root["children"] is JsonArray c ? Of(c, Id(root)) : []);
    }

    /// <summary>The list of <paramref name="parent"/>'s children (the top level for null); null when there is no such group.</summary>
    private static JsonArray? ListOf(JsonObject document, string? parent) =>
        parent == null ? document["items"]!.AsArray() : Locate(document, parent)?.Item["children"] as JsonArray;

    private static (JsonObject Item, string? Parent, JsonArray List)? Locate(JsonObject document, string id)
    {
        foreach (var (item, parent) in Walk(document))
            if (Id(item) == id)
                return (item, parent, ListOf(document, parent)!);
        return null;
    }

    /// <summary>Inserts at <paramref name="index"/> (absent, or past the end: the end).</summary>
    private static void InsertAt(JsonArray list, JsonObject item, int? index)
    {
        if (index is { } at && at < list.Count)
            list.Insert(at, item);
        else
            list.Add(item);
    }

    private sealed record Step(JsonObject Before, JsonObject After, string Actor);

    /// <summary>
    /// What a change stamped: the items with a field group at its revision, the items it removed, and whether a profile
    /// field is at its revision.
    /// </summary>
    public sealed record Change(SortedSet<string> Items, List<string> Removed, bool Profile, bool Merged, string? UndoneActor)
    {
        /// <summary>What a request that changed nothing stamped: nothing.</summary>
        public static Change None { get; } = new([], [], false, false, null);
    }
}
