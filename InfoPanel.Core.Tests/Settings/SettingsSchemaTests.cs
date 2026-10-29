using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Spec;
using InfoPanel.Settings;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Settings;

/// <summary>
/// The settings definitions of spec/schemas/api.schema.json (<c>engineSettings</c>, <c>libreSettings</c>,
/// <c>logLevel</c>, <c>clientName</c>, <c>settingsChange</c>) against the settings store: the ranges, defaults, levels,
/// client names and limits the store keeps, and every <c>settingsChange</c> example answered by the store's own check as
/// spec/examples/expected.json gives it.
/// </summary>
public class SettingsSchemaTests
{
    private static readonly JsonObject Defs = JsonNode.Parse(SpecSchemas.Files["api.schema.json"])!["$defs"]!.AsObject();

    private static readonly string ExamplesDir = Path.Combine(SpecExampleTests.SpecDir, "examples");

    private static readonly JsonObject Expected = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, "expected.json")))!.AsObject();

    private static (double Min, double Max) RangeOf(JsonNode schema) =>
        (schema["minimum"]!.GetValue<double>(), schema["maximum"]!.GetValue<double>());

    private static (double, double) RangeOf(SettingRange range) => (range.Min, range.Max);

    /// <summary>engineSettings' two shapes: <c>ok</c>, and the settings the engine couldn't read, with their problem.</summary>
    private static readonly string[] Shapes = ["engineSettingsOk", "engineSettingsProblem"];

    public static TheoryData<string> SettingsShapes => new(Shapes);

    [Fact]
    public void EngineSettings_IsItsTwoShapes()
    {
        Assert.Equal(Shapes.Select(s => $"#/$defs/{s}"),
            Defs["engineSettings"]!["oneOf"]!.AsArray().Select(b => b!["$ref"]!.GetValue<string>()));
    }

    [Fact]
    public void TheTwoShapes_AreTheSameButForStatusAndProblem()
    {
        // The shapes repeat their shared settings, so this is what keeps them from drifting: every property schema
        // they share, whole (references, ranges, names, limits), their required members and their closedness.
        static JsonObject Without(JsonObject properties) =>
            new(properties.Where(p => p.Key is not ("status" or "problem")).Select(p => KeyValuePair.Create(p.Key, p.Value!.DeepClone())));

        static string[] RequiredWithout(JsonNode shape) =>
            [.. shape["required"]!.AsArray().Select(r => r!.GetValue<string>()).Where(r => r is not ("status" or "problem")).Order(StringComparer.Ordinal)];

        var (ok, problem) = (Defs[Shapes[0]]!, Defs[Shapes[1]]!);

        Assert.Equal(["clients", "frameRate", "graphRate", "logLevel", "sources", "version"], RequiredWithout(ok));
        Assert.Equal(RequiredWithout(ok), RequiredWithout(problem));
        Assert.Equal(Without(ok["properties"]!.AsObject()).Select(p => p.Key), Without(problem["properties"]!.AsObject()).Select(p => p.Key));
        foreach (var (name, schema) in Without(ok["properties"]!.AsObject()))
            Assert.True(JsonNode.DeepEquals(schema, problem["properties"]![name]), $"{name} differs between {Shapes[0]} and {Shapes[1]}");
        Assert.Equal(ok["type"]!.GetValue<string>(), problem["type"]!.GetValue<string>());
        Assert.Equal(ok["additionalProperties"]!.GetValue<bool>(), problem["additionalProperties"]!.GetValue<bool>());
    }

    [Theory]
    [MemberData(nameof(SettingsShapes))]
    public void TheSchemasRanges_AreTheStoresRanges(string shape)
    {
        var settings = Defs[shape]!["properties"]!;
        var change = Defs["settingsChange"]!["properties"]!;
        Assert.Equal(RangeOf(SettingsLimits.FrameRate), RangeOf(settings["frameRate"]!));
        Assert.Equal(RangeOf(SettingsLimits.FrameRate), RangeOf(change["frameRate"]!));
        Assert.Equal(RangeOf(SettingsLimits.GraphRate), RangeOf(settings["graphRate"]!));
        Assert.Equal(RangeOf(SettingsLimits.GraphRate), RangeOf(change["graphRate"]!));
        Assert.Equal(RangeOf(SettingsLimits.StorageInterval), RangeOf(Defs["libreSettings"]!["properties"]!["storageInterval"]!));
        Assert.Equal(RangeOf(SettingsLimits.StorageInterval), RangeOf(change["sources"]!["properties"]!["libre"]!["properties"]!["storageInterval"]!));
    }

    [Fact]
    public void TheSchemasLogLevels_AreTheStoresLevels()
    {
        var levels = Defs["logLevel"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>());

        Assert.Equal(Enum.GetValues<EngineLogLevel>().Select(SettingsLimits.NameOf), levels);
    }

    [Theory]
    [InlineData("web", true)]
    [InlineData("a", true)]
    [InlineData("tray.app-1_B", true)]
    [InlineData("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", true)] // 64
    [InlineData("ccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", false)] // 65
    [InlineData("", false)]
    [InlineData("my client", false)]
    [InlineData("café", false)]
    [InlineData("web\n", false)]
    [InlineData("web/x", false)]
    public void TheSchemasClientNames_AreTheStores(string name, bool valid)
    {
        var json = JsonSerializer.SerializeToElement(name);

        Assert.Equal(valid, SpecSchemas.Strict.Validate(json, $"{SpecSchemas.Api}#/$defs/clientName").IsValid);
        Assert.Equal(valid, SettingsLimits.IsClientName(name));
    }

    [Fact]
    public void TheStoresLimits_AreTheFormatsSettingsLimits()
    {
        var limits = SpecLimits.Default;
        Assert.Equal((16, 64 * 1024, 1024 * 1024, 64, 512 * 1024),
            (limits.SettingsClients, limits.SettingsClientBytes, limits.SettingsBytes, limits.SettingsDepth, limits.SettingsRequestBytes));
        Assert.Equal((limits.SettingsClients, limits.SettingsClientBytes, limits.SettingsBytes),
            (SettingsLimits.Clients, SettingsLimits.ClientBytes, SettingsLimits.MaxFileBytes));
        foreach (var shape in Shapes)
            Assert.Equal(limits.SettingsClients, Defs[shape]!["properties"]!["clients"]!["maxProperties"]!.GetValue<int>());
    }

    [Fact]
    public void TheDefaultsExample_IsTheStoresDefaults()
    {
        var example = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, "api", "engineSettings", "defaults.json")))!;
        var defaults = EngineSettings.Default;

        Assert.Equal(defaults.FrameRate, example["frameRate"]!.GetValue<int>());
        Assert.Equal(defaults.GraphRate, example["graphRate"]!.GetValue<int>());
        Assert.Equal(SettingsLimits.NameOf(defaults.LogLevel), example["logLevel"]!.GetValue<string>());
        var libre = example["sources"]!["libre"]!;
        Assert.Equal(defaults.Libre, new LibreSettings(libre["enabled"]!.GetValue<bool>(), libre["storage"]!.GetValue<bool>(), libre["storageInterval"]!.GetValue<int>()));
        Assert.Empty(example["clients"]!.AsObject());
    }

    public static TheoryData<string> ChangeExamples(bool invalid)
    {
        var data = new TheoryData<string>();
        var folder = Path.Combine(ExamplesDir, "api", "settingsChange", invalid ? "invalid" : "");
        foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            data.Add(Path.GetRelativePath(ExamplesDir, file).Replace('\\', '/'));
        return data;
    }

    private static JsonElement Load(string rel)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExamplesDir, rel)), SpecJson.DocumentOptions);
        return document.RootElement.Clone();
    }

    [Theory]
    [MemberData(nameof(ChangeExamples), false)]
    public void AValidChangeExample_IsAppliedByTheStore(string rel)
    {
        using var temp = new Persistence.TempDirectory();
        var store = SettingsStore.Open(new InfoPanel.Persistence.DataPaths(temp.Path));
        var change = Load(rel);

        Assert.Empty(SettingsStore.CheckChange(change, SpecLimits.Default));
        store.Apply(change);
    }

    [Theory]
    [MemberData(nameof(ChangeExamples), true)]
    public void AnInvalidChangeExample_IsRefusedByTheStoresCheck_WithItsCodeAtItsPath(string rel)
    {
        // Schema examples too: the store checks every change itself, so it refuses each as the schema does, at the
        // same path, whoever calls it.
        var expected = Expected[rel]!.AsObject();
        var limits = expected["limits"] is JsonObject tightened
            ? SpecLimits.Default.With(tightened.ToDictionary(p => p.Key, p => p.Value!.GetValue<double>()))
            : SpecLimits.Default;

        var errors = SettingsStore.CheckChange(Load(rel), limits).Select(e => (e.Path, e.Code)).ToList();

        var want = ((string)expected["path"]!, (string)expected["code"]!);
        Assert.True(errors.Contains(want), $"{rel}: expected {want.Item2} at '{want.Item1}', got: {string.Join("; ", errors)}");
    }
}
