using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using Xunit;
using static InfoPanel.Core.Tests.Documents.StorageTestData;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Each storage operation failed at every step it takes, as a crash (nothing after the step runs, temp files stay) and
/// as an I/O error (the operation cleans up and throws), then the folder reopened: each document is exactly its old or
/// its new saved bytes, the order is the old or the new one, every blob a surviving document refers to exists, and a
/// deleted profile never comes back.
/// </summary>
public class CrashSafetyTests
{
    public static TheoryData<bool> Modes => new() { true, false };

    private static readonly string A = "0a000000-0000-4000-8000-000000000000";
    private static readonly string B = "0b000000-0000-4000-8000-000000000000";
    private static readonly string C = "0c000000-0000-4000-8000-000000000000";

    [Theory]
    [MemberData(nameof(Modes))]
    public void Save_IsOldOrNew(bool crash)
    {
        var old = Document(A, "old", BlobId("old"));
        var next = Document(A, "new", BlobId("new"));
        var steps = ForEveryStep(crash,
            arrange: s =>
            {
                s.Blobs.Put(Encoding.UTF8.GetBytes("old"));
                s.Documents.Create(A, old);
            },
            act: s =>
            {
                s.Blobs.Put(Encoding.UTF8.GetBytes("new"));
                s.Documents.Save(A, next);
            },
            check: (faults, live, reopened, completed) =>
            {
                var saved = reopened.Documents.Read(A);
                Assert.True(saved.SequenceEqual(old) || saved.SequenceEqual(next));
                if (completed)
                    Assert.Equal(next, saved);
                Assert.Equal(saved, live.Documents.Read(A));
                AssertReferencedBlobsExist(reopened);
            });
        Assert.True(steps >= 8, $"{steps} steps");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Create_IsAllOrNothing(bool crash)
    {
        var a = Document(A, "a");
        var b = Document(B, "b", BlobId("b"));
        var steps = ForEveryStep(crash,
            arrange: s => s.Documents.Create(A, a),
            act: s =>
            {
                s.Blobs.Put(Encoding.UTF8.GetBytes("b"));
                s.Documents.Create(B, b);
            },
            check: (faults, live, reopened, completed) =>
            {
                Assert.Equal(a, reopened.Documents.Read(A));
                var created = faults.Completed(StorageStep.MoveFolder, p => p == reopened.Documents.Paths.ProfileFolder(B));
                Assert.Equal(completed || created, reopened.Documents.Contains(B));
                Assert.Equal(reopened.Documents.Contains(B) ? [A, B] : [A], reopened.Documents.List());
                Assert.Equal(reopened.Documents.List(), live.Documents.List());
                if (reopened.Documents.Contains(B))
                    Assert.Equal(b, reopened.Documents.Read(B));
                AssertReferencedBlobsExist(reopened);
            });
        Assert.True(steps >= 10, $"{steps} steps");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Delete_NeverComesBack(bool crash)
    {
        var a = Document(A, "a");
        var b = Document(B, "b", BlobId("b"));
        var steps = ForEveryStep(crash,
            arrange: s =>
            {
                s.Documents.Create(A, a);
                s.Blobs.Put(Encoding.UTF8.GetBytes("b"));
                s.Documents.Create(B, b);
            },
            act: s => s.Documents.Delete(B),
            check: (faults, live, reopened, completed) =>
            {
                Assert.Equal(a, reopened.Documents.Read(A));
                var deleted = faults.Completed(StorageStep.MoveFolder, p => p.Contains(".deleted-", StringComparison.Ordinal));
                Assert.Equal(!(completed || deleted), reopened.Documents.Contains(B));
                Assert.Equal(reopened.Documents.List(), live.Documents.List());
                if (reopened.Documents.Contains(B))
                    Assert.Equal(b, reopened.Documents.Read(B));
                AssertReferencedBlobsExist(reopened);

                // Once gone, it stays gone.
                var again = new Stores(reopened.Documents.Paths).Open();
                Assert.Equal(reopened.Documents.List(), again.Documents.List());
            });
        Assert.True(steps >= 6, $"{steps} steps");
    }

    /// <summary>A delete whose rename can't be flushed throws, and leaves the renamed folder whole for the next start to remove.</summary>
    [Fact]
    public void Delete_RenameNotFlushed_ThrowsAndRemovesNothingYet()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var setup = new Stores(paths).Open();
        setup.Documents.Create(A, Document(A, "a"));
        setup.Documents.Create(B, Document(B, "b"));
        var faults = new StepFaults(2, crash: false);
        var live = new Stores(paths, faults).Open();
        faults.Armed = true;

        Assert.Throws<IOException>(() => live.Documents.Delete(B));

        Assert.Equal((StorageStep.SyncFolder, paths.ProfilesDirectory), faults.Steps[1]);
        Assert.DoesNotContain(faults.Steps, s => s.Step is StorageStep.DeleteFile or StorageStep.DeleteFolder);
        Assert.Equal([A], live.Documents.List());
        var renamed = Assert.Single(Directory.GetDirectories(paths.ProfilesDirectory, $"{B}.deleted-*"));
        Assert.Equal(Document(B, "b"), File.ReadAllBytes(Path.Combine(renamed, "document.json")));

        Assert.Equal([A], live.Reopen().Documents.List());
        Assert.Empty(Directory.GetDirectories(paths.ProfilesDirectory, "*.deleted-*"));
    }

    /// <summary>
    /// After a delete whose rename couldn't be flushed, the next start removes the renamed folder only once a flush of
    /// profiles/ succeeds; while it fails, the folder is left whole and start-up carries on.
    /// </summary>
    [Fact]
    public void Delete_RenameNotFlushed_OpenRemovesOnlyOnceFlushed()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var setup = new Stores(paths).Open();
        setup.Documents.Create(A, Document(A, "a"));
        setup.Documents.Create(B, Document(B, "b"));
        var profilesFlush = (StorageStep step, string path) => step == StorageStep.SyncFolder && path == paths.ProfilesDirectory;
        var faults = new MatchFaults { Fail = profilesFlush };
        Assert.Throws<IOException>(() => new Stores(paths, faults).Open().Documents.Delete(B));

        var failing = new MatchFaults { Fail = profilesFlush };
        Assert.Equal([A], new Stores(paths, failing).Open().Documents.List());
        Assert.Contains(failing.Steps, s => profilesFlush(s.Step, s.Path));
        Assert.DoesNotContain(failing.Steps, s => s.Step is StorageStep.DeleteFile or StorageStep.DeleteFolder);
        var renamed = Assert.Single(Directory.GetDirectories(paths.ProfilesDirectory, $"{B}.deleted-*"));
        Assert.Equal(Document(B, "b"), File.ReadAllBytes(Path.Combine(renamed, "document.json")));

        var flushing = new MatchFaults();
        Assert.Equal([A], new Stores(paths, flushing).Open().Documents.List());
        Assert.Equal((StorageStep.SyncFolder, paths.ProfilesDirectory), flushing.Steps[0]);
        Assert.Empty(Directory.GetDirectories(paths.ProfilesDirectory, "*.deleted-*"));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void BlobWrite_IsWholeOrAbsent(bool crash)
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 1000));
        var id = BlobStore.IdOf(content);
        var steps = ForEveryStep(crash,
            arrange: _ => { },
            act: s => Assert.Equal(id, s.Blobs.Put(content)),
            check: (faults, live, reopened, completed) =>
            {
                Assert.Equal(completed || faults.Completed(StorageStep.ReplaceFile, _ => true), reopened.Blobs.Exists(id));
                Assert.Equal(reopened.Blobs.Exists(id) ? content.Length : null, reopened.Blobs.Size(id));

                // The id is returned only once the blob's entry in blobs/ is flushed to disk.
                Assert.Equal(completed, faults.Completed(StorageStep.SyncFolder, p => p == reopened.Blobs.Paths.BlobsDirectory));
            });
        Assert.True(steps >= 5, $"{steps} steps");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void OrderWrite_IsOldOrNew(bool crash)
    {
        var steps = ForEveryStep(crash,
            arrange: s =>
            {
                s.Documents.Create(A, Document(A, "a"));
                s.Documents.Create(B, Document(B, "b"));
                s.Documents.Create(C, Document(C, "c"));
            },
            act: s => s.Documents.Reorder([C, A, B]),
            check: (faults, live, reopened, completed) =>
            {
                var order = reopened.Documents.List();
                Assert.True(order.SequenceEqual([A, B, C]) || order.SequenceEqual([C, A, B]), string.Join(",", order));
                if (completed)
                    Assert.Equal([C, A, B], order);
                Assert.Equal(order, live.Documents.List());
            });
        Assert.Equal(4, steps); // create the temp, flush, replace, flush the folder
    }

    /// <summary>The sweep after a crash of a save that moves a document to a new blob: the blob the saved document refers to is kept.</summary>
    [Fact]
    public void SaveThenSweep_KeepsWhatTheSavedDocumentRefersTo()
    {
        ForEveryStep(crash: true,
            arrange: s =>
            {
                s.Blobs.Put(Encoding.UTF8.GetBytes("old"));
                s.Documents.Create(A, Document(A, "old", BlobId("old")));
                AgeAllBlobs(s);
            },
            act: s =>
            {
                s.Blobs.Put(Encoding.UTF8.GetBytes("new"));
                s.Documents.Save(A, Document(A, "new", BlobId("new")));
            },
            check: (faults, live, reopened, completed) =>
            {
                AgeAllBlobs(reopened);
                var result = BlobSweep.Run(reopened.Documents, reopened.Blobs, DateTimeOffset.UtcNow);
                Assert.Null(result.SkippedBecause);
                AssertReferencedBlobsExist(reopened);
                Assert.Single(reopened.Blobs.List()); // the other one is old and unreferenced
            });
    }

    /// <summary>A sweep that crashes part-way has only deleted unreferenced blobs, and the next one finishes.</summary>
    [Fact]
    public void Sweep_CrashedPartWay_DeletesOnlyUnreferenced()
    {
        var steps = ForEveryStep(crash: true,
            arrange: s =>
            {
                foreach (var name in new[] { "kept", "gone1", "gone2", "gone3" })
                    s.Blobs.Put(Encoding.UTF8.GetBytes(name));
                s.Documents.Create(A, Document(A, "a", BlobId("kept")));
                AgeAllBlobs(s);
            },
            act: s => BlobSweep.Run(s.Documents, s.Blobs, DateTimeOffset.UtcNow),
            check: (faults, live, reopened, completed) =>
            {
                Assert.True(reopened.Blobs.Exists(BlobId("kept")));
                BlobSweep.Run(reopened.Documents, reopened.Blobs, DateTimeOffset.UtcNow);
                Assert.Equal([BlobId("kept")], reopened.Blobs.List());
            });
        Assert.Equal(3, steps);
    }

    /// <summary>A crash during start-up reconciliation leaves a folder the next start reconciles the same way.</summary>
    [Fact]
    public void Open_CrashedPartWay_ReconcilesOnNextOpen()
    {
        var a = Document(A, "a");
        ForEveryStep(crash: true,
            arrange: s =>
            {
                var paths = s.Documents.Paths;
                s.Documents.Create(A, a);
                s.Documents.Create(B, Document(B, "b"));
                Directory.Move(paths.ProfileFolder(B), Path.Combine(paths.ProfilesDirectory, $"{B}.deleted-{Guid.NewGuid():N}"));
                Directory.CreateDirectory(paths.ProfileFolder(C));
                File.WriteAllBytes(paths.DocumentFile(C), Document(C, "c"));
                File.WriteAllText(paths.DocumentFile(A) + $".{Guid.NewGuid():N}.tmp", "{\"partial");
                File.WriteAllText(paths.ProfileOrderFile + $".{Guid.NewGuid():N}.tmp", "{");
            },
            act: s => s.Open(),
            check: (faults, live, reopened, completed) =>
            {
                Assert.Equal([A, C], reopened.Documents.List());
                Assert.Equal(a, reopened.Documents.Read(A));
            },
            openFirst: false);
    }

    private static void AgeAllBlobs(Stores stores)
    {
        foreach (var id in stores.Blobs.List())
            File.SetLastWriteTimeUtc(stores.Blobs.Paths.BlobFile(id), DateTime.UtcNow.AddDays(-2));
    }

    private static void AssertReferencedBlobsExist(Stores stores)
    {
        var references = new HashSet<string>();
        foreach (var id in stores.Documents.List())
            BlobSweep.CollectReferences(stores.Documents.Read(id), references);
        Assert.All(references, id => Assert.True(stores.Blobs.Exists(id), $"{id} is missing"));
    }
}
