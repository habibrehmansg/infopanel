using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Import;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>
/// spec/docs/limits.md: every acceptance limit is declared once, with its value, in the docs and in
/// <see cref="SpecLimits"/>; the import's limits take their values from there; and no cap is hidden in the runtime.
/// </summary>
public class LimitsTests
{
    private static readonly string LimitsDoc = File.ReadAllText(Path.Combine(SpecExampleTests.SpecDir, "docs", "limits.md"));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json, SpecJson.DocumentOptions).RootElement;

    private static string Show(IEnumerable<SpecError> errors) => string.Join("; ", errors.Select(e => $"{e.Code} {e.Path} {e.Message}"));

    /// <summary>Each row of a limits.md table headed <c>| Limit |</c>: the limit's name and its value cell.</summary>
    private static List<(string Name, string Value)> DocumentedLimits()
    {
        var rows = new List<(string, string)>();
        var inTable = false;
        foreach (var line in LimitsDoc.Split('\n'))
        {
            if (!line.StartsWith('|')) { inTable = false; continue; }
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells[0] == "Limit") { inTable = true; continue; }
            if (!inTable || cells[0].StartsWith("---", StringComparison.Ordinal)) continue;
            var name = Regex.Match(cells[0], "^`([A-Za-z0-9]+)`$");
            Assert.True(name.Success, $"limits.md: a limit row names one limit: {line}");
            rows.Add((name.Groups[1].Value, cells[1]));
        }
        return rows;
    }

    /// <summary>The number a value cell starts with, in its unit: "16 MiB" is 16,777,216; "2,000" is 2,000.</summary>
    private static double ValueOf(string cell)
    {
        var m = Regex.Match(cell, @"^([0-9][0-9,]*(?:\.[0-9]+)?)\s*(KiB|MiB)?");
        Assert.True(m.Success, $"limits.md: a value starts with its number: {cell}");
        var n = double.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "KiB" => n * 1024, "MiB" => n * 1024 * 1024, _ => n };
    }

    // ---- one home ----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_limit_is_named_once_in_limits_md_with_the_value_SpecLimits_has()
    {
        var documented = DocumentedLimits();
        var code = SpecLimits.Default.ToDictionary();

        Assert.Empty(documented.GroupBy(r => r.Name).Where(g => g.Count() > 1).Select(g => g.Key));
        Assert.Equal(code.Keys.Order(StringComparer.Ordinal), documented.Select(r => r.Name).Order(StringComparer.Ordinal));
        foreach (var (name, value) in documented)
            Assert.True(code[name] == ValueOf(value), $"{name}: limits.md says {value}, SpecLimits {code[name]}");
    }

    [Fact]
    public void The_request_archive_import_and_record_limits_are_named()
    {
        // Each was a number in the engine's code alone, or under another name; each is now a limit of its own.
        var code = SpecLimits.Default.ToDictionary();
        var expected = new Dictionary<string, double>
        {
            ["composeDepth"] = 16,
            ["itemDepth"] = 8,
            ["requestBytes"] = 65_536,
            ["tokenRequestBytes"] = 4_096,
            ["archiveBytes"] = 536_870_912,
            ["archiveEntries"] = 10_000,
            ["expandedBytes"] = 536_870_912,
            ["entryNameBytes"] = 1_024,
            ["diagnostics"] = 1_000,
            ["recordOutcomes"] = 1_000,
            ["recordBytes"] = 1_048_576,
            ["recordReasonLength"] = 256,
            ["v1ProfileXmlBytes"] = 4_194_304,
            ["v1ItemsXmlBytes"] = 16_777_216,
            ["v1XmlDepth"] = 64,
            ["v1XmlElements"] = 500_000,
            ["v1Profiles"] = 1_000,
            ["v1Devices"] = 64,
            ["v1DeviceFields"] = 64,
            ["v1SettingLength"] = 1_024,
            ["v1Hotkeys"] = 256,
        };
        foreach (var (name, value) in expected)
            Assert.True(code.TryGetValue(name, out var actual) && actual == value, $"{name}: expected {value}, SpecLimits has {(code.TryGetValue(name, out var a) ? a : "none")}");
        Assert.DoesNotContain("groupDepth", code.Keys);
    }

    [Fact]
    public void The_svg_directory_record_and_socket_message_caps_are_named()
    {
        // Each was a constant in the engine's code with its number in prose only; each is now a limit of its own.
        var code = SpecLimits.Default.ToDictionary();
        var expected = new Dictionary<string, double>
        {
            ["svgNumber"] = 1_000_000_000,
            ["svgDepth"] = 32,
            ["svgDrawnDepth"] = 64,
            ["svgGradientChain"] = 4,
            ["entryRecordBytes"] = 4_096,
            ["socketMessageBytes"] = 4_096,
        };
        foreach (var (name, value) in expected)
            Assert.True(code.TryGetValue(name, out var actual) && actual == value, $"{name}: expected {value}, SpecLimits has {(code.TryGetValue(name, out var a) ? a : "none")}");
    }

    // ---- the SVG caps: enforced through their names ---------------------------------------------------------------

    private static byte[] Svg(string body) =>
        System.Text.Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'>{body}</svg>");

    /// <summary>The SVG's one error (code and message), or null when it is accepted.</summary>
    private static (string Code, string Message)? SvgError(byte[] svg, SpecLimits? limits = null)
    {
        var (info, errors) = MediaValidator.Check(svg, limits);
        if (errors.Count == 0) { Assert.NotNull(info); return null; }
        var error = Assert.Single(errors);
        return (error.Code, error.Message);
    }

    private static string Nested(int groups) => string.Concat(Enumerable.Repeat("<g>", groups)) + "<rect width='1' height='1'/>" + string.Concat(Enumerable.Repeat("</g>", groups));

    [Fact]
    public void Svg_elements_nest_at_most_svgDepth_deep_the_root_counting_1()
    {
        // The root, 30 groups and the rect: 32 levels; one group more is 33.
        Assert.Null(SvgError(Svg(Nested(30))));
        var over = SvgError(Svg(Nested(31)));
        Assert.Equal("media.svg-external", over?.Code);
        Assert.Contains("more than 32 deep", over?.Message);

        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["svgDepth"] = 4 });
        Assert.Null(SvgError(Svg(Nested(2)), limits));
        Assert.Contains("more than 4 deep", SvgError(Svg(Nested(3)), limits)?.Message);
    }

    [Fact]
    public void Svg_drawing_nests_at_most_svgDrawnDepth_levels()
    {
        // svg > g > use draws the rect inside a definition: the root 1, the group 2, the use 3, the rect 4.
        var drawn = Svg("<defs><rect id='r' width='1' height='1'/></defs><g><use href='#r'/></g>");
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["svgDrawnDepth"] = 4 });
        Assert.Null(SvgError(drawn, limits));
        var over = SvgError(drawn, SpecLimits.Default.With(new Dictionary<string, double> { ["svgDrawnDepth"] = 3 }));
        Assert.Equal("media.svg-external", over?.Code);
        Assert.Contains("draws more than 3 levels deep", over?.Message);
    }

    [Fact]
    public void A_gradient_inherits_through_at_most_svgGradientChain_hrefs()
    {
        // g0 inherits from nothing; gN from g(N-1): the last of n + 1 gradients inherits through n references.
        static byte[] Chain(int n)
        {
            var defs = "<linearGradient id='g0'/>" + string.Concat(Enumerable.Range(1, n).Select(i => $"<linearGradient id='g{i}' href='#g{i - 1}'/>"));
            return Svg($"<defs>{defs}</defs><rect width='1' height='1' fill='url(#g{n})'/>");
        }

        Assert.Null(SvgError(Chain(4)));
        Assert.Contains("more than 4 href references", SvgError(Chain(5))?.Message);

        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["svgGradientChain"] = 1 });
        Assert.Null(SvgError(Chain(1), limits));
        var over = SvgError(Chain(2), limits);
        Assert.Equal("media.svg-external", over?.Code);
        Assert.Contains("more than 1 href references", over?.Message);
    }

    [Fact]
    public void An_svg_number_beyond_svgNumber_refuses_the_file()
    {
        // Refused, not clamped: the drawing clamp of scenes (docs/geometry.md) doesn't apply to an SVG's values.
        Assert.Null(SvgError(Svg("<rect x='1000000000' y='-1000000000' width='1' height='1'/>")));
        Assert.NotNull(SvgError(Svg("<rect x='1000000001' width='1' height='1'/>")));
        Assert.NotNull(SvgError(Svg("<rect y='-1000000001' width='1' height='1'/>")));
        Assert.NotNull(SvgError(Svg("<path d='M 0 0 L 1000000001 0'/>")));
    }

    /// <summary>Each <see cref="ImportLimits"/> member and the limit it is (by name in <see cref="SpecLimits"/>).</summary>
    private static readonly Dictionary<string, string> ImportLimitNames = new()
    {
        [nameof(ImportLimits.ArchiveBytes)] = "archiveBytes",
        [nameof(ImportLimits.Entries)] = "archiveEntries",
        [nameof(ImportLimits.ExpandedBytes)] = "expandedBytes",
        [nameof(ImportLimits.EntryNameBytes)] = "entryNameBytes",
        [nameof(ImportLimits.EntryRecordBytes)] = "entryRecordBytes",
        [nameof(ImportLimits.ProfileXmlBytes)] = "v1ProfileXmlBytes",
        [nameof(ImportLimits.ItemsXmlBytes)] = "v1ItemsXmlBytes",
        [nameof(ImportLimits.XmlDepth)] = "v1XmlDepth",
        [nameof(ImportLimits.XmlElements)] = "v1XmlElements",
        [nameof(ImportLimits.Profiles)] = "v1Profiles",
        [nameof(ImportLimits.DevicesPerFamily)] = "v1Devices",
        [nameof(ImportLimits.FieldsPerDevice)] = "v1DeviceFields",
        [nameof(ImportLimits.SettingValueLength)] = "v1SettingLength",
        [nameof(ImportLimits.Hotkeys)] = "v1Hotkeys",
        [nameof(ImportLimits.RecordOutcomes)] = "recordOutcomes",
        [nameof(ImportLimits.RecordBytes)] = "recordBytes",
        [nameof(ImportLimits.RecordReasonLength)] = "recordReasonLength",
        [nameof(ImportLimits.Diagnostics)] = "diagnostics",
    };

    [Fact]
    public void Every_import_limit_takes_its_value_from_SpecLimits()
    {
        var code = SpecLimits.Default.ToDictionary();
        var members = typeof(ImportLimits).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(ImportLimitNames.Keys.Order(StringComparer.Ordinal), members.Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var member in members)
        {
            var name = ImportLimitNames[member.Name];
            Assert.True(code.ContainsKey(name), $"ImportLimits.{member.Name}: no limit named {name}");
            Assert.Equal(code[name], Convert.ToDouble(member.GetValue(ImportLimits.Default), CultureInfo.InvariantCulture));
        }
    }

    // ---- composeDepth: no paint is dropped silently -------------------------------------------------------------

    /// <summary>A rect painted by <paramref name="depth"/> compose paints nested in one another, through <c>dst</c>.</summary>
    internal static string ComposeScene(int depth, string inner = "#ff0000ff")
    {
        var paint = $"\"{inner}\"";
        for (var i = 0; i < depth; i++)
            paint = $$$"""{"type": "compose", "blend": "normal", "dst": {{{paint}}}, "src": "#00000000"}""";
        return $$$"""{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": 10, "h": 10, "fill": {{{paint}}}}]}""";
    }

    [Fact]
    public void A_compose_paint_nested_17_deep_is_over_composeDepth()
    {
        var at = SceneValidator.Check(Parse(ComposeScene(16)));
        Assert.True(at.Status == CheckStatus.Valid, Show(at.Errors));

        var over = SceneValidator.Check(Parse(ComposeScene(17)));
        Assert.Equal(CheckStatus.Invalid, over.Status);
        var error = Assert.Single(over.Errors);
        // At the 17th compose: the outermost is the fill itself, each one deeper is its dst.
        Assert.Equal(("limit.composeDepth", "/nodes/0/fill" + string.Concat(Enumerable.Repeat("/dst", 16))), (error.Code, error.Path));
    }

    [Fact]
    public void ComposeDepth_counts_composes_in_src_strokes_and_spans_too()
    {
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["composeDepth"] = 1 });
        const string Twice = """{"type": "compose", "blend": "normal", "dst": "#000000", "src": {"type": "compose", "blend": "normal", "dst": "#000000", "src": "#ffffff"}}""";
        const string Once = """{"type": "compose", "blend": "normal", "dst": "#000000", "src": "#ffffff"}""";

        var stroke = SceneValidator.Check(Parse($$$"""{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": 1, "h": 1, "stroke": {"paint": {{{Twice}}}, "width": 1}}]}"""), limits);
        Assert.Equal(("limit.composeDepth", "/nodes/0/stroke/paint/src"), (Assert.Single(stroke.Errors).Code, stroke.Errors[0].Path));

        var span = SceneValidator.Check(Parse($$$"""{"format": "infopanel.scene/1", "nodes": [{"kind": "text", "font": {"family": "Inter", "size": 12}, "fill": {{{Once}}}, "text": [{"text": "a", "fill": {{{Twice}}}}]}]}"""), limits);
        Assert.Equal(("limit.composeDepth", "/nodes/0/text/0/fill/src"), (Assert.Single(span.Errors).Code, span.Errors[0].Path));

        // Two composes side by side, each one deep, are within it.
        var side = SceneValidator.Check(Parse($$$"""{"format": "infopanel.scene/1", "nodes": [{"kind": "rect", "w": 1, "h": 1, "fill": {{{Once}}}, "stroke": {"paint": {{{Once}}}, "width": 1}}]}"""), limits);
        Assert.True(side.Status == CheckStatus.Valid, Show(side.Errors));
    }

    // ---- deps: no count of their own ------------------------------------------------------------------------------

    [Fact]
    public void A_source_with_257_deps_is_accepted()
    {
        var deps = string.Join(", ", Enumerable.Range(0, 257).Select(i => $"\"sha256:{i:x64}\""));
        var document = Parse($$$"""
            {"format": "infopanel.document/1", "id": "3dd19c0e-675a-4591-b096-83417853861f",
             "profile": {"name": "Deps", "width": 100, "height": 100},
             "items": [{"kind": "scene", "id": "a", "placement": {"x": 0, "y": 0},
                        "source": {"ref": "made-elsewhere", "deps": [{{{deps}}}]},
                        "scene": {"format": "infopanel.scene/1", "nodes": []}}]}
            """);
        var check = DocumentValidator.Check(document);
        Assert.True(check.Status == CheckStatus.Valid, Show(check.Errors));
    }
}
