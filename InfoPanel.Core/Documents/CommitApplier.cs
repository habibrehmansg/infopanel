using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using InfoPanel.Spec;

namespace InfoPanel.Documents;

/// <summary>A commit body that passed step 1 (no document needed): the request, its hash for <c>opId</c> retries, warnings and its checked scenes.</summary>
internal sealed record PreparedCommit(CommitRequest Request, byte[] Hash, IReadOnlyList<CommitWarning> Warnings, SceneChecker Scenes);

/// <summary>The outcome of steps 6 and 7: conflicts (409), else errors (422), else the new content and what changed (none when nothing did).</summary>
internal sealed class AppliedCommit
{
    public List<Conflict> Conflicts { get; } = [];
    public List<SpecError> Errors { get; } = [];
    public GenerationContent? Content { get; set; }
    public ChangeSet? Changes { get; set; }
}

/// <summary>
/// Applies a commit as spec/docs/commit.md, "What the engine checks when it applies a commit", says. Step 1
/// (<see cref="Prepare"/>) needs no document and runs outside its lock; the document runs steps 2 to 5 (<c>opId</c>,
/// <c>instance</c>, the retry window, <see cref="FutureBaseRevs"/>), then <see cref="Apply"/> runs the ops in order against a tentative copy of the current content and
/// checks the result's limits and budgets. Checked scenes come with the commit; the document's aggregate budgets are
/// summed from each item's cached cost, so scenes the commit didn't change are never checked again.
/// </summary>
internal static class CommitApplier
{
    /// <summary>
    /// Step 1: the body's size, counts, schema, each new scene and inserted item on its own (a scene that can't be
    /// compiled is invalid here, at its path in the body). Null with the errors when it fails. The <c>opId</c> hash is
    /// over <paramref name="bytes"/>, the body as received: a retry is the same body byte for byte.
    /// </summary>
    public static PreparedCommit? Prepare(JsonElement body, ReadOnlySpan<byte> bytes, SpecLimits limits, EngineSupport support,
        out IReadOnlyList<SpecError> errors)
    {
        var scenes = new SceneChecker(limits, support);
        var check = CommitValidator.Check(body, limits, support, scenes.CheckCompiles);
        errors = check.Errors;
        if (check.Errors.Count > 0 || check.Commit is null) return null;
        return new PreparedCommit(check.Commit, SHA256.HashData(bytes), check.Warnings, scenes);
    }

