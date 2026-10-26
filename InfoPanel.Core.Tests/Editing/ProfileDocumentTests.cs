using InfoPanel.Editing;
using InfoPanel.Models;
using InfoPanel.Persistence;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace InfoPanel.Core.Tests.Editing;

public class ProfileDocumentTests
{
    private readonly ManualTimeProvider _time = new();

    private static JsonObject Set(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// Top level: text "A", group "G1" (text "B", group "G2" (text "C")), gauge "Gauge" (two frames), group "G3" (empty).
    /// </summary>
    private ProfileDocument NewDocument(out Ids ids)
    {
        var profile = new Profile("Doc", 400, 200) { Font = "Inter", FontSize = 22, Color = "#FFABCDEF" };
        var a = new TextDisplayItem("A", profile) { X = 10, Y = 20 };
        var b = new TextDisplayItem("B", profile) { X = 30, Y = 40 };
        var c = new TextDisplayItem("C", profile) { X = 50, Y = 60 };
        var g2 = new GroupDisplayItem { Name = "G2" };
        g2.DisplayItems.Add(c);
        var g1 = new GroupDisplayItem { Name = "G1", X = 7, Y = 8 };
        g1.DisplayItems.Add(b);
        g1.DisplayItems.Add(g2);
        var gauge = new GaugeDisplayItem("Gauge", profile) { X = 70, Y = 80 };
        gauge.Images.Add(new ImageDisplayItem("Frame 0", profile, "f0.png", relativePath: true));
        gauge.Images.Add(new ImageDisplayItem("Frame 1", profile, "f1.png", relativePath: true));
        var g3 = new GroupDisplayItem { Name = "G3" };

        ids = new Ids(a.Guid, b.Guid, c.Guid, g1.Guid, g2.Guid, gauge.Guid, gauge.Images[0].Guid, g3.Guid);
        return new ProfileDocument(profile, [a, g1, gauge, g3], time: _time);
    }

    private sealed record Ids(Guid A, Guid B, Guid C, Guid G1, Guid G2, Guid Gauge, Guid Frame, Guid G3);

    private static string Names(IEnumerable<DisplayItem> items) =>
        string.Join(" ", items.Select(i => i is GroupDisplayItem g ? $"{g.Name}[{Names(g.DisplayItems)}]" : i.Name));

    private static ItemPatch Patch(Guid id, string json) => new(id, Set(json));

    [Fact]
    public void PatchItems_ReturnsTheTouchedNodes_AndBumpsTheRevision()
    {
        var doc = NewDocument(out var ids);

        var result = doc.PatchItems([Patch(ids.A, """{"fontSize":48,"x":5}"""), Patch(ids.C, """{"name":"See"}""")]);

        Assert.Equal(2, result.Revision);
        Assert.True(result.Dirty && result.CanUndo && !result.CanRedo);
        Assert.Null(result.Doc);
        Assert.Equal([ids.A.ToString(), ids.C.ToString()], result.Items!.Select(n => n.Id));
        Assert.Equal(48, (int)result.Items![0].Props["fontSize"]!);
        Assert.Equal("See", (string?)result.Items[1].Props["name"]);
        Assert.Equal("A G1[B G2[See]] Gauge G3[]", Names(doc.Items));
    }

    [Fact]
    public void PatchItems_ClampedByTheSetters_IsANoOp_AndShowsTheKeptValue()
    {
        var profile = new Profile("Doc", 400, 200);
        var donut = new DonutDisplayItem("Donut", profile);
        var doc = new ProfileDocument(profile, [donut]);

        var result = doc.PatchItems([Patch(donut.Guid, """{"width":0,"span":999}""")]);

        Assert.Equal(1, result.Revision);
        Assert.False(result.Dirty || result.CanUndo);
        Assert.Equal(100, (int)result.Items![0].Props["width"]!);
        Assert.Equal(360, (int)result.Items[0].Props["span"]!);
    }

    [Fact]
    public void PatchItems_WithIdenticalValues_KeepsTheRevision_AndRaisesNothing()
    {
        var doc = NewDocument(out var ids);
        var events = 0;
        doc.Changed += (_, _) => events++;

        var result = doc.PatchItems([Patch(ids.A, """{"x":10,"name":"A"}""")], coalesceKey: "k");

        Assert.Equal(1, result.Revision);
        Assert.False(result.CanUndo);
        Assert.Equal(0, events);
    }

    [Fact]
    public void PatchItems_IsAllOrNothing()
    {
        var doc = NewDocument(out var ids);
        var before = doc.Snapshot;

        Assert.Equal("fontSize", Assert.Throws<PatchException>(() =>
            doc.PatchItems([Patch(ids.A, """{"x":1}"""), Patch(ids.B, """{"fontSize":"big"}""")])).Key);
        var notFound = Assert.Throws<EditException>(() => doc.PatchItems([Patch(ids.A, """{"x":1}"""), Patch(Guid.NewGuid(), """{"x":1}""")]));
        Assert.Equal(EditError.NotFound, notFound.Error);
        Assert.Equal(EditError.Conflict, Assert.Throws<EditException>(() => doc.PatchItems([Patch(ids.Frame, """{"x":1}""")])).Error);

        Assert.Equal(before, doc.Snapshot);
        Assert.Equal(1, doc.Revision);
    }

    [Fact]
    public void ViewState_WithRecordFalse_SkipsHistory_DoesNotDirty_AndSurvivesUndo()
    {
        var doc = NewDocument(out var ids);
        doc.PatchItems([Patch(ids.A, """{"x":99}""")]);
        doc.Undo();
        Assert.False(doc.Dirty);

        var collapsed = doc.PatchItems([Patch(ids.G1, """{"isExpanded":false}""")], record: false);
        Assert.Equal(4, collapsed.Revision);
        Assert.False(collapsed.Dirty);
        Assert.False(collapsed.CanUndo);
        Assert.True(collapsed.CanRedo); // the view-state patch didn't clear redo

        // View state is kept across redo and undo.
        doc.Redo();
        doc.Undo();
        Assert.False(((GroupDisplayItem)doc.Items[1]).IsExpanded);
        Assert.False(doc.Dirty);

        // record:false is only for view state.
        var refused = Assert.Throws<PatchException>(() => doc.PatchItems([Patch(ids.G1, """{"isExpanded":true,"x":3}""")], record: false));
        Assert.Equal("x", refused.Key);

        // A view-state-only patch never enters the history, even when recorded.
        var expanded = doc.PatchItems([Patch(ids.G1, """{"isExpanded":true}""")]);
        Assert.False(expanded.CanUndo);
    }

    [Fact]
    public void AddItem_InsertsIntoAGroupAtTheIndex_WithFactoryDefaults()
    {
        var doc = NewDocument(out var ids);

        var result = doc.AddItem("text", ids.G1, 1, Set("""{"y":3}"""));

        var node = Assert.Single(result.Items!);
        Assert.Equal("text", node.Kind);
        Assert.Equal("Custom Text", (string?)node.Props["name"]);
        Assert.Equal(("Inter", 22, "#FFABCDEF", 3),
            ((string?)node.Props["font"], (int)node.Props["fontSize"]!, (string?)node.Props["color"], (int)node.Props["y"]!));
        Assert.Equal("A G1[B Custom Text G2[C]] Gauge G3[]", Names(doc.Items));
        Assert.Equal(2, result.Revision);

        doc.AddItem("clock", null, -5);
        doc.AddItem("group", ids.G3, 99);
        doc.AddItem("bar");
        Assert.Equal("Clock A G1[B Custom Text G2[C]] Gauge G3[New Group[]] Bar", Names(doc.Items));
    }

    [Fact]
    public void AddItem_Refusals_ChangeNothing()
    {
        var doc = NewDocument(out var ids);

        Assert.Equal("kind", Assert.Throws<PatchException>(() => doc.AddItem("image")).Key);
        Assert.Equal("kind", Assert.Throws<PatchException>(() => doc.AddItem("nope")).Key);
        Assert.Equal("x", Assert.Throws<PatchException>(() => doc.AddItem("text", set: Set("""{"x":"1"}"""))).Key);
        var notGroup = Assert.Throws<EditException>(() => doc.AddItem("text", ids.A));
        Assert.Equal((EditError.Conflict, "parentId"), (notGroup.Error, notGroup.Key));
        var unknown = Assert.Throws<EditException>(() => doc.AddItem("text", Guid.NewGuid()));
        Assert.Equal((EditError.NotFound, "parentId"), (unknown.Error, unknown.Key));
        Assert.Equal(EditError.Conflict, Assert.Throws<EditException>(() => doc.AddItem("text", ids.Frame)).Error);

        Assert.Equal(1, doc.Revision);
        Assert.Equal("A G1[B G2[C]] Gauge G3[]", Names(doc.Items));
    }

    [Fact]
    public void RemoveItem_ReturnsTheDoc()
    {
        var doc = NewDocument(out var ids);

        var result = doc.RemoveItem(ids.G2);

        Assert.NotNull(result.Doc);
        Assert.Null(result.Items);
        Assert.Equal(2, result.Doc.Revision);
        Assert.Equal("A G1[B] Gauge G3[]", Names(doc.Items));
        Assert.Equal(EditError.NotFound, Assert.Throws<EditException>(() => doc.RemoveItem(ids.C)).Error);
        Assert.Equal(EditError.Conflict, Assert.Throws<EditException>(() => doc.RemoveItem(ids.Frame)).Error);
    }

    [Fact]
    public void MoveItem_AcrossGroups_ClampsTheIndex_AndRefusesCycles()
    {
        var doc = NewDocument(out var ids);

        doc.MoveItem(ids.C, ids.G3, 0);
        Assert.Equal("A G1[B G2[]] Gauge G3[C]", Names(doc.Items));
        doc.MoveItem(ids.B, null, 99);
        Assert.Equal("A G1[G2[]] Gauge G3[C] B", Names(doc.Items));
        var result = doc.MoveItem(ids.A, null, 2); // index in the list without A
        Assert.Equal("G1[G2[]] Gauge A G3[C] B", Names(doc.Items));
        Assert.NotNull(result.Doc);
        doc.MoveItem(ids.G2, ids.G3, -3);
        Assert.Equal("G1[] Gauge A G3[G2[] C] B", Names(doc.Items));

        var revision = doc.Revision;
        Assert.Equal(EditError.Conflict, Assert.Throws<EditException>(() => doc.MoveItem(ids.G3, ids.G3, 0)).Error);
        Assert.Equal(EditError.Conflict, Assert.Throws<EditException>(() => doc.MoveItem(ids.G3, ids.G2, 0)).Error);
        Assert.Equal(EditError.Conflict, Assert.Throws<EditException>(() => doc.MoveItem(ids.B, ids.A, 0)).Error);
        Assert.Equal(EditError.Conflict, Assert.Throws<EditException>(() => doc.MoveItem(ids.Frame, null, 0)).Error);
        Assert.Equal(EditError.NotFound, Assert.Throws<EditException>(() => doc.MoveItem(ids.B, Guid.NewGuid(), 0)).Error);
        Assert.Equal(EditError.NotFound, Assert.Throws<EditException>(() => doc.MoveItem(Guid.NewGuid(), null, 0)).Error);
        Assert.Equal(revision, doc.Revision);

        // Moving to where it already is changes nothing.
        doc.MoveItem(ids.B, null, 99);
        Assert.Equal(revision, doc.Revision);
    }

    [Fact]
    public void DuplicateItem_IsAFaithfulDeepCopy_WithFreshIds_Offset_AfterTheOriginal()
    {
        var doc = NewDocument(out var ids);
        var g1 = (GroupDisplayItem)doc.Items[1];
        g1.IsLocked = true;
        var original = ProfileSerializer.SnapshotItems([g1]);

        var result = doc.DuplicateItem(ids.G1);

        Assert.Equal("A G1[B G2[C]] G1[B G2[C]] Gauge G3[]", Names(doc.Items));
        var copy = (GroupDisplayItem)doc.Items[2];
        Assert.Equal(copy.Guid.ToString(), Assert.Single(result.Items!).Id);
        Assert.Equal(original, ProfileSerializer.SnapshotItems([g1])); // the original is untouched

        var allIds = ItemTree.Walk(doc.Items).Select(i => i.Guid).ToList();
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
        Assert.All(ItemTree.Walk([copy]), i => Assert.Same(doc.Profile, i.Profile));

        // Same XML as the original with the descendants moved by 10 px, once the ids are set aside.
        var expected = (GroupDisplayItem)ProfileDocument.DeepCopy(g1, doc.Profile);
        foreach (var item in ItemTree.Walk(expected.DisplayItems).Where(i => i is not GroupDisplayItem))
        {
            item.X += 10;
            item.Y += 10;
        }
        Assert.Equal(WithoutIds(ProfileSerializer.SnapshotItems([expected])), WithoutIds(ProfileSerializer.SnapshotItems([copy])));
        Assert.Equal((7, 8, true), (copy.X, copy.Y, copy.IsLocked));

        // A gauge copies its frames.
        var gauge = doc.DuplicateItem(ids.Gauge);
        var gaugeCopy = (GaugeDisplayItem)doc.Items[4];
        Assert.Equal((80, 90), (gaugeCopy.X, gaugeCopy.Y));
        Assert.Equal(["f0.png", "f1.png"], gaugeCopy.Images.Select(i => i.FilePath));
        Assert.DoesNotContain(gaugeCopy.Images[0].Guid, ItemTree.Walk(doc.Items.Take(4)).Select(i => i.Guid));
        Assert.Equal(2, gauge.Items![0].Images!.Count);
    }

    private static string WithoutIds(string xml) => Regex.Replace(xml, @"^\s*<Guid>[^<]*</Guid>\r?\n", "", RegexOptions.Multiline);

    [Fact]
    public void UndoRedo_AcrossStructuralOps_RestoresTheExactXml_WithStableIds()
    {
        var doc = NewDocument(out var ids);
        var states = new List<DocumentSnapshot> { doc.Snapshot };

        doc.PatchItems([Patch(ids.A, """{"x":1}""")]);
        states.Add(doc.Snapshot);
        doc.AddItem("sensor", ids.G2, 0);
        states.Add(doc.Snapshot);
        doc.MoveItem(ids.C, null, 0);
        states.Add(doc.Snapshot);
        doc.DuplicateItem(ids.G1);
        states.Add(doc.Snapshot);
        doc.RemoveItem(ids.Gauge);
        states.Add(doc.Snapshot);
        doc.PatchProfile(Set("""{"name":"Renamed","backgroundColor":"#FF000000"}"""));
        states.Add(doc.Snapshot);

        for (var i = states.Count - 2; i >= 0; i--)
        {
            var result = doc.Undo();
            Assert.Equal(states[i], doc.Snapshot);
            Assert.NotNull(result.Doc);
            Assert.Equal(i > 0, result.CanUndo);
            Assert.True(result.CanRedo);
        }

        Assert.False(doc.Dirty);
        Assert.Equal("Doc", doc.Profile.Name);
        Assert.Same(doc.Profile, ItemTree.Walk(doc.Items).First().Profile);
        var noop = doc.Undo();
        Assert.Equal(doc.Revision, noop.Revision);

        for (var i = 1; i < states.Count; i++)
        {
            doc.Redo();
            Assert.Equal(states[i], doc.Snapshot);
        }

        Assert.False(doc.CanRedo);
        Assert.Equal("Renamed", doc.ToDoc().Profile["name"]!.GetValue<string>());
        Assert.Equal(1 + 6 + 6 + 6, doc.Revision); // every edit, undo and redo is a new revision
    }

    [Fact]
    public void Dirty_ComparesWithTheSavedState()
    {
        var doc = NewDocument(out var ids);
        Assert.False(doc.Dirty);

        doc.PatchItems([Patch(ids.A, """{"x":1}""")]);
        Assert.True(doc.Dirty);
        doc.Undo();
        Assert.False(doc.Dirty); // back to the saved state
        doc.Redo();

        IReadOnlyList<DisplayItem>? written = null;
        var saved = doc.Save((_, items) => written = items);
        Assert.False(saved.Dirty);
        Assert.Same(doc.Items, written);
        Assert.Equal(doc.Snapshot, doc.SavedSnapshot);

        doc.Undo();
        Assert.True(doc.Dirty); // the saved state now has x = 1
        doc.Redo();
        Assert.False(doc.Dirty);
    }

    [Fact]
    public void Revert_GoesBackToTheSavedState_AsOneUndoableStep()
    {
        var doc = NewDocument(out var ids);
        doc.PatchItems([Patch(ids.A, """{"x":1}""")]);
        doc.RemoveItem(ids.G1);
        var edited = doc.Snapshot;

        var result = doc.Revert();

        Assert.NotNull(result.Doc);
        Assert.False(result.Dirty);
        Assert.Equal(doc.SavedSnapshot, doc.Snapshot);
        Assert.True(result.CanUndo);

        doc.Undo();
        Assert.Equal(edited, doc.Snapshot);

        // A clean document is left alone.
        doc.Redo();
        var revision = doc.Revision;
        Assert.Equal(revision, doc.Revert().Revision);
    }

    [Fact]
    public void PatchProfile_ReturnsTheDoc_AndValidates()
    {
        var doc = NewDocument(out _);

        var result = doc.PatchProfile(Set("""{"name":"New","width":800}"""));

        Assert.Equal("New", (string?)result.Doc!.Profile["name"]);
        Assert.Equal(800, doc.Profile.Width);
        Assert.Equal("width", Assert.Throws<PatchException>(() => doc.PatchProfile(Set("""{"width":"wide"}"""))).Key);
        Assert.Equal("id", Assert.Throws<PatchException>(() => doc.PatchProfile(Set("""{"id":"x"}"""))).Key);
    }

    [Fact]
    public void Changed_IsRaisedOncePerRevisionChange_OutsideTheLock()
    {
        var doc = NewDocument(out var ids);
        var events = new List<(long Revision, DocumentChange Change)>();
        doc.Changed += (sender, e) =>
        {
            Assert.False(Monitor.IsEntered(doc.SyncRoot));
            events.Add((e.Revision, e.Change));
        };

        doc.PatchItems([Patch(ids.A, """{"x":1}""")]);
        doc.PatchItems([Patch(ids.A, """{"x":1}""")]); // no-op
        doc.AddItem("text");
        doc.Undo();
        doc.Redo();
        doc.Redo(); // nothing to redo
        doc.PatchProfile(Set("""{"name":"N"}"""));
        doc.PatchItems([Patch(ids.G1, """{"isExpanded":false}""")], record: false);
        doc.Save((_, _) => { });
        doc.Revert(); // clean: nothing

        Assert.Equal(
            [(2, DocumentChange.Items), (3, DocumentChange.Structure), (4, DocumentChange.Undo), (5, DocumentChange.Redo),
             (6, DocumentChange.Profile), (7, DocumentChange.Items), (7, DocumentChange.Saved)],
            events);
    }

    [Fact]
    public void History_KeepsAtMost100Entries()
    {
        var doc = NewDocument(out var ids);

        for (var i = 1; i <= 150; i++)
            doc.PatchItems([Patch(ids.A, $$"""{"x":{{i}}}""")]);

        var undos = 0;
        while (doc.CanUndo)
        {
            doc.Undo();
            undos++;
        }

        Assert.Equal(UndoHistory.Limit, undos);
        Assert.Equal(50, doc.Items[0].X);
    }

    [Fact]
    public void Coalescing_ThroughTheDocument()
    {
        var doc = NewDocument(out var ids);

        // A drag: gesture keys merge however long it takes.
        doc.PatchItems([Patch(ids.A, """{"x":11}""")], "gesture:1");
        _time.Advance(5000);
        doc.PatchItems([Patch(ids.A, """{"x":12}""")], "gesture:1");
        // Typing: merges within 1000 ms of the previous keystroke.
        _time.Advance(10);
        doc.PatchItems([Patch(ids.A, """{"name":"Ab"}""")], "text:a");
        _time.Advance(999);
        doc.PatchItems([Patch(ids.A, """{"name":"Abc"}""")], "text:a");
        _time.Advance(1000);
        doc.PatchItems([Patch(ids.A, """{"name":"Abcd"}""")], "text:a");
        // No key: never merges.
        doc.PatchItems([Patch(ids.A, """{"y":1}""")]);
        doc.PatchItems([Patch(ids.A, """{"y":2}""")]);

        doc.Undo();
        Assert.Equal(1, doc.Items[0].Y);
        doc.Undo();
        Assert.Equal(20, doc.Items[0].Y);
        Assert.Equal("Abcd", doc.Items[0].Name);
        doc.Undo();
        Assert.Equal("Abc", doc.Items[0].Name);
        doc.Undo();
        Assert.Equal(("A", 12), (doc.Items[0].Name, doc.Items[0].X));
        doc.Undo();
        Assert.Equal(10, doc.Items[0].X);
        Assert.False(doc.CanUndo);
    }

    [Fact]
    public void Strings_XmlCantCarry_AreRefused_AndLaterEditsStillWork()
    {
        var doc = NewDocument(out var ids);
        var before = doc.Snapshot;

        Assert.Equal("name", Assert.Throws<PatchException>(() => doc.PatchItems([Patch(ids.A, """{"name":"\u0000"}""")])).Key);
        Assert.Equal("name", Assert.Throws<PatchException>(() => doc.PatchItems([Patch(ids.A, """{"x":5,"name":"a\uD800"}""")])).Key);
        Assert.Equal("name", Assert.Throws<PatchException>(() => doc.PatchProfile(Set("""{"name":"\u0001"}"""))).Key);
        Assert.Equal("name", Assert.Throws<PatchException>(() => doc.AddItem("text", set: Set("""{"name":"\u0002"}"""))).Key);

        Assert.Equal(before, doc.Snapshot);
        Assert.Equal((1L, false, false), (doc.Revision, doc.Dirty, doc.CanUndo));
        Assert.Equal("A", doc.Items[0].Name);

        Assert.Equal(2, doc.PatchItems([Patch(ids.A, """{"name":"fine"}""")]).Revision);
    }

    [Fact]
    public void FailureAfterApplying_RollsBackTheModel_Revision_Dirty_AndHistory()
    {
        var doc = NewDocument(out var ids);
        doc.PatchItems([Patch(ids.A, """{"x":1}""")], coalesceKey: "x");
        var before = doc.Snapshot;
        var raised = 0;
        doc.Changed += (_, _) => raised++;

        doc.AfterEdit = () => throw new InvalidOperationException("forced");
        Assert.Throws<InvalidOperationException>(() => doc.PatchItems([Patch(ids.A, """{"x":2}""")], coalesceKey: "x"));
        Assert.Throws<InvalidOperationException>(() => doc.AddItem("text"));
        Assert.Throws<InvalidOperationException>(() => doc.RemoveItem(ids.G1));
        Assert.Throws<InvalidOperationException>(() => doc.PatchProfile(Set("""{"name":"Forced"}""")));
        doc.AfterEdit = null;

        Assert.Equal(before, doc.Snapshot);
        Assert.Equal((2L, true, true, false), (doc.Revision, doc.Dirty, doc.CanUndo, doc.CanRedo));
        Assert.Equal(1, doc.Items[0].X);
        Assert.Equal("A G1[B G2[C]] Gauge G3[]", Names(doc.Items));
        Assert.Equal(0, raised);

        // The history is as before the failures: one undo goes back to the original, then nothing is left.
        doc.Undo();
        Assert.Equal(10, doc.Items[0].X);
        Assert.False(doc.CanUndo);
        Assert.False(doc.Dirty);
    }

    [Fact]
    public void SetterThatThrows_RollsBackTheWholeBatch()
    {
        var doc = NewDocument(out var ids);
        var calendar = Guid.Parse(doc.AddItem("calendar").Items![0].Id);
        var before = doc.Snapshot;
        var revision = doc.Revision;

        var ex = Assert.Throws<PatchException>(() =>
            doc.PatchItems([Patch(ids.A, """{"x":99}"""), Patch(calendar, """{"format":"Q"}""")]));

        Assert.Equal("format", ex.Key);
        Assert.Equal(before, doc.Snapshot);
        Assert.Equal(revision, doc.Revision);
        Assert.Equal(10, doc.Items[0].X);
        Assert.Equal("format", Assert.Throws<PatchException>(() => doc.AddItem("calendar", set: Set("""{"format":"Q"}"""))).Key);
        Assert.Equal(before, doc.Snapshot);
    }

    [Fact]
    public void NonSquareDonut_KeepsItsSize_AcrossUndoRevertAndDuplicate()
    {
        var doc = NewDocument(out var ids);
        var donut = Guid.Parse(doc.AddItem("donut", set: Set("""{"width":121,"height":80}""")).Items![0].Id);
        doc.Save((_, _) => { });

        (int, int) Size(Guid id) => ItemTree.Walk(doc.Items).OfType<DonutDisplayItem>().Where(d => d.Guid == id).Select(d => (d.Width, d.Height)).Single();
        Assert.Equal((121, 80), Size(donut));

        // An unrelated edit and its undo restore every item from the snapshot.
        doc.PatchItems([Patch(ids.A, """{"x":1}""")]);
        doc.Undo();
        Assert.Equal((121, 80), Size(donut));
        Assert.False(doc.Dirty);

        // Revert restores the saved dimensions.
        doc.PatchItems([Patch(donut, """{"width":150,"height":150}""")]);
        doc.Revert();
        Assert.Equal((121, 80), Size(donut));
        Assert.False(doc.Dirty);

        // Duplicates keep them, item and profile alike.
        var copy = Guid.Parse(doc.DuplicateItem(donut).Items![0].Id);
        Assert.Equal((121, 80), Size(copy));
        Assert.Equal((121, 80), doc.CaptureState().Items.OfType<DonutDisplayItem>().Select(d => (d.Width, d.Height)).First());

        // Files keep the 1.x format, Radius included.
        using var file = new MemoryStream();
        ProfileSerializer.WriteItems(file, doc.Items);
        Assert.Contains("<Radius>60</Radius>", System.Text.Encoding.UTF8.GetString(file.ToArray()));
        Assert.DoesNotContain("<Radius>", doc.Snapshot.ItemsXml);
    }
}
