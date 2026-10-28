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
/// Commits as spec/docs/commit.md specifies them: revisions, per-field conflicts against <c>baseRev</c>, missing targets,
/// all-or-none application, the 422 checks, <c>opId</c> retries and <c>instance</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class CommitTests
{
    [Fact]
    public void A_commit_is_one_revision_with_the_changed_items()
    {
        using var doc = Sample();
        var result = doc.Ok(Commit(Place("a", 30, 12, baseRev: 1)));

        Assert.Equal(2, result.Rev);
        Assert.Equal(new Dictionary<string, long> { ["a"] = 2 }, result.Items);
        Assert.Null(result.Removed);
        Assert.Null(result.ProfileRev);
        Assert.True(result.Dirty);
        Assert.True(result.CanUndo);
        Assert.False(result.CanRedo);
        Assert.Equal(30, doc.Current.Item("a").Placement.X);
        Assert.Equal(2, doc.ReadRevisions(r => r.FieldRev("a", ItemField.Placement)));
        Assert.Equal(1, doc.ReadRevisions(r => r.FieldRev("a", ItemField.Design)));
        Assert.Same(doc.Saved.Item("b"), doc.Current.Item("b"));
    }

    [Fact]
    public void An_item_rev_is_its_last_change_and_a_group_keeps_its_own()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("c", 1, 2)));

        var state = doc.Read();
        Assert.Equal(2, state.Document.Rev);
        Assert.Equal(1, state.Document.Profile.Rev);
        var group = (GroupItem)Find(state.Document.Items, "g")!;
        Assert.Equal(1, group.Rev);
        Assert.Equal(2, group.Children[0].Rev);
        Assert.Equal(1, Find(state.Document.Items, "a")!.Rev);
        Assert.Null(doc.Current.Document.Rev);
        Assert.Null(doc.Current.Item("c").Rev);
    }

    [Fact]
    public void A_stale_design_conflicts_and_a_stale_placement_still_applies()
    {
        using var doc = Sample();
        doc.Ok(Commit(Design("a", Rect("#00ff00"), baseRev: 1)));
        var newer = doc.Current.Item("a");

        // Another client read revision 1 and produced a design from it: stale design (409).
        var conflict = doc.Conflict(Commit(Design("a", Rect("#0000ff"), baseRev: 1)));
        Assert.Equal(409, conflict.Status);
        Assert.Equal("conflict", conflict.Code);
        Assert.Equal(2, conflict.Rev);
        var c = Assert.Single(conflict.Conflicts);
        Assert.Equal((0, "a", "changed", 2L), (c.Op, c.Id, c.Reason, c.Rev!.Value));
        Assert.Equal(["design"], c.Fields);
        Assert.Same(newer, doc.Current.Item("a"));

        // A drag based on the same revision writes another field group and applies.
        var result = doc.Ok(Commit(Place("a", 70, 20, baseRev: 1)));
        Assert.Equal(3, result.Rev);
        Assert.Equal(70, doc.Current.Item("a").Placement.X);
        Assert.Equal(Canonical(newer), Canonical(doc.Current.Item("a") with { Placement = newer.Placement }));
    }

    [Fact]
    public void A_removed_target_or_parent_is_missing()
    {
        using var doc = Sample();
        doc.Ok(Commit(Remove("b"), Remove("g")));

        var conflict = doc.Conflict(Commit(Place("b", 1, 1, baseRev: 1), Insert(Scene("new"), parentId: "g"), Move("a", "g", 0)));
        Assert.Equal(["0:b:missing", "1:g:missing", "2:g:missing"], conflict.Conflicts.Select(c => $"{c.Op}:{c.Id}:{c.Reason}"));
        Assert.All(conflict.Conflicts, c => Assert.Null(c.Rev));
        Assert.Equal(2, doc.Rev);
    }

    [Fact]
    public void A_conflict_at_op_3_of_5_rolls_back_ops_1_and_2()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("c", 95, 10)));
        var before = doc.Current;
        var history = doc.HistorySize;

        var conflict = doc.Conflict(Commit(
            Place("a", 1, 1, baseRev: 1),
            Attrs("b", name: "renamed", baseRev: 1),
            Place("c", 3, 3, baseRev: 1),
            Place("g", 4, 4, baseRev: 1),
            Profile(new JsonObject { ["name"] = "Renamed" }, baseRev: 1)));

        Assert.Equal("2:c:changed:2:placement", string.Join(" ", conflict.Conflicts.Select(c => $"{c.Op}:{c.Id}:{c.Reason}:{c.Rev}:{string.Join(",", c.Fields!)}")));
        Assert.Same(before, doc.Current);
        Assert.Equal(history, doc.HistorySize);
        Assert.Equal(10, doc.Current.Item("a").Placement.X);
        Assert.Null(doc.Current.Item("b").Name);
    }

    [Fact]
    public void Every_conflicting_op_is_listed_with_the_fields_changed_after_baseRev()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("a", 1, 1), Attrs("a", name: "x")));
        doc.Ok(Commit(Profile(new JsonObject { ["name"] = "P", ["width"] = 300 })));

        var conflict = doc.Conflict(Commit(
            Attrs("a", name: "y", visible: false, baseRev: 1),
            Place("b", 5, 5, baseRev: 1),
            Profile(new JsonObject { ["name"] = "Q", ["height"] = 50, ["width"] = 10 }, baseRev: 2)));

        Assert.Equal(["0:a:changed:2:name", "2::changed:3:name,width"],
            conflict.Conflicts.Select(c => $"{c.Op}:{c.Id}:{c.Reason}:{c.Rev}:{string.Join(",", c.Fields!)}"));
        Assert.Equal(3, conflict.Rev);
    }

    [Fact]
    public void A_remove_conflicts_on_any_change_inside_it_after_baseRev()
    {
        // spec/docs/commit.md: the group's own rev is older than its child's change; a client that read the latest revision may remove it.
        using (var doc = Sample())
        {
            doc.Ok(Commit(Design("c", Rect("#00ff00"))));
            var result = doc.Ok(Commit(Remove("g", baseRev: 2)));
            Assert.Equal(["g", "c"], result.Removed);
        }
        using (var doc = Sample())
        {
            doc.Ok(Commit(Design("c", Rect("#00ff00"))));
            doc.Ok(Commit(Insert(Scene("d"), parentId: "g")));
            doc.Ok(Commit(Move("c", "g", 1)));
            var conflict = doc.Conflict(Commit(Remove("g", baseRev: 2)));
            // The move of c past d moved d too (rev 4).
            Assert.Equal(["0:g:changed:3:children", "0:d:changed:4:placement,design,bindings,name,visible,locked,position", "0:c:changed:4:position"],
                conflict.Conflicts.Select(c => $"{c.Op}:{c.Id}:{c.Reason}:{c.Rev}:{string.Join(",", c.Fields!)}"));
        }
    }

    [Fact]
    public void An_item_inserted_earlier_in_the_commit_never_conflicts()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("a", 2, 2)));
        var result = doc.Ok(Commit(Remove("a", baseRev: 2), Insert(Scene("a", 5, 5)), Place("a", 6, 6, baseRev: 1), Insert(Scene("n")), Attrs("n", name: "N", baseRev: 0)));
        Assert.Equal(3, result.Rev);
        Assert.Equal(6, doc.Current.Item("a").Placement.X);
        Assert.Equal("N", doc.Current.Item("n").Name);
        Assert.Null(result.Removed);
    }

    [Fact]
    public void A_commit_that_changes_nothing_records_nothing()
    {
        using var doc = Sample();
        var before = doc.Current;
        var same = doc.Ok(Commit(Place("a", 10, 10, baseRev: 1), Move("a", null, 0), Place("b", 1, 1), Place("b", 50, 10),
            Remove("c"), Insert(Scene("c", 90, 10), "g")));
        Assert.Equal(1, same.Rev);
        Assert.Empty(same.Items);
        Assert.Null(same.Removed);
        Assert.False(same.Dirty);
        Assert.False(same.CanUndo);
        Assert.Same(before, doc.Current);

        doc.Ok(Commit(Attrs("b", name: "x")));
        var renamed = doc.Ok(Commit(Attrs("b", name: "x")));
        Assert.Equal((2L, 0), (renamed.Rev, renamed.Items.Count));
        Assert.Equal(1, doc.HistorySize.Undo);
    }

    [Fact]
    public void Inserts_and_moves_place_items_by_index()
    {
        using var doc = Sample();
        doc.Ok(Commit(Insert(Scene("front")), Insert(Scene("back"), index: 0), Insert(Scene("far"), index: 2000), Insert(Scene("in"), "g", 0)));
        Assert.Equal("back() a() b() g() in(g) c(g) front() far()", Tree(doc.Current));

        var result = doc.Ok(Commit(Move("a", "g", 99), Move("far", null, 0), Move("in", null, 1)));
        Assert.Equal("far() in() back() b() g() c(g) a(g) front()", Tree(doc.Current));
        // far went to the front past back, b, g and front: they moved too. c only had items taken out and put in next to it.
        Assert.Equal(new[] { "a", "far", "in", "g", "back", "b", "front" }.Order(), result.Items.Keys.Order());
        Assert.Equal(3, doc.ReadRevisions(r => r.FieldRev("g", ItemField.Children)));
        Assert.Equal(3, doc.ReadRevisions(r => r.FieldRev("back", ItemField.Position)));
        Assert.Equal(1, doc.ReadRevisions(r => r.FieldRev("c", ItemField.Position)));
    }

    [Fact]
    public void A_reorder_moves_the_siblings_it_passes()
    {
        using var doc = Open(Doc(Scene("a"), Scene("b"), Scene("c")));
        var result = doc.Ok(Commit(Move("a", null, 2, baseRev: 1)));
        Assert.Equal("b() c() a()", Tree(doc.Current));
        Assert.Equal(new[] { "a", "b", "c" }, result.Items.Keys.Order());

        // b's place among a and c changed at rev 2: a stale move or remove of it conflicts; its placement didn't change.
        Assert.Equal(["0:b:changed:2:position"], doc.Conflict(Commit(Move("b", null, 2, baseRev: 1))).Conflicts.Select(Text));
        Assert.Equal(["0:b:changed:2:position"], doc.Conflict(Commit(Remove("b", baseRev: 1))).Conflicts.Select(Text));
        doc.Ok(Commit(Place("b", 5, 5, baseRev: 1)));
    }

    [Fact]
    public void Op_rules_that_need_the_document_are_422_and_change_nothing()
    {
        using var doc = Sample();
        var before = doc.Current;

        Assert.Equal("op.target /ops/0/id", Error(doc.Invalid(Commit(Design("g", Rect())))));
        Assert.Equal("op.target /ops/1/id", Error(doc.Invalid(Commit(Place("a", 1, 1), Bind("g", "v", "k")))));
        Assert.Equal("op.parent /ops/0/parentId", Error(doc.Invalid(Commit(Insert(Scene("x"), parentId: "a")))));
        Assert.Equal("op.parent /ops/0/parentId", Error(doc.Invalid(Commit(Move("b", "a", 0)))));
        Assert.Equal("op.cycle /ops/0/parentId", Error(doc.Invalid(Commit(Move("g", "g", 0)))));
        Assert.Equal("op.cycle /ops/1/parentId", Error(doc.Invalid(Commit(Insert(Group("h"), "g"), Move("g", "h", 0)))));
        Assert.Equal("id.duplicate /ops/0/item/id", Error(doc.Invalid(Commit(Insert(Scene("a"))))));
        Assert.Equal("id.duplicate /ops/0/item/children/1/id", Error(doc.Invalid(Commit(Insert(Group("h", Scene("x"), Scene("c")))))));
        Assert.Same(before, doc.Current);
        Assert.False(doc.CanUndo);

        static string Error(InvalidProblem p)
        {
            Assert.Equal((422, "invalid"), (p.Status, p.Code));
            var e = Assert.Single(p.Errors);
            return $"{e.Code} {e.Path}";
        }
    }

    [Fact]
    public void An_invalid_design_is_422_and_keeps_document_rev_history_and_frame()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("a", 12, 12)));
        var before = doc.Current;
        var history = doc.HistorySize;
        var frame = Png(before);

        var problem = doc.Invalid(Commit(Place("b", 1, 1), Design("a", InvalidScene(), baseRev: 2)));
        var e = Assert.Single(problem.Errors);
        Assert.Equal(("ref.input", "/ops/1/scene/nodes/0/w"), (e.Code, e.Path));
        Assert.Same(before, doc.Current);
        Assert.Equal(2, doc.Rev);
        Assert.Equal(history, doc.HistorySize);
        Assert.Equal(frame, Png(doc.Current));
    }

    [Fact]
    public void A_number_that_rounds_to_infinity_is_json_at_the_number()
    {
        // 1e400 is past the 64-bit range: refused wherever it is, opaque members included, and nothing else is checked.
        var bad = (JsonObject)JsonNode.Parse("""{"format": "infopanel.scene/1", "state": {"c": {"init": 1e400, "next": {"op": "state", "name": "c"}}}, "nodes": [{"kind": "rect", "w": {"op": "state", "name": "c"}, "h": 10, "fill": "#ffffff"}]}""")!;
        using var doc = Sample();
        var history = doc.HistorySize;

        var origin = Design("b", Rect());
        origin["origin"] = JsonNode.Parse("""{"n": [-1e400]}""");
        var problem = doc.Invalid(Commit(Place("b", 1, 1), Design("a", bad.DeepClone().AsObject()), Insert(Scene("n", scene: bad.DeepClone().AsObject())), origin));
        Assert.Equal(["json /ops/1/scene/state/c/init", "json /ops/2/item/scene/state/c/init", "json /ops/3/origin/n/0"],
            problem.Errors.Select(e => $"{e.Code} {e.Path}"));
        Assert.Equal(1, doc.Rev);
        Assert.Equal(history, doc.HistorySize);

        // A stored document holding one is not JSON as the formats read it: it doesn't open.
        var (stored, check) = ProfileDocument.Open(Doc(Scene("a"), Scene("bad", scene: bad)), Options());
        Assert.Null(stored);
        Assert.Equal("json /items/1/scene/state/c/init", string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path}")));
    }

    [Fact]
    public void A_chain_of_inserts_past_the_limits_is_refused_before_the_tree_is_built()
    {
        // Each insert adds eight nested groups under the deepest group of the one before it.
        JsonElement Chain(int inserts)
        {
            var ops = new List<JsonObject>();
            for (var i = 0; i < inserts; i++)
            {
                var item = Group($"c{i}-7");
                for (var j = 6; j >= 0; j--) item = Group($"c{i}-{j}", item);
                ops.Add(Insert(item, i == 0 ? null : $"c{i - 1}-7"));
            }
            return Commit([.. ops]);
        }
        using var doc = Sample();
        var before = doc.Current;

        var tooMany = doc.Invalid(Chain(1000));
        Assert.Equal("limit.items ", string.Join(" ", tooMany.Errors.Select(e => $"{e.Code} {e.Path}")));
        var tooDeep = doc.Invalid(Chain(200));
        Assert.Equal(101, tooDeep.Errors.Count);
        Assert.All(tooDeep.Errors.Take(100), e => Assert.Equal("limit.groupDepth", e.Code));
        Assert.Equal(("/ops/0", "more", "100 more errors not listed."), (tooDeep.Errors[0].Path, tooDeep.Errors[100].Code, tooDeep.Errors[100].Message));
        Assert.Same(before, doc.Current);
        Assert.Equal((0, 0), (doc.HistorySize.Undo, doc.HistorySize.Redo));
    }

    [Fact]
    public void Errors_found_against_the_document_are_capped_like_any_check()
    {
        using var doc = Sample();
        var problem = doc.Invalid(Commit([.. Enumerable.Range(0, 150).Select(_ => Bind("g", "v", "test:v"))]));
        Assert.Equal(101, problem.Errors.Count);
        Assert.All(problem.Errors.Take(100), e => Assert.Equal("op.target", e.Code));
        Assert.Equal(("more", "50 more errors not listed."), (problem.Errors[100].Code, problem.Errors[100].Message));
    }

    [Fact]
    public void An_unsupported_scene_is_accepted_with_a_warning_kept_verbatim_and_drawn_as_a_placeholder()
    {
        using var doc = Sample();
        var future = FutureScene();
        var result = doc.Ok(Commit(Design("a", future), Insert(Scene("pasted", 100, 50, FutureScene()))));

        Assert.Equal(["0:a:unsupported", "1:pasted:unsupported"], result.Warnings!.Select(w => $"{w.Op}:{w.Id}:{w.Code}"));
        Assert.Equal(global::InfoPanel.Scenes.CanonicalJson.Write(Element(future)),
            global::InfoPanel.Scenes.CanonicalJson.Write(((SceneItem)doc.Current.Item("a")).Scene));
        Assert.Equal(["a:unsupported", "pasted:unsupported"], doc.Current.Problems.Select(p => $"{p.Id}:{p.Problem}"));

        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(doc.Current);
        using var snapshot = runtime.Snapshot(SceneKit.Now);
        Assert.Equal("unsupported", snapshot.Bounds.Single(b => b.Id == "a").Problem);
        Assert.Null(snapshot.Bounds.Single(b => b.Id == "b").Problem);
    }

    [Fact]
    public void Aggregate_budgets_refuse_a_commit_that_goes_over_and_tolerate_what_was_over()
    {
        var options = new ProfileDocumentOptions
        {
            Instance = Instance, Time = new ManualTimeProvider(Start), HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes),
            Limits = SpecLimits.Default with { DocumentNodes = 2 },
        };
        using var doc = Open(Doc(Scene("a"), Scene("b"), Scene("over")), options);
        Assert.Equal(["over:budget"], doc.Current.Problems.Select(p => $"{p.Id}:{p.Problem}"));

        // Stored over budget: still placed, renamed, hidden.
        doc.Ok(Commit(Place("over", 1, 1), Attrs("over", name: "o")));
        // A new scene that would take the document over is refused.
        var problem = doc.Invalid(Commit(Place("a", 3, 3), Insert(Scene("more"))));
        Assert.Equal("limit.documentNodes /ops/1", string.Join(" ", problem.Errors.Select(e => $"{e.Code} {e.Path}")));
        // Removing one makes room, in document order.
        doc.Ok(Commit(Remove("a")));
        Assert.Empty(doc.Current.Problems);
    }

    [Fact]
    public void Group_depth_is_checked_where_items_land()
    {
        var deep = Scene("leaf");
        for (var i = 7; i >= 1; i--) deep = Group($"d{i}", deep);
        using var doc = Sample();
        var problem = doc.Invalid(Commit(Insert(deep.DeepClone().AsObject(), "g")));
        Assert.Equal("limit.groupDepth /ops/0", string.Join(" ", problem.Errors.Select(e => $"{e.Code} {e.Path}")));
        doc.Ok(Commit(Insert(deep)));
        Assert.Equal("limit.groupDepth /ops/0", string.Join(" ", doc.Invalid(Commit(Move("d1", "g", 0))).Errors.Select(e => $"{e.Code} {e.Path}")));
    }

    [Fact]
    public void A_replay_returns_the_stored_result_after_other_commits_and_after_undo()
    {
        using var doc = Sample();
        var body = Commit("drag-1", null, Place("a", 40, 40, baseRev: 1));
        var first = doc.Ok(body);
        var again = doc.Ok(body);

        Assert.True(again.Replayed);
        Assert.Null(first.Replayed);
        Assert.Equal(SpecJson.Write(first).GetRawText(), SpecJson.Write(again with { Replayed = null }).GetRawText());
        Assert.Equal(2, doc.Rev);
        Assert.Equal(1, doc.HistorySize.Undo);

        doc.Ok(Commit(Place("b", 1, 1)));
        doc.Undo();
        doc.Undo();
        var late = doc.Ok(body);
        Assert.True(late.Replayed);
        Assert.Equal(2, late.Rev);
        Assert.Equal(10, doc.Current.Item("a").Placement.X);
        Assert.Equal(5, doc.Rev);

        // A retry is the same body byte for byte: the same ops with members in another order are another body.
        var reordered = Element(JsonNode.Parse("""{"ops": [{"placement": {"y": 4.0E1, "x": 40}, "baseRev": 1, "id": "a", "op": "setPlacement"}], "baseRev": 0, "instance": "engine-instance-1", "opId": "drag-1"}""")!);
        Assert.Equal("opId.reused", Assert.Single(doc.Invalid(reordered).Errors).Code);
    }

    [Fact]
    public void A_retry_is_compared_byte_for_byte_even_where_numbers_are_the_same_double()
    {
        using var doc = Sample();
        doc.Ok(Element(JsonNode.Parse("""{"opId": "o", "instance": "engine-instance-1", "baseRev": 1, "ops": [{"op": "setDesign", "id": "a", "scene": {"format": "infopanel.scene/1", "nodes": []}, "origin": {"n": 1}}]}""")!));
        var problem = doc.Invalid(Element(JsonNode.Parse("""{"opId": "o", "instance": "engine-instance-1", "baseRev": 1, "ops": [{"op": "setDesign", "id": "a", "scene": {"format": "infopanel.scene/1", "nodes": []}, "origin": {"n": 1.00000000000000001}}]}""")!));
        Assert.Equal(("opId.reused", "/opId"), (Assert.Single(problem.Errors).Code, problem.Errors[0].Path));
        Assert.Equal(2, doc.Rev);
    }

    [Fact]
    public void A_reused_opId_with_another_body_is_422()
    {
        using var doc = Sample();
        doc.Ok(Commit("same", null, Place("a", 40, 40)));
        var problem = doc.Invalid(Commit("same", null, Place("a", 41, 40)));
        var e = Assert.Single(problem.Errors);
        Assert.Equal(("opId.reused", "/opId"), (e.Code, e.Path));
        Assert.Equal(40, doc.Current.Item("a").Placement.X);
    }

    [Fact]
    public void A_refused_commit_is_checked_again_on_retry()
    {
        using var doc = Sample();
        doc.Ok(Commit(Remove("b")));
        var missing = Commit("retry-1", null, Place("b", 1, 1));
        doc.Conflict(missing);
        doc.Ok(Commit(Insert(Scene("b"))));
        Assert.Null(doc.Ok(missing).Replayed);

        var invalid = Commit("retry-2", null, Insert(Scene("x"), parentId: "a"));
        doc.Invalid(invalid);
        doc.Invalid(invalid);

        doc.Ok(Commit(Place("a", 3, 3)));
        var stale = Commit("retry-3", null, Place("a", 5, 5, baseRev: 1));
        Assert.Equal("changed", doc.Conflict(stale).Conflicts[0].Reason);
        Assert.Equal("changed", doc.Conflict(stale).Conflicts[0].Reason);
    }

    [Fact]
    public void A_baseRev_newer_than_the_document_is_422_at_its_path_after_the_opId_and_instance()
    {
        using var doc = Sample();
        var applied = Commit(1, "drag", null, Place("a", 40, 40, baseRev: 1));
        Assert.Equal(2, doc.Ok(applied).Rev);
        static string Errors(InvalidProblem p) => string.Join(" ", p.Errors.Select(e => $"{e.Code} {e.Path}"));

        // The commit's own baseRev and every op's newer than the document's revision (2) are listed; an insert has none.
        var ahead = doc.Invalid(Commit(3, "ahead", null, Place("a", 41, 40, baseRev: 2), Insert(Scene("n")), Design("a", Rect("#00ff00"), baseRev: 3),
            Bind("b", "v", "test:v", baseRev: 3), Attrs("b", name: "x", baseRev: 4), Remove("b", baseRev: 9), Move("a", "g", 0, baseRev: 3),
            Profile(new JsonObject { ["background"] = "#111111" }, baseRev: 3)));
        Assert.Equal((422, "invalid"), (ahead.Status, ahead.Code));
        Assert.Equal("baseRev.future /baseRev baseRev.future /ops/2/baseRev baseRev.future /ops/3/baseRev baseRev.future /ops/4/baseRev "
            + "baseRev.future /ops/5/baseRev baseRev.future /ops/6/baseRev baseRev.future /ops/7/baseRev", Errors(ahead));
        Assert.Equal((2L, 40.0, 1), (doc.Rev, doc.Current.Item("a").Placement.X, doc.HistorySize.Undo));

        // Only an op's: the commit's own is current. Ops that would otherwise conflict or miss aren't checked.
        Assert.Equal("baseRev.future /ops/1/baseRev", Errors(doc.Invalid(Commit(2, "op-ahead", null, Place("gone", 1, 1), Place("a", 1, 1, baseRev: 3)))));

        // A retry of an applied commit still replays, and another instance's revisions are refused as such first.
        Assert.True(doc.Ok(applied).Replayed);
        var other = Element(new JsonObject { ["opId"] = "other", ["instance"] = "an-older-run", ["baseRev"] = 9, ["ops"] = new JsonArray(Place("a", 3, 3, baseRev: 9)) });
        Assert.Equal("instance", Assert.Single(doc.Conflict(other).Conflicts).Reason);

        // A refused commit isn't remembered: once the document has reached its baseRev, its retry applies.
        var early = Commit(3, "early", null, Place("b", 7, 7, baseRev: 3));
        doc.Invalid(early);
        Assert.Equal(3, doc.Ok(Commit(2, "next", null, Place("a", 50, 50, baseRev: 2))).Rev);
        Assert.Equal(4, doc.Ok(early).Rev);
        Assert.Equal(7, doc.Current.Item("b").Placement.X);
    }

    [Fact]
    public void Applied_opIds_are_remembered_for_the_last_1000_commits_then_expire()
    {
        using var doc = Sample();
        var first = Commit(1, "op-first", null, Place("a", 1, 1));
        doc.Ok(first);
        var bodies = new List<JsonElement>();
        for (var i = 0; i < 1000; i++)
        {
            bodies.Add(Commit(doc.Rev, $"op-{i:0000}", null, Place("b", i, 0)));
            doc.Ok(bodies[^1]);
        }

        Assert.True(doc.Ok(bodies[999]).Replayed);
        Assert.True(doc.Ok(bodies[0]).Replayed);
        // op-first fell out; the window starts at op-0000's revision (3), so its retry (based on 1) is refused, not applied again.
        var expired = Expired(doc, first);
        Assert.Equal((409, "expired", 1002L), (expired.Status, expired.Code, expired.Rev));
        Assert.Equal(1002, doc.Rev);

        doc.Ok(Commit(doc.Rev, "op-new", null, Place("a", 2, 2)));
        Assert.Equal(1003, Expired(doc, bodies[0]).Rev);
        Assert.True(doc.Ok(bodies[1]).Replayed);
    }

    [Fact]
    public void The_retry_window_starts_at_the_oldest_remembered_opId_and_counts_commits_that_changed_nothing()
    {
        var options = Options();
        using var doc = Sample(new ProfileDocumentOptions
        {
            Instance = options.Instance, Time = options.Time, HistoryBudget = options.HistoryBudget, Persistence = options.Persistence,
            RememberedOps = 2,
        });
        var c1 = Commit(1, "c1", null, Place("a", 20, 20));
        Assert.Equal(2, doc.Ok(c1).Rev);
        Assert.Equal(2, doc.Ok(Commit(2, "c2", null, Place("a", 20, 20))).Rev);
        var c3 = Commit(2, "c3", null, Place("a", 30, 30));
        Assert.Equal(3, doc.Ok(c3).Rev);

        // c1 is forgotten and the window starts at c2's revision (2): c1's retry, based on 1, is refused and applies nothing.
        Assert.Equal(3, Expired(doc, c1).Rev);
        Assert.Equal((3L, 30.0, 2), (doc.Rev, doc.Current.Item("a").Placement.X, doc.HistorySize.Undo));

        // A baseRev at the window's start isn't older than it.
        Assert.Equal(4, doc.Ok(Commit(2, "c4", null, Place("b", 1, 1))).Rev);
        // c2 is forgotten now: the window starts at c3's revision (3).
        Expired(doc, Commit(2, "c5", null, Place("b", 2, 2)));
        Assert.True(doc.Ok(c3).Replayed);

        // The instance is checked before the window.
        var other = Element(new JsonObject { ["opId"] = "c6", ["instance"] = "an-older-run", ["baseRev"] = 0, ["ops"] = new JsonArray(Place("a", 3, 3)) });
        Assert.Equal("instance", Assert.Single(doc.Conflict(other).Conflicts).Reason);
    }

    [Fact]
    public void A_commit_from_another_engine_instance_conflicts()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("a", 2, 2)));
        var body = Element(new JsonObject { ["opId"] = "o1", ["instance"] = "an-older-run", ["baseRev"] = 2, ["ops"] = new JsonArray(Place("a", 3, 3)) });
        var conflict = doc.Conflict(body);
        var c = Assert.Single(conflict.Conflicts);
        Assert.Equal(((int?)null, (string?)null, "instance", (long?)null), (c.Op, c.Id, c.Reason, c.Rev));
        Assert.Equal(2, conflict.Rev);

        var current = Element(new JsonObject { ["opId"] = "o2", ["instance"] = Instance, ["baseRev"] = 2, ["ops"] = new JsonArray(Place("a", 3, 3)) });
        Assert.Equal(3, doc.Ok(current).Rev);
    }

    [Fact]
    public void A_retry_is_the_whole_body_byte_for_byte_whitespace_around_it_included()
    {
        using var doc = Sample();
        const string body = """{"opId": "ws", "instance": "engine-instance-1", "baseRev": 1, "ops": [{"op": "setPlacement", "id": "a", "placement": {"x": 40, "y": 40}}]}""";
        Assert.Equal(200, doc.Commit(Encoding.UTF8.GetBytes(body)).Status);
        Assert.True(doc.Commit(Encoding.UTF8.GetBytes(body)).Result!.Replayed);

        var padded = doc.Commit(Encoding.UTF8.GetBytes(" " + body + "\n"));
        Assert.Equal(("opId.reused", "/opId"), (Assert.Single(padded.Invalid!.Errors).Code, padded.Invalid.Errors[0].Path));
        Assert.Equal(2, doc.Rev);

        var notJson = doc.Commit(Encoding.UTF8.GetBytes(body[..^1]));
        Assert.Equal(("json", ""), (Assert.Single(notJson.Invalid!.Errors).Code, notJson.Invalid.Errors[0].Path));

        // The document keeps nothing of the buffer it was read from.
        var buffer = Encoding.UTF8.GetBytes(Commit(Design("a", Rect("#00ff00"))).GetRawText());
        Assert.Equal(3, doc.Commit(buffer).Result!.Rev);
        Array.Fill(buffer, (byte)' ');
        Assert.Contains("#00ff00", SpecJson.Write(doc.Read().Document).GetRawText());
    }

    [Fact]
    public void A_locked_item_is_still_editable()
    {
        using var doc = Open(Doc(Scene("a", 1, 1, extra: new JsonObject { ["locked"] = true })));
        doc.Ok(Commit(Place("a", 9, 9), Attrs("a", name: "still"), Design("a", Rect("#123456"))));
        Assert.Equal((9.0, "still", true), (doc.Current.Item("a").Placement.X, doc.Current.Item("a").Name, doc.Current.Item("a").Locked!.Value));
        doc.Ok(Commit(Remove("a")));
    }

    [Fact]
    public void A_window_placement_is_an_ordinary_profile_commit()
    {
        using var doc = Sample();
        var window = new JsonObject { ["enabled"] = true, ["x"] = 120, ["y"] = 40, ["draggable"] = true };
        var result = doc.Ok(Commit("w1", "gesture:window-drag:1", Profile(new JsonObject { ["window"] = window })));
        doc.Ok(Commit("w2", "gesture:window-drag:1", Profile(new JsonObject { ["window"] = window.DeepClone()!.AsObject().Also(w => w["x"] = 140), ["width"] = 220 })));

        Assert.Equal(2, result.ProfileRev);
        Assert.Equal((140, 220), (doc.Current.Document.Profile.Window!.X!.Value, doc.Current.Document.Profile.Width));
        Assert.Equal(1, doc.HistorySize.Undo);
        doc.Undo();
        Assert.Null(doc.Current.Document.Profile.Window);
        Assert.Equal(4, doc.ReadRevisions(r => r.FieldRev(ProfileField.Window)));
        Assert.Equal(1, doc.ReadRevisions(r => r.FieldRev(ProfileField.Name)));
    }

    private static ExpiredProblem Expired(ProfileDocument doc, JsonElement body)
    {
        var outcome = doc.Commit(body);
        Assert.True(outcome.Expired is not null, Describe(outcome));
        return outcome.Expired;
    }

    private static string Text(Conflict c) => $"{c.Op}:{c.Id}:{c.Reason}:{c.Rev}:{string.Join(",", c.Fields ?? [])}";

    internal static byte[] Png(Generation generation)
    {
        using var runtime = new SceneRuntime(SceneKit.Options());
        runtime.Load(generation);
        using var snapshot = runtime.Snapshot(SceneKit.Now);
        return snapshot.RenderPng();
    }
}

internal static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}