    /// <summary>
    /// Steps 6 and 7 against <paramref name="current"/>, with conflicts checked against <paramref name="revisions"/> as
    /// they are (the caller stamps them only after the commit applied). Never changes <paramref name="current"/>.
    /// </summary>
    public static AppliedCommit Apply(GenerationContent current, FieldRevisions revisions, PreparedCommit commit)
    {
        var result = new AppliedCommit();
        var draft = new Draft(current);
        var inserted = new HashSet<string>(StringComparer.Ordinal);
        var designed = new HashSet<string>(StringComparer.Ordinal);
        var placed = new List<(string Id, int Op)>();
        var lastOp = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new Candidates();
        var ops = commit.Request.Ops;

        for (var i = 0; i < ops.Count; i++)
        {
            var p = $"/ops/{i}";
            switch (ops[i])
            {
                case SetPlacementOp o:
                {
                    if (Target(i, o.Id, o.BaseRev, ItemField.Placement) is not { } e) break;
                    draft.Replace(o.Id, e.Item with { Placement = o.Placement });
                    candidates.Add(o.Id, ItemField.Placement);
                    break;
                }
                case SetDesignOp o:
                {
                    if (Target(i, o.Id, o.BaseRev, ItemField.Design) is not { } e) break;
                    if (e.Item is not SceneItem s)
                    {
                        result.Errors.Add(NotScene(p, o.Id));
                        break;
                    }
                    var item = s with { Layout = o.Layout, Source = o.Source, Scene = o.Scene, Origin = o.Origin ?? s.Origin };
                    draft.Replace(o.Id, item, DesignFacts.Of(item, commit.Scenes.Facts(o.Scene)));
                    designed.Add(o.Id);
                    lastOp[o.Id] = i;
                    candidates.Add(o.Id, ItemField.Design);
                    break;
                }
                case SetBindingsOp o:
                {
                    if (Target(i, o.Id, o.BaseRev, ItemField.Bindings) is not { } e) break;
                    if (e.Item is not SceneItem s)
                    {
                        result.Errors.Add(NotScene(p, o.Id));
                        break;
                    }
                    draft.Replace(o.Id, s with { Bindings = o.Bindings });
                    candidates.Add(o.Id, ItemField.Bindings);
                    break;
                }
                case SetAttributesOp o:
                {
                    var fields = (o.Set.Name is null ? ItemField.None : ItemField.Name) | (o.Set.Visible is null ? ItemField.None : ItemField.Visible)
                        | (o.Set.Locked is null ? ItemField.None : ItemField.Locked);
                    if (Target(i, o.Id, o.BaseRev, fields) is not { } e) break;
                    draft.Replace(o.Id, e.Item with
                    {
                        Name = o.Set.Name ?? e.Item.Name,
                        Visible = o.Set.Visible ?? e.Item.Visible,
                        Locked = o.Set.Locked ?? e.Item.Locked,
                    });
                    candidates.Add(o.Id, fields);
                    break;
                }
                case InsertOp o:
                {
                    var ok = Parent(i, p, o.ParentId);
                    void Ids(Item item, string path)
                    {
                        if (draft.Find(item.Id) is not null)
                        {
                            result.Errors.Add(new SpecError(path + "/id", "id.duplicate", $"Another item has the id '{item.Id}'."));
                            ok = false;
                        }
                        if (item is GroupItem g)
                            for (var c = 0; c < g.Children.Count; c++) Ids(g.Children[c], $"{path}/children/{c}");
                    }
                    Ids(o.Item, p + "/item");
                    if (!ok) break;
                    var ids = draft.Insert(Strip(o.Item), o.ParentId, o.Index, commit.Scenes);
                    foreach (var id in ids)
                    {
                        inserted.Add(id);
                        designed.Add(id);
                        lastOp[id] = i;
                        candidates.Add(id, ItemField.All);
                    }
                    placed.Add((o.Item.Id, i));
                    candidates.Add(o.ParentId, ItemField.Children);
                    break;
                }
                case RemoveOp o:
                {
                    if (draft.Find(o.Id) is not { } e)
                    {
                        result.Conflicts.Add(Missing(i, o.Id));
                        break;
                    }
                    var subtree = draft.Subtree(o.Id);
                    if (o.BaseRev is { } baseRev)
                    {
                        var conflicted = false;
                        foreach (var id in subtree)
                        {
                            if (inserted.Contains(id)) continue;
                            var changed = revisions.ChangedAfter(id, FieldRevisions.Tracked(draft.Find(id)!.Item), baseRev);
                            if (changed == ItemField.None) continue;
                            result.Conflicts.Add(Changed(i, id, revisions.ItemRev(id), FieldRevisions.Names(changed)));
                            conflicted = true;
                        }
                        if (conflicted) break;
                    }
                    draft.Remove(o.Id);
                    foreach (var id in subtree)
                    {
                        inserted.Remove(id);
                        candidates.Add(id, ItemField.All);
                    }
                    candidates.Add(e.ParentId, ItemField.Children);
                    break;
                }
                case MoveOp o:
                {
                    var fields = ItemField.Position | (o.Placement is null ? ItemField.None : ItemField.Placement);
                    var target = Target(i, o.Id, o.BaseRev, fields);
                    var parentOk = Parent(i, p, o.ParentId);
                    if (target is null || !parentOk) break;
                    if (o.ParentId is { } parentId && draft.IsWithin(parentId, o.Id))
                    {
                        result.Errors.Add(new SpecError(p + "/parentId", "op.cycle", $"'{o.Id}' can't move into itself or an item inside it."));
                        break;
                    }
                    var from = target.ParentId;
                    draft.Move(o.Id, o.ParentId, o.Index, o.Placement);
                    placed.Add((o.Id, i));
                    lastOp[o.Id] = i;
                    candidates.Add(o.Id, fields);
                    if (from != o.ParentId)
                    {
                        candidates.Add(from, ItemField.Children);
                        candidates.Add(o.ParentId, ItemField.Children);
                    }
                    break;
                }
                case ProfileOp o:
                {
                    var set = o.Set;
                    var fields = (set.Name is null ? ProfileField.None : ProfileField.Name) | (set.Width is null ? ProfileField.None : ProfileField.Width)
                        | (set.Height is null ? ProfileField.None : ProfileField.Height) | (set.Background is null ? ProfileField.None : ProfileField.Background)
                        | (set.StatsOverlay is null ? ProfileField.None : ProfileField.StatsOverlay) | (set.Window is null ? ProfileField.None : ProfileField.Window)
                        | (set.TriggerApps is null ? ProfileField.None : ProfileField.TriggerApps) | (set.Guides is null ? ProfileField.None : ProfileField.Guides)
                        | (set.Editor is null ? ProfileField.None : ProfileField.Editor);
                    if (o.BaseRev is { } baseRev && revisions.ChangedAfter(fields, baseRev) is var changed and not ProfileField.None)
                    {
                        result.Conflicts.Add(new Conflict { Op = i, Reason = "changed", Rev = revisions.ProfileRev, Fields = FieldRevisions.Names(changed) });
                        break;
                    }
                    var profile = draft.Profile;
                    draft.Profile = profile with
                    {
                        Name = set.Name ?? profile.Name,
                        Width = set.Width ?? profile.Width,
                        Height = set.Height ?? profile.Height,
                        Background = set.Background ?? profile.Background,
                        StatsOverlay = set.StatsOverlay ?? profile.StatsOverlay,
                        Window = set.Window ?? profile.Window,
                        TriggerApps = set.TriggerApps ?? profile.TriggerApps,
                        Guides = set.Guides ?? profile.Guides,
                        Editor = set.Editor ?? profile.Editor,
                    };
                    candidates.Profile |= fields;
                    break;
                }
            }
        }
        if (result.Conflicts.Count > 0 || result.Errors.Count > 0) return result;

        // The limits, checked once on the result: the item count and group depth first, on the draft, without recursion
        // and stopping just past the limit, so nothing below runs over a tree larger or deeper than the limits allow.
        var limits = current.Limits;
        if (draft.Count > limits.Items)
        {
            result.Errors.Add(DocumentValidator.Over("", "items", draft.Count, limits.Items));
            return result;
        }
        foreach (var (id, op) in placed)
        {
            if (draft.Find(id) is null) continue;
            var depth = draft.Depth(id, limits.GroupDepth);
            if (depth > limits.GroupDepth) result.Errors.Add(DocumentValidator.Over($"/ops/{op}", "groupDepth", depth, limits.GroupDepth));
        }
        if (result.Errors.Count > 0) return result;

        // A reorder moves the siblings it passes: every item whose index among the siblings present before and after
        // changed has a new position, so the lists the ops changed are compared item by item.
        foreach (var id in draft.ReorderedSiblings()) candidates.Add(id, ItemField.Position);

        var content = draft.Finish();
        string At(string id) => lastOp.TryGetValue(id, out var op) ? $"/ops/{op}" : "";
        if (content.Bytes > limits.DocumentBytes)
            result.Errors.Add(DocumentValidator.Over("", "documentBytes", content.Bytes, limits.DocumentBytes));
        // Each value in the body was within jsonDepth there, but the document nests it under the groups it lands in.
        if (content.JsonDepth > limits.JsonDepth)
            result.Errors.Add(DocumentValidator.Over("", "jsonDepth", content.JsonDepth, limits.JsonDepth));
        // A scene that this commit takes over an aggregate budget is refused; one the document already held over it stays as it was.
        foreach (var (id, error) in content.OverBudget)
            if (!current.OverBudget.ContainsKey(id) || designed.Contains(id))
                result.Errors.Add(error with { Path = At(id) });
        if (result.Errors.Count > 0) return result;

        var changes = ChangeSet.Diff(current, content, candidates);
        if (!changes.IsEmpty)
        {
            result.Content = content;
            result.Changes = changes;
        }
        return result;

        ItemEntry? Target(int op, string id, long? baseRev, ItemField fields)
        {
            if (draft.Find(id) is not { } entry)
            {
                result.Conflicts.Add(Missing(op, id));
                return null;
            }
            if (baseRev is not { } b || inserted.Contains(id)) return entry;
            var changed = revisions.ChangedAfter(id, fields, b);
            if (changed == ItemField.None) return entry;
            result.Conflicts.Add(Changed(op, id, revisions.ItemRev(id), FieldRevisions.Names(changed)));
            return null;
        }

        bool Parent(int op, string path, string? parentId)
        {
            if (parentId is null) return true;
            if (draft.Find(parentId) is not { } parent)
            {
                result.Conflicts.Add(Missing(op, parentId));
                return false;
            }
            if (parent.Item is GroupItem) return true;
            result.Errors.Add(new SpecError(path + "/parentId", "op.parent", $"'{parentId}' is not a group."));
            return false;
        }
    }

