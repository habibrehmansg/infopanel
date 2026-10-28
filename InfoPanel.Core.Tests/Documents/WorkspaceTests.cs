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

    private static byte[] ReadAll(ExportedDocument exported)
    {
        using (exported)
        using (var copy = new MemoryStream())
        {
            exported.Content.CopyTo(copy);
            return copy.ToArray();
        }
    }

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

        Assert.True(workspace.Delete(ExampleId));
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
        File.WriteAllBytes(Paths.DocumentFile(E), new byte[workspace.Limits.DocumentBytes + 1]);

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
    public void AnUnsupportedProfile_IsExportedByteForByte_AndNeverRewritten()
    {
        var future = "{ \"format\": \"infopanel.document/2\" ,\n \"id\":\"0a000000-0000-4000-8000-000000000000\", \"x\": [1.50] }";
        var workspace = With((A, future));

        var (exported, problem) = workspace.Export(A);
        Assert.Null(problem);
        Assert.True(exported!.Stored);
        Assert.Equal(Encoding.UTF8.GetBytes(future), ReadAll(exported));
        Assert.Equal(future, File.ReadAllText(Paths.DocumentFile(A)));
        Assert.True(workspace.Delete(A));
    }

    [Fact]
    public void AnExport_StreamsTheFileOfTheProfileItFound_WhenItIsDeletedAndCreatedAgainMeanwhile()
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
                Assert.True(workspace.Delete(id));
                return workspace.Create(CreateBody(id, "Replacement"));
            });
        };

        var (exported, problem) = workspace.Export(A);
        Assert.Null(problem);
        Assert.True(exported!.Stored);
        Assert.Equal(Encoding.UTF8.GetBytes(future), ReadAll(exported));
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

        // The bad scene is kept exactly as stored.
        var (exported, _) = workspace.Export(A);
        Assert.Equal(InvalidScene().ToJsonString(), JsonNode.Parse(ReadAll(exported!))!["items"]![1]!["scene"]!.ToJsonString());
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

    [Fact]
    public void Duplicate_CopiesTheCurrentDocument_WithAFreshHistory()
    {
        var workspace = With((A, DocJson(A, Scene("a"))));
        var source = workspace.Open(A)!;
        source.Ok(Commit(source.Rev, "move", null, Place("a", 40, 40)));

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

        Assert.True(workspace.Delete(A));
        Assert.False(workspace.IsOpen(doc));
        Assert.Throws<ObjectDisposedException>(() => doc.Save());
        Assert.False(workspace.Delete(A));
        Assert.False(workspace.Find(A).Found);
        Assert.Equal([1L], versions);
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
        doc.Ok(Commit(doc.Rev, "rename", null, Profile(new JsonObject { ["name"] = "Edited" })));
        Assert.Equal(("Edited", 2L, true), (workspace.Entry(A)!.Name, workspace.Entry(A)!.Rev, workspace.Entry(A)!.Dirty));
    }

    [Fact]
    public void ADeletedDocumentsSave_NeverWritesTheProfileCreatedAgainUnderItsId()
    {
        var workspace = With((A, DocJson(A, Scene("a"))));
        var old = workspace.Open(A)!;
        old.Ok(Commit(old.Rev, "rename", null, Profile(new JsonObject { ["name"] = "Unsaved" })));

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

        Assert.True(workspace.Delete(A));
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
        Assert.Throws<IOException>(() => workspace.Delete(A));
        faults.Fail = null;

        Assert.False(workspace.Documents.Contains(A));
        Assert.False(workspace.Find(A).Found);
        Assert.False(workspace.IsOpen(doc));
        Assert.Throws<ObjectDisposedException>(() => doc.Commit(Commit(doc.Rev, "ghost", null, Profile(new JsonObject { ["name"] = "Ghost" }))));
        Assert.Equal([2L], versions);
        Assert.Empty(workspace.List());
    }

    [Fact]
    public void ACreateWhoseStoredBytesWouldBeOverDocumentBytes_IsRefused_AndNothingIsStored()
    {
        // 4,000 é: 8,000 bytes as sent, 24,000 once stored (escaped as \u00e9), over a 16,000-byte limit.
        var limits = SpecLimits.Default with { DocumentBytes = 16_000 };
        var workspace = Over(new DocumentStore(Paths, limits), new BlobStore(Paths, limits), limits);
        var scene = new JsonObject { ["format"] = "infopanel.scene/2", ["text"] = new string('\u00e9', 4_000) };
        var unescaped = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var body = Encoding.UTF8.GetBytes(new JsonObject { ["id"] = A, ["document"] = JsonNode.Parse(DocJson(A, Scene("wide", 0, 0, scene))) }.ToJsonString(unescaped));
        Assert.True(body.Length < limits.DocumentBytes);

        var outcome = workspace.Create(body);
        Assert.Equal(422, outcome.Status);
        Assert.Equal(("limit.documentBytes", "/document"), (outcome.Invalid!.Errors[0].Code, outcome.Invalid.Errors[0].Path));
        Assert.False(workspace.Documents.Contains(A));
        Assert.False(Directory.Exists(Paths.ProfileFolder(A)));
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
    public void AProfileOverDocumentBytesOnDisk_IsExportedAsStored_WithoutBeingRead()
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
        Assert.Null(problem);
        Assert.True(exported!.Stored);
        Assert.Equal(Encoding.UTF8.GetBytes(stored), ReadAll(exported));
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
