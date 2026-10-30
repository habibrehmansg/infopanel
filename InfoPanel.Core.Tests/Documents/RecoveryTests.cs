using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using static InfoPanel.Core.Tests.Documents.DocKit;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Unsaved changes kept across a restart (docs/2.0/data-folder.md, "Unsaved changes"): the recovery file's cadence (2 s
/// after the last change, at most 10 s after the first not yet kept, every change that makes a generation), its removal
/// before the answer whenever a change leaves the document clean, Save rewriting or removing it, Delete racing a
/// checkpoint, the orderly stop, failing writes, and the restore at open as one undoable step, with every file that
/// can't be used left alone. On a clock that moves only when the test says, so nothing here waits on time.
/// </summary>
public sealed class RecoveryTests : IDisposable
{
    private const string A = "0a000000-0000-4000-8000-000000000000";
    private const string B = "0b000000-0000-4000-8000-000000000000";
    private const string C = "0c000000-0000-4000-8000-000000000000";

    private readonly TempDirectory _temp = new();
    private readonly TimerTimeProvider _time = new(Start);
    private readonly List<LogEvent> _log = [];
    private readonly List<Workspace> _open = [];

    private DataPaths Paths => new(_temp.Path);

    public void Dispose()
    {
        foreach (var workspace in _open)
            workspace.Dispose();
        _temp.Dispose();
    }

