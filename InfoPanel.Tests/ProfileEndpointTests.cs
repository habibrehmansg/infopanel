using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using static InfoPanel.Tests.ReadEndpointTests;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests;

/// <summary>
/// The engine with a data folder seeded before it starts: profiles it can't open (<c>unsupported</c>, <c>unreadable</c>
/// JSON, an unreadable file), one whose document has an invalid scene, and one whose item's source is an opaque
/// <c>ref</c>. The store exists, so no example is created.
/// </summary>
public sealed class SeededEngineFactory : EngineFactory
{
    public const string Unsupported = "0a000000-0000-4000-8000-00000000000a";
    public const string NotJson = "0b000000-0000-4000-8000-00000000000b";
    public const string NoAccess = "0c000000-0000-4000-8000-00000000000c";
    public const string BadScene = "0d000000-0000-4000-8000-00000000000d";
    public const string RefSource = "0e000000-0000-4000-8000-00000000000e";

    /// <summary>The unsupported document's stored text, byte for byte (spacing and number spelling included).</summary>
    public const string UnsupportedText = "{\"format\": \"infopanel.document/2\",  \"id\":\"0a000000-0000-4000-8000-00000000000a\",\n \"profile\": {\"name\": \"From a newer engine\"}, \"x\": 1.50}";

    public const string SourceJson = """{"ref":"c1c2b7f0-opaque-reference","deps":["sha256:1111111111111111111111111111111111111111111111111111111111111111"]}""";

    public SeededEngineFactory()
    {
        File.WriteAllText(Seed(Unsupported, UnsupportedText), UnsupportedText);
        Seed(NotJson, "{ not json");
        Seed(NoAccess, "{}");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(DataDir, "profiles", NoAccess, "document.json"), UnixFileMode.None);

        var bad = Doc("Bad scene", 200, 100, Scene("good", 0, 0), Scene("bad", 50, 0, InvalidScene()));
        bad["id"] = BadScene;
        Seed(BadScene, bad.ToJsonString());

        var held = Doc("Ref source", 200, 100, Scene("held", 20, 20, InputScene(), new JsonObject
        {
            ["source"] = JsonNode.Parse(SourceJson),
            ["layout"] = new JsonObject { ["w"] = 60, ["h"] = 10 },
            ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = "plugin:/infopanel.mock/cpu/cpu-load" } },
        }));
        held["id"] = RefSource;
        Seed(RefSource, held.ToJsonString());
        File.WriteAllText(Path.Combine(DataDir, "profiles.json"), """{"order":[]}""");
    }

    private string Seed(string id, string json)
    {
        var folder = Path.Combine(DataDir, "profiles", id);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "document.json");
        File.WriteAllText(file, json);
        return file;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !OperatingSystem.IsWindows())
        {
            var file = Path.Combine(DataDir, "profiles", NoAccess, "document.json");
            if (File.Exists(file))
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        base.Dispose(disposing);
    }
}