    /// <summary>
    /// Step 5: <c>baseRev.future</c> at <c>/baseRev</c> and at each <c>/ops/{i}/baseRev</c> greater than
    /// <paramref name="rev"/>, the document's revision. No client read such a revision, and an op based on it could
    /// never conflict, so it would act as if it carried no guard.
    /// </summary>
    public static List<SpecError> FutureBaseRevs(CommitRequest request, long rev)
    {
        var errors = new List<SpecError>();
        if (request.BaseRev > rev) errors.Add(FutureBaseRev("/baseRev", request.BaseRev, rev));
        for (var i = 0; i < request.Ops.Count; i++)
        {
            var baseRev = request.Ops[i] switch
            {
                SetPlacementOp o => o.BaseRev,
                SetDesignOp o => o.BaseRev,
                SetBindingsOp o => o.BaseRev,
                SetAttributesOp o => o.BaseRev,
                RemoveOp o => o.BaseRev,
                MoveOp o => o.BaseRev,
                ProfileOp o => o.BaseRev,
                _ => null,
            };
            if (baseRev > rev) errors.Add(FutureBaseRev($"/ops/{i}/baseRev", baseRev.Value, rev));
        }
        return errors;
    }

    /// <summary>A <c>baseRev</c> newer than the document's revision <paramref name="rev"/> (a commit's or a history request's).</summary>
    public static SpecError FutureBaseRev(string path, long baseRev, long rev) =>
        new(path, "baseRev.future", $"baseRev {baseRev} is newer than the document's revision {rev}.");