    private sealed class CollectingSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    private Workspace StartWorkspace(IStorageFaults? faults = null)
    {
        var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new CollectingSink(_log)).CreateLogger();
        var workspace = Workspace.Start(Paths, new WorkspaceOptions
        {
            Instance = Instance,
            Time = _time,
            HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes),
            Logger = logger,
            ExamplesWanted = static () => false,
            StorageFaults = faults,
        }, Start);
        _open.Add(workspace);
        return workspace;
    }

    /// <summary>The warnings about a profile's unsaved changes (a write that failed, a file that wasn't restored).</summary>
    private List<LogEvent> RecoveryWarnings()
    {
        lock (_log)
            return _log.Where(e => e.Level == LogEventLevel.Warning && e.MessageTemplate.Text.Contains("unsaved changes", StringComparison.Ordinal)).ToList();
    }

    private static string DocJson(string id, params JsonObject[] items)
    {
        var doc = JsonNode.Parse(Doc(items).GetRawText())!.AsObject();
        doc["id"] = id;
        return doc.ToJsonString();
    }

    /// <summary>Creates profile <paramref name="id"/> with <c>a</c> at x 10 and <c>b</c> at x 50, and opens it.</summary>
    private static ProfileDocument Create(Workspace workspace, string id = A)
    {
        var body = $"{{\"id\":\"{id}\",\"document\":{DocJson(id, Scene("a", 10, 10), Scene("b", 50, 10))}}}";
        var created = workspace.Create(Encoding.UTF8.GetBytes(body));
        Assert.True(created.Entry is not null, created.Invalid?.Errors.FirstOrDefault()?.Message);
        return workspace.Open(id)!;
    }

    private static CommitResult Move(ProfileDocument doc, double x, string item = "a", string? coalesceKey = null) =>
        doc.Ok(Commit(doc, $"op-{Guid.NewGuid():N}", coalesceKey, Place(item, x, 10)));

    private static double X(Generation generation, string item = "a") => generation.Item(item).Placement.X;

    private string RecoveryPath(string id = A) => Paths.RecoveryFile(id);

    private bool HasRecovery(string id = A) => File.Exists(RecoveryPath(id));

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The recovery file's members: its base, when it was written, and its document's x of <c>a</c> and <c>b</c>.</summary>
    private (string Base, string Written, double A, double B, JsonElement Document) ReadRecovery(string id = A)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(RecoveryPath(id)));
        var root = json.RootElement;
        Assert.Equal("infopanel.recovery/1", root.GetProperty("format").GetString());
        var document = root.GetProperty("document").Clone();
        double XOf(string item) => document.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetString() == item).GetProperty("placement").GetProperty("x").GetDouble();
        return (root.GetProperty("base").GetString()!, root.GetProperty("written").GetString()!, XOf("a"), XOf("b"), document);
    }

    private string SavedHash(string id = A) => Hash(File.ReadAllBytes(Paths.DocumentFile(id)));

    private void Advance(int milliseconds) => _time.Advance(TimeSpan.FromMilliseconds(milliseconds));

    // ---- when it is written ----------------------------------------------------------------------------------------

    [Fact]
    public void WrittenTwoSecondsAfterTheLastChange()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);

        Advance(1999);
        Assert.False(HasRecovery());
        Advance(1);
        Assert.True(HasRecovery());

        var kept = ReadRecovery();
        Assert.Equal(SavedHash(), kept.Base);
        Assert.Equal("2026-10-27T09:00:02Z", kept.Written);
        Assert.Equal(20, kept.A);
        // The document as Save writes it: canonical, without rev members.
        Assert.Equal(Workspace.Serialize(doc.Current), Encoding.UTF8.GetBytes(kept.Document.GetRawText()));
        // The saved document is untouched.
        Assert.Equal(10, X(doc.Saved));
        Assert.True(doc.Dirty);
    }

    [Fact]
    public void ALongDragIsKeptEveryTenSeconds()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        var x = 10;
        for (var t = 0; t < 10_000; t += 500)
        {
            Move(doc, ++x);
            Assert.False(HasRecovery(), $"written at {t} ms, while changes went on");
            Advance(500);
        }

        // 10 s after the first change not yet kept: the commit made at 9.5 s.
        Assert.True(HasRecovery());
        Assert.Equal(x, ReadRecovery().A);
    }

    [Fact]
    public void AKeptRevisionIsNotWrittenAgain()
    {
        var workspace = StartWorkspace();
        var writes = 0;
        workspace.CheckpointWriting = _ => writes++;
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        Assert.Equal(1, writes);

        Advance(60_000);
        Assert.Equal(1, writes);
    }

    [Fact]
    public void UndoRedoAndAGestureEachScheduleOneAndARevertRemovesIt()
    {
        var workspace = StartWorkspace();
        var writes = 0;
        workspace.CheckpointWriting = _ => writes++;
        var doc = Create(workspace);
        Move(doc, 20);
        Move(doc, 30);
        Advance(2000);
        Assert.Equal((1, 30d), (writes, ReadRecovery().A));

        doc.UndoOwn();
        Advance(2000);
        Assert.Equal((2, 20d), (writes, ReadRecovery().A));

        doc.RedoOwn();
        Advance(2000);
        Assert.Equal((3, 30d), (writes, ReadRecovery().A));

        // A desktop window's drag: a profile-style gesture commit.
        Move(doc, 40, coalesceKey: "gesture:window");
        Advance(2000);
        Assert.Equal((4, 40d), (writes, ReadRecovery().A));

        // Profile settings count too.
        doc.Ok(Commit(doc, "profile-name", null, DocKit.Profile(new JsonObject { ["name"] = "Renamed" })));
        Advance(2000);
        Assert.Equal(5, writes);
        Assert.Equal("Renamed", ReadRecovery().Document.GetProperty("profile").GetProperty("name").GetString());

        doc.RevertOwn();
        Assert.False(HasRecovery());
        Advance(10_000);
        Assert.Equal(5, writes);
        Assert.False(HasRecovery());
    }

    // ---- removed before the answer ---------------------------------------------------------------------------------

    public static TheoryData<string> CleanChanges => new() { "undo", "revert", "commit back", "save" };

    [Theory]
    [MemberData(nameof(CleanChanges))]
    public void AChangeThatLeavesTheDocumentCleanRemovesItBeforeItAnswers(string change)
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        Assert.True(HasRecovery());

        switch (change)
        {
            case "undo": doc.UndoOwn(); break;
            case "revert": doc.RevertOwn(); break;
            case "commit back": Move(doc, 10); break;
            case "save": doc.Save(); break;
        }

        Assert.False(doc.Dirty);
        Assert.False(HasRecovery());
    }

    [Fact]
    public void AChangeThatLeavesTheDocumentCleanWaitsForACheckpointUnderWay()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        workspace.CheckpointWriting = _ =>
        {
            writing.Set();
            release.Wait();
        };
        // The checkpoint read the dirty revision and is about to write it.
        var checkpoint = Task.Run(() => doc.Checkpoint());
        Task revert;
        try
        {
            Assert.True(writing.Wait(TimeSpan.FromSeconds(30)));
            revert = Task.Run(() => doc.RevertOwn());
            // The revert's new revision is published; it then waits for the checkpoint before it answers.
            Assert.True(SpinWait.SpinUntil(() => doc.Rev == 3 || revert.IsCompleted, TimeSpan.FromSeconds(30)));
            Assert.False(revert.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        Assert.True(revert.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(checkpoint.Wait(TimeSpan.FromSeconds(30)));

        Assert.False(doc.Dirty);
        Assert.False(HasRecovery());
    }

    /// <summary>
    /// A revert while a save is writing, with nothing kept yet: it waits for the save, which keeps the reverted content
    /// against the version it saved, before it answers. Answering at once would leave a window where a crash brings back
    /// the saved content the revert discarded, with nothing kept.
    /// </summary>
    [Fact]
    public async Task ARevertDuringASaveWaitsForItEvenWithNothingKept()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Assert.False(HasRecovery());
        using var saving = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        workspace.Saving = _ =>
        {
            saving.Set();
            release.Wait();
        };
        using var waiting = new SemaphoreSlim(0);
        doc.WaitingForSave = () => waiting.Release();
        var save = Task.Run(() => doc.Save());
        Task<double?> revert;
        try
        {
            Assert.True(saving.Wait(TimeSpan.FromSeconds(30)));
            // What is kept the moment the revert answers.
            revert = Task.Run(() =>
            {
                doc.RevertOwn();
                return HasRecovery() ? ReadRecovery().A : (double?)null;
            });
            // It answers at once, or waits for the save.
            await Task.WhenAny(revert, waiting.WaitAsync(TestContext.Current.CancellationToken));
            Assert.False(revert.IsCompleted, "The revert answered while the save was writing");
        }
        finally
        {
            release.Set();
        }
        Assert.Equal(10, await revert);
        await save;

        // Saved at 20, the revert kept against it: a restart brings back the revert's 10.
        Assert.Equal(20, X(doc.Saved));
        Assert.Equal(10, X(doc.Current));
        Assert.Equal(SavedHash(), ReadRecovery().Base);
    }

    /// <summary>
    /// A change that left the document clean at revision 4 removes what was kept before it, not a checkpoint made after
    /// it: its removal waits for a checkpoint under way, and edits at 5 are kept meanwhile and must survive it; also when
    /// a checkpoint of revision 6 then fails before it writes, which leaves the one of revision 5 on disk.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARemovalDelayedPastANewerCheckpointLeavesThatCheckpoint(bool nextCheckpointFails)
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        Move(doc, 30);
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        workspace.CheckpointWriting = _ =>
        {
            writing.Set();
            release.Wait();
        };
        // A checkpoint of revision 3 holds the save lock.
        var checkpoint = Task.Run(() => doc.Checkpoint());
        using var waiting = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        Task back;
        try
        {
            Assert.True(writing.Wait(TimeSpan.FromSeconds(30)));
            doc.WaitingForSave = () =>
            {
                waiting.Set();
                resume.Wait();
            };
            // Back to the saved content at revision 4: its removal waits for the save lock, and is held there.
            back = Task.Run(() => Move(doc, 10));
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(30)));
            doc.WaitingForSave = null;
            workspace.CheckpointWriting = null;
        }
        finally
        {
            release.Set();
        }

        try
        {
            Assert.True(checkpoint.Wait(TimeSpan.FromSeconds(30)));
            // Edits after the clean revision, one of them kept.
            Move(doc, 40);
            Assert.True(doc.Checkpoint());
            Assert.Equal(40, ReadRecovery().A);
            Move(doc, 50);
            if (nextCheckpointFails)
            {
                workspace.CheckpointWriting = _ => throw new IOException("injected: the checkpoint failed before it wrote");
                Assert.Throws<IOException>(() => doc.Checkpoint());
                workspace.CheckpointWriting = null;
            }
        }
        finally
        {
            resume.Set();
        }
        Assert.True(back.Wait(TimeSpan.FromSeconds(30)));

        Assert.Equal(6, doc.Rev);
        Assert.True(HasRecovery(), "The removal for revision 4 deleted the checkpoint of revision 5");
        Assert.Equal(40, ReadRecovery().A);
    }

    /// <summary>
    /// A commit back to the saved content, retried while the first is still waiting to remove what was kept: the retry
    /// answers (replayed, clean) only once the file is gone, so a crash after the answer never brings the discarded
    /// changes back.
    /// </summary>
    [Fact]
    public async Task ARetriedCommitBackToCleanAnswersOnlyOnceTheFileIsGone()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        Move(doc, 30);
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        workspace.CheckpointWriting = _ =>
        {
            writing.Set();
            release.Wait();
        };
        var checkpoint = Task.Run(() => doc.Checkpoint());
        using var waiting = new SemaphoreSlim(0);
        var body = Commit(doc, $"op-{Guid.NewGuid():N}", null, Place("a", 10, 10));
        Task<CommitOutcome> first;
        Task<(CommitOutcome Outcome, bool Kept)> retry;
        try
        {
            Assert.True(writing.Wait(TimeSpan.FromSeconds(30)));
            doc.WaitingForSave = () => waiting.Release();
            first = Task.Run(() => doc.Commit(body));
            // The commit is applied and waits to remove the file.
            Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal(4, doc.Rev);
            retry = Task.Run(() =>
            {
                var outcome = doc.Commit(body);
                return (outcome, HasRecovery());
            });
            // It answers at once, or waits for the removal.
            await Task.WhenAny(retry, waiting.WaitAsync(TestContext.Current.CancellationToken));
            Assert.False(retry.IsCompleted, "The retry answered before the first commit removed the file");
        }
        finally
        {
            release.Set();
        }
        Assert.True(checkpoint.Wait(TimeSpan.FromSeconds(30)));
        var (outcome, kept) = await retry;
        Assert.True(outcome.Result is { Replayed: true, Dirty: false, Rev: 4 }, Describe(outcome));
        Assert.False(kept, "The retry answered while the discarded changes were still kept");
        Assert.True((await first).Result is { Dirty: false, Rev: 4 });
        Assert.False(HasRecovery());
    }

    [Fact]
    public void ASaveWithCommitsDuringItRewritesTheFileAgainstTheNewSave()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        var before = ReadRecovery();

        var committed = false;
        workspace.Saving = _ =>
        {
            if (committed)
                return;
            committed = true;
            Move(doc, 77, "b");
        };
        doc.Save();

        Assert.True(doc.Dirty);
        var after = ReadRecovery();
        Assert.NotEqual(before.Base, after.Base);
        Assert.Equal(SavedHash(), after.Base);
        Assert.Equal((20d, 77d), (after.A, after.B));
        Assert.Equal((20d, 50d), (X(doc.Saved), X(doc.Saved, "b")));
    }

    [Fact]
    public void ADeleteDuringACheckpointWritesNothingAndTheIdCreatedAgainHasNoFile()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        workspace.CheckpointWriting = _ =>
        {
            writing.Set();
            release.Wait();
        };
        var checkpoint = Task.Run(() => Record.Exception(() => doc.Checkpoint()));
        using var unlisted = new ManualResetEventSlim();
        Task<bool> delete;
        try
        {
            Assert.True(writing.Wait(TimeSpan.FromSeconds(30)));
            workspace.StoreChanged = _ => unlisted.Set();
            delete = Task.Run(() => workspace.DeleteWithItsTag(A));
            // The store no longer lists the profile; the delete now waits for the checkpoint to dispose the document.
            Assert.True(unlisted.Wait(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            release.Set();
        }
        Assert.True(delete.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(delete.Result);
        Assert.True(checkpoint.Wait(TimeSpan.FromSeconds(30)));
        Assert.Null(checkpoint.Result);

        Assert.False(Directory.Exists(Paths.ProfileFolder(A)));
        workspace.CheckpointWriting = null;
        workspace.StoreChanged = null;
        var again = Create(workspace);
        Assert.False(HasRecovery());
        Assert.Equal(1, again.Rev);
        Assert.False(again.Dirty);
        Advance(60_000);
        Assert.False(HasRecovery());
    }

    [Fact]
    public void TheOrderlyStopWritesEveryDirtyDocumentNotYetKept()
    {
        var workspace = StartWorkspace();
        var a = Create(workspace, A);
        var b = Create(workspace, B);
        Create(workspace, C);
        Move(b, 60);
        Advance(2000);
        Move(a, 20);
        var writes = new List<string>();
        workspace.CheckpointWriting = id => writes.Add(id);

        workspace.Dispose();

        Assert.Equal([A], writes);
        Assert.Equal(20, ReadRecovery(A).A);
        Assert.Equal(60, ReadRecovery(B).A);
        Assert.False(HasRecovery(C));
    }

    [Fact]
    public void AFailingWriteIsLoggedOnceAndTriedAgainAndNeverFailsACommit()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        // A folder where the file goes: every write fails.
        Directory.CreateDirectory(RecoveryPath());

        Move(doc, 20);
        Advance(2000);
        Assert.Single(RecoveryWarnings());
        Move(doc, 30);
        Advance(2000);
        Move(doc, 40);
        Advance(2000);
        Assert.Single(RecoveryWarnings());
        Assert.Equal(4, doc.Rev);

        Directory.Delete(RecoveryPath());
        // Tried again at the interval, with no change.
        Advance(10_000);
        Assert.True(HasRecovery());
        Assert.Equal(40, ReadRecovery().A);
    }

    /// <summary>
    /// A removal that fails still answers (the failure is logged), and is tried again on its own: by the scheduler while
    /// it keeps failing, with no further change, until it succeeds. A restart then brings nothing back.
    /// </summary>
    [Theory]
    [InlineData("revert")]
    [InlineData("save")]
    public void AFailedRemovalIsTriedAgainOnItsOwn(string change)
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        Assert.True(HasRecovery());
        var fail = true;
        var removals = 0;
        workspace.CheckpointRemoving = _ =>
        {
            removals++;
            if (fail)
                throw new IOException("injected: the removal failed");
        };

        if (change == "revert")
            doc.RevertOwn();
        else
            doc.Save();
        Assert.False(doc.Dirty);
        Assert.Equal(1, removals);
        Assert.True(HasRecovery());

        // Tried again, and again while it fails.
        Advance(2000);
        Assert.Equal(2, removals);
        Advance(10_000);
        Assert.Equal(3, removals);
        Assert.True(HasRecovery());

        fail = false;
        Advance(10_000);
        Assert.Equal(4, removals);
        Assert.False(HasRecovery());
        // Nothing more to do.
        Advance(60_000);
        Assert.Equal(4, removals);

        workspace.Dispose();
        var reopened = StartWorkspace().Open(A)!;
        Assert.False(reopened.Dirty);
        Assert.Equal(change == "revert" ? 10 : 20, X(reopened.Current));
    }

    /// <summary>A removal still failing when the engine stops is tried again by the orderly stop.</summary>
    [Fact]
    public void AFailedRemovalIsTriedAgainAtTheOrderlyStop()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        var fail = true;
        workspace.CheckpointRemoving = _ =>
        {
            if (fail)
                throw new IOException("injected: the removal failed");
        };
        doc.RevertOwn();
        Assert.True(HasRecovery());

        fail = false;
        workspace.Dispose();
        Assert.False(HasRecovery());
        var reopened = StartWorkspace().Open(A)!;
        Assert.False(reopened.Dirty);
        Assert.Equal(10, X(reopened.Current));
    }

    /// <summary>
    /// Faults that fail every flush of profile A's folder from its recovery file's deletion on, while <c>fail</c> says
    /// so: the file is gone, but its removal isn't on disk yet. Counts those flushes.
    /// </summary>
    private (MatchFaults Faults, Func<int> Flushes) FailTheFlushAfterTheRemoval(Func<bool> fail)
    {
        var faults = new MatchFaults();
        var folder = Paths.ProfileFolder(A);
        var recovery = RecoveryPath(A);
        // The index of the deletion's step: the steps are recorded before they are failed.
        var deleted = -1;
        faults.Fail = (step, path) =>
        {
            if (deleted < 0 && step == StorageStep.DeleteFile && path == recovery)
                deleted = faults.Steps.Count - 1;
            return deleted >= 0 && fail() && step == StorageStep.SyncFolder && path == folder;
        };
        return (faults, () => deleted < 0 ? 0 : faults.Steps.Skip(deleted).Count(s => s.Step == StorageStep.SyncFolder && s.Path == folder));
    }

    /// <summary>
    /// A removal whose folder flush fails after the file is deleted is still pending: the retry flushes the folder though
    /// the file is gone already, while it fails and until it succeeds, and only then is it done.
    /// </summary>
    [Theory]
    [InlineData("revert")]
    [InlineData("save")]
    public void AFailedFlushAfterTheRemovalIsTriedAgainOnItsOwn(string change)
    {
        var fail = true;
        var (faults, flushes) = FailTheFlushAfterTheRemoval(() => fail);
        var workspace = StartWorkspace(faults);
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        Assert.True(HasRecovery());

        if (change == "revert")
            doc.RevertOwn();
        else
            doc.Save();
        Assert.False(doc.Dirty);
        Assert.False(HasRecovery());
        Assert.Equal(1, flushes());
        Assert.True(doc.RemovalPending);

        // Tried again, and again while it fails.
        Advance(2000);
        Assert.Equal(2, flushes());
        Advance(10_000);
        Assert.Equal(3, flushes());
        Assert.True(doc.RemovalPending);

        fail = false;
        Advance(10_000);
        Assert.Equal(4, flushes());
        Assert.False(doc.RemovalPending);
        // Nothing more to do.
        Advance(60_000);
        Assert.Equal(4, flushes());
    }

    /// <summary>A removal whose folder flush still fails when the engine stops is flushed by the orderly stop.</summary>
    [Fact]
    public void AFailedFlushAfterTheRemovalIsTriedAgainAtTheOrderlyStop()
    {
        var fail = true;
        var (faults, flushes) = FailTheFlushAfterTheRemoval(() => fail);
        var workspace = StartWorkspace(faults);
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        doc.RevertOwn();
        Assert.False(HasRecovery());
        Assert.Equal(1, flushes());
        Assert.True(doc.RemovalPending);

        fail = false;
        workspace.Dispose();
        Assert.Equal(2, flushes());
    }

    // ---- the restore at open ---------------------------------------------------------------------------------------

    /// <summary>Profile A saved with a at 10, its unsaved move to 20 kept by an orderly stop at <c>Start + 2 s</c>.</summary>
    private void KeepAMove()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        Advance(2000);
        workspace.Dispose();
        Assert.True(HasRecovery());
    }

    [Fact]
    public void TheOrderlyStopKeepsChangesMadeJustBeforeIt()
    {
        var workspace = StartWorkspace();
        var doc = Create(workspace);
        Move(doc, 20);
        workspace.Dispose();

        var reopened = StartWorkspace().Open(A)!;
        Assert.True(reopened.Dirty);
        Assert.Equal(20, X(reopened.Current));
    }

    [Fact]
    public void KeptChangesAreRestoredAsOneUndoableStep()
    {
        KeepAMove();

        var workspace = StartWorkspace();
        var doc = workspace.Open(A)!;
        Assert.Equal(2, doc.Rev);
        Assert.True(doc.Dirty);
        Assert.True(doc.CanUndo);
        Assert.False(doc.CanRedo);
        Assert.Equal(1, doc.HistorySize.Undo);
        Assert.Equal(Start.AddSeconds(2), doc.Recovered);
        Assert.Equal(Start.AddSeconds(2), doc.Status.Recovered);
        Assert.Equal(Start.AddSeconds(2), doc.Read().Recovered);
        Assert.Equal(20, X(doc.Current));
        Assert.Equal(10, X(doc.Saved));
        Assert.True(workspace.Entry(A)!.Dirty);
        // The step stamps what it changed.
        Assert.Equal(2, doc.ReadRevisions(r => r.ItemRev("a")));
        Assert.Equal(1, doc.ReadRevisions(r => r.ItemRev("b")));
        // Nothing is written again for the restored revision: the file holds it.
        var writes = 0;
        workspace.CheckpointWriting = _ => writes++;
        Advance(60_000);
        Assert.Equal(0, writes);

        doc.UndoOwn();
        Assert.Equal(3, doc.Rev);
        Assert.False(doc.Dirty);
        Assert.Equal(10, X(doc.Current));
        Assert.False(HasRecovery());

        doc.RedoOwn();
        Assert.True(doc.Dirty);
        Assert.Equal(20, X(doc.Current));
        // Shown again while dirty: it ends at a save or a revert only.
        Assert.Equal(Start.AddSeconds(2), doc.Recovered);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("revert")]
    public void RecoveredEndsAtASaveOrARevert(string end)
    {
        KeepAMove();
        var doc = StartWorkspace().Open(A)!;
        Assert.NotNull(doc.Recovered);

        if (end == "save")
            doc.Save();
        else
            doc.RevertOwn();

        Assert.Null(doc.Recovered);
        Assert.Null(doc.Status.Recovered);
        Assert.False(HasRecovery());
        Move(doc, 30);
        Assert.Null(doc.Recovered);
    }

    [Fact]
    public void KeptChangesEqualToTheSavedDocumentMakeNoStepAndTheFileGoes()
    {
        var workspace = StartWorkspace();
        Create(workspace);
        workspace.Dispose();
        var saved = File.ReadAllBytes(Paths.DocumentFile(A));
        File.WriteAllText(RecoveryPath(), RecoveryJson(Hash(saved), Encoding.UTF8.GetString(saved)));

        var doc = StartWorkspace().Open(A)!;
        Assert.Equal(1, doc.Rev);
        Assert.False(doc.Dirty);
        Assert.False(doc.CanUndo);
        Assert.Null(doc.Recovered);
        Assert.False(HasRecovery());
        Assert.Empty(RecoveryWarnings());
    }

    [Fact]
    public void ScenesThatCantBeDrawnAreKeptAsInAStoredDocument()
    {
        var workspace = StartWorkspace();
        Create(workspace);
        workspace.Dispose();
        var saved = File.ReadAllBytes(Paths.DocumentFile(A));
        var kept = DocJson(A, Scene("a", 10, 10), Scene("b", 50, 10), Scene("broken", 0, 0, InvalidScene()), Scene("later", 0, 0, FutureScene()));
        File.WriteAllText(RecoveryPath(), RecoveryJson(Hash(saved), kept));

        var doc = StartWorkspace().Open(A)!;
        Assert.Equal(2, doc.Rev);
        Assert.True(doc.Dirty);
        Assert.Equal(Canonical(InvalidScene()), Canonical(((SceneItem)doc.Current.Item("broken")).Scene));
        Assert.Equal(Canonical(FutureScene()), Canonical(((SceneItem)doc.Current.Item("later")).Scene));
    }

    private static string RecoveryJson(string @base, string document, string format = "infopanel.recovery/1", string written = "2026-10-27T08:59:00Z") =>
        $"{{\"format\":\"{format}\",\"base\":\"{@base}\",\"written\":\"{written}\",\"document\":{document}}}";

    public static TheoryData<string> Unusable => new()
    {
        "stale base", "not json", "no format", "another format", "no document", "bad written", "invalid document", "unsupported document",
        "over the cap", "fifo", "folder", "format not text", "base not text", "written not text",
    };

    /// <summary>A file the engine can't use: not restored, logged once, the profile opens as saved, the file left as it is.</summary>
    [Theory]
    [MemberData(nameof(Unusable))]
    public void AFileThatCantBeUsedIsNotRestoredAndIsLeft(string what)
    {
        if (what == "fifo" && !Fifo.Supported)
            return;
        var workspace = StartWorkspace();
        Create(workspace);
        workspace.Dispose();
        var saved = File.ReadAllBytes(Paths.DocumentFile(A));
        var moved = DocJson(A, Scene("a", 20, 10), Scene("b", 50, 10));
        var path = RecoveryPath();
        switch (what)
        {
            case "stale base": File.WriteAllText(path, RecoveryJson(Hash([.. saved, (byte)' ']), moved)); break;
            case "not json": File.WriteAllText(path, "{\"format\":\"infopanel.recovery/1\","); break;
            case "no format": File.WriteAllText(path, $"{{\"base\":\"{Hash(saved)}\",\"written\":\"2026-10-27T08:59:00Z\",\"document\":{moved}}}"); break;
            case "another format": File.WriteAllText(path, RecoveryJson(Hash(saved), moved, "infopanel.recovery/2")); break;
            case "no document": File.WriteAllText(path, $"{{\"format\":\"infopanel.recovery/1\",\"base\":\"{Hash(saved)}\",\"written\":\"2026-10-27T08:59:00Z\"}}"); break;
            case "bad written": File.WriteAllText(path, RecoveryJson(Hash(saved), moved, written: "yesterday")); break;
            case "invalid document": File.WriteAllText(path, RecoveryJson(Hash(saved), $"{{\"format\":\"infopanel.document/1\",\"id\":\"{A}\",\"items\":[]}}")); break;
            case "unsupported document": File.WriteAllText(path, RecoveryJson(Hash(saved), moved.Replace("infopanel.document/1", "infopanel.document/2"))); break;
            case "over the cap":
                var text = RecoveryJson(Hash(saved), moved);
                File.WriteAllText(path, text + new string(' ', DocumentStore.ReadCap(SpecLimits.Default) + 1 - Encoding.UTF8.GetByteCount(text)));
                break;
            case "fifo": Fifo.Create(path); break;
            case "folder": Directory.CreateDirectory(path); break;
            // JSON that parses, with a string that isn't Unicode text (an unpaired surrogate): reading it as a string throws.
            case "format not text": File.WriteAllText(path, RecoveryJson(Hash(saved), moved, "\\uD800")); break;
            case "base not text": File.WriteAllText(path, RecoveryJson(Hash(saved)[..58] + "\\uD800", moved)); break;
            case "written not text": File.WriteAllText(path, RecoveryJson(Hash(saved), moved, written: "2026-10-27T08:59:00Z\\uD800")); break;
        }
        var before = what is "fifo" or "folder" ? null : File.ReadAllBytes(path);

        var reopened = StartWorkspace();
        var doc = Fifo.Within(30, () => reopened.Open(A)!, what == "fifo" ? [path] : []);
        Assert.Equal(1, doc.Rev);
        Assert.False(doc.Dirty);
        Assert.Null(doc.Recovered);
        Assert.Equal(10, X(doc.Current));
        Assert.Single(RecoveryWarnings());
        if (before is not null)
            Assert.Equal(before, File.ReadAllBytes(path));
        else
            Assert.True(Path.Exists(path));

        // Nor does the orderly stop touch it while the profile is clean.
        reopened.Dispose();
        if (before is not null)
            Assert.Equal(before, File.ReadAllBytes(path));
        else
            Assert.True(Path.Exists(path));
    }

    [Fact]
    public void AStoredDocumentWhoseIdIsAnotherKeepsItsFolderIdWhenRestored()
    {
        var workspace = StartWorkspace();
        Create(workspace);
        workspace.Dispose();
        var saved = File.ReadAllBytes(Paths.DocumentFile(A));
        File.WriteAllText(RecoveryPath(), RecoveryJson(Hash(saved), DocJson(B, Scene("a", 20, 10), Scene("b", 50, 10))));

        var doc = StartWorkspace().Open(A)!;
        Assert.Equal(2, doc.Rev);
        Assert.Equal(A, doc.Current.Document.Id);
        Assert.Equal(20, X(doc.Current));
    }
}
