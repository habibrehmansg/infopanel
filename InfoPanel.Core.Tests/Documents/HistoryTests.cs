using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Undo, redo and revert as new revisions over earlier generations, their field revisions, coalescing, the history's
/// count and byte caps (per document and per process), exact dirty state and save.
/// </summary>
public sealed class HistoryTests
{
    [Fact]
    public void Undo_and_redo_are_new_revisions_over_earlier_generations()
    {
        using var doc = Sample();
        var opened = doc.Current;
        doc.Ok(Commit(Place("a", 30, 30)));
        var moved = doc.Current;

        var undo = doc.Undo();
        Assert.Equal(3, undo.Rev);
        Assert.Equal(new Dictionary<string, long> { ["a"] = 3 }, undo.Items);
        Assert.False(undo.Dirty);
        Assert.False(undo.CanUndo);
        Assert.True(undo.CanRedo);
        Assert.True(doc.Current.HasSameContent(opened));
        Assert.Equal(3, doc.ReadRevisions(r => r.FieldRev("a", ItemField.Placement)));
        Assert.Equal(1, doc.ReadRevisions(r => r.FieldRev("a", ItemField.Design)));

        var redo = doc.Redo();
        Assert.Equal((4L, true, true, false), (redo.Rev, redo.Dirty, redo.CanUndo, redo.CanRedo));
        Assert.True(doc.Current.HasSameContent(moved));

        // Nothing to redo: the revision stays.
        Assert.Equal(4, doc.Redo().Rev);
        Assert.Empty(doc.Redo().Items);
    }

    [Fact]
    public void Undo_of_a_structural_change_stamps_what_differs()
    {
        using var doc = Sample();
        doc.Ok(Commit(Remove("g"), Insert(Scene("n")), Move("a", null, 2)));
        var undo = doc.Undo();
        Assert.Equal("a() b() g() c(g)", Tree(doc.Current));
        Assert.Equal(["n"], undo.Removed);
        Assert.Equal(new[] { "a", "b", "c", "g" }, undo.Items.Keys.Order());
        Assert.Equal(3, doc.ReadRevisions(r => r.ItemRev("c")));
        Assert.Equal(3, doc.ReadRevisions(r => r.FieldRev("b", ItemField.Position)));
        Assert.Equal(1, doc.ReadRevisions(r => r.FieldRev("b", ItemField.Placement)));
    }

