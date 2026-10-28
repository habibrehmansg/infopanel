using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Three writers (two clients with their own coalesce-key prefixes and a script) commit at once from stale and fresh
/// reads; every outcome is checked against a sequential oracle that implements spec/docs/commit.md on its own, keeping
/// a full state per revision and finding each field group's last change by comparing successive states: accepted
/// revisions are strictly increasing and contiguous, each 409 lists exactly the conflicts (ids, revisions and changed
/// fields) at the revision it answered, each result lists exactly the items and profile the oracle changed, the final
/// document and every field revision are the accepted commits applied in revision order, and undo walks back exactly
/// the steps the coalescing rules make (different coalesce-key prefixes never merge).
/// </summary>
public sealed class ConcurrentWritersTests
{
    private const int Writers = 3, CommitsPerWriter = 50;

    private sealed record Sent(int Writer, JsonArray Ops, string? Key, CommitOutcome Outcome);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Writers_at_once_match_a_sequential_oracle(int seed)
    {
        using var doc = Open(Doc(Scene("a", 10, 10), Scene("b", 50, 10), Scene("c", 90, 10),
            Group("g1", Scene("d", 10, 50), Scene("e", 50, 50)), Group("g2", Scene("f", 90, 50))), Options(steps: 10_000));
        var states = new List<OState> { OState.From(doc.Current) };
        var sent = new List<Sent>[Writers];

