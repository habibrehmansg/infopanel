using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// A profile's earlier saves listed and restored through the workspace (spec/docs/api.md, "Earlier saves"): a restore is
/// one undoable step from the current content to the save's, written by Save's path under the save lock, so the save it
/// replaces is kept as the newest earlier one; unsaved changes, another client's included, are undone as one step that
/// Undo brings back; every refusal writes nothing; a save that can't be read or opened lists with its status and is
/// refused; a write that fails leaves the step, unsaved, kept by the recovery file. On a clock that moves only when the
/// test says.
/// </summary>
public sealed class RestoreTests : IDisposable
{
    private const string A = "0a000000-0000-4000-8000-000000000000";

    private readonly TempDirectory _temp = new();
    private readonly TimerTimeProvider _time = new(Start);
    private readonly List<Workspace> _open = [];

    private DataPaths Paths => new(_temp.Path);

    public void Dispose()
    {
        foreach (var workspace in _open)
            workspace.Dispose();
        _temp.Dispose();
    }

    private Workspace StartWorkspace(IStorageFaults? faults = null, SpecLimits? limits = null)
    {
        var workspace = Workspace.Start(Paths, new WorkspaceOptions
        {
            Instance = Instance,
            Time = _time,
            HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes),
            ExamplesWanted = static () => false,
            StorageFaults = faults,
            Limits = limits ?? SpecLimits.Default,
        }, Start);
        _open.Add(workspace);
        return workspace;
    }

    private static string DocJson(string id, params JsonObject[] items)
    {
        var doc = JsonNode.Parse(Doc(items).GetRawText())!.AsObject();
        doc["id"] = id;
        return doc.ToJsonString();
    }

    /// <summary>Creates profile A with <c>a</c> at x 10 and <c>b</c> at x 50, saved, and opens it.</summary>
    private static ProfileDocument Create(Workspace workspace)
    {
        var body = $"{{\"id\":\"{A}\",\"document\":{DocJson(A, Scene("a", 10, 10), Scene("b", 50, 10))}}}";
        var created = workspace.Create(Encoding.UTF8.GetBytes(body));
        Assert.True(created.Entry is not null, created.Invalid?.Errors.FirstOrDefault()?.Message);
        return workspace.Open(A)!;
    }

    private static CommitResult Move(ProfileDocument doc, double x, string item = "a") =>
        doc.Ok(Commit(doc, $"op-{Guid.NewGuid():N}", null, Place(item, x, 10)));

    /// <summary>Moves <c>a</c> to <paramref name="x"/> and saves.</summary>
    private static void SaveAt(ProfileDocument doc, double x)
    {
        Move(doc, x);
        doc.Save();
    }

    private static double X(Generation generation, string item = "a") => generation.Item(item).Placement.X;

    private static JsonElement Body(string instance, long baseRev) => SpecJson.Write(new HistoryRequest { Instance = instance, BaseRev = baseRev });

    /// <summary>Restores version <paramref name="n"/> as a client that has read the document's current state.</summary>
    private static CommitOutcome Restore(Workspace workspace, ProfileDocument doc, long n) =>
        Restore(workspace, n, Body(doc.Instance, doc.Rev));

    private static CommitOutcome Restore(Workspace workspace, long n, JsonElement body)
    {
        var found = workspace.FindVersion(A, n);
        Assert.True(found.Found && found.Version is not null, found.Problem?.Detail);
        return found.Document!.RestoreVersion(body, found.Version!);
    }

    private static long[] Numbers(Workspace workspace) => [.. workspace.Versions(A).List!.Versions.Select(v => v.N)];

    private byte[] Saved() => File.ReadAllBytes(Paths.DocumentFile(A));

    private byte[] VersionBytes(ulong n) => File.ReadAllBytes(Paths.VersionFile(A, n));

    private string[] VersionFiles() => Directory.Exists(Paths.VersionsFolder(A))
        ? [.. Directory.GetFileSystemEntries(Paths.VersionsFolder(A)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
        : [];

    [Fact]
    public void FromACleanDocument_TheVersionIsSaved_AndTheSaveItReplacesIsKeptAsTheNext()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20); // version 1: a at 10
        SaveAt(doc, 30); // version 2: a at 20
        var first = VersionBytes(1);
        var replaced = Saved();
        var rev = doc.Rev;

        var restored = Restore(workspace, doc, 1);

        var result = Assert.IsType<CommitResult>(restored.Result);
        Assert.Equal(rev + 1, result.Rev);
        Assert.Contains("a", result.Items.Keys);
        Assert.False(result.Dirty);
        Assert.True(result.CanUndo);
        Assert.False(doc.Dirty);
        Assert.Equal(10, X(doc.Current));
        Assert.Equal(first, Saved());
        Assert.Equal([3L, 2, 1], Numbers(workspace));
        Assert.Equal(replaced, VersionBytes(3));
    }

    [Fact]
    public void UnsavedChanges_AnotherClientsToo_AreUndoneAsOneStep_ThatUndoBringsBack_AndRedoRestoresAgain()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20); // version 1: a at 10, b at 50
        // Another client's unsaved changes, then a restore by a client that has read them.
        Move(doc, 70);
        Move(doc, 90, "b");
        Assert.True(doc.Dirty);

        var result = Restore(workspace, doc, 1).Result!;

        Assert.False(result.Dirty);
        Assert.Equal((10d, 50d), (X(doc.Current), X(doc.Current, "b")));
        Assert.Equal(VersionBytes(1), Saved());

        var undone = doc.UndoOwn();
        Assert.True(undone.Dirty);
        Assert.Equal((70d, 90d), (X(doc.Current), X(doc.Current, "b")));
        // Undo never unsaves: the restored save stays the saved one.
        Assert.Equal(10, X(doc.Saved));

        var redone = doc.RedoOwn();
        Assert.False(redone.Dirty);
        Assert.Equal((10d, 50d), (X(doc.Current), X(doc.Current, "b")));
    }

    [Fact]
    public void AClientThatHasntSeenAnotherClientsChange_IsRefused_AndNothingOfItIsLost()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20);
        var seen = doc.Rev;
        Move(doc, 70); // another client's change, after what this client read
        var saved = Saved();

        var refused = Restore(workspace, 1, Body(doc.Instance, seen));

        var conflict = Assert.IsType<ConflictProblem>(refused.Conflict);
        Assert.Contains(conflict.Conflicts, c => c.Reason == "changed" && c.Id == "a");
        Assert.Equal(70, X(doc.Current));
        Assert.True(doc.Dirty);
        Assert.Equal(saved, Saved());
        Assert.Equal(["1.json"], VersionFiles());
    }

    [Fact]
    public void TheRecoveryFileGoes_AndRecoveredEnds()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20);
        Move(doc, 70);
        workspace.Dispose(); // the orderly stop keeps the unsaved change
        Assert.True(File.Exists(Paths.RecoveryFile(A)));

        workspace = StartWorkspace();
        doc = workspace.Open(A)!;
        Assert.NotNull(doc.Recovered);

        Assert.False(Restore(workspace, doc, 1).Result!.Dirty);

        Assert.Null(doc.Recovered);
        Assert.False(File.Exists(Paths.RecoveryFile(A)));
        Assert.Equal(10, X(doc.Current));
    }

    [Fact]
    public void CommitsDuringTheWrite_LeaveItDirty_WithTheRecoveryFileKeptAgainstTheNewSave()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20);
        var once = false;
        workspace.Saving = _ =>
        {
            if (once)
                return;
            once = true;
            Move(doc, 99, "b");
        };

        var result = Restore(workspace, doc, 1).Result!;

        Assert.True(result.Dirty);
        Assert.True(doc.Dirty);
        Assert.Equal(VersionBytes(1), Saved());
        Assert.Equal((10d, 99d), (X(doc.Current), X(doc.Current, "b")));
        using var kept = JsonDocument.Parse(File.ReadAllBytes(Paths.RecoveryFile(A)));
        Assert.Equal(RecoveryFile.BaseOf(Saved()), kept.RootElement.GetProperty("base").GetString());
    }

    [Fact]
    public void TheInstance_AFutureBaseRev_AndAChangedDocument_RefuseIt_WithNothingWritten()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20);
        SaveAt(doc, 30);
        var saved = Saved();
        var files = VersionFiles();
        var rev = doc.Rev;

        Assert.Equal("instance", Assert.IsType<ConflictProblem>(Restore(workspace, 1, Body("another-instance", rev)).Conflict).Conflicts.Single().Reason);
        Assert.Equal("baseRev.future", Assert.IsType<InvalidProblem>(Restore(workspace, 1, Body(doc.Instance, rev + 1)).Invalid).Errors.Single().Code);
        Assert.Equal(422, Restore(workspace, 1, JsonSerializer.SerializeToElement(new { instance = doc.Instance })).Status);
        Move(doc, 40);
        Assert.Equal("changed", Assert.IsType<ConflictProblem>(Restore(workspace, 1, Body(doc.Instance, rev)).Conflict).Conflicts[0].Reason);

        Assert.Equal(saved, Saved());
        Assert.Equal(files, VersionFiles());
        Assert.Equal(rev + 1, doc.Rev);
        Assert.Equal(40, X(doc.Current));
    }

    [Fact]
    public void AVersionDroppedMeanwhile_IsNotFound()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20);
        File.Delete(Paths.VersionFile(A, 1));

        var found = workspace.FindVersion(A, 1);

        Assert.False(found.Found);
        Assert.Same(doc, found.Document);
        Assert.Null(found.Problem);
        Assert.Empty(workspace.Versions(A).List!.Versions);
        Assert.False(workspace.FindVersion("0b000000-0000-4000-8000-000000000000", 1).Found);
        Assert.Null(workspace.FindVersion("0b000000-0000-4000-8000-000000000000", 1).Document);
    }

    /// <summary>
    /// A save that isn't JSON, is over the read cap, holds a built-in key in the plugin form (a document from before the
    /// keys' own form, never converted), or needs a newer format lists with its status and problem, and is refused with
    /// that status and problem, nothing written; the profile itself stays ok.
    /// </summary>
    [Fact]
    public void AVersionThatCantBeOpened_ListsWithItsStatus_AndIsRefused_WithNothingWritten()
    {
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["documentBytes"] = 64 * 1024 });
        var workspace = StartWorkspace(limits: limits);
        var doc = Create(workspace);
        SaveAt(doc, 20);
        var folder = Paths.VersionsFolder(A);
        File.WriteAllText(Path.Combine(folder, "2.json"), "{ not json");
        File.WriteAllBytes(Path.Combine(folder, "3.json"), new byte[DocumentStore.ReadCap(limits) + 1]);
        var bound = Scene("k", 0, 0, InputScene(), new JsonObject { ["bindings"] = new JsonObject { ["v"] = new JsonObject { ["key"] = "plugin:/infopanel.mock/cpu/cpu-load" } } });
        File.WriteAllText(Path.Combine(folder, "4.json"), DocJson(A, bound));
        var future = JsonNode.Parse(DocJson(A, Scene("a")))!.AsObject();
        future["format"] = "infopanel.document/2";
        File.WriteAllText(Path.Combine(folder, "5.json"), future.ToJsonString());
        var saved = Saved();
        var files = VersionFiles();
        var rev = doc.Rev;

        var listed = workspace.Versions(A).List!.Versions;

        Assert.Equal([5L, 4, 3, 2, 1], listed.Select(v => v.N));
        Assert.Equal(("unsupported", "unsupported.format", "/format"), (listed[0].Status, listed[0].Problem!.Code, listed[0].Problem!.Path));
        Assert.Equal("Test", listed[0].Name);
        Assert.Equal(("unreadable", "key.pluginForm"), (listed[1].Status, listed[1].Problem!.Code));
        Assert.Equal(("unreadable", "limit.documentBytes"), (listed[2].Status, listed[2].Problem!.Code));
        Assert.Equal(("unreadable", "json"), (listed[3].Status, listed[3].Problem!.Code));
        Assert.All(listed.Take(4), v => Assert.Null(v.Width));
        Assert.Equal("ok", listed[4].Status);
        for (var n = 2; n <= 5; n++)
        {
            var found = workspace.FindVersion(A, n);
            Assert.True(found.Found);
            Assert.Null(found.Version);
            Assert.Equal(409, found.Problem!.Status);
            Assert.Equal((listed[5 - n].Status, listed[5 - n].Problem!.Code), (found.Problem.Code, found.Problem.Problem.Code));
        }

        Assert.Equal(saved, Saved());
        Assert.Equal(files, VersionFiles());
        Assert.Equal(rev, doc.Rev);
        Assert.Equal("ok", workspace.Entry(A)!.Status);
    }

    /// <summary>
    /// A versions folder holding many more saves than the store keeps (a pruning that failed, files made by hand): the
    /// list is the newest ten, each read once, by the one listing of the request.
    /// </summary>
    [Fact]
    public void ManyLeftoverSaves_ListOnlyTheNewestTen_EachReadOnce()
    {
        var workspace = StartWorkspace();
        Create(workspace);
        var folder = Directory.CreateDirectory(Paths.VersionsFolder(A)).FullName;
        var content = DocJson(A, Scene("a", 10, 10));
        for (var n = 1; n <= 40; n++)
            File.WriteAllText(Path.Combine(folder, $"{n}.json"), content);
        var read = new List<string>();
        workspace.Documents.ReadingVersion = read.Add;

        var listed = workspace.Versions(A).List!.Versions;

        Assert.Equal(Enumerable.Range(31, 10).Reverse().Select(n => (long)n), listed.Select(v => v.N));
        Assert.All(listed, v => Assert.Equal("ok", v.Status));
        Assert.Equal(Enumerable.Range(31, 10).Reverse().Select(n => Path.Combine(folder, $"{n}.json")), read);
    }

    /// <summary>
    /// A save there but not to be opened (its permissions) lists as <c>unreadable</c> with <c>io</c>, as its restore
    /// finds it, and one over the read cap with <c>limit.documentBytes</c>: each with a fixed message that names no path
    /// of the computer and no exception.
    /// </summary>
    [Fact]
    public void ASaveThatCantBeOpened_ListsAsUnreadable_WithAFixedMessage()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes: a file the engine may not open");
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["documentBytes"] = 64 * 1024 });
        var workspace = StartWorkspace(limits: limits);
        var doc = Create(workspace);
        SaveAt(doc, 20);
        var folder = Paths.VersionsFolder(A);
        File.WriteAllBytes(Path.Combine(folder, "2.json"), new byte[DocumentStore.ReadCap(limits) + 1]);
        File.WriteAllText(Path.Combine(folder, "3.json"), DocJson(A, Scene("a")));
        File.SetUnixFileMode(Path.Combine(folder, "3.json"), UnixFileMode.None);
        try
        {
            var listed = workspace.Versions(A).List!.Versions;

            Assert.Equal([3L, 2, 1], listed.Select(v => v.N));
            Assert.Equal(("unreadable", "io"), (listed[0].Status, listed[0].Problem!.Code));
            Assert.Equal(("unreadable", "limit.documentBytes"), (listed[1].Status, listed[1].Problem!.Code));
            foreach (var n in new long[] { 3, 2 })
            {
                var found = workspace.FindVersion(A, n);
                Assert.Equal(409, found.Problem!.Status);
                var said = found.Problem.Detail + " " + found.Problem.Problem.Message + " " + listed[(int)(3 - n)].Problem!.Message;
                Assert.DoesNotContain(_temp.Path, said);
                Assert.DoesNotContain(Path.GetFileName(_temp.Path), said);
                Assert.DoesNotContain("Exception", said);
            }
        }
        finally
        {
            File.SetUnixFileMode(Path.Combine(folder, "3.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// A save whose bytes aren't JSON lists as <c>unreadable</c> with <c>json</c>, as its restore finds it: the message
    /// is fixed, the parser's own text (its line and position) only in the log.
    /// </summary>
    [Fact]
    public void ASaveThatIsntJson_ListsAsUnreadable_WithAFixedMessage_NotTheParsersText()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20);
        File.WriteAllText(Path.Combine(Paths.VersionsFolder(A), "2.json"), "{ not json");

        var listed = workspace.Versions(A).List!.Versions;

        Assert.Equal([2L, 1], listed.Select(v => v.N));
        Assert.Equal(("unreadable", "json"), (listed[0].Status, listed[0].Problem!.Code));
        var found = workspace.FindVersion(A, 2);
        Assert.Equal(409, found.Problem!.Status);
        Assert.Equal("The earlier save isn't JSON.", listed[0].Problem!.Message);
        Assert.Equal("The earlier save isn't JSON.", found.Problem.Problem.Message);
        foreach (var said in new[] { listed[0].Problem!.Message, found.Problem.Detail, found.Problem.Problem.Message })
        {
            Assert.DoesNotContain("LineNumber", said);
            Assert.DoesNotContain("BytePositionInLine", said);
            Assert.DoesNotContain("Exception", said);
        }
    }

    [Fact]
    public void RestoringTheOldestOfTen_DropsIt_AfterTheSave_AndTheDocumentEqualsIt()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        for (var i = 1; i <= 10; i++)
            SaveAt(doc, 10 + i);
        Assert.Equal(Enumerable.Range(1, 10).Reverse().Select(n => (long)n), Numbers(workspace));
        var oldest = VersionBytes(1);

        Restore(workspace, doc, 1);

        Assert.Equal(Enumerable.Range(2, 10).Reverse().Select(n => (long)n), Numbers(workspace));
        Assert.Equal(oldest, Saved());
        Assert.Equal(10, X(doc.Current));
    }

    /// <summary>
    /// A version equal to the current content takes no step and writes nothing; the newest version (the save before the
    /// current one, which never holds the current save's bytes) restores as any other, and restoring it undoes a restore.
    /// </summary>
    [Fact]
    public void AVersionEqualToTheCurrentContent_ChangesNothing_AndTheNewestRestoresAsAnyOther()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        SaveAt(doc, 20); // version 1: a at 10
        Restore(workspace, doc, 1); // version 2: a at 20; saved: a at 10
        var saved = Saved();
        var files = VersionFiles();
        var rev = doc.Rev;
        var undo = doc.HistorySize.Undo;

        var same = Restore(workspace, doc, 1).Result!;

        Assert.Equal(rev, same.Rev);
        Assert.Empty(same.Items.Keys);
        Assert.Equal(saved, Saved());
        Assert.Equal(files, VersionFiles());
        Assert.Equal(undo, doc.HistorySize.Undo);

        var newest = Restore(workspace, doc, 2).Result!;
        Assert.Equal(rev + 1, newest.Rev);
        Assert.Equal(20, X(doc.Current));
        Assert.Equal(VersionBytes(2), Saved());
        Assert.Equal([3L, 2, 1], Numbers(workspace));
    }

    /// <summary>
    /// The save's write failing after the step: the step stays, the document shows the restored content unsaved, the
    /// recovery file is kept and then rewritten with it, and the failure is thrown as Save's is (the API's 500).
    /// </summary>
    [Fact]
    public void ASaveThatFails_LeavesTheStepUnsaved_KeptByTheRecoveryFile()
    {
        var faults = new MatchFaults();
        var workspace = StartWorkspace(faults);
        var doc = Create(workspace);
        SaveAt(doc, 20); // version 1: a at 10
        Move(doc, 70);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(File.Exists(Paths.RecoveryFile(A)));
        var saved = Saved();
        faults.Fail = (step, path) => step == StorageStep.CreateTemp && path.StartsWith(Paths.DocumentFile(A) + ".", StringComparison.Ordinal);

        Assert.Throws<IOException>(() => Restore(workspace, doc, 1));

        Assert.Equal(saved, Saved());
        Assert.True(doc.Dirty);
        Assert.Equal(10, X(doc.Current));
        Assert.Equal(20, X(doc.Saved));
        Assert.True(File.Exists(Paths.RecoveryFile(A)));
        faults.Fail = null;
        _time.Advance(TimeSpan.FromSeconds(2));
        using (var kept = JsonDocument.Parse(File.ReadAllBytes(Paths.RecoveryFile(A))))
        {
            Assert.Equal(RecoveryFile.BaseOf(saved), kept.RootElement.GetProperty("base").GetString());
            Assert.Equal(10, kept.RootElement.GetProperty("document").GetProperty("items")[0].GetProperty("placement").GetProperty("x").GetDouble());
        }

        // Undo brings back what the step replaced.
        doc.UndoOwn();
        Assert.Equal(70, X(doc.Current));
    }
}