    [Fact]
    public void A_stale_baseRev_after_undo_conflicts()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("a", 30, 30)));
        doc.Undo();

        // A client that read revision 2 drags again: the undo changed the placement at 3.
        var conflict = doc.Conflict(Commit(Place("a", 31, 30, baseRev: 2)));
        Assert.Equal("0:a:changed:3:placement", string.Join(" ", conflict.Conflicts.Select(c => $"{c.Op}:{c.Id}:{c.Reason}:{c.Rev}:{string.Join(",", c.Fields!)}")));
        doc.Ok(Commit(Attrs("a", name: "fine", baseRev: 2)));
    }

    [Fact]
    public void Undo_redo_and_revert_act_only_on_this_instance_at_the_current_revision()
    {
        using var doc = Sample();
        doc.Ok(Commit(Place("a", 30, 30)));
        doc.Ok(Commit(Design("c", Rect("#00ff00")), Profile(new JsonObject { ["background"] = "#111111" })));
        doc.Ok(Commit(Remove("b")));
        Assert.Equal(4, doc.Rev);

        // Another instance: one conflict for the whole request, whatever the revision.
        var other = doc.Undo(new HistoryRequest { Instance = "an-older-run", BaseRev = 4 }).Conflict!;
        var c = Assert.Single(other.Conflicts);
        Assert.Equal(((int?)null, (string?)null, "instance"), (c.Op, c.Id, c.Reason));

        // A stale revision lists what changed after it, in document order, the profile last; removed items aren't listed.
        string Text(Conflict x) => $"{x.Op}:{x.Id}:{x.Reason}:{x.Rev}:{string.Join(",", x.Fields ?? [])}";
        var stale = doc.Undo(new HistoryRequest { Instance = Instance, BaseRev = 1 }).Conflict!;
        Assert.Equal((409, "conflict", 4L), (stale.Status, stale.Code, stale.Rev));
        Assert.Equal([":a:changed:2:placement", ":c:changed:3:design", "::changed:3:background"], stale.Conflicts.Select(Text));
        Assert.Equal([":c:changed:3:design", "::changed:3:background"], doc.Redo(new HistoryRequest { Instance = Instance, BaseRev = 2 }).Conflict!.Conflicts.Select(Text));
        // Only a removal since: the list is empty.
        Assert.Empty(doc.Revert(new HistoryRequest { Instance = Instance, BaseRev = 3 }).Conflict!.Conflicts);
        Assert.Equal((4L, 3), (doc.Rev, doc.HistorySize.Undo));

        // A revision the document hasn't reached is 422 at /baseRev, after the instance check; nothing is done.
        foreach (var act in new Func<HistoryRequest, CommitOutcome>[] { doc.Undo, doc.Redo, doc.Revert })
        {
            var ahead = act(new HistoryRequest { Instance = Instance, BaseRev = 5 }).Invalid!;
            Assert.Equal((422, "invalid"), (ahead.Status, ahead.Code));
            Assert.Equal(("baseRev.future", "/baseRev"), (Assert.Single(ahead.Errors).Code, ahead.Errors[0].Path));
            Assert.Equal("instance", Assert.Single(act(new HistoryRequest { Instance = "an-older-run", BaseRev = 5 }).Conflict!.Conflicts).Reason);
        }
        Assert.Equal((4L, 3, true), (doc.Rev, doc.HistorySize.Undo, doc.Dirty));

        // A body that breaks the schema is 422; nothing is done.
        var invalid = doc.Undo(Element(new JsonObject { ["instance"] = Instance })).Invalid!;
        Assert.Equal(("schema", ""), (Assert.Single(invalid.Errors).Code, invalid.Errors[0].Path));

        var undo = doc.Undo(Here(doc));
        Assert.Equal(5, undo.Result!.Rev);
        Assert.Equal(6, doc.Redo(Here(doc)).Result!.Rev);
        Assert.Equal(7, doc.Revert(Here(doc)).Result!.Rev);
        Assert.Null(undo.Result.Replayed);
        Assert.Null(undo.Result.Warnings);
    }

    [Fact]
    public void Conflicts_on_a_restored_scene_item_never_list_children_but_a_restored_groups_do()
    {
        const string SceneFields = "placement,design,bindings,name,visible,locked,position";
        string Text(Conflict x) => $"{x.Op}:{x.Id}:{x.Reason}:{x.Rev}:{string.Join(",", x.Fields ?? [])}";

        // Undo restores b at 3.
        using (var doc = Sample())
        {
            doc.Ok(Commit(Remove("b")));
            doc.Undo();
            Assert.Equal([$":b:changed:3:{SceneFields}"], doc.Undo(new HistoryRequest { Instance = Instance, BaseRev = 2 }).Conflict!.Conflicts.Select(Text));
            Assert.Equal([$":b:changed:3:{SceneFields}"], doc.Redo(new HistoryRequest { Instance = Instance, BaseRev = 2 }).Conflict!.Conflicts.Select(Text));
            Assert.Equal([$"0:b:changed:3:{SceneFields}"], doc.Conflict(Commit(Remove("b", baseRev: 2))).Conflicts.Select(Text));
        }

        // Redo restores n at 4.
        using (var doc = Sample())
        {
            doc.Ok(Commit(Insert(Scene("n"))));
            doc.Undo();
            doc.Redo();
            Assert.Equal([$":n:changed:4:{SceneFields}"], doc.Undo(new HistoryRequest { Instance = Instance, BaseRev = 3 }).Conflict!.Conflicts.Select(Text));
        }

        // Revert restores b at 3.
        using (var doc = Sample())
        {
            doc.Ok(Commit(Remove("b")));
            doc.Revert();
            Assert.Equal([$":b:changed:3:{SceneFields}"], doc.Revert(new HistoryRequest { Instance = Instance, BaseRev = 2 }).Conflict!.Conflicts.Select(Text));
        }

        // A group restored with its child: the group's membership is a field group of its own.
        using (var doc = Sample())
        {
            doc.Ok(Commit(Remove("g")));
            doc.Undo();
            Assert.Equal([$":g:changed:3:{SceneFields},children", $":c:changed:3:{SceneFields}"], doc.Undo(new HistoryRequest { Instance = Instance, BaseRev = 2 }).Conflict!.Conflicts.Select(Text));
        }
    }

    [Fact]
    public void A_refused_undo_does_nothing_and_leaves_a_gesture_merging()
    {
        using var doc = Sample();
        doc.Ok(Commit("g1", "gesture:x:1", Place("a", 20, 20)));
        Assert.NotNull(doc.Undo(new HistoryRequest { Instance = Instance, BaseRev = 1 }).Conflict);
        Assert.NotNull(doc.Undo(new HistoryRequest { Instance = "an-older-run", BaseRev = 2 }).Conflict);
        Assert.NotNull(doc.Undo(new HistoryRequest { Instance = Instance, BaseRev = 3 }).Invalid);
        doc.Ok(Commit("g2", "gesture:x:1", Place("a", 25, 25)));
        Assert.Equal((3L, 1), (doc.Rev, doc.HistorySize.Undo));

        doc.Undo();
        Assert.Equal(10, doc.Current.Item("a").Placement.X);
    }

    [Fact]
    public void Gestures_merge_however_long_other_keys_within_a_second_and_never_across_keys()
    {
        var time = new ManualTimeProvider(Start);
        using var doc = Sample(Options(time));
        for (var i = 0; i < 5; i++)
        {
            doc.Ok(Commit($"g{i}", "gesture:tab-1:1", Place("a", i, 0)));
            time.Advance(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(1, doc.HistorySize.Undo);

        doc.Ok(Commit("n1", "name:tab-1:1", Attrs("a", name: "x")));
        time.Advance(TimeSpan.FromMilliseconds(900));
        doc.Ok(Commit("n2", "name:tab-1:1", Attrs("a", name: "xy")));
        time.Advance(TimeSpan.FromMilliseconds(1000));
        doc.Ok(Commit("n3", "name:tab-1:1", Attrs("a", name: "xyz")));
        Assert.Equal(3, doc.HistorySize.Undo);

        // Two clients' gestures interleaved: never merged.
        doc.Ok(Commit("t1", "gesture:tab-1:2", Place("a", 0, 1)));
        doc.Ok(Commit("t2", "gesture:tab-2:1", Place("b", 0, 1)));
        doc.Ok(Commit("t3", "gesture:tab-1:2", Place("a", 0, 2)));
        Assert.Equal(6, doc.HistorySize.Undo);

        // Undo ends a merge: the same key starts a new step.
        doc.Undo();
        doc.Ok(Commit("t4", "gesture:tab-1:2", Place("a", 0, 3)));
        doc.Ok(Commit("t5", "gesture:tab-1:2", Place("a", 0, 4)));
        Assert.Equal(6, doc.HistorySize.Undo);
        doc.Undo();
        Assert.Equal(1, doc.Current.Item("a").Placement.Y);
    }

    [Fact]
    public void An_undo_or_redo_with_nothing_to_do_still_ends_a_merge()
    {
        using var doc = Sample();
        doc.Ok(Commit("m1", "gesture:tab-1:3", Place("a", 1, 0)));
        Assert.Equal(2, doc.Redo().Rev);
        doc.Ok(Commit("m2", "gesture:tab-1:3", Place("a", 2, 0)));
        Assert.Equal(2, doc.HistorySize.Undo);
    }

    [Fact]
    public void A_merged_step_is_charged_its_whole_change_once()
    {
        var budget = new HistoryBudget(HistoryBudget.DefaultMaxBytes);
        using var doc = Open(Doc(Scene("a", scene: BigScene(1000)), Scene("b")), Options(budget: budget));
        var size = Element(BigScene(1000)).GetRawText().Length;
        for (var i = 0; i < 40; i++) doc.Ok(Commit($"d{i}", "gesture:tab-1:4", Design("a", BigScene(i))));

        // One step from the first scene to the last: both scenes and the item's records, not the 38 scenes in between.
        var (undo, _, bytes) = doc.HistorySize;
        Assert.Equal(1, undo);
        Assert.InRange(bytes, 2 * size - 200, 2 * size + 400);
        Assert.Equal(bytes, budget.Used);
        doc.Undo();
        Assert.Equal(Canonical(Element(BigScene(1000))), Canonical(((SceneItem)doc.Current.Item("a")).Scene));
    }

    [Fact]
    public void History_keeps_the_last_100_steps()
    {
        using var doc = Sample();
        for (var i = 1; i <= 105; i++) doc.Ok(Commit(Place("a", i, 0)));
        Assert.Equal(100, doc.HistorySize.Undo);
        for (var i = 0; i < 100; i++) doc.Undo();
        Assert.False(doc.CanUndo);
        Assert.Equal(5, doc.Current.Item("a").Placement.X);
        Assert.Equal(206, doc.Undo().Rev);
    }

    [Fact]
    public void The_byte_cap_wins_over_the_count()
    {
        using var doc = Sample(Options(bytes: 40_000));
        for (var i = 0; i < 10; i++) doc.Ok(Commit(Design("a", BigScene(i))));
        var (undo, _, bytes) = doc.HistorySize;
        Assert.InRange(undo, 1, 9);
        Assert.InRange(bytes, 1, 40_000);
        for (var i = 0; i < undo; i++) doc.Undo();
        Assert.False(doc.CanUndo);
        Assert.InRange(doc.HistorySize.Bytes, 1, 40_000);
    }

    [Fact]
    public void The_process_cap_evicts_the_oldest_steps_of_any_document()
    {
        var budget = new HistoryBudget(60_000);
        using var first = Sample(Options(budget: budget));
        using var second = Sample(Options(budget: budget));
        for (var i = 0; i < 3; i++) first.Ok(Commit(Design("a", BigScene(i))));
        Assert.True(first.CanUndo);
        for (var i = 0; i < 6; i++) second.Ok(Commit(Design("a", BigScene(10 + i))));

        Assert.InRange(budget.Used, 1, 60_000);
        Assert.False(first.CanUndo);
        Assert.Equal(4, first.Undo().Rev);
        Assert.True(second.CanUndo);
        Assert.InRange(second.HistorySize.Undo, 1, 5);

        second.Dispose();
        Assert.Equal(0, budget.Used);
        Assert.Equal(0, budget.Count);
    }

    [Fact]
    public void The_process_cap_holds_with_documents_editing_at_once()
    {
        var budget = new HistoryBudget(200_000);
        var docs = Enumerable.Range(0, 4).Select(_ => Sample(Options(budget: budget))).ToArray();
        Parallel.For(0, docs.Length, d =>
        {
            for (var i = 0; i < 40; i++)
            {
                docs[d].Ok(Commit(Design("a", BigScene(i))));
                if (i % 7 == 3) docs[d].Undo();
            }
        });
        Assert.InRange(budget.Used, 1, 200_000);
        foreach (var doc in docs)
        {
            while (doc.CanUndo) doc.Undo();
            doc.Dispose();
        }
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void Dirty_is_exact_after_a_change_undone_by_hand()
    {
        using var doc = Sample();
        Assert.True(doc.Ok(Commit(Place("a", 30, 30))).Dirty);
        Assert.False(doc.Ok(Commit(Place("a", 10, 10))).Dirty);

        Assert.True(doc.Ok(Commit(Insert(Scene("n")))).Dirty);
        Assert.False(doc.Ok(Commit(Remove("n"))).Dirty);

        // The same scene sent again (other member order and spellings) is the same content.
        Assert.True(doc.Ok(Commit(Design("c", Rect("#00ff00")))).Dirty);
        var again = Element(JsonNode.Parse("""{"opId": "same-scene", "instance": "engine-instance-1", "baseRev": 0, "ops": [{"op": "setDesign", "id": "c", "scene": {"nodes": [{"fill": "#ff0000", "h": 1e1, "w": 20.0, "kind": "rect"}], "format": "infopanel.scene/1"}}]}""")!);
        Assert.False(doc.Ok(again).Dirty);

        // Moving an item away and back is clean too; a reorder is not.
        Assert.True(doc.Ok(Commit(Move("c", null, 0))).Dirty);
        Assert.False(doc.Ok(Commit(Move("c", "g", 0))).Dirty);
        Assert.True(doc.Ok(Commit(Move("b", null, 0))).Dirty);
    }

    [Fact]
    public void Dirty_and_field_revisions_see_any_change_of_a_scene_kept_as_sent()
    {
        // Scenes of a newer format are kept as sent: any change of their value is a change, whatever the engine reads of them.
        using var doc = Sample();
        doc.Ok(Commit(Design("a", Future("1e300"))));
        doc.Save();

        var result = doc.Ok(Commit(Design("a", Future("null"))));
        Assert.Equal((3L, true, "a"), (result.Rev, result.Dirty, string.Join(" ", result.Items.Keys)));
        Assert.Equal(3, doc.ReadRevisions(r => r.FieldRev("a", ItemField.Design)));
        Assert.True(doc.Ok(Commit(Design("a", Future("-1e300")))).Dirty);
        Assert.False(doc.Ok(Commit(Design("a", Future("1e300")))).Dirty);
    }

    // Scenes of a newer format are kept as sent and compared as JSON, each number as the double nearest its text.
    private static JsonObject Future(string n) => JsonNode.Parse($$"""{"format": "infopanel.scene/2", "nodes": [], "n": {{n}}}""")!.AsObject();

    [Fact]
    public void Integers_that_are_the_same_double_are_the_same_number()
    {
        // 9007199254740993 is the double 9007199254740992 (2^53): no change, and the scene stays as first stored.
        using var doc = Sample();
        doc.Ok(Commit(Design("a", Future("9007199254740992"))));
        doc.Save();
        var result = doc.Ok(Commit(Design("a", Future("9007199254740993"))));
        Assert.Equal((2L, 0, false), (result.Rev, result.Items.Count, result.Dirty));
        Assert.Equal(2, doc.ReadRevisions(r => r.FieldRev("a", ItemField.Design)));
        Assert.Equal("9007199254740992", ((SceneItem)doc.Current.Item("a")).Scene.GetProperty("n").GetRawText());
    }

    [Fact]
    public void A_number_too_small_for_a_double_is_zero()
    {
        using var doc = Sample();
        doc.Ok(Commit(Design("a", Future("0"))));
        doc.Save();
        var result = doc.Ok(Commit(Design("a", Future("1e-400"))));
        Assert.Equal((2L, 0, false), (result.Rev, result.Items.Count, result.Dirty));
        Assert.False(doc.Ok(Commit(Design("a", Future("-0")))).Dirty);
    }

    [Fact]
    public void The_same_number_spelled_otherwise_changes_nothing()
    {
        using var doc = Sample();
        doc.Ok(Commit(Design("a", Future("100"))));
        doc.Save();
        var same = doc.Ok(Commit(Design("a", Future("1e2"))));
        Assert.Equal((2L, 0, false), (same.Rev, same.Items.Count, same.Dirty));
        Assert.True(doc.Ok(Commit(Design("a", Future("2")))).Dirty);
        var back = doc.Ok(Commit(Design("a", Future("10.0e1"))));
        Assert.Equal((4L, false), (back.Rev, back.Dirty));
    }

    [Fact]
    public void A_number_in_an_origin_is_the_double_nearest_its_text()
    {
        // 1.00000000000000001 is the double 1: writing it where 1 was changes nothing (no revision, still clean), as the
        // same change to a placement does.
        using var doc = Sample();
        doc.Ok(Commit(WithOrigin(Design("a", Rect()), "1")));
        doc.Save();

        var same = doc.Ok(Commit(WithOrigin(Design("a", Rect()), "1.00000000000000001")));
        Assert.Equal((2L, 0, false), (same.Rev, same.Items.Count, same.Dirty));
        Assert.Equal(2, doc.ReadRevisions(r => r.FieldRev("a", ItemField.Design)));
        var placed = doc.Ok(Element(JsonNode.Parse("""{"opId": "x-1", "instance": "engine-instance-1", "baseRev": 0, "ops": [{"op": "setPlacement", "id": "a", "placement": {"x": 10.00000000000000001, "y": 10}}]}""")!));
        Assert.Equal((2L, 0), (placed.Rev, placed.Items.Count));
        Assert.True(doc.Ok(Commit(WithOrigin(Design("a", Rect()), "1.0000000000000002"))).Dirty);
    }

    private static JsonObject WithOrigin(JsonObject design, string x)
    {
        design["origin"] = JsonNode.Parse($$"""{"x": {{x}}}""");
        return design;
    }

    [Fact]
    public void Revert_is_one_undoable_step_to_the_saved_version()
    {
        using var doc = Sample();
        var saved = doc.Current;
        doc.Ok(Commit(Place("a", 30, 30)));
        doc.Ok(Commit(Remove("g")));
        var changed = doc.Current;

        var revert = doc.Revert();
        Assert.Equal((4L, false, true), (revert.Rev, revert.Dirty, revert.CanUndo));
        Assert.True(doc.Current.HasSameContent(saved));
        Assert.Equal(new[] { "a", "c", "g" }, revert.Items.Keys.Order());

        // A clean document is left alone.
        Assert.Equal(4, doc.Revert().Rev);

        doc.Undo();
        Assert.True(doc.Current.HasSameContent(changed));
        Assert.True(doc.Dirty);
    }

    [Fact]
    public void Save_hands_the_current_generation_to_the_persistence_seam()
    {
        var store = new MemoryPersistence();
        using var doc = Sample(Options(persistence: store));
        doc.Ok(Commit(Place("a", 30, 30)));

        var result = doc.Save();
        Assert.Equal((2L, false, true), (result.Rev, result.Dirty, result.CanUndo));
        var saved = Assert.Single(store.Saved);
        Assert.Same(doc.Current, saved);
        Assert.Same(saved, doc.Saved);
        Assert.Null(saved.Document.Rev);

        // Commits go on while a save writes; the saved version is the one written.
        store.Before = _ => doc.Ok(Commit(Place("b", 1, 1)));
        doc.Save();
        Assert.Equal(2, store.Saved[1].Rev);
        Assert.True(doc.Dirty);

        // A failed write leaves the document dirty.
        store.Before = _ => throw new IOException("disk full");
        Assert.Throws<IOException>(() => doc.Save());
        Assert.True(doc.Dirty);
        Assert.Same(store.Saved[1], doc.Saved);

        // Undo back to the saved version reads clean.
        doc.Undo();
        Assert.False(doc.Dirty);
    }

    [Fact]
    public void A_disposed_document_never_saves_and_disposal_waits_for_a_save_under_way()
    {
        var store = new MemoryPersistence();
        var doc = Sample(Options(persistence: store));
        Task? disposing = null;
        store.Before = _ =>
        {
            disposing = Task.Run(doc.Dispose);
            Assert.False(disposing.Wait(TimeSpan.FromMilliseconds(200)), "disposal should wait for the save");
        };
        doc.Save();
        Assert.True(disposing!.Wait(TimeSpan.FromSeconds(5)));
        store.Before = null;

        Assert.Throws<ObjectDisposedException>(() => doc.Save());
        Assert.Single(store.Saved);
    }

    [Fact]
    public void Changed_is_raised_once_per_revision_and_save()
    {
        using var doc = Sample();
        var events = new List<string>();
        doc.Changed += (_, e) => events.Add($"{e.Change}:{e.Generation.Rev}");
        doc.Ok(Commit(Place("a", 30, 30)));
        doc.Ok(Commit(Place("a", 30, 30)));
        doc.Undo();
        doc.Undo();
        doc.Redo();
        doc.Revert();
        doc.Save();
        Assert.Equal(["Commit:2", "Undo:3", "Redo:4", "Revert:5", "Saved:5"], events);
    }

    [Fact]
    public void A_document_whose_steps_the_budget_evicts_is_told_once_the_evicting_change_is_done()
    {
        var budget = new HistoryBudget(20_000);
        using var a = Sample(Options(budget: budget));
        using var b = Sample(Options(budget: budget));
        a.Ok(Commit(Design("a", BigScene(1))));
        Assert.True(a.CanUndo);
        var events = new List<string>();
        a.Changed += (_, e) => events.Add($"{e.Change}:{e.Generation.Rev}:{a.Status.CanUndo}");

        // The other document's steps take the total over the budget: the oldest, the first document's, is evicted.
        for (var i = 0; i < 4; i++) b.Ok(Commit(Design("a", BigScene(10 + i))));

        Assert.Equal(["History:2:False"], events);
        Assert.False(a.CanUndo);
        Assert.True(b.CanUndo);
    }

    [Fact]
    public void A_document_disposed_before_the_evicting_change_tells_it_is_never_told()
    {
        var budget = new HistoryBudget(20_000);
        using var a = Sample(Options(budget: budget));
        using var b = Sample(Options(budget: budget));
        a.Ok(Commit(Design("a", BigScene(1))));
        var events = new List<DocumentChange>();
        a.Changed += (_, e) => events.Add(e.Change);

        // The commit that evicts the first document's step tells its own handlers before it tells the owners of evicted
        // steps: the first document is deleted (disposed) in between.
        var disposed = false;
        b.Changed += (_, _) =>
        {
            if (!disposed && !a.CanUndo)
            {
                a.Dispose();
                disposed = true;
            }
        };
        for (var i = 0; i < 4; i++) b.Ok(Commit(Design("a", BigScene(10 + i))));

        Assert.True(disposed);
        Assert.Empty(events);
    }

    /// <summary>A valid scene of about 6 KB: many rects.</summary>
    internal static JsonObject BigScene(int seed)
    {
        var nodes = new JsonArray();
        for (var i = 0; i < 120; i++)
            nodes.Add(new JsonObject { ["kind"] = "rect", ["x"] = i + seed, ["y"] = i % 7, ["w"] = 2, ["h"] = 2, ["fill"] = "#336699" });
        return new JsonObject { ["format"] = "infopanel.scene/1", ["nodes"] = nodes };
    }
}