/// <summary>The profile, document and blob endpoints of spec/docs/api.md and commit.md, over HTTP.</summary>
public class ProfileEndpointTests(SeededEngineFactory factory) : IClassFixture<SeededEngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Api() => factory.CreateApiClient();

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status, string? definition = null)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await Body(response);
        if (definition != null)
            Assert.True(Valid(body, definition));
        return body;
    }

    private async Task<string> NewProfile(params JsonObject[] items)
    {
        using var api = Api();
        return await CreateAsync(api, Doc("Profile", 300, 100, items), Ct);
    }

    private async Task<HttpResponseMessage> Post(string path, JsonNode? body)
    {
        using var api = Api();
        return await api.PostAsync(path, body == null ? null : Json(body), Ct);
    }

    private async Task<HttpResponseMessage> PostBytes(string path, byte[] body)
    {
        using var api = Api();
        return await api.PostAsync(path, new ByteArrayContent(body), Ct);
    }

    private async Task<JsonElement> Get(string path)
    {
        using var api = Api();
        using var response = await api.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await Body(response);
    }

    private async Task<string> Instance()
    {
        using var api = Api();
        return await InstanceAsync(api, Ct);
    }

    private async Task<byte[]> Thumbnail(string id)
    {
        using var api = Api();
        return await api.GetByteArrayAsync($"/api/profiles/{id}/thumbnail?w=300", Ct);
    }

    // ---- profiles --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheList_HasEveryStatus()
    {
        var entries = (await Get("/api/profiles")).EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        foreach (var entry in entries.Values)
            Assert.True(Valid(entry, "profileEntry"));

        Assert.Equal("""{"id":"0a000000-0000-4000-8000-00000000000a","status":"unsupported","name":"From a newer engine","problem":{"path":"/format","code":"unsupported.format","message":"This engine reads infopanel.document/1, not infopanel.document/2."}}""",
            entries[SeededEngineFactory.Unsupported].GetRawText());
        Assert.Equal(("unreadable", "json"), (entries[SeededEngineFactory.NotJson].GetProperty("status").GetString(),
            entries[SeededEngineFactory.NotJson].GetProperty("problem").GetProperty("code").GetString()));
        if (!OperatingSystem.IsWindows())
            Assert.Equal("io", entries[SeededEngineFactory.NoAccess].GetProperty("problem").GetProperty("code").GetString());
        Assert.Equal("ok", entries[SeededEngineFactory.BadScene].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_Empty_IsSavedAtOnce_AndAnExistingIdIs409()
    {
        var id = NewId();
        var body = new JsonObject { ["id"] = id, ["name"] = "Empty", ["width"] = 64, ["height"] = 32 };
        using var created = await Post("/api/profiles", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = await Body(created);
        Assert.True(Valid(entry, "profileEntryOk"));
        Assert.Equal($$"""{"id":"{{id}}","status":"ok","name":"Empty","width":64,"height":32,"rev":1,"dirty":false}""", entry.GetRawText());
        Assert.True(File.Exists(Path.Combine(factory.DataDir, "profiles", id, "document.json")));

        // The same request again (its answer lost): 409 exists, and nothing changes.
        using var again = await Post("/api/profiles", body);
        Assert.Equal("exists", (await Problem(again, HttpStatusCode.Conflict, "existsProblem")).GetProperty("code").GetString());
        Assert.Equal(id, (await Get("/api/profiles")).EnumerateArray().Last().GetProperty("id").GetString());
    }

    [Fact]
    public async Task Create_WithAnIdEndingInANewline_Is422()
    {
        // A pattern's $ matched before a final newline, so the id reached the store, which refused it: a 500.
        using var response = await PostBytes("/api/profiles", System.Text.Encoding.UTF8.GetBytes($$"""{"id": "{{NewId()}}\n", "name": "x", "width": 1, "height": 1}"""));
        var error = (await Problem(response, HttpStatusCode.UnprocessableEntity, "invalidProblem")).GetProperty("errors")[0];
        Assert.Equal(("schema", "/id"), (error.GetProperty("code").GetString(), error.GetProperty("path").GetString()));
    }

    [Fact]
    public async Task Create_FromADocument_IsTolerant_AndRefusesWhatTheEngineWouldntOpen()
    {
        // A scene that breaks a rule is kept as it was sent and drawn as a placeholder; the document's id and revs are replaced.
        var document = Doc("Imported", 200, 100, Scene("good"), Scene("bad", 50, 0, InvalidScene()));
        document["rev"] = 41;
        var id = NewId();
        using var created = await Post("/api/profiles", new JsonObject { ["id"] = id, ["document"] = document });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var stored = (await Get($"/api/profiles/{id}")).GetProperty("document");
        Assert.Equal((id, 1L), (stored.GetProperty("id").GetString(), stored.GetProperty("rev").GetInt64()));
        Assert.True(JsonNode.DeepEquals(InvalidScene(), JsonNode.Parse(stored.GetProperty("items")[1].GetProperty("scene").GetRawText())));

        async Task<JsonElement> Refused(JsonNode body) =>
            (await Problem(await Post("/api/profiles", body), HttpStatusCode.UnprocessableEntity, "invalidProblem")).GetProperty("errors")[0];

        var future = await Refused(new JsonObject { ["id"] = NewId(), ["document"] = new JsonObject { ["format"] = "infopanel.document/2" } });
        Assert.Equal(("/document/format", "unsupported.format"), (future.GetProperty("path").GetString(), future.GetProperty("code").GetString()));
        var noProfile = (JsonObject)document.DeepClone();
        noProfile.Remove("profile");
        Assert.StartsWith("/document", (await Refused(new JsonObject { ["id"] = NewId(), ["document"] = noProfile })).GetProperty("path").GetString());
        Assert.Equal("schema", (await Refused(new JsonObject { ["id"] = "UPPER-case", ["name"] = "x", ["width"] = 1, ["height"] = 1 })).GetProperty("code").GetString());
        Assert.Equal("schema", (await Refused(new JsonObject { ["id"] = NewId(), ["name"] = "x", ["width"] = 16385, ["height"] = 1 })).GetProperty("code").GetString());
        Assert.Equal("json", (await Problem(await PostBytes("/api/profiles", "{"u8.ToArray()), HttpStatusCode.UnprocessableEntity)).GetProperty("errors")[0].GetProperty("code").GetString());

        // 422 comes before 409: a refused body with an existing id is 422, its document checked whole first.
        Assert.Equal("schema", (await Refused(new JsonObject { ["id"] = id, ["name"] = "x", ["width"] = 0, ["height"] = 1 })).GetProperty("code").GetString());
        Assert.Equal("unsupported.format", (await Refused(new JsonObject { ["id"] = id, ["document"] = new JsonObject { ["format"] = "infopanel.document/2" } }))
            .GetProperty("code").GetString());
    }

    [Fact]
    public async Task ADocumentNestedToJsonDepth_IsAnsweredInItsEnvelope()
    {
        // A scene kept as sent (a newer scene format) holding 252 nested arrays: the document is 256 levels deep, and
        // the documentResponse around it 257.
        var id = NewId();
        var nested = new string('[', 252) + new string(']', 252);
        var body = $$$"""
            {"id": "{{{id}}}", "document": {"format": "infopanel.document/1", "id": "{{{id}}}", "profile": {"name": "Deep", "width": 200, "height": 100},
             "items": [{"kind": "scene", "id": "deep", "placement": {"x": 0, "y": 0}, "scene": {"format": "infopanel.scene/2", "x": {{{nested}}}}}]}}
            """;
        using (var created = await PostBytes("/api/profiles", Encoding.UTF8.GetBytes(body)))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var api = Api();
        using var response = await api.GetAsync($"/api/profiles/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var read = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(Ct), new JsonDocumentOptions { MaxDepth = 1000 });
        var x = read.RootElement.GetProperty("document").GetProperty("items")[0].GetProperty("scene").GetProperty("x");
        var depth = 0;
        for (; x.ValueKind == JsonValueKind.Array && x.GetArrayLength() == 1; x = x[0])
            depth++;
        Assert.Equal((251, 0), (depth, x.GetArrayLength()));
    }

    [Fact]
    public async Task ACommitOverCommitBytes_Is422_FromItsContentLengthAlone()
    {
        var id = await NewProfile(Scene("a"));
        using var api = Api();
        using var response = await api.PostAsync($"/api/profiles/{id}/commit", new ByteArrayContent(new byte[InfoPanel.Spec.SpecLimits.Default.CommitBytes + 1]), Ct);
        var error = (await Problem(response, HttpStatusCode.UnprocessableEntity, "invalidProblem")).GetProperty("errors")[0];
        Assert.Equal(("", "limit.commitBytes"), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Duplicate_CopiesTheCurrentDocument_AndChecksInOrder()
    {
        var source = await NewProfile(Scene("a"));
        using (var api = Api())
            Assert.Equal(HttpStatusCode.OK, (await CommitAsync(api, source, Ct, Place("a", 40, 30))).StatusCode);

        var copy = NewId();
        using var created = await Post($"/api/profiles/{source}/duplicate", new JsonObject { ["id"] = copy });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var response = await Get($"/api/profiles/{copy}");
        Assert.Equal((1L, false, false), (response.GetProperty("rev").GetInt64(), response.GetProperty("dirty").GetBoolean(), response.GetProperty("canUndo").GetBoolean()));
        Assert.Equal(40, response.GetProperty("document").GetProperty("items")[0].GetProperty("placement").GetProperty("x").GetDouble());

        await Problem(await Post($"/api/profiles/{NewId()}/duplicate", new JsonObject { ["id"] = NewId() }), HttpStatusCode.NotFound);

        // The source is checked before the body: with a body too large to read, a missing source is 404, one that can't be opened 409.
        var oversized = new JsonObject { ["id"] = NewId(), ["pad"] = new string('x', 70 * 1024) };
        await Problem(await Post($"/api/profiles/{NewId()}/duplicate", oversized), HttpStatusCode.NotFound);
        await Problem(await Post($"/api/profiles/{SeededEngineFactory.Unsupported}/duplicate", oversized), HttpStatusCode.Conflict, "profileStatusProblem");
        Assert.Equal("unsupported", (await Problem(await Post($"/api/profiles/{SeededEngineFactory.Unsupported}/duplicate", new JsonObject { ["id"] = NewId() }),
            HttpStatusCode.Conflict, "profileStatusProblem")).GetProperty("code").GetString());
        await Problem(await Post($"/api/profiles/{source}/duplicate", new JsonObject { ["id"] = NewId(), ["name"] = "x" }), HttpStatusCode.UnprocessableEntity, "invalidProblem");
        Assert.Equal("exists", (await Problem(await Post($"/api/profiles/{source}/duplicate", new JsonObject { ["id"] = copy }), HttpStatusCode.Conflict, "existsProblem"))
            .GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_Is204_ThenTheProfileIsGone_And404()
    {
        var id = await NewProfile(Scene("a"));
        using var api = Api();
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync($"/api/profiles/{id}", Ct)).StatusCode);
        await Problem(await api.DeleteAsync($"/api/profiles/{id}", Ct), HttpStatusCode.NotFound);
        await Problem(await api.GetAsync($"/api/profiles/{id}", Ct), HttpStatusCode.NotFound);
        Assert.False(Directory.Exists(Path.Combine(factory.DataDir, "profiles", id)));
    }

    [Fact]
    public async Task AProfileThatCantBeOpened_IsListedExportedAndDeletable_AndEverythingElseIs409()
    {
        var instance = await Instance();
        using var api = Api();
        foreach (var path in new[] { "", "/thumbnail" })
        {
            var problem = await Problem(await api.GetAsync($"/api/profiles/{SeededEngineFactory.NotJson}{path}", Ct), HttpStatusCode.Conflict, "profileStatusProblem");
            Assert.Equal(("unreadable", "json"), (problem.GetProperty("code").GetString(), problem.GetProperty("problem").GetProperty("code").GetString()));
        }
        await Problem(await Post($"/api/profiles/{SeededEngineFactory.NotJson}/commit", CommitBody(instance, 1, Place("a", 1, 1))), HttpStatusCode.Conflict, "profileStatusProblem");
        await Problem(await Post($"/api/profiles/{SeededEngineFactory.NotJson}/undo", new JsonObject { ["instance"] = instance, ["baseRev"] = 1 }), HttpStatusCode.Conflict, "profileStatusProblem");
        await Problem(await Post($"/api/profiles/{SeededEngineFactory.NotJson}/save", null), HttpStatusCode.Conflict, "profileStatusProblem");

        // Exported byte for byte, as stored.
        using var exported = await api.GetAsync($"/api/profiles/{SeededEngineFactory.Unsupported}/document", Ct);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("application/octet-stream", exported.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Encoding.UTF8.GetBytes(SeededEngineFactory.UnsupportedText), await exported.Content.ReadAsByteArrayAsync(Ct));
        if (!OperatingSystem.IsWindows())
        {
            var io = await Problem(await api.GetAsync($"/api/profiles/{SeededEngineFactory.NoAccess}/document", Ct), HttpStatusCode.Conflict, "profileStatusProblem");
            Assert.Equal("io", io.GetProperty("problem").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Export_OfAnOkProfile_IsTheCurrentDocumentWithoutRevs()
    {
        var id = await NewProfile(Scene("a"));
        using var api = Api();
        await CommitAsync(api, id, Ct, Place("a", 12, 34));

        using var exported = await api.GetAsync($"/api/profiles/{id}/document", Ct);
        Assert.Equal("application/json", exported.Content.Headers.ContentType?.MediaType);
        var text = await exported.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("\"rev\"", text);
        Assert.Equal(12, JsonNode.Parse(text)!["items"]![0]!["placement"]!["x"]!.GetValue<double>());

        // It creates the same document again elsewhere.
        var copy = NewId();
        using var created = await api.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = copy, ["document"] = JsonNode.Parse(text) }), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var again = await (await api.GetAsync($"/api/profiles/{copy}/document", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Equal(text.Replace(id, copy), again);
    }

    [Fact]
    public async Task AStoredDocumentWithABadScene_Loads_WithThatItemAsAPlaceholder()
    {
        var response = await Get($"/api/profiles/{SeededEngineFactory.BadScene}");
        Assert.True(Valid(response, "documentResponse"));
        Assert.True(JsonNode.DeepEquals(InvalidScene(), JsonNode.Parse(response.GetProperty("document").GetProperty("items")[1].GetProperty("scene").GetRawText())));

        var bounds = factory.Services.GetRequiredService<InfoPanel.Output.RenderService>();
        var doc = factory.Services.GetRequiredService<InfoPanel.Documents.Workspace>().Open(SeededEngineFactory.BadScene)!;
        using var snapshot = InfoPanel.Scenes.SceneRuntime.RenderOnce(InfoPanel.Spec.SpecJson.Write(doc.Current.Document), DateTimeOffset.UnixEpoch,
            bounds.SceneOptions);
        Assert.Equal([null, "invalid"], snapshot.Bounds.Select(b => b.Problem));
        Assert.NotEmpty(await Thumbnail(SeededEngineFactory.BadScene));
    }

    // ---- thumbnails ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 16384, 320, 1, 4096)]
    [InlineData(16384, 1, 4096, 4096, 1)]
    [InlineData(16384, 1, 10, 16, 1)]
    [InlineData(300, 100, 99999, 4096, 1365)]
    public async Task ThumbnailSizes_AtTheExtremes(int width, int height, int requested, int expectedWidth, int expectedHeight)
    {
        using var api = Api();
        var id = NewId();
        using var created = await api.PostAsync("/api/profiles", Json(new JsonObject { ["id"] = id, ["name"] = "Extreme", ["width"] = width, ["height"] = height }), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var bitmap = SKBitmap.Decode(await api.GetByteArrayAsync($"/api/profiles/{id}/thumbnail?w={requested}", Ct));
        Assert.Equal((expectedWidth, expectedHeight), (bitmap.Width, bitmap.Height));
    }

    // ---- commits, undo, redo, revert, save -------------------------------------------------------------------------

    [Fact]
    public async Task ACommit_RetriedWithTheSameBytes_Replays_AndAnotherBodyWithItsOpIdIs422()
    {
        var id = await NewProfile(Scene("a"));
        var body = Encoding.UTF8.GetBytes(CommitBody(await Instance(), 1, Place("a", 10, 10)).ToJsonString() + "\n");

        using var first = await PostBytes($"/api/profiles/{id}/commit", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var result = await Body(first);
        Assert.True(Valid(result, "commitResult"));
        Assert.Equal(2, result.GetProperty("rev").GetInt64());

        using var retry = await PostBytes($"/api/profiles/{id}/commit", body);
        var replayed = await Body(retry);
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal(2, replayed.GetProperty("rev").GetInt64());
        Assert.Equal(2, (await Get($"/api/profiles/{id}")).GetProperty("rev").GetInt64());

        // The same opId with other bytes (whitespace too) is another body.
        var other = await Problem(await PostBytes($"/api/profiles/{id}/commit", body[..^1]), HttpStatusCode.UnprocessableEntity, "invalidProblem");
        Assert.Equal("opId.reused", other.GetProperty("errors")[0].GetProperty("code").GetString());
        await Problem(await PostBytes($"/api/profiles/{id}/commit", "{"u8.ToArray()), HttpStatusCode.UnprocessableEntity, "invalidProblem");
        await Problem(await Post($"/api/profiles/{NewId()}/commit", CommitBody(await Instance(), 1, Place("a", 1, 1))), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AStaleSetDesign_Is409_AndTheFrameIsUnchanged()
    {
        var id = await NewProfile(Scene("a", 10, 10, Rect("#ff0000", 40, 40)));
        var instance = await Instance();

        // Another client changes the design after this one read revision 1.
        using (var other = await Post($"/api/profiles/{id}/commit", CommitBody(instance, 1, Design("a", Rect("#00ff00", 40, 40), baseRev: 1))))
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var before = await Thumbnail(id);

        var stale = await Problem(await Post($"/api/profiles/{id}/commit", CommitBody(instance, 1, Design("a", Rect("#0000ff", 40, 40), baseRev: 1))),
            HttpStatusCode.Conflict, "conflictProblem");
        var conflict = stale.GetProperty("conflicts")[0];
        Assert.Equal(("a", "changed"), (conflict.GetProperty("id").GetString(), conflict.GetProperty("reason").GetString()));
        Assert.Contains("design", conflict.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(2, (await Get($"/api/profiles/{id}")).GetProperty("rev").GetInt64());
        Assert.Equal(before, await Thumbnail(id));

        // Another instance's commit is a conflict of the whole request.
        var foreign = await Problem(await Post($"/api/profiles/{id}/commit", CommitBody(new string('0', 32), 2, Place("a", 1, 1))), HttpStatusCode.Conflict, "conflictProblem");
        Assert.Equal("instance", foreign.GetProperty("conflicts")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task UndoRedoRevert_AreGuarded_AndSaveIsNot()
    {
        var id = await NewProfile(Scene("a"));
        var instance = await Instance();
        JsonObject History(long baseRev, string? at = null) => new() { ["instance"] = at ?? instance, ["baseRev"] = baseRev };

        using (var moved = await Post($"/api/profiles/{id}/commit", CommitBody(instance, 1, Place("a", 50, 0))))
            Assert.Equal(2, (await Body(moved)).GetProperty("rev").GetInt64());

        // A stale baseRev, another instance, a body that isn't the history request: refused, nothing done.
        var stale = await Problem(await Post($"/api/profiles/{id}/undo", History(1)), HttpStatusCode.Conflict, "conflictProblem");
        Assert.Equal(2, stale.GetProperty("rev").GetInt64());
        Assert.Equal("instance", (await Problem(await Post($"/api/profiles/{id}/redo", History(2, new string('0', 32))), HttpStatusCode.Conflict))
            .GetProperty("conflicts")[0].GetProperty("reason").GetString());
        await Problem(await Post($"/api/profiles/{id}/revert", new JsonObject { ["instance"] = instance }), HttpStatusCode.UnprocessableEntity, "invalidProblem");
        Assert.Equal("json", (await Problem(await PostBytes($"/api/profiles/{id}/undo", "nope"u8.ToArray()), HttpStatusCode.UnprocessableEntity))
            .GetProperty("errors")[0].GetProperty("code").GetString());

        using var undo = await Post($"/api/profiles/{id}/undo", History(2));
        var undone = await Body(undo);
        Assert.True(Valid(undone, "commitResult"));
        Assert.Equal((3, false, true), (undone.GetProperty("rev").GetInt64(), undone.GetProperty("dirty").GetBoolean(), undone.GetProperty("canRedo").GetBoolean()));
        using var redo = await Post($"/api/profiles/{id}/redo", History(3));
        Assert.Equal((4, true), ((await Body(redo)).GetProperty("rev").GetInt64(), (await Body(redo)).GetProperty("dirty").GetBoolean()));
        using var revert = await Post($"/api/profiles/{id}/revert", History(4));
        Assert.Equal((5, false), ((await Body(revert)).GetProperty("rev").GetInt64(), (await Body(revert)).GetProperty("dirty").GetBoolean()));

        // Save takes no body and no guard; it changes no revision.
        using (var dirty = await Post($"/api/profiles/{id}/commit", CommitBody(instance, 5, Place("a", 70, 0))))
            Assert.Equal(6, (await Body(dirty)).GetProperty("rev").GetInt64());
        using var save = await Post($"/api/profiles/{id}/save", null);
        var saved = await Body(save);
        Assert.Equal((6, false), (saved.GetProperty("rev").GetInt64(), saved.GetProperty("dirty").GetBoolean()));
        Assert.Equal(70, JsonNode.Parse(File.ReadAllText(Path.Combine(factory.DataDir, "profiles", id, "document.json")))!["items"]![0]!["placement"]!["x"]!.GetValue<double>());
    }

    [Fact]
    public async Task RefSourceItems_AreDrawnPlacedRotatedReboundHiddenAndRemoved_AndTheirSourceIsKeptByteForByte()
    {
        var id = SeededEngineFactory.RefSource;
        string SourceOf(JsonElement document) => document.GetProperty("items")[0].GetProperty("source").GetRawText();

        var response = await Get($"/api/profiles/{id}");
        Assert.Equal(SeededEngineFactory.SourceJson, SourceOf(response.GetProperty("document")));
        Assert.NotEmpty(await Thumbnail(id));

        using var api = Api();
        var rotate = new JsonObject { ["op"] = "setPlacement", ["id"] = "held", ["placement"] = new JsonObject { ["x"] = 40, ["y"] = 30, ["rotation"] = 15 } };
        foreach (var op in new[] { Place("held", 30, 30), rotate, Bind("held", "v", "plugin:/infopanel.mock/gpu/gpu-load"), Attrs("held", visible: false), Attrs("held", visible: true) })
            Assert.Equal(HttpStatusCode.OK, (await CommitAsync(api, id, Ct, op)).StatusCode);

        var document = (await Get($"/api/profiles/{id}")).GetProperty("document");
        Assert.Equal(SeededEngineFactory.SourceJson, SourceOf(document));
        Assert.Equal(15, document.GetProperty("items")[0].GetProperty("placement").GetProperty("rotation").GetDouble());
        var exported = await (await api.GetAsync($"/api/profiles/{id}/document", Ct)).Content.ReadAsStringAsync(Ct);
        Assert.Contains(SeededEngineFactory.SourceJson, exported);

        // A copy keeps it; removing it from the copy leaves the original alone.
        var copy = NewId();
        Assert.Equal(HttpStatusCode.Created, (await api.PostAsync($"/api/profiles/{id}/duplicate", Json(new JsonObject { ["id"] = copy }), Ct)).StatusCode);
        Assert.Equal(SeededEngineFactory.SourceJson, SourceOf((await Get($"/api/profiles/{copy}")).GetProperty("document")));
        Assert.Equal(HttpStatusCode.OK, (await CommitAsync(api, copy, Ct, Remove("held"))).StatusCode);
        Assert.Equal(0, (await Get($"/api/profiles/{copy}")).GetProperty("document").GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Get($"/api/profiles/{id}")).GetProperty("document").GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Mutations_NeedTheBearerToken_AndAnAllowedOrigin()
    {
        var id = await NewProfile(Scene("a"));
        var body = CommitBody(await Instance(), 1, Place("a", 5, 5));

        using var raw = factory.CreateRawClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await raw.PostAsync($"/api/profiles/{id}/commit", Json(body), Ct)).StatusCode);

        using var foreign = factory.CreateApiClient();
        foreign.DefaultRequestHeaders.Add("Origin", "http://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsync($"/api/profiles/{id}/commit", Json(body), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.DeleteAsync($"/api/profiles/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.PostAsync("/api/blobs", new ByteArrayContent([1]), Ct)).StatusCode);

        using var dev = factory.CreateApiClient();
        dev.DefaultRequestHeaders.Add("Origin", EngineFactory.DevOrigin);
        Assert.Equal(HttpStatusCode.OK, (await dev.PostAsync($"/api/profiles/{id}/commit", Json(body), Ct)).StatusCode);
    }

    // ---- blobs -----------------------------------------------------------------------------------------------------

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public async Task Blobs_201ThenTheSame200_HeadAndGetGiveThemBack()
    {
        var png = Png(3, 2);
        var id = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(png));

        using var first = await PostBytes("/api/blobs", png);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var info = await Body(first);
        Assert.True(Valid(info, "blobInfoMedia"));
        Assert.Equal($$$"""{"id":"{{{id}}}","bytes":{{{png.Length}}},"media":{"format":"png","width":3,"height":2,"frames":1,"duration":0}}""", info.GetRawText());

        using var again = await PostBytes("/api/blobs", png);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(info.GetRawText(), (await Body(again)).GetRawText());

        using var api = Api();
        using var head = await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/{id}"), Ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(png.Length, head.Content.Headers.ContentLength);
        using var get = await api.GetAsync($"/api/blobs/{id}", Ct);
        Assert.Equal("application/octet-stream", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await get.Content.ReadAsByteArrayAsync(Ct));

        var absent = "sha256:" + new string('0', 64);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/{absent}"), Ct)).StatusCode);
        await Problem(await api.GetAsync($"/api/blobs/{absent}", Ct), HttpStatusCode.NotFound);
        await Problem(await api.GetAsync("/api/blobs/sha256:..%2F..%2Fprofiles.json", Ct), HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A stored file whose bytes don't hash to its id is not the blob: HEAD and GET answer 404, so a client that checks
    /// with HEAD uploads the bytes again, and that upload repairs it.
    /// </summary>
    [Fact]
    public async Task Blobs_AStoredFileThatDoesNotHashToItsId_Is404_UntilTheRightBytesArePosted()
    {
        var png = Png(5, 4);
        var id = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(png));
        using var first = await PostBytes("/api/blobs", png);
        Assert.True(first.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, first.StatusCode.ToString());

        using var api = Api();
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/{id}"), Ct)).StatusCode);
        var file = Path.Combine(factory.DataDir, "blobs", id[7..]);
        var written = File.GetLastWriteTimeUtc(file);
        var damaged = (byte[])png.Clone();
        damaged[^1] ^= 1;
        File.WriteAllBytes(file, damaged);
        File.SetLastWriteTimeUtc(file, written.AddSeconds(1));

        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/{id}"), Ct)).StatusCode);
        await Problem(await api.GetAsync($"/api/blobs/{id}", Ct), HttpStatusCode.NotFound);

        // The upload stores it anew (201: it wasn't stored), and both answer again.
        using var repaired = await PostBytes("/api/blobs", png);
        Assert.Equal(HttpStatusCode.Created, repaired.StatusCode);
        Assert.Equal(png, File.ReadAllBytes(file));
        using var head = await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/api/blobs/{id}"), Ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(png.Length, head.Content.Headers.ContentLength);
        using var get = await api.GetAsync($"/api/blobs/{id}", Ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(png, await get.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task Blobs_ThatArentMediaOrBreakAMediaLimit_AreStoredWithAMediaError_AndOverMediaBytesIs413()
    {
        using var notes = await PostBytes("/api/blobs", "{\"a client's\": \"notes\"}"u8.ToArray());
        Assert.Equal(HttpStatusCode.Created, notes.StatusCode);
        var info = await Body(notes);
        Assert.True(Valid(info, "blobInfoMediaError"));
        Assert.Equal(("", "media.format"), (info.GetProperty("mediaError").GetProperty("path").GetString(), info.GetProperty("mediaError").GetProperty("code").GetString()));

        // A PNG whose header says 9000 pixels wide: over mediaSide, still stored.
        var wide = Png(1, 1);
        BinaryPrimitives.WriteUInt32BigEndian(wide.AsSpan(16), 9000);
        using var tooWide = await PostBytes("/api/blobs", wide);
        Assert.Equal(HttpStatusCode.Created, tooWide.StatusCode);
        Assert.Equal("limit.mediaSide", (await Body(tooWide)).GetProperty("mediaError").GetProperty("code").GetString());

        var limit = InfoPanel.Spec.SpecLimits.Default.MediaBytes;
        using var huge = await PostBytes("/api/blobs", new byte[limit + 1]);
        await Problem(huge, HttpStatusCode.RequestEntityTooLarge);
        Assert.False(File.Exists(Path.Combine(factory.DataDir, "blobs", Convert.ToHexStringLower(SHA256.HashData(new byte[limit + 1])))));

        // Exactly the limit is stored.
        using var full = await PostBytes("/api/blobs", new byte[limit]);
        Assert.Equal(HttpStatusCode.Created, full.StatusCode);
    }
}
