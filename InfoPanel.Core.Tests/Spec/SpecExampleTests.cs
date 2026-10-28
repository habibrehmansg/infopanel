using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Spec;

/// <summary>
/// Every file under spec/examples: valid ones pass the schemas and the engine's rules and read back unchanged; the others
/// fail (or come out unsupported, as placeholders, or with warnings) with the reason spec/examples/expected.json gives.
/// Examples that test a limit may tighten it there to stay small.
/// </summary>
public class SpecExampleTests
{
    internal static readonly string SpecDir = FindSpecDir();
    private static readonly string ExamplesDir = Path.Combine(SpecDir, "examples");
    private static readonly JsonObject Expected = JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, "expected.json")))!.AsObject();

    private static string FindSpecDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var spec = Path.Combine(dir.FullName, "spec");
            if (File.Exists(Path.Combine(spec, "examples", "expected.json"))) return spec;
        }
        throw new DirectoryNotFoundException("spec/ not found above " + AppContext.BaseDirectory);
    }

    public static TheoryData<string> Files(string folder)
    {
        var data = new TheoryData<string>();
        foreach (var rel in FileList(folder)) data.Add(rel);
        return data;
    }

    private static IEnumerable<string> FileList(string folder) =>
        Directory.EnumerateFiles(Path.Combine(ExamplesDir, folder), "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(ExamplesDir, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    /// <summary>API bodies (<c>api/{definition}/*.json</c>); with <paramref name="invalid"/>, the ones a definition refuses (<c>api/{definition}/invalid/</c>).</summary>
    public static TheoryData<string> ApiFiles(bool invalid)
    {
        var data = new TheoryData<string>();
        foreach (var rel in FileList("api").Where(f => IsInvalidApiBody(f) == invalid)) data.Add(rel);
        return data;
    }

    private static bool IsInvalidApiBody(string rel) => rel.StartsWith("api/", StringComparison.Ordinal) && rel.Split('/') is [_, _, "invalid", _];

    private static JsonElement Load(string rel)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExamplesDir, rel)), SpecJson.DocumentOptions);
        return doc.RootElement.Clone();
    }

    private static JsonObject Expect(string rel) =>
        Expected[rel]?.AsObject() ?? throw new Xunit.Sdk.XunitException($"expected.json has no entry for {rel}.");

    private static SpecLimits Limits(JsonObject expected) =>
        expected["limits"] is JsonObject limits
            ? SpecLimits.Default.With(limits.ToDictionary(p => p.Key, p => p.Value!.GetValue<double>()))
            : SpecLimits.Default;

    /// <summary>
    /// <paramref name="errors"/> holds the expected code at exactly the expected path: codes and paths are part of the
    /// contract (spec/docs/versioning.md#error-codes-and-paths), so a path is never matched by prefix.
    /// </summary>
    private static void AssertHas(IReadOnlyList<(string Path, string Code)> errors, JsonObject expected, string rel)
    {
        var code = (string)expected["code"]!;
        var path = (string?)expected["path"] ?? throw new Xunit.Sdk.XunitException($"expected.json gives {rel} no path.");
        Assert.True(errors.Contains((path, code)),
            $"{rel}: expected {code} at '{path}', got: {string.Join("; ", errors.Select(e => $"{e.Code} '{e.Path}'"))}");
    }

    private static void AssertHas(IReadOnlyList<SpecError> errors, JsonObject expected, string rel) =>
        AssertHas([.. errors.Select(e => (e.Path, e.Code))], expected, rel);

    [Fact]
    public void AnExpectedPath_MatchesOnlyItself_NotAPathItIsAPrefixOf()
    {
        IReadOnlyList<(string, string)> errors = [("/values/b/args/0", "cycle.value")];
        AssertHas(errors, new JsonObject { ["code"] = "cycle.value", ["path"] = "/values/b/args/0" }, "exact");
        foreach (var path in new[] { "", "/values/b", "/values/b/args" })
            Assert.ThrowsAny<Exception>(() => AssertHas(errors, new JsonObject { ["code"] = "cycle.value", ["path"] = path }, "prefix"));
        Assert.ThrowsAny<Exception>(() => AssertHas(errors, new JsonObject { ["code"] = "cycle.value" }, "no path"));
    }

    // ---- scenes ------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Files), "scene/valid")]
    public void ValidScene_PassesAndRoundTrips(string rel)
    {
        var json = Load(rel);
        var check = SceneValidator.Check(json, support: EngineSupport.All);
        Assert.True(check.Status == CheckStatus.Valid, $"{rel}: {string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}"))}");
        AssertJsonEqual(json, SpecJson.Write(check.Scene!), rel);
    }

    [Theory]
    [MemberData(nameof(Files), "scene/unsupported")]
    public void UnsupportedScene_IsUnsupported(string rel)
    {
        var check = SceneValidator.Check(Load(rel), support: EngineSupport.All);
        Assert.Equal(CheckStatus.Unsupported, check.Status);
        AssertHas(check.Errors, Expect(rel), rel);
    }

    [Theory]
    [MemberData(nameof(Files), "scene/invalid")]
    public void InvalidScene_FailsForItsReason(string rel)
    {
        var json = Load(rel);
        var expected = Expect(rel);
        var check = SceneValidator.Check(json, Limits(expected), EngineSupport.All);
        Assert.Equal(CheckStatus.Invalid, check.Status);
        AssertHas(check.Errors, expected, rel);
        var schemaValid = SpecSchemas.Strict.Validate(json, SpecSchemas.Scene).IsValid;
        // A rule example must be schema-valid, so it tests the rule; a schema example must fail the schema itself, and so
        // must a json one (the schema validator reads numbers as the formats do).
        Assert.Equal((string)expected["code"]! is not ("schema" or "json"), schemaValid);
    }

    [Fact]
    public void ProviderScene_IsUnsupportedWithoutTheCapability()
    {
        var check = SceneValidator.Check(Load("scene/valid/provider-media.json"), support: EngineSupport.Base);
        Assert.Equal(CheckStatus.Unsupported, check.Status);
        Assert.Equal("unsupported.capability", check.Errors.Single().Code);
    }

    // ---- documents ---------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Files), "document/valid")]
    public void ValidDocument_LoadsWithoutPlaceholdersAndRoundTrips(string rel)
    {
        var json = Load(rel);
        var check = DocumentValidator.Check(json, support: EngineSupport.All);
        Assert.True(check.Status == CheckStatus.Valid, $"{rel}: {string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}"))}");
        Assert.Empty(check.Items);
        AssertJsonEqual(json, SpecJson.Write(check.Document!), rel);
        Assert.True(SpecSchemas.Strict.Validate(json, SpecSchemas.Document).IsValid, $"{rel} fails the strict schema");
    }

    [Theory]
    [MemberData(nameof(Files), "document/placeholder")]
    public void PlaceholderDocument_LoadsWithTheExpectedPlaceholders(string rel)
    {
        var expected = Expect(rel);
        var check = DocumentValidator.Check(Load(rel), Limits(expected), EngineSupport.All);
        Assert.Equal(CheckStatus.Valid, check.Status);
        // Each placeholder's path, problem, and its first error's code and exact path.
        var want = expected["items"]!.AsArray().Select(i => ((string)i!["path"]!, (string)i["problem"]!, (string)i["code"]!, (string)i["errorPath"]!)).ToList();
        var got = check.Items.Select(i => (i.Path, i.Problem, i.Errors.First().Code, i.Errors.First().Path)).ToList();
        Assert.Equal(want, got);
    }

    [Theory]
    [MemberData(nameof(Files), "document/unsupported")]
    public void UnsupportedDocument_IsUnsupported(string rel)
    {
        var check = DocumentValidator.Check(Load(rel), support: EngineSupport.All);
        Assert.Equal(CheckStatus.Unsupported, check.Status);
        AssertHas(check.Errors, Expect(rel), rel);
    }

    [Theory]
    [MemberData(nameof(Files), "document/invalid")]
    public void InvalidDocument_FailsForItsReason(string rel)
    {
        var json = Load(rel);
        var expected = Expect(rel);
        var check = DocumentValidator.Check(json, Limits(expected), EngineSupport.All);
        Assert.Equal(CheckStatus.Invalid, check.Status);
        AssertHas(check.Errors, expected, rel);
        // A rule example passes the schema; a schema example fails it, and so does one nested deeper than jsonDepth (the
        // schema validator's depth guard) or a json one (it reads numbers as the formats do).
        Assert.Equal((string)expected["code"]! is not ("schema" or "limit.jsonDepth" or "json"), SpecSchemas.StructureOnly.Validate(json, SpecSchemas.Document).IsValid);
    }

    // ---- commits -----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Files), "commit/valid")]
    public void ValidCommit_PassesAndRoundTrips(string rel)
    {
        var json = Load(rel);
        var check = CommitValidator.Check(json, support: EngineSupport.All);
        Assert.True(check.Errors.Count == 0, $"{rel}: {string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}"))}");
        Assert.Empty(check.Warnings);
        AssertJsonEqual(json, SpecJson.Write(check.Commit!), rel);
        Assert.True(SpecSchemas.Strict.Validate(json, SpecSchemas.CommitRequest).IsValid, $"{rel} fails the strict schema");
    }

    [Theory]
    [MemberData(nameof(Files), "commit/warning")]
    public void WarningCommit_IsAcceptedWithWarnings(string rel)
    {
        var check = CommitValidator.Check(Load(rel), support: EngineSupport.All);
        Assert.Empty(check.Errors);
        var want = Expect(rel)["warnings"]!.AsArray().Select(w => ((int)w!["op"]!, (string)w["code"]!)).ToList();
        Assert.Equal(want, check.Warnings.Select(w => (w.Op, w.Code)).ToList());
    }

    [Theory]
    [MemberData(nameof(Files), "commit/invalid")]
    public void InvalidCommit_FailsForItsReason(string rel)
    {
        var json = Load(rel);
        var expected = Expect(rel);
        var check = CommitValidator.Check(json, Limits(expected), EngineSupport.All);
        Assert.NotEmpty(check.Errors);
        AssertHas(check.Errors, expected, rel);
    }

    // ---- API bodies --------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(ApiFiles), false)]
    public void ApiBody_MatchesItsDefinitionAndRoundTrips(string rel)
    {
        var def = rel.Split('/')[1];
        var json = Load(rel);
        var result = SpecSchemas.Strict.Validate(json, $"{SpecSchemas.Api}#/$defs/{def}");
        Assert.True(result.IsValid, $"{rel}: {string.Join("; ", result.Errors.Select(e => $"{e.InstancePath} {e.Message}"))}");
        Assert.Empty(ApiBodyRules.Check(def, json));
        var type = ApiModelType(def);
        var model = json.Deserialize(type, SpecJson.Options)!;
        AssertJsonEqual(json, JsonSerializer.SerializeToElement(model, type, SpecJson.Options), rel);
    }

    [Theory]
    [MemberData(nameof(ApiFiles), true)]
    public void InvalidApiBody_FailsItsDefinition(string rel)
    {
        // Each example breaks one rule: a schema example fails its definition only at the expected path; a rule example
        // (ApiBodyRules) matches its definition and breaks only that rule.
        var def = rel.Split('/')[1];
        var expected = Expect(rel);
        var json = Load(rel);
        var code = (string)expected["code"]!;
        var path = (string)expected["path"]!;
        var result = SpecSchemas.Strict.Validate(json, $"{SpecSchemas.Api}#/$defs/{def}");
        if (code == "schema" && def == "createProfileRequest")
        {
            // A union whose branch the body's members choose ({id, document} or an empty profile's members): the schema
            // can only say it matches neither, and the engine reads the branch it chose, so the path is the engine's.
            Assert.False(result.IsValid, $"{rel} matches {def}");
            AssertHas(EngineCreateErrors(File.ReadAllBytes(Path.Combine(ExamplesDir, rel))), expected, rel);
        }
        else if (code == "schema")
        {
            Assert.False(result.IsValid, $"{rel} matches {def}");
            Assert.True(result.Errors.All(e => e.InstancePath == path),
                $"{rel}: expected errors only at '{path}', got: {string.Join("; ", result.Errors.Select(e => $"{e.InstancePath} {e.Message}"))}");
        }
        else
        {
            Assert.True(result.IsValid, $"{rel}: a rule example must match {def}: {string.Join("; ", result.Errors.Select(e => $"{e.InstancePath} {e.Message}"))}");
            var errors = ApiBodyRules.Check(def, json);
            Assert.True(errors.SequenceEqual([(path, code)]), $"{rel}: expected only {code} at '{path}', got: {string.Join("; ", errors)}");
        }
    }

    /// <summary>The errors of the engine's 422 to <c>POST /api/profiles</c> with <paramref name="body"/>, on an empty workspace.</summary>
    private static IReadOnlyList<(string Path, string Code)> EngineCreateErrors(byte[] body)
    {
        using var temp = new Persistence.TempDirectory();
        using var workspace = InfoPanel.Documents.Workspace.Start(new InfoPanel.Persistence.DataPaths(temp.Path),
            new InfoPanel.Documents.WorkspaceOptions { HistoryBudget = new InfoPanel.Documents.HistoryBudget(InfoPanel.Documents.HistoryBudget.DefaultMaxBytes) },
            DateTimeOffset.UnixEpoch);
        var created = workspace.Create(body);
        Assert.Equal(422, created.Status);
        return [.. created.Invalid!.Errors.Select(e => (e.Path, e.Code))];
    }

    /// <summary>
    /// The model type of an API definition: the type that mirrors it, or, for a definition that is a union of others
    /// (<c>profileEntry</c>, <c>blobInfo</c>, <c>createProfileRequest</c>), the one type that mirrors every branch.
    /// </summary>
    private static Type ApiModelType(string def)
    {
        var reference = $"{SpecSchemas.Api}#/$defs/{def}";
        var direct = SpecSchemaAgreementTests.ModelTypes.Where(t => t.Refs.Contains(reference)).ToList();
        if (direct.Count > 0) return Assert.Single(direct).Type;
        var schema = JsonNode.Parse(SpecSchemas.Files["api.schema.json"])!["$defs"]![def]!;
        var branches = schema["oneOf"]!.AsArray().Select(b => SpecSchemas.Api + (string)b!["$ref"]!).ToList();
        return SpecSchemaAgreementTests.ModelTypes.Single(t => branches.All(t.Refs.Contains)).Type;
    }

    // ---- media -------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Files), "media/valid")]
    public void ValidMedia_PassesInspection(string rel)
    {
        var (info, errors) = MediaValidator.Check(File.ReadAllBytes(Path.Combine(ExamplesDir, rel)));
        Assert.NotNull(info);
        Assert.Empty(errors);
    }

    [Theory]
    [MemberData(nameof(Files), "media/invalid")]
    public void InvalidMedia_IsRefusedBeforeDecoding(string rel)
    {
        var expected = Expect(rel);
        var (_, errors) = MediaValidator.Check(File.ReadAllBytes(Path.Combine(ExamplesDir, rel)), Limits(expected));
        AssertHas(errors, expected, rel);
    }

    // ---- the example set itself --------------------------------------------------------------------------------

    [Fact]
    public void EveryExampleHasAnExpectation_AndEveryExpectationAnExample()
    {
        var files = Directory.EnumerateFiles(ExamplesDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(ExamplesDir, f).Replace('\\', '/'))
            .Where(f => f != "expected.json")
            .ToList();
        string[] folders = ["scene/valid/", "scene/unsupported/", "scene/invalid/", "document/valid/", "document/placeholder/",
            "document/unsupported/", "document/invalid/", "commit/valid/", "commit/warning/", "commit/invalid/", "api/",
            "media/valid/", "media/invalid/"];
        Assert.Empty(files.Where(f => !folders.Any(f.StartsWith)));
        // API bodies are api/{definition}/*.json, and the ones a definition refuses api/{definition}/invalid/*.json.
        var misplaced = files.Where(f => f.StartsWith("api/", StringComparison.Ordinal) && f.Split('/').Length != 3 && !IsInvalidApiBody(f)).ToList();
        Assert.True(misplaced.Count == 0, "Misplaced API examples: " + string.Join(", ", misplaced));
        var needExpectation = files.Where(f => f.StartsWith("api/", StringComparison.Ordinal) ? IsInvalidApiBody(f) : !f.Contains("/valid/")).ToHashSet();
        Assert.Empty(needExpectation.Where(f => !Expected.ContainsKey(f)));
        Assert.Empty(Expected.Select(p => p.Key).Where(k => !needExpectation.Contains(k)));
    }

    [Fact]
    public void ValidExamples_UseEveryNodePaintEffectTransformOperationAndOp()
    {
        var declared = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, text) in SpecSchemas.Files)
        {
            var defs = JsonNode.Parse(text)!["$defs"]!.AsObject();
            foreach (var (_, def) in defs)
            {
                if (def?["properties"] is not JsonObject props) continue;
                foreach (var key in new[] { "kind", "type", "op" })
                {
                    if (props[key] is not JsonObject p) continue;
                    if (p["const"] is JsonValue c) declared.Add($"{key}:{c}");
                    if (key == "op" && p["enum"] is JsonArray e) foreach (var v in e) declared.Add($"{key}:{v}");
                }
            }
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in new[] { "scene/valid", "document/valid", "commit/valid" }.SelectMany(FileList)
                     .Concat(FileList("api").Where(f => !IsInvalidApiBody(f))))
            Collect(JsonNode.Parse(File.ReadAllText(Path.Combine(ExamplesDir, rel)), documentOptions: SpecJson.DocumentOptions), used);
        Assert.Empty(declared.Where(d => !used.Contains(d)));

        static void Collect(JsonNode? node, HashSet<string> used)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key is "kind" or "type" or "op" && value is JsonValue v && v.GetValueKind() == JsonValueKind.String)
                            used.Add($"{key}:{v.GetValue<string>()}");
                        Collect(value, used);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) Collect(item, used);
                    break;
            }
        }
    }

    [Fact]
    public void EmbeddedSchemas_AreTheFilesInSpec()
    {
        var onDisk = Directory.EnumerateFiles(Path.Combine(SpecDir, "schemas"), "*.schema.json")
            .ToDictionary(Path.GetFileName, File.ReadAllText);
        Assert.Equal(onDisk.Keys.Order(StringComparer.Ordinal), SpecSchemas.Files.Keys);
        foreach (var (name, text) in onDisk) Assert.Equal(text, SpecSchemas.Files[name!]);
    }

    [Fact]
    public void TypeScriptTypes_AreCurrent()
    {
        // spec/types/index.d.ts is generated from the schemas by spec/tools/gen-types.mjs (Node, no dependencies).
        var node = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .SelectMany(d => new[] { Path.Combine(d, "node"), Path.Combine(d, "node.exe") })
            .FirstOrDefault(File.Exists);
        if (node is null) Assert.Skip("Node.js isn't on PATH; run `npm run check` in spec/ instead.");
        var start = new System.Diagnostics.ProcessStartInfo(node, [Path.Combine(SpecDir, "tools", "check.mjs")])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Remove("TSC");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }

    /// <summary>
    /// Equal as JSON values: member order, number spelling (1 and 1.0) and members that are null don't matter, except in
    /// opaque members (<see cref="AssertOpaqueKept"/>), which must come back token for token.
    /// </summary>
    internal static void AssertJsonEqual(JsonElement expected, JsonElement actual, string what)
    {
        var a = Normalize(JsonNode.Parse(expected.GetRawText(), documentOptions: SpecJson.DocumentOptions));
        var b = Normalize(JsonNode.Parse(actual.GetRawText(), documentOptions: SpecJson.DocumentOptions));
        Assert.True(JsonNode.DeepEquals(a, b), $"{what} doesn't round-trip:\n{a?.ToJsonString()}\n{b?.ToJsonString()}");
        AssertOpaqueKept(expected, actual, "", what);
    }

    /// <summary>The members the engine keeps as received: a scene, a source's data, an origin, the profile's editor data.</summary>
    private static readonly HashSet<string> Opaque = ["scene", "data", "origin", "editor"];

    /// <summary>
    /// Every opaque member of <paramref name="expected"/> is in <paramref name="actual"/> with the same tokens in the same
    /// order: no normalisation, so a null member, a number's spelling and member order all count.
    /// </summary>
    internal static void AssertOpaqueKept(JsonElement expected, JsonElement actual, string path, string what)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in expected.EnumerateObject())
                {
                    var at = path + "/" + p.Name;
                    Assert.True(actual.ValueKind == JsonValueKind.Object && actual.TryGetProperty(p.Name, out var v)
                        || p.Value.ValueKind == JsonValueKind.Null && !Opaque.Contains(p.Name), $"{what}: {at} is missing");
                    if (actual.ValueKind != JsonValueKind.Object || !actual.TryGetProperty(p.Name, out v)) continue;
                    if (Opaque.Contains(p.Name))
                        Assert.True(Tokens(p.Value) == Tokens(v), $"{what}: opaque {at} changed:\n{Tokens(p.Value)}\n{Tokens(v)}");
                    else AssertOpaqueKept(p.Value, v, at, what);
                }
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in expected.EnumerateArray())
                {
                    if (actual.ValueKind == JsonValueKind.Array && i < actual.GetArrayLength())
                        AssertOpaqueKept(item, actual[i], $"{path}/{i}", what);
                    i++;
                }
                break;
        }
    }

    /// <summary>A value's tokens, written compactly: the same for the same JSON text whatever its whitespace or escapes.</summary>
    private static string Tokens(JsonElement e) => JsonSerializer.Serialize(e, Deep);

    private static readonly JsonSerializerOptions Deep = new() { MaxDepth = SpecJson.SchemaDepth };

    [Fact]
    public void OpaqueMembers_KeepNullsAndNumberSpelling()
    {
        // source.data: null is kept as null, not dropped; numbers inside opaque members keep their spelling.
        var json = JsonDocument.Parse("""
            {"format": "infopanel.document/1", "id": "3dd19c0e-675a-4591-b096-83417853861f", "profile": {"name": "P", "width": 10, "height": 10, "editor": {"a": 1.0, "b": null}},
             "items": [{"kind": "scene", "id": "s", "placement": {"x": 0, "y": 0}, "origin": {"n": 1e0},
                        "source": {"format": "f/1", "data": null}, "scene": {"format": "infopanel.scene/1", "nodes": []}},
                       {"kind": "scene", "id": "t", "placement": {"x": 0, "y": 0},
                        "source": {"ref": "r"}, "scene": {"format": "infopanel.scene/1", "nodes": []}}]}
            """).RootElement;
        var check = DocumentValidator.Check(json);
        Assert.True(check.Status == CheckStatus.Valid, string.Join("; ", check.Errors.Select(e => $"{e.Code} {e.Path} {e.Message}")));
        var items = check.Document!.Items;
        Assert.Equal(JsonValueKind.Null, ((SceneItem)items[0]).Source!.Data.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, ((SceneItem)items[1]).Source!.Data.ValueKind);
        var written = SpecJson.Write(check.Document);
        AssertOpaqueKept(json, written, "", "document");
        Assert.True(written.GetProperty("items")[0].GetProperty("source").TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Null);
        Assert.False(written.GetProperty("items")[1].GetProperty("source").TryGetProperty("data", out _));
        Assert.Equal("{\"a\":1.0,\"b\":null}", Tokens(written.GetProperty("profile").GetProperty("editor")));
    }

    private static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Normalize(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Normalize).ToArray()),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => JsonValue.Create(v.GetValue<double>()),
        _ => node?.DeepClone(),
    };
}