    private static Conflict Missing(int op, string id) => new() { Op = op, Id = id, Reason = "missing" };

    private static Conflict Changed(int op, string id, long rev, IReadOnlyList<string> fields) =>
        new() { Op = op, Id = id, Reason = "changed", Rev = rev, Fields = fields };

    private static SpecError NotScene(string path, string id) =>
        new(path + "/id", "op.target", $"'{id}' is a group: setDesign and setBindings apply to scene items.");

    /// <summary>An inserted item without its <c>rev</c> members (set by the engine; ignored when a client inserts).</summary>
    private static Item Strip(Item item) => item switch
    {
        GroupItem g => g with { Rev = null, Children = g.Children.Select(Strip).ToArray() },
        _ => item.Rev is null ? item : item with { Rev = null },
    };

    /// <summary>
    /// The tentative copy of a content that a commit's ops change: the item index as a builder, and copies of only the
    /// child lists that change. <see cref="Finish"/> rebuilds the records above every changed item (deepest first) and
    /// shares everything else with the content it started from.
    /// </summary>
    private sealed class Draft(GenerationContent start)
    {
        /// <summary>The key of the top-level list (ids are never empty).</summary>
        private const string Top = "";

        private readonly ImmutableDictionary<string, ItemEntry>.Builder _entries = start.Entries.ToBuilder();
        private readonly Dictionary<string, List<string>> _lists = new(StringComparer.Ordinal);
        private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
        private long _bytes = start.ItemBytes;
        private bool _structure, _designs;

