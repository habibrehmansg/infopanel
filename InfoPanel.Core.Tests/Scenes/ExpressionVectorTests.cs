using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Core.Tests.Spec;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The exact results the spec gives: every vector in <c>spec/examples/expressions/vectors.json</c> and every example in
/// <c>spec/docs/expressions.md</c>'s "Exact results" table, evaluated by the runtime and compared exactly (a number bit for
/// bit, so a zero has no sign).
/// </summary>
public sealed class ExpressionVectorTests
{
    private static readonly string VectorsFile = Path.Combine(SpecExampleTests.SpecDir, "examples", "expressions", "vectors.json");
    private static readonly string DocFile = Path.Combine(SpecExampleTests.SpecDir, "docs", "expressions.md");

    public static TheoryData<int, string, string, string> Vectors()
    {
        var data = new TheoryData<int, string, string, string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorsFile), SpecJson.DocumentOptions);
        var i = 0;
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            Assert.Equal(["expr", "result", "type"], v.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            data.Add(i++, v.GetProperty("expr").GetRawText(), v.GetProperty("type").GetString()!, v.GetProperty("result").GetRawText());
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Vector_evaluates_exactly(int index, string expr, string type, string result)
    {
        var (value, ty, _) = SceneExpressionTests.EvalJson(expr);
        var what = $"vector {index}: {expr}";
        var expected = Ty(type);
        Assert.True((ty & expected) != 0, $"{what}: the engine infers {ty}, the vector says {type}");
        AssertResult(value, type, result, what);
    }

    /// <summary>The rows of the table under "## Exact results": an expression and its result, each in backticks.</summary>
    public static TheoryData<string, string> DocExamples()
    {
        var data = new TheoryData<string, string>();
        foreach (var (expr, result) in DocRows()) data.Add(expr, result);
        return data;
    }

    private static List<(string Expr, string Result)> DocRows()
    {
        var text = File.ReadAllText(DocFile);
        var start = text.IndexOf("\n## Exact results", StringComparison.Ordinal);
        if (start < 0) return [];
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = end < 0 ? text[start..] : text[start..end];
        return Regex.Matches(section, @"^\| `(\{.*?\})` \| `(.*?)` \|", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
    }

    [Fact]
    public void The_docs_give_exact_results_with_the_colour_comparison_among_them()
    {
        var rows = DocRows();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Expr.Contains("\"#FF0000FF\"", StringComparison.Ordinal) && r.Expr.StartsWith("{\"op\": \"eq\"", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(DocExamples))]
    public void Doc_example_evaluates_as_written(string expr, string result)
    {
        var (value, ty, _) = SceneExpressionTests.EvalJson(expr);
        // The result's type is the expression's: a colour-like string result is a string unless only a colour fits.
        var type = ty switch
        {
            InfoPanel.Scenes.Ty.Number => "number",
            InfoPanel.Scenes.Ty.Bool => "bool",
            InfoPanel.Scenes.Ty.Color => "color",
            _ => "string",
        };
        AssertResult(value, type, result, $"expressions.md: {expr}");
    }

    private static Ty Ty(string type) => type switch
    {
        "number" => InfoPanel.Scenes.Ty.Number,
        "bool" => InfoPanel.Scenes.Ty.Bool,
        "string" => InfoPanel.Scenes.Ty.String,
        "color" => InfoPanel.Scenes.Ty.Color,
        _ => throw new Xunit.Sdk.XunitException($"Unknown type '{type}'."),
    };

    private static void AssertResult(Val value, string type, string resultJson, string what)
    {
        using var doc = JsonDocument.Parse(resultJson, SpecJson.DocumentOptions);
        var r = doc.RootElement;
        switch (type)
        {
            case "number":
                SceneExpressionTests.AssertExact(r.GetDouble(), value.N, what);
                break;
            case "bool":
                Assert.True(r.GetBoolean() == value.B, $"{what}: expected {r.GetBoolean()}, got {value.B}");
                break;
            case "color":
                Assert.True(r.GetString() == SceneColor.Format(value.C), $"{what}: expected {r.GetString()}, got {SceneColor.Format(value.C)}");
                break;
            default:
                Assert.True(string.Equals(r.GetString(), value.Text, StringComparison.Ordinal), $"{what}: expected \"{r.GetString()}\", got \"{value.Text}\"");
                break;
        }
    }
}
