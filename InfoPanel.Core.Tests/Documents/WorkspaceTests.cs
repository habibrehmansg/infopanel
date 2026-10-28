using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Scenes;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// The engine's workspace over the 2.0 store: start-up (sweep, first-run example, step-1 data left alone), the profile
/// list with its statuses, opening on demand, create, duplicate, delete, export and blobs.
/// </summary>
public sealed class WorkspaceTests : IDisposable
{
    private const string A = "0a000000-0000-4000-8000-000000000000";
    private const string B = "0b000000-0000-4000-8000-000000000000";
    private const string ExampleId = "3682945e-c157-4ed7-a6b3-60e651328cf5";

    private readonly TempDirectory _temp = new();
    private readonly List<Workspace> _open = [];

    private DataPaths Paths => new(_temp.Path);

    public void Dispose()
    {
        foreach (var workspace in _open)
            workspace.Dispose();
        _temp.Dispose();
    }

    private Workspace StartWorkspace()
    {
        var workspace = Workspace.Start(Paths, new WorkspaceOptions { Instance = Instance, HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes) }, Start);
        _open.Add(workspace);
        return workspace;
    }

    /// <summary>A workspace whose store already exists (no example), holding the given stored documents.</summary>
    private Workspace With(params (string Id, string Json)[] stored)
    {
        Directory.CreateDirectory(Paths.ProfilesDirectory);
        File.WriteAllText(Paths.ProfileOrderFile, """{"order":[]}""");
        foreach (var (id, json) in stored)
        {
            Directory.CreateDirectory(Paths.ProfileFolder(id));
            File.WriteAllText(Paths.DocumentFile(id), json);
        }
        return StartWorkspace();
    }

    private static string DocJson(string id, params JsonObject[] items)
    {
        var doc = JsonNode.Parse(Doc(items).GetRawText())!.AsObject();
        doc["id"] = id;
        return doc.ToJsonString();
    }

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());


    [Fact]
    public void Example_documents_are_valid()
    {
        var folder = Path.Combine(TestPaths.ProjectDirectory, "..", "InfoPanel.Core", "Documents", "Examples");
        var files = Directory.GetFiles(folder, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(file), SpecJson.DocumentOptions);
            var check = DocumentValidator.Check(json.RootElement, SpecLimits.Default, EngineSupport.Base);
            Assert.True(check.Status == CheckStatus.Valid, $"{Path.GetFileName(file)}: {string.Join("; ", check.Errors.Select(e => $"{e.Path} {e.Code} {e.Message}"))}");
            Assert.Empty(check.Items);

            static IEnumerable<Item> All(IEnumerable<Item> items) => items.SelectMany(i => i is GroupItem g ? [i, .. All(g.Children)] : new[] { i });
            Assert.All(All(check.Document!.Items).OfType<SceneItem>(), item => Assert.Null(item.Source));
        }

        // The embedded examples are these files.
        Assert.Equal(files.Select(Path.GetFileName).Order(StringComparer.Ordinal), Workspace.ExampleDocuments().Select(e => e.Name));
    }

    [Fact]
    public void FirstRun_StoresTheExample_AndDeletingEveryProfileDoesntBringItBack()
    {
        var workspace = StartWorkspace();
        var entry = Assert.Single(workspace.List());
        Assert.Equal((ExampleId, "ok", "Wide bar", 1920, 480, 1L, false), (entry.Id, entry.Status, entry.Name, entry.Width, entry.Height, entry.Rev, entry.Dirty));

        Assert.True(workspace.DeleteWithItsTag(ExampleId));
        workspace.Dispose();

        Assert.Empty(StartWorkspace().List());
    }

    [Fact]
    public void AStep1DataFolder_IsLeftUntouchedAndUnread()
    {
        var step1 = new Dictionary<string, string>
        {
            ["profiles.xml"] = "<ArrayOfProfile><not-read/></ArrayOfProfile>",
            [Path.Combine("profiles", "9c1f2b3a-0000-4000-8000-000000000000.xml")] = "<ArrayOfDisplayItem/>",
            [Path.Combine("assets", "9c1f2b3a-0000-4000-8000-000000000000", "image.png")] = "not an image",
        };
        foreach (var (name, text) in step1)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_temp.Combine(name))!);
            File.WriteAllText(_temp.Combine(name), text);
        }
        var times = step1.Keys.ToDictionary(k => k, k => File.GetLastWriteTimeUtc(_temp.Combine(k)));

        var workspace = StartWorkspace();

        // The v2 store is new, so this is a first run: the example only.
        Assert.Equal([ExampleId], workspace.List().Select(e => e.Id));
        foreach (var (name, text) in step1)
        {
            Assert.Equal(text, File.ReadAllText(_temp.Combine(name)));
            Assert.Equal(times[name], File.GetLastWriteTimeUtc(_temp.Combine(name)));
        }
    }

    [Fact]
    public void AStoredDocumentWhoseIdIsNotItsFolders_OpensUnderTheFolder_AndIsSavedWithIt()
    {
        var workspace = With((A, DocJson(B, Scene("a"))));

        var doc = workspace.Open(A)!;
        Assert.Equal(A, doc.Id);
        Assert.Equal(A, doc.Current.Document.Id);
        Assert.Equal(B, JsonNode.Parse(File.ReadAllText(Paths.DocumentFile(A)))!["id"]!.GetValue<string>()); // untouched until saved

        doc.Save();
        Assert.Equal(A, JsonNode.Parse(File.ReadAllText(Paths.DocumentFile(A)))!["id"]!.GetValue<string>());
        Assert.Null(workspace.Entry(B));
    }

    [Fact]
    public void Statuses_OkUnsupportedUnreadable_AndOnlyOkOpens()
    {
        var future = """{"format":"infopanel.document/2","id":"0b000000-0000-4000-8000-000000000000","profile":{"name":"From later","width":10,"height":10},"items":[]}""";
        const string C = "0c000000-0000-4000-8000-000000000000";
        const string D = "0d000000-0000-4000-8000-000000000000";
        const string E = "0e000000-0000-4000-8000-000000000000";
        var workspace = With(
            (A, DocJson(A, Scene("a"))),
            (B, future),
            (C, "{ not json"),
            (D, """{"format":"infopanel.document/1","id":"0d000000-0000-4000-8000-000000000000","profile":{"name":"No size"},"items":[]}"""),
            (E, DocJson(E, Scene("a"))));
        File.WriteAllBytes(Paths.DocumentFile(E), new byte[DocumentStore.ReadCap(workspace.Limits) + 1]);

        var entries = workspace.List().ToDictionary(e => e.Id);
        Assert.Equal("ok", entries[A].Status);
        Assert.Equal(("unsupported", "From later", "unsupported.format", "/format"),
            (entries[B].Status, entries[B].Name, entries[B].Problem!.Code, entries[B].Problem!.Path));
        Assert.Equal(("unreadable", "json"), (entries[C].Status, entries[C].Problem!.Code));
        Assert.Equal(("unreadable", "schema"), (entries[D].Status, entries[D].Problem!.Code));
        Assert.Null(entries[D].Name);
        Assert.Equal(("unreadable", "limit.documentBytes"), (entries[E].Status, entries[E].Problem!.Code));

        foreach (var id in new[] { B, C, D, E })
        {
            var found = workspace.Find(id);
            Assert.True(found.Found);
            Assert.Null(found.Document);
            Assert.Equal((409, entries[id].Status, entries[id].Problem), (found.Problem!.Status, found.Problem.Code, found.Problem.Problem));
        }

        Assert.False(workspace.Find(Guid.NewGuid().ToString()).Found);
        Assert.False(workspace.Find("../x").Found);
    }

    [Fact]
    public void AnUnsupportedProfile_IsNotExported_AndNeverRewritten()
    {
        var future = "{ \"format\": \"infopanel.document/2\" ,\n \"id\":\"0a000000-0000-4000-8000-000000000000\", \"x\": [1.50] }";
        var workspace = With((A, future));

        // Not exported: its status problem (409), as every other request on a profile the engine can't open.
        var (exported, problem) = workspace.Export(A);
        Assert.Null(exported);
        Assert.Equal((409, "unsupported", "unsupported.format", "/format"), (problem!.Status, problem.Code, problem.Problem.Code, problem.Problem.Path));
        Assert.Equal(future, File.ReadAllText(Paths.DocumentFile(A)));
        Assert.True(workspace.DeleteWithItsTag(A));
    }

    [Fact]
    public void AnExport_AnswersTheStatusOfTheProfileItFound_WhenItIsDeletedAndCreatedAgainMeanwhile()
    {
        var future = """{"format":"infopanel.document/2","id":"0a000000-0000-4000-8000-000000000000"}""";
        var workspace = With((A, future));
        Task<CreateOutcome>? replaced = null;
        workspace.ExportFound = id =>
        {
            workspace.ExportFound = null;
            // The unsupported profile is found: another request deletes it and creates a profile under its id.
            replaced = Alongside(workspace, () =>
            {
                Assert.True(workspace.DeleteWithItsTag(id));
                return workspace.Create(CreateBody(id, "Replacement"));
            });
        };

        var (exported, problem) = workspace.Export(A);
        Assert.Null(exported);
        Assert.Equal(("unsupported", "unsupported.format"), (problem!.Code, problem.Problem.Code));
        Assert.Equal(201, replaced!.Result.Status);
        Assert.Equal("Replacement", workspace.Open(A)!.Current.Document.Profile.Name);
    }

    [Fact]
    public void AStoredDocumentWithABadScene_Opens_WithThatItemAsAPlaceholder()
    {
        var workspace = With((A, DocJson(A, Scene("good", 0, 0), Scene("bad", 50, 0, InvalidScene()))));

        var doc = workspace.Open(A)!;
        using var snapshot = SceneRuntime.RenderOnce(SpecJson.Write(doc.Current.Document), Start, new SceneRuntimeOptions { Support = workspace.Support });
        Assert.Equal([null, "invalid"], snapshot.Bounds.Select(b => b.Problem));

        // The bad scene is kept as the same JSON value, exported in the canonical form the store writes (members sorted).
        var (exported, _) = workspace.Export(A);
        Assert.Equal(Canonical(InvalidScene()), JsonNode.Parse(exported!)!["items"]![1]!["scene"]!.ToJsonString());
    }

    [Fact]
    public void Create_Empty_FromADocument_Exists_Invalid()
    {
        var workspace = With();
        var empty = workspace.Create(Bytes(new JsonObject { ["id"] = A, ["name"] = "Empty", ["width"] = 64, ["height"] = 32 }));
        Assert.Equal((201, "Empty", 64, 32, 1L, false), (empty.Status, empty.Entry!.Name, empty.Entry.Width, empty.Entry.Height, empty.Entry.Rev, empty.Entry.Dirty));
        Assert.Empty(workspace.Open(A)!.Current.Document.Items);
        Assert.True(File.Exists(Paths.DocumentFile(A))); // stored and saved at once

        Assert.Equal((409, "exists"), (workspace.Create(Bytes(new JsonObject { ["id"] = A, ["name"] = "Again", ["width"] = 1, ["height"] = 1 })).Status,
            workspace.Create(Bytes(new JsonObject { ["id"] = A, ["name"] = "Again", ["width"] = 1, ["height"] = 1 })).Exists!.Code));

        // From a document: its id and revisions replaced, a bad scene kept as a placeholder.
        var document = JsonNode.Parse(DocJson("ffffffff-0000-4000-8000-000000000000", Scene("ok"), Scene("bad", 1, 1, InvalidScene())))!.AsObject();
        document["rev"] = 99;
        var created = workspace.Create(Bytes(new JsonObject { ["id"] = B, ["document"] = document }));
        Assert.Equal(201, created.Status);
        var doc = workspace.Open(B)!;
        Assert.Equal((B, 1L), (doc.Id, doc.Rev));
        Assert.Equal(2, doc.Current.Document.Items.Count);

        // Refused: a body that isn't JSON, breaks its schema, or a document the engine wouldn't open.
        Assert.Equal("json", workspace.Create("{"u8.ToArray()).Invalid!.Errors[0].Code);
        Assert.Equal("schema", workspace.Create(Bytes(new JsonObject { ["id"] = "NOT-AN-ID", ["name"] = "x", ["width"] = 1, ["height"] = 1 })).Invalid!.Errors[0].Code);
        Assert.Equal(422, workspace.Create(Bytes(new JsonObject { ["id"] = "0c000000-0000-4000-8000-000000000000", ["name"] = "x", ["width"] = 0, ["height"] = 1 })).Status);
        var future = workspace.Create(Bytes(new JsonObject { ["id"] = "0c000000-0000-4000-8000-000000000000", ["document"] = JsonNode.Parse("""{"format":"infopanel.document/2"}""") }));
        Assert.Equal(("unsupported.format", "/document/format"), (future.Invalid!.Errors[0].Code, future.Invalid.Errors[0].Path));
        Assert.Equal("schema", workspace.Create(Bytes(new JsonObject { ["id"] = "0c000000-0000-4000-8000-000000000000", ["document"] = document.DeepClone(), ["extra"] = 1 })).Invalid!.Errors[0].Code);
        Assert.Equal([A, B], workspace.List().Select(e => e.Id));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("[1, 2e400]")]
    public void Create_And_TheImportCheck_ReadTheDocumentsNumbers_BeforeReplacingItsId(string id)
    {
        // The document's own id is replaced by the request's, but only after its numbers are read: a number that rounds
        // to ±∞ is json at /document/id, as anywhere else in the document (spec/docs/validation.md#numbers).
        var workspace = With();
        var document = JsonNode.Parse(DocJson("ffffffff-0000-4000-8000-000000000000", Scene("ok")))!.AsObject();
        document["id"] = JsonNode.Parse(id);
        var path = id.StartsWith('[') ? "/document/id/1" : "/document/id";

        var created = workspace.Create(Bytes(new JsonObject { ["id"] = A, ["document"] = document }));
        Assert.Equal(422, created.Status);
        Assert.Equal(("json", path), (created.Invalid!.Errors[0].Code, created.Invalid.Errors[0].Path));
        Assert.Empty(workspace.List());

        var check = workspace.CheckImportDocument(Element(document));
        Assert.Equal(("json", path), (Assert.Single(check).Code, check[0].Path));
    }

    [Fact]
    public void ACreatedDocumentsOwnId_IsOptionalAndIgnored_ButCheckedWhenGiven()
    {
        // importDocument: the document's id is replaced by the request's, so it may be left out; one that is given is
        // still a document id, checked as rev is (it was replaced first, so id: 7 passed).
        var workspace = With();
        foreach (var bad in new JsonNode[] { 7, "NOT-AN-ID" })
        {
            var document = JsonNode.Parse(DocJson("ffffffff-0000-4000-8000-000000000000", Scene("ok")))!.AsObject();
            document["id"] = bad;
            var created = workspace.Create(Bytes(new JsonObject { ["id"] = A, ["document"] = document }));
            Assert.Equal((422, "schema /document/id"), (created.Status, Show(created.Invalid!.Errors)));
            Assert.Equal("schema /document/id", Show(workspace.CheckImportDocument(Element(document))));
            Assert.Empty(workspace.List());
        }

        var withoutId = JsonNode.Parse(DocJson("ffffffff-0000-4000-8000-000000000000", Scene("ok")))!.AsObject();
        withoutId.Remove("id");
        Assert.Empty(workspace.CheckImportDocument(Element(withoutId)));
        var made = workspace.Create(Bytes(new JsonObject { ["id"] = A, ["document"] = withoutId }));
        Assert.Equal(201, made.Status);
        Assert.Equal(A, workspace.Open(A)!.Current.Document.Id);
    }

    private static JsonElement Raw(string json)
    {
        using var parsed = JsonDocument.Parse(json, SpecJson.DocumentOptions);
        return parsed.RootElement.Clone();
    }

    /// <summary>The document's text with its first <c>"id":</c> (the root's) replaced.</summary>
    private static string RootId(string json, string with)
    {
        var at = json.IndexOf("\"id\":", StringComparison.Ordinal);
        return json[..at] + with + json[(at + 5)..];
    }

    private static string Show(IEnumerable<ValidationErrorBody> errors) => string.Join("; ", errors.Select(e => $"{e.Code} {e.Path}"));

    private static string Show(IEnumerable<SpecError> errors) => string.Join("; ", errors.Select(e => $"{e.Code} {e.Path}"));

    [Fact]
    public void ACreatesIdWithATrailingNewline_IsASchemaError()
    {
        // .NET's $ matches before a final newline: the id passed the schema and failed in the store (a 500).
        var workspace = With();
        var empty = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{A}}\n", "name": "x", "width": 1, "height": 1}"""));
        Assert.Equal((422, "schema /id"), (empty.Status, Show(empty.Invalid!.Errors)));
        var document = DocJson(B, Scene("a"));
        var fromDocument = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{A}}\n", "document": {{document}}}"""));
        Assert.Equal((422, "schema /id"), (fromDocument.Status, Show(fromDocument.Invalid!.Errors)));
        Assert.Empty(workspace.List());
    }

    [Fact]
    public void ACreateWithRepeatedDocumentMembers_IsRefused_EveryOccurrenceRead()
    {
        // TryGetProperty takes the last one and removing "document" from the envelope dropped them all, unread.
        var workspace = With();
        var document = DocJson(B, Scene("a"));
        var twice = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{A}}", "document": {{document}}, "document": {{document}}}"""));
        Assert.Equal((422, "schema "), (twice.Status, Show(twice.Invalid!.Errors)));
        var infinite = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{A}}", "document": 1e400, "document": {{document}}}"""));
        Assert.Equal((422, "schema ; json /document"), (infinite.Status, Show(infinite.Invalid!.Errors)));
        Assert.Empty(workspace.List());
    }

    [Fact]
    public void RepeatedIds_AreRefused_BeforeTheIdIsReplaced()
    {
        // A document's id is replaced by the request's or the folder's, but only after the document is read as received:
        // two "id" members are a repeated member (schema, at the document), not an ambiguity the replacement settles.
        const string C = "0c000000-0000-4000-8000-000000000000";
        var workspace = With(
            (A, RootId(DocJson(B, Scene("a")), $"\"id\":\"{C}\",\"id\":")),
            (C, RootId(DocJson(B, Scene("a")), "\"id\":1e400,\"id\":")));
        var entries = workspace.List().ToDictionary(e => e.Id);
        Assert.Equal(("unreadable", "schema", ""), (entries[A].Status, entries[A].Problem!.Code, entries[A].Problem!.Path));
        Assert.Equal(("unreadable", "schema", ""), (entries[C].Status, entries[C].Problem!.Code, entries[C].Problem!.Path));

        var document = RootId(DocJson(B, Scene("a")), $"\"id\":\"{C}\",\"id\":");
        var created = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{B}}", "document": {{document}}}"""));
        Assert.Equal((422, "schema /document"), (created.Status, Show(created.Invalid!.Errors)));
        Assert.Equal("schema /document", Show(workspace.CheckImportDocument(Raw(document))));
        Assert.Null(workspace.Entry(B));
    }

    [Fact]
    public void ADocumentTooDeepInTheIdItsCreateReplaces_IsRefused()
    {
        // The depth failure is kept, whatever is rewritten afterwards.
        var workspace = With();
        var deep = new string('[', 256) + "1e400" + new string(']', 256);
        var document = DocJson(B, Scene("a")).Replace($"\"id\":\"{B}\"", $"\"id\":{deep}");
        var created = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{A}}", "document": {{document}}}"""));
        Assert.Equal((422, "limit.jsonDepth /document"), (created.Status, Show(created.Invalid!.Errors)));
        Assert.Equal("limit.jsonDepth /document", Show(workspace.CheckImportDocument(Raw(document))));
        Assert.Empty(workspace.List());
    }

    [Fact]
    public void LoneSurrogates_InACreateOrAStoredDocument_AreJson()
    {
        // GetString and a member's Name threw on them, outside every handler.
        var workspace = With((A, DocJson(B, Scene("a")).Replace($"\"id\":\"{B}\"", "\"id\":\"\\ud800\"")));
        var entry = Assert.Single(workspace.List());
        Assert.Equal(("unreadable", "json", "/id"), (entry.Status, entry.Problem!.Code, entry.Problem!.Path));

        var name = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{B}}", "\udc00": 1, "name": "x", "width": 1, "height": 1}"""));
        Assert.Equal((422, "json "), (name.Status, Show(name.Invalid!.Errors)));
        var value = workspace.Create(Encoding.UTF8.GetBytes($$"""{"id": "{{B}}", "name": "x\ud800", "width": 1, "height": 1}"""));
        Assert.Equal((422, "json /name"), (value.Status, Show(value.Invalid!.Errors)));
    }

    [Fact]
    public void TheImportCheck_ReadsTheJsonBeforeTheFormat()
    {
        // The order of validation.md: the JSON-level pass, then the format and its major version.
        var workspace = With();
        Assert.Equal("json /document/x", Show(workspace.CheckImportDocument(Raw("""{"format": "infopanel.document/2", "x": 1e400}"""))));
        var created = workspace.Create(Encoding.UTF8.GetBytes($$$"""{"id": "{{{A}}}", "document": {"format": "infopanel.document/2", "x": 1e400}}"""));
        Assert.Equal("json /document/x", Show(created.Invalid!.Errors));
    }

    [Fact]
    public void Duplicate_CopiesTheCurrentDocument_WithAFreshHistory()
    {
        var workspace = With((A, DocJson(A, Scene("a"))));
        var source = workspace.Open(A)!;
        source.Ok(Commit(source, "move", null, Place("a", 40, 40)));

        var copy = workspace.Duplicate(A, Bytes(new JsonObject { ["id"] = B }));
        Assert.Equal(201, copy.Status);
        var doc = workspace.Open(B)!;
        Assert.Equal((1L, false, false, false), (doc.Status.Rev, doc.Status.Dirty, doc.Status.CanUndo, doc.Status.CanRedo));
        Assert.Equal(40, ((SceneItem)doc.Current.Document.Items[0]).Placement.X);

        Assert.Equal(409, workspace.Duplicate(A, Bytes(new JsonObject { ["id"] = B })).Status);
        Assert.Equal(404, workspace.Duplicate("0c000000-0000-4000-8000-000000000000", Bytes(new JsonObject { ["id"] = "0d000000-0000-4000-8000-000000000000" })).Status);
        Assert.Equal(422, workspace.Duplicate(A, Bytes(new JsonObject { ["id"] = B, ["name"] = "x" })).Status);
    }

    [Fact]
    public void Delete_DisposesTheOpenDocument_AndRaisesProfilesChanged()
    {
        var workspace = With((A, DocJson(A, Scene("a"))));
        var doc = workspace.Open(A)!;
        var versions = new List<long>();
        workspace.ProfilesChanged += (_, e) => versions.Add(e.Version);

        Assert.True(workspace.DeleteWithItsTag(A));
        Assert.False(workspace.IsOpen(doc));
        Assert.Throws<ObjectDisposedException>(() => doc.Save());
        Assert.False(workspace.DeleteWithItsTag(A));
        Assert.False(workspace.Find(A).Found);
        Assert.Equal([1L], versions);
    }

    [Fact]
    public void EveryProfile_HasATagOfItsOwn_KeptWhileItLives_AndNewWhenItsIdIsCreatedAgainOrReopened()
    {
        var workspace = With((A, DocJson(A, Scene("a"))));
        var tag = workspace.Entry(A)!.Instance;
        Assert.Equal(tag, workspace.Open(A)!.Instance);
        Assert.NotEqual(workspace.Instance, tag);
        var created = workspace.Create(CreateBody(B, "B")).Entry!.Instance;
        Assert.Equal(created, workspace.Open(B)!.Instance);
        Assert.NotEqual(tag, created);

        // Another tag deletes nothing: a conflict of the whole request, at the document's revision.
        var stale = workspace.Delete(A, created);
        Assert.False(stale.Deleted);
        Assert.Equal((1L, "instance"), (stale.Conflict!.Rev, Assert.Single(stale.Conflict.Conflicts).Reason));
        Assert.True(workspace.IsOpen(workspace.Open(A)!));

        Assert.True(workspace.Delete(A, tag).Deleted);
        Assert.Equal(201, workspace.Create(CreateBody(A, "Again")).Status);
        var again = workspace.Entry(A)!.Instance;
        Assert.NotEqual(tag, again);
        // A commit built on the profile before it: refused for its tag.
        var retry = JsonNode.Parse(Commit(1, "after-recreate", null, Place("a", 1, 1)).GetRawText())!.AsObject();
        retry["instance"] = tag;
        Assert.Equal("instance", Assert.Single(workspace.Open(A)!.Commit(Element(retry)).Conflict!.Conflicts).Reason);
        Assert.False(workspace.Delete(A, tag).Deleted);

        // The next run opens every document again, under a new tag.
        workspace.Dispose();
        Assert.NotEqual(again, StartWorkspace().Entry(A)!.Instance);
    }

    [Fact]
    public void AProfileThatCantBeOpened_HasATag_AndOnlyItDeletesIt()
    {
        var workspace = With((A, """{"format":"infopanel.document/2","id":"0a000000-0000-4000-8000-000000000000"}"""));
        var entry = workspace.Entry(A)!;
        Assert.Equal("unsupported", entry.Status);
        Assert.Equal(entry.Instance, workspace.List().Single().Instance);

        var stale = workspace.Delete(A, entry.Instance + "0");
        Assert.Equal(((long?)null, "instance"), (stale.Conflict!.Rev, Assert.Single(stale.Conflict.Conflicts).Reason));
        Assert.True(workspace.Find(A).Found);

        Assert.True(workspace.Delete(A, entry.Instance).Deleted);
        Assert.False(workspace.Find(A).Found);
        Assert.Equal(DeleteOutcome.NotFound, workspace.Delete(A, entry.Instance));
    }

    [Fact]
    public void Blobs_AreStoredOnce_WithWhatTheMediaCheckFound()
    {
        var workspace = With();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAYAAAC56t6BAAAAEUlEQVR4nGP4z8DwHxkzEEcAAAt3Ef9Btr/EAAAAAElFTkSuQmCC");

        var first = workspace.PutBlob(png);
        Assert.True(first.Created);
        Assert.Equal(("png", 2, 3, 1, 0.0), (first.Info.Media!.Format, first.Info.Media.Width, first.Info.Media.Height, first.Info.Media.Frames, first.Info.Media.Duration));
        Assert.Null(first.Info.MediaError);
        Assert.False(workspace.PutBlob(png).Created);

        var other = workspace.PutBlob("a client's notes"u8.ToArray());
        Assert.True(other.Created);
        Assert.Equal(("", "media.format"), (other.Info.MediaError!.Path, other.Info.MediaError.Code));
        Assert.Equal(16, workspace.Blobs.Size(other.Info.Id));
        Assert.Equal("a client's notes"u8.ToArray(), new BlobMediaStore(workspace.Blobs).Read(other.Info.Id));
    }

    // ---- one lifetime per document: creates, deletes and what runs alongside them ---------------------------------------

    /// <summary>How long a test waits for a step another request must reach; only a failure takes it.</summary>
    private static readonly TimeSpan Failure = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="request"/> on another thread and returns once it has finished or waits for a lock this thread
    /// holds (<paramref name="waitingForLock"/> is the hook a lock tells it waits by): either way, it ran as far as it can
    /// while this thread holds what it holds.
    /// </summary>
    private static Task<T> Alongside<T>(Func<T> request, Action<Action?> waitingForLock)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waitingForLock(() => waiting.TrySetResult());
        var task = Task.Run(request);
        Assert.True(Task.WaitAny([task, waiting.Task], Failure) >= 0, "The other request neither finished nor waited for the lock.");
        waitingForLock(null);
        return task;
    }

    private static Task<T> Alongside<T>(Workspace workspace, Func<T> request) => Alongside(request, hook => workspace.WaitingForLock = hook);

    private Workspace Over(DocumentStore documents, BlobStore blobs, SpecLimits? limits = null)
    {
        documents.Open();
        blobs.Open();
        var workspace = new Workspace(documents, blobs, new WorkspaceOptions
        {
            Instance = Instance,
            HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes),
            Limits = limits ?? SpecLimits.Default,
        });
        _open.Add(workspace);
        return workspace;
    }

    private static byte[] CreateBody(string id, string name) =>
        Bytes(new JsonObject { ["id"] = id, ["name"] = name, ["width"] = 64, ["height"] = 32 });

    [Fact]
    public void ACreate_PublishesItsDocument_BeforeAnotherRequestCanOpenTheStoredProfile()
    {
        var workspace = With();
        Task<ProfileDocument?>? found = null;
        workspace.StoreChanged = id =>
        {
            // The profile is in the store, its document not yet in its slot: a request on it now must wait for both.
            found = Alongside(workspace, () => workspace.Find(id).Document);
        };

        Assert.Equal(201, workspace.Create(CreateBody(A, "Created")).Status);
        workspace.StoreChanged = null;

        // The request got the published document, so its edit is the profile's.
        var doc = found!.Result!;
        Assert.Same(workspace.Open(A), doc);
        doc.Ok(Commit(doc, "rename", null, Profile(new JsonObject { ["name"] = "Edited" })));
        Assert.Equal(("Edited", 2L, true), (workspace.Entry(A)!.Name, workspace.Entry(A)!.Rev, workspace.Entry(A)!.Dirty));
    }

    [Fact]
    public void ADeletedDocumentsSave_NeverWritesTheProfileCreatedAgainUnderItsId()
    {
        var workspace = With((A, DocJson(A, Scene("a"))));
        var old = workspace.Open(A)!;
        old.Ok(Commit(old, "rename", null, Profile(new JsonObject { ["name"] = "Unsaved" })));

        Task<CreateOutcome>? created = null;
        Exception? saved = null;
        workspace.StoreChanged = id =>
        {
            workspace.StoreChanged = null;
            // Between the store's delete and the old document's disposal: a create of the same id, then the old
            // document's save (the order the requests could arrive in).
            created = Alongside(workspace, () => workspace.Create(CreateBody(id, "Replacement")));
            saved = Record.Exception(() => old.Save());
        };

        Assert.True(workspace.DeleteWithItsTag(A));
        Assert.Equal(201, created!.Result.Status);
        Assert.IsType<KeyNotFoundException>(saved);

        // The replacement is the profile, open and stored as created; the old document is gone for good.
        var replacement = workspace.Open(A)!;
        Assert.NotSame(old, replacement);
        Assert.Equal("Replacement", replacement.Current.Document.Profile.Name);
        Assert.Equal("Replacement", JsonNode.Parse(File.ReadAllText(Paths.DocumentFile(A)))!["profile"]!["name"]!.GetValue<string>());
        Assert.Throws<ObjectDisposedException>(() => old.Save());
        replacement.Save();
    }

    [Fact]
    public void ADeleteThatFailsAfterTheStoreLetGo_StillDisposesTheDocument()
    {
        // The folder is renamed away and unlisted, then flushing profiles/ fails: the store throws, the profile is gone.
        var faults = new MatchFaults();
        var workspace = Over(new DocumentStore(Paths, null, faults), new BlobStore(Paths));
        Assert.Equal(201, workspace.Create(CreateBody(A, "Doomed")).Status);
        var doc = workspace.Open(A)!;
        var versions = new List<long>();
        workspace.ProfilesChanged += (_, e) => versions.Add(e.Version);
        Assert.Equal(1, workspace.Version);

        var renamed = false;
        faults.Fail = (step, path) =>
        {
            if (step == StorageStep.MoveFolder) renamed = true;
            return renamed && step == StorageStep.SyncFolder && path == Paths.ProfilesDirectory;
        };
        Assert.Throws<IOException>(() => workspace.DeleteWithItsTag(A));
        faults.Fail = null;

        Assert.False(workspace.Documents.Contains(A));
        Assert.False(workspace.Find(A).Found);
        Assert.False(workspace.IsOpen(doc));
        Assert.Throws<ObjectDisposedException>(() => doc.Commit(Commit(doc, "ghost", null, Profile(new JsonObject { ["name"] = "Ghost" }))));
        Assert.Equal([2L], versions);
        Assert.Empty(workspace.List());
    }

    /// <summary>Writes JSON text without escaping non-ASCII characters (é is two bytes, not the six of an escape).</summary>
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The canonical form of a stored file's JSON (spec/docs/limits.md#sizes), as text.</summary>
    private static string CanonicalOf(byte[] file) => global::InfoPanel.Spec.CanonicalJson.Write(Raw(Encoding.UTF8.GetString(file)));

    [Fact]
    public void ACreate_IsMeasuredInTheFormItIsStoredIn_NotAsSent()
    {
        // documentBytes measures the canonical form, and the store writes that form, so the size checked is the size
        // stored. 4,000 é sent escaped are 24,000 bytes as sent but 8,000 measured and stored, under a 16,000-byte limit.
        var limits = SpecLimits.Default with { DocumentBytes = 16_000 };
        var workspace = Over(new DocumentStore(Paths, limits), new BlobStore(Paths, limits), limits);
        var accented = new JsonObject { ["format"] = "infopanel.scene/2", ["text"] = new string('é', 4_000) };
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = A, ["document"] = JsonNode.Parse(DocJson(A, Scene("wide", 0, 0, accented))) }.ToJsonString());
        Assert.True(body.Length > limits.DocumentBytes);

        var created = workspace.Create(body);
        Assert.True(created.Status == 201, Show(created.Invalid?.Errors ?? []));
        var stored = File.ReadAllBytes(Paths.DocumentFile(A));
        Assert.Equal(CanonicalOf(stored), Encoding.UTF8.GetString(stored));
        Assert.Equal(stored.LongLength, workspace.Open(A)!.Current.Content.Bytes);
        Assert.True(stored.Length < 9_000, $"{stored.Length} bytes stored");

        // 800 numbers spelled 1e20: 4,000 bytes as sent, but each is 21 digits in the canonical form, over the limit.
        var spelled = new JsonObject { ["format"] = "infopanel.scene/2", ["n"] = new JsonArray(Enumerable.Range(0, 800).Select(_ => JsonNode.Parse("1e20")).ToArray()) };
        var expanded = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = B, ["document"] = JsonNode.Parse(DocJson(B, Scene("wide", 0, 0, spelled))) }.ToJsonString());
        Assert.True(expanded.Length < limits.DocumentBytes / 2);
        var refused = workspace.Create(expanded);
        Assert.Equal(422, refused.Status);
        Assert.Equal("limit.documentBytes /document", Show(refused.Invalid!.Errors));
        Assert.False(workspace.Documents.Contains(B));
        Assert.False(Directory.Exists(Paths.ProfileFolder(B)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AThousandAccentsInAnOrigin_PassOriginBytes_AndTheDocumentReopensAfterARestart(bool sentEscaped)
    {
        // 1,000 é are 2,000 bytes measured (originBytes is 4 KiB) whether sent as UTF-8 or as six-byte escapes. The store
        // wrote them escaped, so an origin accepted at 2,000 bytes came back at 6,000 and the document didn't open again.
        var workspace = With();
        var origin = new JsonObject { ["note"] = new string('é', 1_000) };
        byte[] Send(JsonNode body) => Encoding.UTF8.GetBytes(sentEscaped ? body.ToJsonString() : body.ToJsonString(Unescaped));

        var document = JsonNode.Parse(DocJson(A, Scene("a", extra: new JsonObject { ["origin"] = origin.DeepClone() })));
        var created = workspace.Create(Send(new JsonObject { ["id"] = A, ["document"] = document }));
        Assert.True(created.Status == 201, Show(created.Invalid?.Errors ?? []));

        // The same origin through a commit, then saved.
        var doc = workspace.Open(A)!;
        var insert = JsonNode.Parse(Commit(doc, "insert", null, Insert(Scene("b", 5, 5, extra: new JsonObject { ["origin"] = origin.DeepClone() }))).GetRawText())!;
        var outcome = doc.Commit(Send(insert));
        Assert.True(outcome.Result is not null, Describe(outcome));
        doc.Save();

        workspace.Dispose();
        var restarted = StartWorkspace();
        var entry = Assert.Single(restarted.List());
        Assert.True(entry.Status == "ok", $"{entry.Status}: {entry.Problem?.Code} {entry.Problem?.Path} {entry.Problem?.Message}");
        var reopened = restarted.Open(A)!.Current;
        foreach (var id in new[] { "a", "b" })
            Assert.Equal(new string('é', 1_000), reopened.Item(id).Origin!.Value.GetProperty("note").GetString());
        var stored = File.ReadAllBytes(Paths.DocumentFile(A));
        Assert.Equal(CanonicalOf(stored), Encoding.UTF8.GetString(stored));
        Assert.Equal(stored.LongLength, reopened.Content.Bytes);
    }

    [Fact]
    public void ADocumentStoredBeforeTheCanonicalForm_Opens_AndItsNextSaveStoresItCanonical()
    {
        // What earlier engines stored: compact JSON with every non-ASCII character a six-byte escape and opaque numbers as
        // sent. This one's origin is 6,000 bytes as stored but 2,000 measured: it opens, and its next save writes the
        // canonical form, the size it was measured at.
        var item = Scene("a", 1.5, 2, extra: new JsonObject
        {
            ["origin"] = new JsonObject { ["z"] = JsonNode.Parse("1.50"), ["note"] = new string('é', 1_000) },
            ["name"] = "Café",
        });
        var earlier = JsonNode.Parse(DocJson(A, item))!.ToJsonString();
        Assert.Contains("\\" + "u00E9", earlier, StringComparison.Ordinal);
        var workspace = With((A, earlier));

        var entry = Assert.Single(workspace.List());
        Assert.True(entry.Status == "ok", $"{entry.Status}: {entry.Problem?.Code} {entry.Problem?.Path} {entry.Problem?.Message}");
        Assert.Equal(Encoding.UTF8.GetBytes(earlier), File.ReadAllBytes(Paths.DocumentFile(A)));

        var doc = workspace.Open(A)!;
        doc.Ok(Commit(doc, "move", null, Place("a", 3, 4)));
        doc.Save();
        var stored = File.ReadAllBytes(Paths.DocumentFile(A));
        Assert.Equal(CanonicalOf(stored), Encoding.UTF8.GetString(stored));
        Assert.Equal(stored.LongLength, doc.Current.Content.Bytes);
        Assert.True(stored.Length < earlier.Length - 3_900, $"{earlier.Length} bytes before, {stored.Length} after");
        Assert.Contains("\"z\":1.5}", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentStoredBeforeTheCanonicalForm_CanMeasureLarger_AndNoLongerOpen()
    {
        // The compatibility narrowing spec/docs/document.md states (allowed while the formats are a draft): an origin of
        // 200 numbers spelled 1e20 is 1,007 bytes as an earlier engine stored it, 4,407 in the canonical form, over the
        // 4,096 of originBytes, so its document is unreadable now.
        var origin = "{\"n\":[" + string.Join(",", Enumerable.Repeat("1e20", 200)) + "]}";
        Assert.Equal(1_007, Encoding.UTF8.GetByteCount(origin));
        Assert.Equal(4_407, Encoding.UTF8.GetByteCount(global::InfoPanel.Spec.CanonicalJson.Write(Raw(origin))));
        var stored = DocJson(A, Scene("a")).Replace("\"kind\":\"scene\"", "\"kind\":\"scene\",\"origin\":" + origin, StringComparison.Ordinal);
        Assert.Contains(origin, stored, StringComparison.Ordinal);

        var entry = Assert.Single(With((A, stored)).List());
        Assert.Equal(("unreadable", "limit.originBytes", "/items/0/origin"), (entry.Status, entry.Problem!.Code, entry.Problem.Path));
        Assert.Contains("4407", entry.Problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACreateIsCheckedWhole_BeforeItsIdIsFoundTaken()
    {
        var workspace = With((A, DocJson(A, Scene("a"))));
        var future = workspace.Create(Bytes(new JsonObject { ["id"] = A, ["document"] = JsonNode.Parse("""{"format":"infopanel.document/2"}""") }));
        Assert.Equal((422, "unsupported.format"), (future.Status, future.Invalid!.Errors[0].Code));
        Assert.Equal(409, workspace.Create(CreateBody(A, "Taken")).Status);
    }

    [Fact]
    public void AProfileOverDocumentBytesOnDisk_IsNotExported()
    {
        var limits = SpecLimits.Default with { DocumentBytes = 1_000 };
        Directory.CreateDirectory(Paths.ProfileFolder(A));
        File.WriteAllText(Paths.ProfileOrderFile, $$"""{"order":["{{A}}"]}""");
        var stored = DocJson(A, Enumerable.Range(0, 40).Select(i => Scene($"s{i}", i, i)).ToArray());
        Assert.True(stored.Length > limits.DocumentBytes);
        File.WriteAllText(Paths.DocumentFile(A), stored);
        var workspace = Over(new DocumentStore(Paths, limits), new BlobStore(Paths, limits), limits);

        Assert.Equal(("unreadable", "limit.documentBytes"), (workspace.Entry(A)!.Status, workspace.Entry(A)!.Problem!.Code));
        var (exported, problem) = workspace.Export(A);
        Assert.Null(exported);
        Assert.Equal((409, "unreadable", "limit.documentBytes"), (problem!.Status, problem.Code, problem.Problem.Code));
        Assert.Equal(stored, File.ReadAllText(Paths.DocumentFile(A)));
    }

    [Fact]
    public void StoringABlob_RepairsAFailedLookupOfIt()
    {
        var workspace = With();
        using var media = new InfoPanel.Images.MediaSampleCache(new BlobMediaStore(workspace.Blobs), new InfoPanel.Images.MediaSampleCacheOptions { Synchronous = true });
        workspace.BlobStored += media.Retry;
        var png = Png();
        var id = BlobStore.IdOf(png);

        var missing = media.Get(id, 0, InfoPanel.Images.MediaPlayback.Still);
        Assert.Equal(InfoPanel.Images.MediaState.Failed, missing.State);
        Assert.StartsWith("media.missing", missing.Problem);

        workspace.PutBlob(png);
        var found = media.Get(id, 0, InfoPanel.Images.MediaPlayback.Still);
        Assert.True(found.State == InfoPanel.Images.MediaState.Ready, found.Problem);
        found.Sample!.Release();
    }

    /// <summary>
    /// A media blob whose stored file is another image draws as missing media: the stored bytes never reach the decoder
    /// unchecked. Uploading the right bytes repairs it.
    /// </summary>
    [Fact]
    public void AMediaBlobThatDoesNotHashToItsId_DrawsAsMissingMedia_UntilItIsStoredAgain()
    {
        var workspace = With();
        using var media = new InfoPanel.Images.MediaSampleCache(new BlobMediaStore(workspace.Blobs), new InfoPanel.Images.MediaSampleCacheOptions { Synchronous = true });
        workspace.BlobStored += media.Retry;
        var png = Png();
        var id = workspace.PutBlob(png).Info.Id;
        var file = Paths.BlobFile(id);
        var written = File.GetLastWriteTimeUtc(file);
        File.WriteAllBytes(file, Png(SkiaSharp.SKColors.Blue));
        File.SetLastWriteTimeUtc(file, written.AddSeconds(1));
        var document = InfoPanel.Core.Tests.Scenes.SceneKit.Doc(InfoPanel.Core.Tests.Scenes.SceneKit.Json(
            $"{{'format':'infopanel.scene/1','nodes':[{{'kind':'media','w':2,'h':3,'source':{{'asset':'{id}'}}}}]}}"), 2, 3);
        var options = InfoPanel.Core.Tests.Scenes.SceneKit.Options(media: media);

        using (var corrupt = SceneRuntime.RenderOnce(document, InfoPanel.Core.Tests.Scenes.SceneKit.Now, options))
        {
            Assert.Contains(corrupt.Diagnostics, d => d.Code == "media.missing");
            Assert.Equal(0, corrupt.SampleCount);
        }

        workspace.PutBlob(png);
        using (var repaired = SceneRuntime.RenderOnce(document, InfoPanel.Core.Tests.Scenes.SceneKit.Now, options))
        {
            Assert.DoesNotContain(repaired.Diagnostics, d => d.Code.StartsWith("media.", StringComparison.Ordinal));
            Assert.Equal(1, repaired.SampleCount);
            using var drawn = repaired.Render();
            Assert.Equal(SkiaSharp.SKColors.Red, drawn.GetPixel(1, 1));
        }
    }

    [Fact]
    public void StoringABlob_RepairsALookupOfItThatFoundItMissingJustBefore()
    {
        var workspace = With();
        var png = Png();
        var id = BlobStore.IdOf(png);
        // The lookup reads the store before the blob is there; the upload completes before the lookup says it failed.
        var store = new ReadThen(new BlobMediaStore(workspace.Blobs), () => workspace.PutBlob(png));
        using var media = new InfoPanel.Images.MediaSampleCache(store, new InfoPanel.Images.MediaSampleCacheOptions { Synchronous = true });
        workspace.BlobStored += media.Retry;

        Assert.Equal(InfoPanel.Images.MediaState.Failed, media.Get(id, 0, InfoPanel.Images.MediaPlayback.Still).State);
        Assert.True(store.Done);

        var found = media.Get(id, 0, InfoPanel.Images.MediaPlayback.Still);
        Assert.True(found.State == InfoPanel.Images.MediaState.Ready, found.Problem);
        found.Sample!.Release();
    }

    /// <summary>A media store that runs <paramref name="then"/> once, after its first read, before that read returns.</summary>
    private sealed class ReadThen(InfoPanel.Images.IMediaAssetStore store, Action then) : InfoPanel.Images.IMediaAssetStore
    {
        public bool Done { get; private set; }

        public byte[]? Read(string hash)
        {
            var bytes = store.Read(hash);
            if (!Done)
            {
                Done = true;
                then();
            }
            return bytes;
        }
    }

    private static byte[] Png() => Png(SkiaSharp.SKColors.Red);

    private static byte[] Png(SkiaSharp.SKColor color)
    {
        using var bitmap = new SkiaSharp.SKBitmap(2, 3);
        bitmap.Erase(color);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void OfConcurrentIdenticalUploads_ExactlyOneIsNew()
    {
        var bytes = "stored twice at once"u8.ToArray();
        Workspace? workspace = null;
        BlobStore? blobs = null;
        Task<StoredBlob>? second = null;
        var faults = new MatchFaults
        {
            Fail = (step, _) =>
            {
                // The first upload is writing the blob: the second arrives.
                if (step == StorageStep.CreateTemp && second is null)
                    second = Alongside(() => workspace!.PutBlob(bytes), hook => blobs!.WaitingForLock = hook);
                return false;
            },
        };
        blobs = new BlobStore(Paths, null, faults);
        workspace = Over(new DocumentStore(Paths), blobs);

        var first = workspace.PutBlob(bytes);
        Assert.Equal([true, false], new[] { first.Created, second!.Result.Created });
    }

    [Fact]
    public void ABlobFileOverMediaBytes_IsNoMedia_AndIsNotRead()
    {
        var limits = SpecLimits.Default with { MediaBytes = 64 };
        var blobs = new BlobStore(Paths, limits);
        var workspace = Over(new DocumentStore(Paths, limits), blobs, limits);
        var content = new byte[65];
        var id = BlobStore.IdOf(content);
        Directory.CreateDirectory(Paths.BlobsDirectory);
        File.WriteAllBytes(Paths.BlobFile(id), content);

        Assert.Null(new BlobMediaStore(workspace.Blobs).Read(id));
        Assert.Equal(64, new BlobMediaStore(workspace.Blobs).Read(workspace.PutBlob(new byte[64]).Info.Id)!.Length);
    }
}