        public DocumentProfile Profile { get; set; } = start.Document.Profile;

        /// <summary>The items in the draft, groups' children included.</summary>
        public int Count => _entries.Count;

        public ItemEntry? Find(string id) => _entries.TryGetValue(id, out var e) ? e : null;

        /// <summary>
        /// The nesting depth of the deepest item in <paramref name="id"/>'s subtree where it is in the draft (1 for an
        /// item at the top level with nothing in it), counted level by level without recursion; stops counting once past
        /// <paramref name="max"/>.
        /// </summary>
        public int Depth(string id, int max)
        {
            var depth = 1;
            for (var p = _entries[id].ParentId; p is not null && depth <= max; p = _entries[p].ParentId) depth++;
            var level = new List<string> { id };
            while (depth <= max)
            {
                var next = new List<string>();
                foreach (var g in level)
                    if (_entries[g].Item is GroupItem)
                        next.AddRange(Children(g));
                if (next.Count == 0) break;
                depth++;
                level = next;
            }
            return depth;
        }

        /// <summary>The items of every list the ops changed whose index among the siblings present before and after changed.</summary>
        public IEnumerable<string> ReorderedSiblings()
        {
            foreach (var (key, list) in _lists)
            {
                IReadOnlyList<Item> before = key == Top ? start.Document.Items
                    : start.Entries.TryGetValue(key, out var e) && e.Item is GroupItem g ? g.Children : [];
                var after = list.ToHashSet(StringComparer.Ordinal);
                var common = before.Where(i => after.Contains(i.Id)).Select(i => i.Id).ToList();
                var kept = common.ToHashSet(StringComparer.Ordinal);
                var index = 0;
                foreach (var id in list)
                {
                    if (!kept.Contains(id)) continue;
                    if (common[index++] != id) yield return id;
                }
            }
        }

        /// <summary>The ids in a group (or the top level), in drawing order.</summary>
        private IReadOnlyList<string> Children(string? parentId)
        {
            if (_lists.TryGetValue(parentId ?? Top, out var list)) return list;
            IReadOnlyList<Item> items = parentId is null ? start.Document.Items : ((GroupItem)_entries[parentId].Item).Children;
            return items.Select(i => i.Id).ToList();
        }

        private List<string> Editable(string? parentId)
        {
            var key = parentId ?? Top;
            if (!_lists.TryGetValue(key, out var list))
            {
                list = [.. Children(parentId)];
                _lists[key] = list;
            }
            _touched.Add(key);
            return list;
        }

        /// <summary>The item and everything in it, pre-order, without recursion.</summary>
        public List<string> Subtree(string id)
        {
            var ids = new List<string>();
            var pending = new Stack<string>();
            pending.Push(id);
            while (pending.TryPop(out var next))
            {
                ids.Add(next);
                if (_entries[next].Item is not GroupItem) continue;
                var children = Children(next);
                for (var i = children.Count - 1; i >= 0; i--) pending.Push(children[i]);
            }
            return ids;
        }

