using System.Text.Json;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>Rules of the formats beyond what one example each shows (the examples are in spec/examples).</summary>
public class SceneRuleTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json, SpecJson.DocumentOptions).RootElement;

    private static SceneCheck Scene(string body) => SceneValidator.Check(Parse("""{"format": "infopanel.scene/1", """ + body + "}"));

    private static string Show(SceneCheck check) => string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}"));

    // ---- integers ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("0.1e1")]
    [InlineData("1")]
    public void HistoryCount_AnyIntegralSpelling_ReadsAndRoundTrips(string count)
    {
        var check = Scene($$$"""
            "inputs": {"v": {"type": "number"}},
            "nodes": [{"kind": "rect", "w": {"op": "history", "input": "v", "fn": "max", "count": {{{count}}}}, "h": 1, "fill": "#ffffff"}]
            """);
        Assert.True(check.Status == CheckStatus.Valid, Show(check));
        var rect = (RectNode)check.Scene!.Nodes[0];
        Assert.Equal(1, ((HistoryExpr)rect.W!).Count);
        var written = SpecJson.Write(check.Scene);
        Assert.Equal(1, written.GetProperty("nodes")[0].GetProperty("w").GetProperty("count").GetInt32());
        Assert.Equal(CheckStatus.Valid, SceneValidator.Check(written).Status);
    }

    // ---- state cells: one type from init and next ---------------------------------------------------------------

    [Fact]
    public void StateCell_TypeComesFromInitAndNextTogether()
    {
        // Both look like colours: the cell is a colour and paints with it.
        var colour = Scene("""
            "state": {"c": {"init": "#000000", "next": "#ffffff"}},
            "nodes": [{"kind": "rect", "w": 1, "h": 1, "fill": {"op": "state", "name": "c"}}]
            """);
        Assert.True(colour.Status == CheckStatus.Valid, Show(colour));

        // next is plain text: the cell is a string, so painting with it is a type error at the paint.
        var text = Scene("""
            "state": {"c": {"init": "#000000", "next": "not-a-color"}},
            "nodes": [{"kind": "rect", "w": 1, "h": 1, "fill": {"op": "state", "name": "c"}}]
            """);
        Assert.Equal(("type.mismatch", "/nodes/0/fill"), (Assert.Single(text.Errors).Code, text.Errors[0].Path));
    }

    [Fact]
    public void StateCell_TypesNarrowThroughOtherCells()
    {
        // b is a string (its next is plain text); a's next is b, so a is a string too, and can't paint.
        var check = Scene("""
            "state": {
              "a": {"init": "#000000", "next": {"op": "state", "name": "b"}},
              "b": {"init": "#111111", "next": "plain"}
            },
            "nodes": [
              {"kind": "rect", "w": 1, "h": 1, "fill": {"op": "state", "name": "a"}},
              {"kind": "text", "font": {"family": "Inter", "size": 12}, "text": {"op": "state", "name": "a"}}
            ]
            """);
        Assert.Equal(("type.mismatch", "/nodes/0/fill"), (Assert.Single(check.Errors).Code, check.Errors[0].Path));
    }

    [Fact]
    public void StateCell_WithoutACommonType_IsAnErrorAtNext()
    {
        var check = Scene("""
            "state": {"c": {"init": 0, "next": true}},
            "nodes": [{"kind": "rect", "w": {"op": "state", "name": "c"}, "h": 1, "fill": "#ffffff"}]
            """);
        Assert.Equal(("type.mismatch", "/state/c/next"), (Assert.Single(check.Errors).Code, check.Errors[0].Path));
    }

    // ---- measure: two phases --------------------------------------------------------------------------------------

    [Fact]
    public void TextLaidOutFromAMeasurement_IsAllowedWhenNotMeasuredItself()
    {
        var check = Scene("""
            "values": {"bw": {"op": "measure", "node": "badge", "field": "w"}},
            "nodes": [
              {"kind": "text", "id": "badge", "font": {"family": "Inter", "size": 12}, "text": "LIVE"},
              {"kind": "text", "id": "after", "w": {"op": "sub", "args": [200, {"op": "value", "name": "bw"}]},
               "font": {"family": "Inter", "size": 12}, "text": "follows"},
              {"kind": "rect", "w": {"op": "value", "name": "bw"}, "h": 1, "fill": "#ffffff"}
            ]
            """);
        Assert.True(check.Status == CheckStatus.Valid, Show(check));
    }

    [Theory]
    [InlineData("""{"kind": "text", "id": "a", "w": {"op": "measure", "node": "a", "field": "h"}, "font": {"family": "Inter", "size": 12}, "text": "x"}""", "/nodes/0/w")]
    [InlineData("""{"kind": "text", "id": "a", "font": {"family": "Inter", "size": {"op": "value", "name": "m"}}, "text": "x"}""", "/nodes/0/font/size")]
    public void MeasuredTextWhoseLayoutReadsAMeasurement_IsACycle(string node, string path)
    {
        // The second form goes through a value, and the value measures another node: still a measure-dependent layout.
        var check = Scene($$$"""
            "values": {"m": {"op": "measure", "node": "b", "field": "h"}},
            "nodes": [
              {{{node}}},
              {"kind": "text", "id": "b", "font": {"family": "Inter", "size": 12}, "text": "y"},
              {"kind": "rect", "w": {"op": "measure", "node": "a", "field": "w"}, "h": 1, "fill": "#ffffff"}
            ]
            """);
        Assert.Equal(("cycle.measure", path), (Assert.Single(check.Errors).Code, check.Errors[0].Path));
    }

    [Fact]
    public void StateReadingAMeasurement_IsStillAContextError()
    {
        var check = Scene("""
            "values": {"m": {"op": "measure", "node": "a", "field": "w"}},
            "state": {"s": {"init": 0, "next": {"op": "value", "name": "m"}}},
            "nodes": [{"kind": "text", "id": "a", "font": {"family": "Inter", "size": 12}, "text": "x"}]
            """);
        Assert.Equal("context.measure", Assert.Single(check.Errors).Code);
    }

    // ---- counts before the schema --------------------------------------------------------------------------------

    [Fact]
    public void NodeCount_IsCheckedBeforeTheSchema()
    {
        // Every node is broken, but there are too many: only the count is reported.
        var nodes = string.Join(",", Enumerable.Repeat("{\"kind\": \"nope\"}", 2_001));
        var check = Scene($"\"nodes\": [{{\"kind\": \"group\", \"children\": [{nodes}]}}]");
        Assert.Equal(("limit.nodes", "/nodes"), (Assert.Single(check.Errors).Code, check.Errors[0].Path));
    }

    // ---- commit indexes --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, false)]
    [InlineData(2147483648L, false)]
    public void ItemIndexes_AreBoundedByTheSchemaNotClamped(long index, bool valid)
    {
        var commit = Parse($$$"""{"opId": "a", "ops": [{"op": "move", "id": "x", "index": {{{index}}}}, {"op": "insert", "index": {{{index}}}, "item": {"kind": "group", "id": "g", "placement": {"x": 0, "y": 0}, "children": []}}]}""");
        var check = CommitValidator.Check(commit);
        if (valid) Assert.Empty(check.Errors);
        else Assert.Equal(["/ops/0/index", "/ops/1/index"], check.Errors.Where(e => e.Code == "schema").Select(e => e.Path).Order());
    }

    // ---- the schema validator's budget ----------------------------------------------------------------------------

    [Fact]
    public void ValidExamples_UseLittleOfTheSchemaWorkBudget()
    {
        // The budget (per value of the instance) must leave real content far from it.
        var examples = Path.Combine(SpecExampleTests.SpecDir, "examples");
        foreach (var (folder, schema) in new[] { ("scene/valid", SpecSchemas.Scene), ("document/valid", SpecSchemas.Document), ("commit/valid", SpecSchemas.CommitRequest) })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(examples, folder)))
            {
                var result = SpecSchemas.Strict.Validate(Parse(File.ReadAllText(file)), schema);
                Assert.True(result.IsValid, file);
                Assert.True(result.Steps * 4 < result.Budget - InfoPanel.Spec.Schema.SchemaSet.BaseWork, $"{file}: {result.Steps} of {result.Budget} steps.");
            }
    }
}