/// <summary>
/// The C# model (InfoPanel.Spec) against the committed schemas: every [SpecDef] type's JSON members are its definition's
/// properties (and its required members the definition's required ones), and every object definition with properties
/// has a model type, so neither can change without the other.
/// </summary>
public class SpecSchemaAgreementTests
{
    internal static readonly IReadOnlyList<(Type Type, IReadOnlyList<string> Refs)> ModelTypes = typeof(SpecJson).Assembly.GetTypes()
        .Select(t => (Type: t, Attr: t.GetCustomAttribute<SpecDefAttribute>()))
        .Where(x => x.Attr is not null)
        .Select(x => (x.Type, x.Attr!.Refs))
        .ToList();

    private static readonly Dictionary<string, JsonNode> Schemas = SpecSchemas.Files.Values
        .Select(t => JsonNode.Parse(t)!)
        .ToDictionary(n => (string)n["$id"]!, n => n);

    private static JsonObject Resolve(string reference)
    {
        var hash = reference.IndexOf('#');
        var node = Schemas[hash < 0 ? reference : reference[..hash]];
        if (hash >= 0)
            foreach (var token in reference[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                node = node[token.Replace("~1", "/").Replace("~0", "~")] ?? throw new KeyNotFoundException(reference);
        return node.AsObject();
    }

    public static TheoryData<string> TypeNames()
    {
        var data = new TheoryData<string>();
        foreach (var (type, _) in ModelTypes) data.Add(type.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(TypeNames))]
    public void ModelType_HasItsDefinitionsMembers(string typeName)
    {
        var (type, refs) = ModelTypes.Single(t => t.Type.Name == typeName);
        var info = SpecJson.Options.GetTypeInfo(type);
        var members = info.Properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var discriminators = new HashSet<string>(StringComparer.Ordinal);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        var required = new List<HashSet<string>>();
        foreach (var reference in refs)
        {
            var def = Resolve(reference);
            var props = def["properties"]!.AsObject();
            foreach (var (name, schema) in props)
            {
                // A const member (kind, type, op) is the polymorphic discriminator, written by the serializer.
                if (schema is JsonObject so && so["const"] is not null && name is "kind" or "type" or "op" && IsPolymorphic(type)) discriminators.Add(name);
                else properties.Add(name);
            }
            required.Add((def["required"]?.AsArray().Select(r => (string)r!) ?? []).Where(r => !discriminators.Contains(r)).ToHashSet());
        }
        Assert.Equal(properties.Order(StringComparer.Ordinal), members.Order(StringComparer.Ordinal));
        // A type that mirrors several definitions (the branches of a union, or a document and the API's returned form of
        // it) requires what every one of them requires.
        var everyRequired = required.Skip(1).Aggregate(required[0], (all, r) => all.Intersect(r).ToHashSet());
        var modelRequired = info.Properties.Where(p => p.IsRequired).Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(everyRequired.Order(StringComparer.Ordinal), modelRequired);
    }

    private static bool IsPolymorphic(Type type) =>
        type.BaseType?.GetCustomAttribute<System.Text.Json.Serialization.JsonPolymorphicAttribute>(inherit: false) is not null;

    [Fact]
    public void EveryObjectDefinition_HasAModelType()
    {
        var covered = ModelTypes.SelectMany(t => t.Refs).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var (id, schema) in Schemas)
        {
            if (id == SpecSchemas.Expression) continue; // Expressions are modelled as Expr records, read by SpecJson.
            if (schema["properties"] is not null && !covered.Contains(id)) missing.Add(id);
            foreach (var (name, def) in schema["$defs"]?.AsObject() ?? [])
                if (def?["properties"] is not null && !covered.Contains($"{id}#/$defs/{name}")) missing.Add($"{id}#/$defs/{name}");
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void ModelDiscriminators_AreTheSchemasConsts()
    {
        foreach (var (type, refs) in ModelTypes.Where(t => t.Refs.Count == 1 && IsPolymorphic(t.Type)))
        {
            var def = Resolve(refs[0]);
            var polymorphic = type.BaseType!.GetCustomAttribute<System.Text.Json.Serialization.JsonPolymorphicAttribute>()!;
            var derived = type.BaseType.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()
                .Single(d => d.DerivedType == type);
            var name = polymorphic.TypeDiscriminatorPropertyName!;
            Assert.Equal((string)def["properties"]![name]!["const"]!, (string)derived.TypeDiscriminator!);
        }
    }
}

/// <summary>
/// What an engine API body must satisfy beyond its schema definition (spec/docs/api.md, "Checks beyond the schema"):
/// revisions that agree (<c>rev.mismatch</c>) and ids listed once (<c>id.duplicate</c>). Run on bodies that match their
/// definition; each error is a JSON pointer into the body and a code, as in spec/examples/expected.json.
/// </summary>
internal static class ApiBodyRules
{
    public static IReadOnlyList<(string Path, string Code)> Check(string def, JsonElement body)
    {
        var errors = new List<(string, string)>();
        switch (def)
        {
            case "documentResponse":
            {
                // The state's rev is the document's, and nothing in the document changed after it.
                var document = body.GetProperty("document");
                var rev = document.GetProperty("rev").GetInt64();
                if (body.GetProperty("rev").GetInt64() != rev) errors.Add(("/document/rev", "rev.mismatch"));
                if (document.GetProperty("profile").GetProperty("rev").GetInt64() > rev) errors.Add(("/document/profile/rev", "rev.mismatch"));
                Items(document.GetProperty("items"), "/document/items", rev, errors);
                break;
            }
            case "commitResult":
            {
                // Every item the result names, and the profile, changed at the result's revision.
                var rev = body.GetProperty("rev").GetInt64();
                foreach (var item in body.GetProperty("items").EnumerateObject())
                    if (item.Value.GetInt64() != rev) errors.Add(("/items/" + Escape(item.Name), "rev.mismatch"));
                if (body.TryGetProperty("profileRev", out var profileRev) && profileRev.GetInt64() != rev) errors.Add(("/profileRev", "rev.mismatch"));
                break;
            }
            case "frameHeader":
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var i = 0;
                foreach (var bounds in body.GetProperty("itemBounds").EnumerateArray())
                {
                    if (!seen.Add(bounds.GetProperty("id").GetString()!)) errors.Add(($"/itemBounds/{i}/id", "id.duplicate"));
                    i++;
                }
                break;
            }
        }
        return errors;
    }

    private static void Items(JsonElement items, string path, long rev, List<(string, string)> errors)
    {
        var i = 0;
        foreach (var item in items.EnumerateArray())
        {
            var at = $"{path}/{i++}";
            if (item.GetProperty("rev").GetInt64() > rev) errors.Add((at + "/rev", "rev.mismatch"));
            if (item.TryGetProperty("children", out var children)) Items(children, at + "/children", rev, errors);
        }
    }

    private static string Escape(string name) => name.Replace("~", "~0").Replace("/", "~1");
}
