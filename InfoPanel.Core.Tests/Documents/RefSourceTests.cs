using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Documents;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using SkiaSharp;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Items whose <c>source</c> is an opaque <c>ref</c> (spec/docs/document.md, "Source and origin"): the engine draws them
/// and lets clients place, rotate, rebind, hide and remove them like any item, keeps their source and origin byte for
/// byte through every change, undo, redo and copy, and replaces them only with a new design.
/// </summary>
[Collection(GoldenCollection.Name)]
public sealed class RefSourceTests
{
    private const string SourceJson = """{"ref":"c1c2b7f0-opaque-reference","deps":["sha256:1111111111111111111111111111111111111111111111111111111111111111"]}""";
    private const string OriginJson = """{"from":"elsewhere","n":1.50}""";

    private static ProfileDocument RefDoc() => Open(Doc(
        Scene("held", 20, 20, InputScene(), new JsonObject
        {
            ["source"] = JsonNode.Parse(SourceJson),
            ["origin"] = JsonNode.Parse(OriginJson),
            ["layout"] = new JsonObject { ["w"] = 60, ["h"] = 10 },
            ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = "test:v" } },
        }),
        Scene("other", 120, 60)));

    private static void AssertKept(Generation g)
    {
        var item = (SceneItem)g.Item("held");
        Assert.Equal(SourceJson, SpecJson.Write(item.Source).GetRawText());
        Assert.Equal(OriginJson, item.Origin!.Value.GetRawText());
    }

    private static SKColor PixelAt(Generation g, int x, int y)
    {
        var inputs = new FakeSceneInputs();
        inputs.Number("test:v", 40);
        inputs.Number("test:w", 10);
        using var runtime = new SceneRuntime(SceneKit.Options(inputs));
        runtime.Load(g);
        using var snapshot = runtime.Snapshot(SceneKit.Now);
        using var bitmap = snapshot.Render();
        return bitmap.GetPixel(x, y);
    }

    [Fact]
    public void RefSource_item_renders()
    {
        using var doc = RefDoc();
        AssertKept(doc.Current);
        Assert.Empty(doc.Current.Problems);
        Assert.Equal(new SKColor(0, 255, 0), PixelAt(doc.Current, 50, 25));
        Assert.Equal(new SKColor(0, 0, 0), PixelAt(doc.Current, 65, 25));
    }

    [Fact]
    public void RefSource_item_moves_rotates_rebinds_hides_and_removes()
    {
        using var doc = RefDoc();
        doc.Ok(Commit(Place("held", 30, 40, baseRev: 1)));
        AssertKept(doc.Current);
        Assert.Equal(new SKColor(0, 255, 0), PixelAt(doc.Current, 60, 45));

        doc.Ok(Commit(Place("held", 30, 40, rotation: 90)));
        AssertKept(doc.Current);
        Assert.Equal(90, doc.Current.Item("held").Placement.Rotation);

        doc.Ok(Commit(Bind("held", "v", "test:w")));
        AssertKept(doc.Current);
        Assert.Equal("test:w", ((SceneItem)doc.Current.Item("held")).Bindings!["v"].Key);

        doc.Ok(Commit(Attrs("held", visible: false, name: "hidden", locked: true)));
        AssertKept(doc.Current);
        Assert.Equal(new SKColor(0, 0, 0), PixelAt(doc.Current, 60, 45));

        doc.Ok(Commit(Move("held", null, 1)));
        AssertKept(doc.Current);

        var removed = doc.Ok(Commit(Remove("held", baseRev: 6)));
        Assert.Equal(["held"], removed.Removed);
    }

    [Fact]
    public void RefSource_undo_redo_and_duplicate_keep_it_byte_identical()
    {
        using var doc = RefDoc();
        doc.Ok(Commit(Place("held", 30, 40), Bind("held", "v", "test:w")));
        doc.Ok(Commit(Remove("held")));
        doc.Undo();
        AssertKept(doc.Current);
        doc.Undo();
        AssertKept(doc.Current);
        doc.Redo();
        doc.Redo();
        doc.Undo();
        AssertKept(doc.Current);

        // A copy of the document (as a client exports it and creates a new one from it).
        var copy = SpecJson.Write(doc.Read().Document with { Id = "0f0e0d0c-0b0a-4908-8706-050403020100" });
        using var duplicate = Open(copy);
        AssertKept(duplicate.Current);
        Assert.Equal(Canonical(doc.Current.Item("held")), Canonical(duplicate.Current.Item("held")));
    }

    [Fact]
    public void RefSource_is_replaced_by_setDesign()
    {
        using var doc = RefDoc();
        doc.Ok(Commit(Design("held", Rect("#0000ff"), baseRev: 1)));
        var item = (SceneItem)doc.Current.Item("held");
        Assert.Null(item.Source);
        Assert.Null(item.Layout);
        Assert.Equal(OriginJson, item.Origin!.Value.GetRawText());

        doc.Ok(Commit(Design("held", Rect("#0000ff"), source: new JsonObject { ["format"] = "example.widget/1", ["data"] = new JsonObject { ["k"] = 1 } })));
        Assert.Equal("example.widget/1", ((SceneItem)doc.Current.Item("held")).Source!.Format);
        doc.Undo();
        doc.Undo();
        AssertKept(doc.Current);
    }
}