        /// <summary>Whether <paramref name="id"/> is <paramref name="ancestor"/> or inside it.</summary>
        public bool IsWithin(string id, string ancestor)
        {
            for (string? p = id; p is not null; p = _entries[p].ParentId)
                if (p == ancestor) return true;
            return false;
        }

        public void Replace(string id, Item item, DesignFacts? design = null)
        {
            var old = _entries[id];
            var entry = design is null ? old.With(item) : new ItemEntry(item, old.ParentId, design);
            _bytes += entry.Bytes - old.Bytes;
            _entries[id] = entry;
            _touched.Add(old.ParentId ?? Top);
            _designs |= design is not null;
        }

        /// <summary>Adds the item with everything in it; returns their ids, pre-order.</summary>
        public List<string> Insert(Item item, string? parentId, int? index, SceneChecker scenes)
        {
            var ids = new List<string>();
            void Add(Item i, string? parent)
            {
                var entry = new ItemEntry(i, parent, DesignFacts.Of(i, i is SceneItem s ? scenes.Facts(s.Scene) : null));
                _entries[i.Id] = entry;
                _bytes += entry.Bytes;
                ids.Add(i.Id);
                if (i is GroupItem g)
                    foreach (var c in g.Children) Add(c, g.Id);
            }
            Add(item, parentId);
            var list = Editable(parentId);
            list.Insert(index is { } at && at < list.Count ? at : list.Count, item.Id);
            _structure = _designs = true;
            return ids;
        }

        public void Remove(string id)
        {
            var parent = _entries[id].ParentId;
            foreach (var d in Subtree(id))
            {
                _bytes -= _entries[d].Bytes;
                _entries.Remove(d);
                _lists.Remove(d);
                _touched.Remove(d);
            }
            Editable(parent).Remove(id);
            _structure = true;
        }

        public void Move(string id, string? parentId, int index, Placement? placement)
        {
            var old = _entries[id];
            Editable(old.ParentId).Remove(id);
            var list = Editable(parentId);
            list.Insert(Math.Min(index, list.Count), id);
            var entry = old.Under(parentId);
            if (placement is not null) entry = entry.With(entry.Item with { Placement = placement });
            _bytes += entry.Bytes - old.Bytes;
            _entries[id] = entry;
            _touched.Add(old.ParentId ?? Top);
            _structure = true;
        }

        public GenerationContent Finish()
        {
            // Every group whose children list or a child's record changed gets a new record, and so does everything above it.
            var dirty = new HashSet<string>(StringComparer.Ordinal);
            var top = false;
            foreach (var key in _touched)
            {
                if (key == Top)
                {
                    top = true;
                    continue;
                }
                if (!_entries.ContainsKey(key)) continue;
                string? p = key;
                for (; p is not null; p = _entries[p].ParentId)
                    if (!dirty.Add(p)) break;
                if (p is null) top = true;
            }
            foreach (var id in dirty.OrderByDescending(Depth).ToList())
            {
                var entry = _entries[id];
                var children = Children(id).Select(c => _entries[c].Item).ToArray();
                var rebuilt = entry.WithChildren((GroupItem)entry.Item with { Children = children });
                _bytes += rebuilt.Bytes - entry.Bytes;
                _entries[id] = rebuilt;
            }
            var items = top ? Children(null).Select(c => _entries[c].Item).ToArray() : start.Document.Items;
            var profiled = !ReferenceEquals(Profile, start.Document.Profile);
            var document = top || profiled ? start.Document with { Profile = Profile, Items = items } : start.Document;
            return GenerationContent.Create(document, _entries.ToImmutable(), start.Limits, start.Support,
                _structure || _designs ? null : start.OverBudget, _bytes, profiled ? null : new(start.EnvelopeBytes, start.EnvelopeDepth));
        }

        private int Depth(string id)
        {
            var depth = 0;
            for (var p = _entries[id].ParentId; p is not null; p = _entries[p].ParentId) depth++;
            return depth;
        }
    }
}
