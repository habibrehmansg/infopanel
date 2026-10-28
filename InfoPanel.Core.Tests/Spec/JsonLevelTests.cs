using System.Text;
using System.Text.Json;
using InfoPanel.Core.Tests.Scenes;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>
/// The JSON-level pass every format runs first (spec/docs/validation.md#json): nesting, numbers, unique member names and
/// Unicode strings, all reported before any format, schema or rule is applied; and patterns anchored at the true end.
/// </summary>
public class JsonLevelTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json, SpecJson.DocumentOptions).RootElement.Clone();

    private static JsonElement Parse(byte[] utf8) => JsonDocument.Parse(utf8, SpecJson.DocumentOptions).RootElement.Clone();

    private static string Show(IEnumerable<SpecError> errors) => string.Join("; ", errors.Select(e => $"{e.Code} {e.Path}"));

    private const string Document = """{"format": "infopanel.document/1", "id": "3dd19c0e-675a-4591-b096-83417853861f", "profile": {"name": "P", "width": 1, "height": 1}, "items": []}""";

    private static string Rect(string fill) =>
        $$"""{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": 10, "h": 10, "fill": {{fill}}}]}""";

    // ---- patterns ----------------------------------------------------------------------------------------------

    [Fact]
    public void AColourWithATrailingNewline_IsASchemaError()
    {
        // .NET's $ matches before a final newline; JSON Schema's (ECMA-262) only at the end.
        var check = SceneValidator.Check(Parse(Rect("\"#ff0000\\n\"")));
        Assert.Equal("schema /nodes/0/fill", Show(check.Errors));

        // As a literal in a colour expression, the schema's pattern refuses it too.
        Assert.Equal("schema /nodes/0/fill/args/1", Show(SceneValidator.Check(Parse(Rect("""{"op": "if", "args": [true, "#ff0000\n", "#00ff00"]}"""))).Errors));

        // As a named value, any string passes the schema: the rules type it as a string, not a colour.
        var value = SceneValidator.Check(Parse("""{"format": "infopanel.scene/1", "values": {"c": "#ff0000\n"}, "nodes": [{"kind": "rect", "w": 10, "h": 10, "fill": {"op": "value", "name": "c"}}]}"""));
        Assert.Equal("type.mismatch /nodes/0/fill", Show(value.Errors));
    }

    [Fact]
    public void SchemaPatterns_AnchorAtTheEnd_AndKeepALiteralDollar()
    {
        var set = SchemaSet.Load([
            """{"$id": "urn:t:end", "type": "string", "pattern": "^a$"}""",
            """{"$id": "urn:t:dollar", "type": "string", "pattern": "^a\\$$"}""",
            """{"$id": "urn:t:class", "type": "string", "pattern": "^[$]$"}""",
        ]);
        Assert.True(set.Validate(Parse("\"a\""), "urn:t:end").IsValid);
        Assert.Equal("pattern", Assert.Single(set.Validate(Parse("\"a\\n\""), "urn:t:end").Errors).Keyword);
        Assert.True(set.Validate(Parse("\"a$\""), "urn:t:dollar").IsValid);
        Assert.False(set.Validate(Parse("\"a$\\n\""), "urn:t:dollar").IsValid);
        Assert.True(set.Validate(Parse("\"$\""), "urn:t:class").IsValid);
        Assert.False(set.Validate(Parse("\"$\\n\""), "urn:t:class").IsValid);
    }

    [Fact]
    public void FormatAndCapabilityStrings_WithATrailingNewline_AreSchemaErrors()
    {
        // Not a format of another major version, nor a capability the engine lacks: not a format or capability at all.
        Assert.Equal("schema /format", Show(SceneValidator.Check(Parse("""{"format": "infopanel.scene/2\n", "nodes": []}""")).Errors));
        var requires = SceneValidator.Check(Parse("""{"format": "infopanel.scene/1", "requires": ["media.provider\n"], "nodes": []}"""), support: EngineSupport.Base);
        Assert.Equal("schema /requires/0", Show(requires.Errors));
    }

    // ---- Unicode -----------------------------------------------------------------------------------------------

    [Fact]
    public void ALoneSurrogateMemberName_IsJson()
    {
        var document = Parse(Document.Replace("\"items\"", "\"\\ud800\": 1, \"items\""));
        Assert.Equal("json ", Show(DocumentValidator.Check(document).Errors));
        var scene = Parse("""{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": 1, "h": 1, "\udc00": 1}]}""");
        Assert.Equal("json /nodes/0", Show(SceneValidator.Check(scene).Errors));
    }

    [Fact]
    public void ALoneSurrogateMemberName_DoesntHideNestingTooDeep()
    {
        // Nesting comes first: the value of a member whose name isn't Unicode text still counts for depth, though nothing
        // else in it is read. {"\ud800": V} with V 256 arrays deep is 257 levels: the one error limit.jsonDepth.
        var deep = new string('[', 256) + new string(']', 256);
        Assert.Equal("limit.jsonDepth ", Show(DocumentValidator.Check(Parse(Document.Replace("\"items\"", "\"\\ud800\": " + deep + ", \"items\""))).Errors));
        Assert.Equal(["depth"], SchemaSet.CheckJson(Parse("{\"\\ud800\": " + deep + "}")).Errors.Select(e => e.Keyword));
        // At the limit, only the name.
        var within = new string('[', 255) + new string(']', 255);
        Assert.Equal(["json"], SchemaSet.CheckJson(Parse("{\"\\ud800\": " + within + "}")).Errors.Select(e => e.Keyword));
        // A create's members are roots of their own, the root object not counted.
        Assert.Equal(["depth"], SchemaSet.CheckJsonMembers(Parse("{\"\\ud800\": [" + deep + "]}")).Errors.Select(e => e.Keyword));
        Assert.Equal(["json"], SchemaSet.CheckJsonMembers(Parse("{\"\\ud800\": " + deep + "}")).Errors.Select(e => e.Keyword));
    }

    [Fact]
    public void ALoneSurrogateOrBrokenUtf8InAString_IsJson()
    {
        Assert.Equal("json /profile/name", Show(DocumentValidator.Check(Parse(Document.Replace("\"P\"", "\"P\\ud800\""))).Errors));
        Assert.Equal("json /profile/name", Show(DocumentValidator.Check(Parse(Document.Replace("\"P\"", "\"\\udc00\\ud800\""))).Errors));
        // A pair is one scalar value, and fine.
        Assert.Empty(DocumentValidator.Check(Parse(Document.Replace("\"P\"", "\"\\ud83d\\ude00\""))).Errors);

        var bytes = Encoding.UTF8.GetBytes(Document.Replace("\"P\"", "\"P#\""));
        bytes[Array.IndexOf(bytes, (byte)'#')] = 0xFF;
        Assert.Equal("json /profile/name", Show(DocumentValidator.Check(Parse(bytes)).Errors));
    }

    // ---- one pass, first ---------------------------------------------------------------------------------------

    [Fact]
    public void ANumberThatRoundsToInfinity_AndARepeatedMember_AreBothReported_BeforeAnySchemaError()
    {
        // The document also lacks "profile" (a schema error): only the two JSON-level errors are reported.
        var check = DocumentValidator.Check(Parse("""{"format": "infopanel.document/1", "id": "3dd19c0e-675a-4591-b096-83417853861f", "items": [], "items": [], "x": 1e400}"""));
        Assert.Equal("schema ; json /x", Show(check.Errors));
        Assert.Contains("Duplicate", check.Errors[0].Message);
    }

    [Fact]
    public void TheJsonLevelPass_ComesBeforeTheFormat()
    {
        // The order of validation.md: the JSON-level pass, then the format and its major version.
        Assert.Equal("json /x", Show(DocumentValidator.Check(Parse("""{"format": "infopanel.document/2", "x": 1e400}""")).Errors));
        Assert.Equal("schema ", Show(DocumentValidator.Check(Parse("""{"format": "infopanel.document/2", "format": "infopanel.document/2"}""")).Errors));
        Assert.Equal("json /x", Show(SceneValidator.Check(Parse("""{"format": "infopanel.scene/2", "x": -1e400}""")).Errors));
    }

    [Fact]
    public void ACommitOverItsByteCap_IsRefusedForItsSize_BeforeTheJsonLevelPass()
    {
        // The request's size cap comes first: what was received is measured before it is read.
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["commitBytes"] = 64 });
        var check = CommitValidator.Check(Parse("""{"opId": "a", "instance": "i", "baseRev": 0, "x": 1e400, "x": 1, "ops": [{"op": "remove", "id": "x"}]}"""), limits);
        Assert.Equal("limit.commitBytes ", Show(check.Errors));
        var within = CommitValidator.Check(Parse("""{"opId": "a", "instance": "i", "baseRev": 0, "x": 1e400, "x": 1, "ops": [{"op": "remove", "id": "x"}]}"""));
        Assert.Equal("schema ; json /x", Show(within.Errors));
    }
}