        using var start = new Barrier(Writers);
        var threads = Enumerable.Range(0, Writers).Select(w => new Thread(() =>
        {
            var random = new Random(seed * 100 + w);
            var prefix = w == 2 ? null : $"gesture:{Guid.NewGuid()}";
            var log = sent[w] = [];
            var view = doc.Read();
            var gesture = 0;
            start.SignalAndWait();
            for (var n = 0; n < CommitsPerWriter; n++)
            {
                if (random.Next(3) == 0) view = doc.Read();
                string? key = null;
                JsonArray ops;
                if (prefix is not null && random.Next(4) == 0 && Ids(view.Document, scenes: true) is { Count: > 0 } targets)
                {
                    // A drag: a few placement commits under one key.
                    key = $"{prefix}:{++gesture}";
                    var id = targets[random.Next(targets.Count)];
                    for (var k = random.Next(1, 4); k > 0; k--)
                    {
                        ops = [Place(id, random.Next(200), random.Next(100), baseRev: view.Document.Rev)];
                        log.Add(new Sent(w, ops, key, doc.Commit(Commit(view.Generation.Rev, $"w{w}-{n}-{k}", key, Clone(ops)))));
                    }
                    continue;
                }
                ops = [];
                for (var k = random.Next(1, 4); k > 0; k--) ops.Add(RandomOp(random, view.Document, w, n, k));
                log.Add(new Sent(w, ops, key, doc.Commit(Commit(view.Generation.Rev, $"w{w}-{n}", key, Clone(ops)))));
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var all = sent.SelectMany(s => s).ToList();
        Assert.All(all, s => Assert.True(s.Outcome.Invalid is null && s.Outcome.Expired is null, Describe(s.Outcome)));

        // Each writer's accepted revisions increase; the changing ones are distinct and contiguous from 2.
        foreach (var log in sent)
        {
            var revs = log.Where(s => s.Outcome.Result is { } r && Changed(r)).Select(s => s.Outcome.Result!.Rev).ToList();
            Assert.Equal(revs.Order(), revs);
            Assert.Equal(revs.Distinct().Count(), revs.Count);
        }
        var accepted = all.Where(s => s.Outcome.Result is { } r && Changed(r)).OrderBy(s => s.Outcome.Result!.Rev).ToList();
        Assert.Equal(Enumerable.Range(2, accepted.Count).Select(i => (long)i), accepted.Select(s => s.Outcome.Result!.Rev));
        Assert.Equal(accepted.Count + 1, doc.Rev);
        Assert.True(all.Count(s => s.Outcome.Conflict is not null) > 10, "the writers should have conflicted");

        // The oracle applies the accepted commits in revision order; each result names exactly what it changed.
        foreach (var s in accepted)
        {
            var (conflicts, next) = Oracle.Apply(states, states.Count - 1, s.Ops);
            Assert.True(conflicts.Count == 0, $"rev {s.Outcome.Result!.Rev}: {string.Join(" ", conflicts)}");
            Assert.Equal(s.Outcome.Result!.Rev, next!.State.Rev);
            Assert.Equal(next.Changed.Order(), s.Outcome.Result.Items.Keys.Order());
            Assert.Equal(next.Removed.Order(), (s.Outcome.Result.Removed ?? []).Order());
            Assert.Equal(next.ProfileChanged ? next.State.Rev : null, s.Outcome.Result.ProfileRev);
            Assert.All(s.Outcome.Result.Items.Values, rev => Assert.Equal(next.State.Rev, rev));
            states.Add(next.State);
        }

        // Each 409 lists exactly the conflicts the oracle finds at the revision it answered; each no-change result changes nothing there.
        foreach (var s in all)
        {
            if (s.Outcome.Conflict is { } problem)
            {
                var (conflicts, _) = Oracle.Apply(states, (int)problem.Rev - 1, s.Ops);
                Assert.Equal(conflicts, problem.Conflicts.Select(Oracle.Text));
            }
            else if (s.Outcome.Result is { } r && !Changed(r))
            {
                var (conflicts, next) = Oracle.Apply(states, (int)r.Rev - 1, s.Ops);
                Assert.Empty(conflicts);
                Assert.Same(states[(int)r.Rev - 1], next!.State);
            }
        }

        // The final document and every field revision.
        var last = states.Count - 1;
        Assert.Equal(states[last].Fingerprint(), OState.From(doc.Current).Fingerprint());
        foreach (var id in states[last].Items.Keys)
            for (var f = 0; f < 8; f++)
                Assert.True(Oracle.FieldRev(states, last, id, f) == doc.ReadRevisions(r => r.FieldRev(id, (ItemField)(1 << f))), $"{id} field {f}");
        for (var f = 0; f < 9; f++)
            Assert.Equal(Oracle.ProfileFieldRev(states, last, FieldRevisions.Names((ProfileField)(1 << f))[0]), doc.ReadRevisions(r => r.FieldRev((ProfileField)(1 << f))));
        Assert.True(states.Skip(1).Count(st => !JsonNode.DeepEquals(st.Profile, states[0].Profile)) > 0, "the writers should have changed the profile");

        // History: a step per commit unless it has the previous recorded commit's gesture key. Undo walks back each one.
        var steps = new List<long>();
        string? lastKey = null;
        foreach (var s in accepted)
        {
            if (s.Key is null || s.Key != lastKey) steps.Add(s.Outcome.Result!.Rev - 1);
            lastKey = s.Key;
        }
        Assert.Equal(steps.Count, doc.HistorySize.Undo);
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            doc.Undo();
            Assert.Equal(states[(int)steps[i] - 1].Fingerprint(), OState.From(doc.Current).Fingerprint());
        }
        Assert.False(doc.CanUndo);
    }

    private static bool Changed(CommitResult r) => r.Items.Count > 0 || r.Removed is not null || r.ProfileRev is not null;

    private static JsonObject[] Clone(JsonArray ops) => ops.Select(o => o!.DeepClone().AsObject()).ToArray();

    private static List<string> Ids(Document document, bool scenes)
    {
        var ids = new List<string>();
        void Walk(IReadOnlyList<Item> items)
        {
            foreach (var i in items)
            {
                if (i is GroupItem g)
                {
                    if (!scenes) ids.Add(i.Id);
                    Walk(g.Children);
                }
                else if (scenes) ids.Add(i.Id);
            }
        }
        Walk(document.Items);
        return ids;
    }

    private static readonly string[] Colors = ["#ff0000", "#00ff00", "#0000ff"];

    /// <summary>An op on what the writer last read: often stale, sometimes without <c>baseRev</c>.</summary>
    private static JsonObject RandomOp(Random random, Document view, int w, int n, int k)
    {
        var scenes = Ids(view, scenes: true);
        var groups = Ids(view, scenes: false);
        long? baseRev = random.Next(5) == 0 ? null : view.Rev;
        string Pick(List<string> ids) => ids[random.Next(ids.Count)];
        string? Parent() => groups.Count == 0 || random.Next(3) == 0 ? null : Pick(groups);
        var kind = scenes.Count == 0 ? 4 : random.Next(12);
        return kind switch
        {
            0 or 1 => Place(Pick(scenes), random.Next(200), random.Next(100), baseRev),
            2 => Design(Pick(scenes), Rect(Colors[random.Next(3)]), baseRev),
            3 => Attrs(Pick(scenes), name: $"n{random.Next(4)}", baseRev: baseRev),
            4 or 5 => Insert(Scene($"w{w}-{n}-{k}", random.Next(200), random.Next(100), Rect(Colors[random.Next(3)])), Parent(),
                random.Next(2) == 0 ? null : random.Next(5)),
            6 or 7 => Move(Pick(scenes), Parent(), random.Next(5), baseRev),
            8 => Bind(Pick(scenes), "v", $"test:k{random.Next(3)}", baseRev),
            9 => Profile(random.Next(2) == 0 ? new JsonObject { ["name"] = $"p{random.Next(3)}" } : new JsonObject { ["width"] = 200 + 100 * random.Next(2) }, baseRev),
            10 when groups.Count > 0 && random.Next(4) == 0 => Remove(Pick(groups), baseRev),
            _ => Remove(Pick(scenes), baseRev),
        };
    }

    // The oracle: plain immutable-by-convention states, one per revision, implementing the ops from the spec text. Field
    // revisions are never stored: a field group's last change is found by comparing successive states, so the oracle
    // shares no stamping logic with the engine.

    private sealed class OItem
    {
        public required string Id;
        public bool IsGroup;
        public string? Parent;
        public required JsonNode Placement;
        public JsonNode? Scene;
        public JsonNode? Bindings;
        public string? Name;
        public List<string> Children = [];

        public OItem Clone() => new()
        {
            Id = Id, IsGroup = IsGroup, Parent = Parent, Placement = Placement.DeepClone(), Scene = Scene?.DeepClone(),
            Bindings = Bindings?.DeepClone(), Name = Name, Children = [.. Children],
        };
    }

    private sealed class OState
    {
        public Dictionary<string, OItem> Items = new(StringComparer.Ordinal);
        public List<string> Top = [];
        public JsonObject Profile = [];
        public long Rev;

        public List<string> List(string? parent) => parent is null ? Top : Items[parent].Children;

        public OState Clone() => new()
        {
            Items = Items.ToDictionary(p => p.Key, p => p.Value.Clone()), Top = [.. Top], Profile = Profile.DeepClone().AsObject(), Rev = Rev,
        };

        public IEnumerable<string> Subtree(string id) =>
            new[] { id }.Concat(Items[id].Children.SelectMany(Subtree));

        public static OState From(Generation g)
        {
            var profile = JsonNode.Parse(SpecJson.Write(g.Document.Profile).GetRawText())!.AsObject();
            profile.Remove("rev");
            var state = new OState { Rev = g.Rev, Profile = profile };
            void Add(Item item, string? parent)
            {
                state.Items[item.Id] = new OItem
                {
                    Id = item.Id, IsGroup = item is GroupItem, Parent = parent, Placement = JsonNode.Parse(SpecJson.Write(item.Placement).GetRawText())!,
                    Scene = item is SceneItem s ? JsonNode.Parse(s.Scene.GetRawText()) : null,
                    Bindings = item is SceneItem { Bindings: { } b } ? JsonNode.Parse(SpecJson.Write(b).GetRawText()) : null, Name = item.Name,
                };
                state.List(parent).Add(item.Id);
                if (item is GroupItem gi)
                    foreach (var c in gi.Children) Add(c, item.Id);
            }
            foreach (var i in g.Document.Items) Add(i, null);
            return state;
        }

        public string Fingerprint()
        {
            var parts = new List<string> { Canon(Profile) };
            void Walk(List<string> ids)
            {
                foreach (var id in ids)
                {
                    var o = Items[id];
                    parts.Add($"{id}<{o.Parent}>{Canon(o.Placement)}{(o.Scene is null ? "" : Canon(o.Scene))}{(o.Bindings is null ? "" : Canon(o.Bindings))}:{o.Name}");
                    Walk(o.Children);
                }
            }
            Walk(Top);
            return string.Join("\n", parts);
        }
    }

    private static string Canon(JsonNode node) => global::InfoPanel.Spec.CanonicalJson.Write(JsonSerializer.SerializeToElement(node));

    private sealed record Next(OState State, List<string> Changed, List<string> Removed, bool ProfileChanged);

    private static class Oracle
    {
        private static readonly string[] Names = ["placement", "design", "bindings", "name", "visible", "locked", "position", "children"];
        private static readonly string[] ProfileNames = ["name", "width", "height", "background", "statsOverlay", "window", "triggerApps", "guides", "editor"];
        private const int P = 1, D = 2, B = 4, N = 8, Pos = 64, All = 255;

        public static string Text(Conflict c) => $"{c.Op}:{c.Id}:{c.Reason}:{c.Rev}:{string.Join(",", c.Fields ?? [])}";

        private static string Fields(int bits) => string.Join(",", Names.Where((_, i) => (bits & (1 << i)) != 0));

        /// <summary>Whether field group <paramref name="f"/> (a bit index) of an item present in both states differs between them.</summary>
        private static bool Differs(OState a, OState b, string id, int f)
        {
            var x = a.Items[id];
            var y = b.Items[id];
            return f switch
            {
                0 => !JsonNode.DeepEquals(x.Placement, y.Placement),
                1 => (x.Scene is null ? "" : Canon(x.Scene)) != (y.Scene is null ? "" : Canon(y.Scene)),
                2 => !JsonNode.DeepEquals(x.Bindings, y.Bindings),
                3 => x.Name != y.Name,
                6 => x.Parent != y.Parent || CommonIndex(a.List(x.Parent), b.List(y.Parent), id),
                7 => x.IsGroup && !x.Children.ToHashSet().SetEquals(y.Children),
                _ => false,
            };
        }

        /// <summary>The revision of the last state, up to <paramref name="k"/>, in which the item's field group changed (or the item appeared).</summary>
        public static long FieldRev(List<OState> states, int k, string id, int f)
        {
            for (var j = k; j > 0; j--)
                if (!states[j - 1].Items.ContainsKey(id) || Differs(states[j - 1], states[j], id, f)) return states[j].Rev;
            return states[0].Rev;
        }

        private static long ItemRev(List<OState> states, int k, string id) => Enumerable.Range(0, 8).Max(f => FieldRev(states, k, id, f));

        public static long ProfileFieldRev(List<OState> states, int k, string name)
        {
            for (var j = k; j > 0; j--)
                if (!JsonNode.DeepEquals(states[j - 1].Profile[name], states[j].Profile[name])) return states[j].Rev;
            return states[0].Rev;
        }

        /// <summary>The conflicts of <paramref name="ops"/> against state <paramref name="k"/>, or the next state with what changed.</summary>
        public static (List<string> Conflicts, Next? Next) Apply(List<OState> states, int k, JsonArray ops)
        {
            var start = states[k];
            var s = start.Clone();
            var conflicts = new List<string>();
            var inserted = new HashSet<string>();
            bool Stale(int op, string id, long? baseRev, int fields)
            {
                if (baseRev is not { } b || inserted.Contains(id)) return false;
                var changed = 0;
                for (var f = 0; f < 8; f++)
                    if ((fields & (1 << f)) != 0 && (f != 7 || s.Items[id].IsGroup) && FieldRev(states, k, id, f) > b) changed |= 1 << f;
                if (changed == 0) return false;
                conflicts.Add($"{op}:{id}:changed:{ItemRev(states, k, id)}:{Fields(changed)}");
                return true;
            }
            bool Missing(int op, string? id)
            {
                if (id is null || s.Items.ContainsKey(id)) return false;
                conflicts.Add($"{op}:{id}:missing::");
                return true;
            }

            for (var i = 0; i < ops.Count; i++)
            {
                var op = ops[i]!.AsObject();
                var id = (string?)op["id"];
                var baseRev = (long?)op["baseRev"];
                switch ((string)op["op"]!)
                {
                    case "setPlacement":
                        if (Missing(i, id) || Stale(i, id!, baseRev, P)) break;
                        s.Items[id!].Placement = op["placement"]!.DeepClone();
                        break;
                    case "setDesign":
                        if (Missing(i, id) || Stale(i, id!, baseRev, D)) break;
                        s.Items[id!].Scene = op["scene"]!.DeepClone();
                        break;
                    case "setBindings":
                        if (Missing(i, id) || Stale(i, id!, baseRev, B)) break;
                        s.Items[id!].Bindings = op["bindings"]!.DeepClone();
                        break;
                    case "setAttributes":
                        if (Missing(i, id) || Stale(i, id!, baseRev, N)) break;
                        s.Items[id!].Name = (string?)op["set"]!["name"];
                        break;
                    case "insert":
                    {
                        var parent = (string?)op["parentId"];
                        if (Missing(i, parent)) break;
                        var item = op["item"]!;
                        var newId = (string)item["id"]!;
                        s.Items[newId] = new OItem { Id = newId, Parent = parent, Placement = item["placement"]!.DeepClone(), Scene = item["scene"]!.DeepClone() };
                        var list = s.List(parent);
                        var index = (int?)op["index"] is { } at && at < list.Count ? at : list.Count;
                        list.Insert(index, newId);
                        inserted.Add(newId);
                        break;
                    }
                    case "remove":
                    {
                        if (Missing(i, id)) break;
                        var subtree = s.Subtree(id!).ToList();
                        if (baseRev is not null && subtree.Where(x => Stale(i, x, baseRev, All)).ToList().Count > 0) break;
                        s.List(s.Items[id!].Parent).Remove(id!);
                        foreach (var x in subtree)
                        {
                            s.Items.Remove(x);
                            inserted.Remove(x);
                        }
                        break;
                    }
                    case "move":
                    {
                        var parent = (string?)op["parentId"];
                        var missing = Missing(i, id);
                        var stale = !missing && Stale(i, id!, baseRev, Pos);
                        if (Missing(i, parent) || missing || stale) break;
                        s.List(s.Items[id!].Parent).Remove(id!);
                        var list = s.List(parent);
                        list.Insert(Math.Min((int)op["index"]!, list.Count), id!);
                        s.Items[id!].Parent = parent;
                        break;
                    }
                    case "profile":
                    {
                        var set = op["set"]!.AsObject();
                        var changed = ProfileNames.Where(n => set.ContainsKey(n) && baseRev is { } b && ProfileFieldRev(states, k, n) > b).ToList();
                        if (changed.Count > 0)
                        {
                            conflicts.Add($"{i}::changed:{ProfileNames.Max(n => ProfileFieldRev(states, k, n))}:{string.Join(",", changed)}");
                            break;
                        }
                        foreach (var (name, value) in set) s.Profile[name] = value?.DeepClone();
                        break;
                    }
                }
            }
            if (conflicts.Count > 0) return (conflicts, null);

            // What changed: every field group of every item, and every profile field, compared between the two states.
            var changedIds = s.Items.Keys.Where(id => !start.Items.ContainsKey(id) || Enumerable.Range(0, 8).Any(f => Differs(start, s, id, f))).ToList();
            var removed = start.Items.Keys.Where(id => !s.Items.ContainsKey(id)).ToList();
            var profileChanged = ProfileNames.Any(n => !JsonNode.DeepEquals(start.Profile[n], s.Profile[n]));
            if (changedIds.Count == 0 && removed.Count == 0 && !profileChanged) return ([], new Next(start, [], [], false));
            s.Rev = start.Rev + 1;
            return ([], new Next(s, changedIds, removed, profileChanged));
        }

        /// <summary>Whether the item's index among the siblings present in both lists differs.</summary>
        private static bool CommonIndex(List<string> before, List<string> after, string id)
        {
            var common = before.Intersect(after).ToHashSet();
            return before.Where(common.Contains).ToList().IndexOf(id) != after.Where(common.Contains).ToList().IndexOf(id);
        }
    }
}
