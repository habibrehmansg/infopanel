using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.StorageTestData;

namespace InfoPanel.Core.Tests.Documents;

public class BlobSweepTests
{
    private const string A = "0a000000-0000-4000-8000-000000000000";
    private const string B = "0b000000-0000-4000-8000-000000000000";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReferencesAnywhereCount()
    {
        using var temp = new TempDirectory();
        var stores = new Stores(new DataPaths(temp.Path)).Open();
        var ids = Enumerable.Range(0, 9).ToDictionary(i => i, i => Put(stores, $"blob {i}", Now.AddDays(-30)));
        string Hex(int i) => ids[i]["sha256:".Length..];

        // A scene's media, a source's deps, a string deep in opaque data, a property name, a JSON escape, upper-case hex.
        var document = """
            {"format":"infopanel.document/1","id":"ID","profile":{"name":"a"},"items":[
              {"kind":"scene","id":"m","scene":{"root":{"type":"media","source":{"asset":"BLOB0"}}},
               "source":{"format":"example.widget/1","data":{"deep":[[{"x":"see BLOB1 here"}]]},"deps":["BLOB2"]}},
              {"kind":"scene","id":"n","origin":{"BLOB3":true},"scene":{"note":"\u0073ha256:HEX4","up":"sha256:UPPER5"}}]}
            """
            .Replace("ID", A).Replace("HEX4", Hex(4)).Replace("UPPER5", Hex(5).ToUpperInvariant());
        for (var i = 0; i < 4; i++)
            document = document.Replace($"BLOB{i}", ids[i]);
        Assert.DoesNotContain(ids[4], document);
        stores.Documents.Create(A, Encoding.UTF8.GetBytes(document));

        // A document of a format this engine doesn't support, and one that isn't even an object: their text still counts.
        stores.Documents.Create(B, Encoding.UTF8.GetBytes($"[\"infopanel.document/9\", \"{ids[6]}\"]"));
        var unrelated = Put(stores, "unreferenced", Now.AddDays(-30));

        var result = Sweep(stores, Now);

        Assert.Null(result.SkippedBecause);
        Assert.Equal((2, 10, 7, 3), (result.Documents, result.Blobs, result.Referenced, result.Deleted));
        Assert.Equal(Enumerable.Range(0, 7).Select(i => ids[i]).Order(StringComparer.Ordinal), stores.Blobs.List());
        Assert.False(stores.Blobs.Exists(unrelated));
    }

    /// <summary>
    /// A blob that only a profile's unsaved changes name (its recovery file, kept by the orderly stop), past the grace
    /// period, is kept by the next start's sweep: every file in a profile's folder is a root.
    /// </summary>
    [Fact]
    public void ABlobOnlyUnsavedChangesNameIsKept()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var options = new WorkspaceOptions { HistoryBudget = new HistoryBudget(HistoryBudget.DefaultMaxBytes), ExamplesWanted = static () => false };
        var workspace = Workspace.Start(paths, options, Now);
        Assert.NotNull(workspace.Create(Encoding.UTF8.GetBytes($"{{\"id\":\"{A}\",\"name\":\"a\",\"width\":100,\"height\":100}}")).Entry);
        var blob = workspace.PutBlob("named by unsaved changes only"u8.ToArray()).Info.Id;
        var doc = workspace.Open(A)!;
        doc.Ok(DocKit.Commit(doc, "name", null, DocKit.Profile(new System.Text.Json.Nodes.JsonObject { ["name"] = blob })));
        workspace.Dispose();
        File.SetLastWriteTimeUtc(paths.BlobFile(blob), Now.AddDays(-2).UtcDateTime);

        using var again = Workspace.Start(paths, options, Now);

        Assert.True(again.Blobs.Exists(blob));
        Assert.Equal(blob, again.Open(A)!.Current.Document.Profile.Name);
    }

    [Fact]
    public void GracePeriodHolds()
    {
        using var temp = new TempDirectory();
        var stores = new Stores(new DataPaths(temp.Path)).Open();
        var fresh = Put(stores, "fresh", Now.AddHours(-23).AddMinutes(-59));
        var future = Put(stores, "future", Now.AddDays(3));
        var old = Put(stores, "old", Now.AddHours(-24).AddMinutes(-1));

        var result = Sweep(stores, Now);

        Assert.Equal(1, result.Deleted);
        Assert.True(stores.Blobs.Exists(fresh));
        Assert.True(stores.Blobs.Exists(future));
        Assert.False(stores.Blobs.Exists(old));
    }

    [Fact]
    public void StoringAgainRenewsTheGracePeriod()
    {
        using var temp = new TempDirectory();
        var stores = new Stores(new DataPaths(temp.Path)).Open();
        var id = Put(stores, "again", DateTimeOffset.UtcNow.AddDays(-5));
        stores.Blobs.Put("again"u8.ToArray());

        Sweep(stores, DateTimeOffset.UtcNow);

        Assert.True(stores.Blobs.Exists(id));
    }

    public static TheoryData<string> UnreadableDocuments => new() { "{\"items\":[", "", "not json", "\"\\uZZZZ\"", "(missing)" };

    [Theory]
    [MemberData(nameof(UnreadableDocuments))]
    public void AnUnreadableDocumentSkipsTheWholeSweep(string content)
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        stores.Documents.Create(B, Document(B, "b"));
        var old = Put(stores, "old", Now.AddDays(-30));
        if (content == "(missing)")
            File.Delete(paths.DocumentFile(B)); // a profile folder without its document
        else
            File.WriteAllText(paths.DocumentFile(B), content);

        var result = Sweep(stores, Now);

        Assert.NotNull(result.SkippedBecause);
        Assert.Contains(B, result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));
    }

    /// <summary>A folder whose document can't even be looked for (no permission) skips the sweep, profile or not.</summary>
    [Theory]
    [InlineData(A)]
    [InlineData("Backup")]
    public void AFolderThatCantBeReadSkipsTheWholeSweep(string name)
    {
        if (OperatingSystem.IsWindows())
            return; // Unix permissions

        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var kept = Put(stores, "kept", Now.AddDays(-30));
        var folder = Path.Combine(paths.ProfilesDirectory, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "document.json"), $"{{\"asset\":\"{kept}\"}}");
        var next = stores.Reopen();

        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            if (CanList(folder))
                return; // running with privileges that ignore the mode

            var result = BlobSweep.Run(next.Documents, next.Blobs, Now);

            Assert.NotNull(result.SkippedBecause);
            Assert.Equal(0, result.Deleted);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(stores.Blobs.Exists(kept));
    }

    [Fact]
    public void AnOversizedDocumentSkipsTheWholeSweep()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var old = Put(stores, "old", Now.AddDays(-30));
        // Over the store's raw read cap (documentBytes plus the read allowance), so it is not read.
        stores.Documents.Create(A, Document(A, "a" + new string('b', DocumentStore.ReadAllowanceBytes)));
        var limit = Document(A, "a").Length - 1;

        var documents = new DocumentStore(paths, SpecLimits.Default.With(new Dictionary<string, double> { ["documentBytes"] = limit }));
        documents.Open();
        var blobs = new BlobStore(paths);
        blobs.Open();
        var result = BlobSweep.Run(documents, blobs, Now);

        Assert.NotNull(result.SkippedBecause);
        Assert.Contains(A, result.SkippedBecause);
        Assert.True(blobs.Exists(old));
    }

    public static TheoryData<string> Changes => new() { "create", "save", "delete", "reorder", "put", "put again" };

    /// <summary>The sweep belongs to start-up: once either store has changed anything, it refuses to run.</summary>
    [Theory]
    [MemberData(nameof(Changes))]
    public void RunsOnlyBeforeAnythingChanges(string change)
    {
        using var temp = new TempDirectory();
        var stores = new Stores(new DataPaths(temp.Path)).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var old = Put(stores, "old", Now.AddDays(-30));
        var next = stores.Reopen();

        switch (change)
        {
            case "create": next.Documents.Create(B, Document(B, "b")); break;
            case "save": next.Documents.Save(A, Document(A, "saved")); break;
            case "delete": next.Documents.Delete(A); break;
            case "reorder": next.Documents.Reorder([A]); break;
            case "put": next.Blobs.Put("new"u8.ToArray()); break;
            case "put again": next.Blobs.Put("old"u8.ToArray()); break;
        }

        Assert.Throws<InvalidOperationException>(() => BlobSweep.Run(next.Documents, next.Blobs, Now));
        Assert.True(next.Blobs.Exists(old));
    }

    [Fact]
    public void RunsOnlyOnceBothStoresAreOpen()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var documents = new DocumentStore(paths);
        var blobs = new BlobStore(paths);

        Assert.Throws<InvalidOperationException>(() => BlobSweep.Run(documents, blobs, Now));
        documents.Open();
        Assert.Throws<InvalidOperationException>(() => BlobSweep.Run(documents, blobs, Now));
        blobs.Open();
        Assert.Null(BlobSweep.Run(documents, blobs, Now).SkippedBecause);
    }

    [Fact]
    public void FoldersThatAreNotProfilesStillCount()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var kept = Put(stores, "kept", Now.AddDays(-30));
        var gone = Put(stores, "gone", Now.AddDays(-30));
        var other = Path.Combine(paths.ProfilesDirectory, "Backup");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "document.json"), $"{{\"asset\":\"{kept}\"}}");
        Directory.CreateDirectory(Path.Combine(paths.ProfilesDirectory, "Empty"));

        var result = Sweep(stores, Now);

        Assert.Null(result.SkippedBecause);
        Assert.True(stores.Blobs.Exists(kept));
        Assert.False(stores.Blobs.Exists(gone));
    }

    /// <summary>
    /// The sweep's roots are every regular file under a profile's folder, at any depth: a blob that only a saved
    /// version, or a file a later engine keeps there, refers to is kept, and the profile's document still counts.
    /// </summary>
    [Fact]
    public void EveryFileUnderAProfileFolderCounts()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var inDocument = Put(stores, "in the document", Now.AddDays(-30));
        var inVersion = Put(stores, "in a saved version", Now.AddDays(-30));
        var deep = Put(stores, "deep down", Now.AddDays(-30));
        var gone = Put(stores, "gone", Now.AddDays(-30));
        stores.Documents.Create(A, Document(A, "a", inDocument));
        var folder = paths.ProfileFolder(A);
        Directory.CreateDirectory(Path.Combine(folder, "versions"));
        File.WriteAllBytes(Path.Combine(folder, "versions", "1.json"), Document(A, "saved before", inVersion));
        Directory.CreateDirectory(Path.Combine(folder, "a", "b", "c"));
        File.WriteAllText(Path.Combine(folder, "a", "b", "c", "later.json"), $"{{\"x\":[\"{deep}\"]}}");

        var result = Sweep(stores, Now);

        Assert.Null(result.SkippedBecause);
        Assert.Equal((3, 1), (result.Documents, result.Deleted));
        Assert.True(stores.Blobs.Exists(inDocument));
        Assert.True(stores.Blobs.Exists(inVersion));
        Assert.True(stores.Blobs.Exists(deep));
        Assert.False(stores.Blobs.Exists(gone));
    }

    /// <summary>
    /// A file under a profile's folder that can't be read (or not whole) is one more root that can't be: nothing is
    /// deleted. What it holds needn't be JSON (<see cref="OsMetadataIsIgnored_AndEveryOtherFileIsScannedAsText"/>).
    /// </summary>
    [Theory]
    [InlineData("(no permission)")]
    [InlineData("(oversized)")]
    public void AnUnreadableFileUnderAProfileFolderSkipsTheWholeSweep(string content)
    {
        if (content == "(no permission)" && OperatingSystem.IsWindows())
            return; // Unix permissions

        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var old = Put(stores, "old", Now.AddDays(-30));
        var file = Path.Combine(paths.ProfileFolder(A), "versions", "1.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content == "(oversized)" ? "\"" + new string('x', stores.Documents.MaxBytes) + "\"" : "{}");
        if (content == "(no permission)")
        {
            File.SetUnixFileMode(file, UnixFileMode.None);
            if (CanRead(file))
                return; // running with privileges that ignore the mode
        }

        var result = Sweep(stores, Now);

        Assert.NotNull(result.SkippedBecause);
        Assert.Contains("1.json", result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));
    }

    /// <summary>
    /// Links under a profile's folder are read through, not skipped: a linked folder's files and a linked file count as
    /// roots (a file there that isn't JSON is scanned as text, like any other), so a blob only they refer to is kept.
    /// </summary>
    [Fact]
    public void LinksUnderAProfileFolderAreReadThroughTheLink()
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need privileges there

        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var linked = Put(stores, "only a linked file refers to it", Now.AddDays(-30));
        var gone = Put(stores, "gone", Now.AddDays(-30));
        File.WriteAllText(Path.Combine(outside.Path, "notes.txt"), "not json");
        File.WriteAllText(Path.Combine(outside.Path, "refers.json"), $"{{\"x\":\"{linked}\"}}");
        Directory.CreateSymbolicLink(Path.Combine(paths.ProfileFolder(A), "elsewhere"), outside.Path);
        File.CreateSymbolicLink(Path.Combine(paths.ProfileFolder(A), "refers.json"), Path.Combine(outside.Path, "refers.json"));

        var result = Sweep(stores, Now);

        Assert.Null(result.SkippedBecause);
        // document.json, elsewhere/notes.txt, elsewhere/refers.json and the linked refers.json.
        Assert.Equal((4, 1), (result.Documents, result.Deleted));
        Assert.True(stores.Blobs.Exists(linked));
        Assert.False(stores.Blobs.Exists(gone));
    }

    /// <summary>
    /// A profile's folder that is a link (moved elsewhere and linked back) is read through the link, its saved versions
    /// included, and so is a linked folder in a profile's folder: a blob only a version there refers to is kept. A link
    /// back to a folder already read is read once.
    /// </summary>
    [Fact]
    public void ALinkedProfileFolderIsReadThroughTheLink()
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need privileges there

        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var inDocument = Put(stores, "in the document", Now.AddDays(-30));
        var inVersion = Put(stores, "in a saved version", Now.AddDays(-30));
        var inLinkedVersions = Put(stores, "in a linked versions folder", Now.AddDays(-30));
        var gone = Put(stores, "gone", Now.AddDays(-30));
        stores.Documents.Create(A, Document(A, "a", inDocument));
        stores.Documents.Create(B, Document(B, "b"));

        // A's folder moved elsewhere, with a saved version, and linked back; a link in it back to itself.
        var moved = Path.Combine(outside.Path, "A");
        Directory.Move(paths.ProfileFolder(A), moved);
        Directory.CreateDirectory(Path.Combine(moved, "versions"));
        File.WriteAllBytes(Path.Combine(moved, "versions", "1.json"), Document(A, "saved before", inVersion));
        Directory.CreateSymbolicLink(Path.Combine(moved, "versions", "again"), "..");
        Directory.CreateSymbolicLink(paths.ProfileFolder(A), moved);
        // B's saved versions kept elsewhere, linked in.
        var versions = Path.Combine(outside.Path, "B-versions");
        Directory.CreateDirectory(versions);
        File.WriteAllBytes(Path.Combine(versions, "1.json"), Document(B, "saved before", inLinkedVersions));
        Directory.CreateSymbolicLink(Path.Combine(paths.ProfileFolder(B), "versions"), versions);

        var result = Sweep(stores, Now);

        Assert.Null(result.SkippedBecause);
        Assert.Equal((4, 1), (result.Documents, result.Deleted));
        Assert.True(stores.Blobs.Exists(inDocument));
        Assert.True(stores.Blobs.Exists(inVersion));
        Assert.True(stores.Blobs.Exists(inLinkedVersions));
        Assert.False(stores.Blobs.Exists(gone));
    }

    /// <summary>
    /// A link whose roots can't be read (one to nothing, in a profile's folder or in <c>profiles/</c> where it may stand
    /// for a profile on a drive that isn't there, or links that loop) means nothing is deleted that run.
    /// </summary>
    [Theory]
    [InlineData("to nothing, in a profile")]
    [InlineData("to nothing, in profiles")]
    [InlineData("looping")]
    public void ALinkWhoseRootsCantBeReadSkipsTheWholeSweep(string link)
    {
        if (OperatingSystem.IsWindows())
            return; // symbolic links need privileges there

        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var old = Put(stores, "old", Now.AddDays(-30));
        var folder = paths.ProfileFolder(A);
        var nowhere = temp.Combine("unmounted", "drive");
        switch (link)
        {
            case "to nothing, in a profile":
                Directory.CreateSymbolicLink(Path.Combine(folder, "versions"), nowhere);
                break;
            case "to nothing, in profiles":
                Directory.CreateSymbolicLink(paths.ProfileFolder(B), nowhere);
                break;
            case "looping":
                File.CreateSymbolicLink(Path.Combine(folder, "x"), Path.Combine(folder, "y"));
                File.CreateSymbolicLink(Path.Combine(folder, "y"), Path.Combine(folder, "x"));
                break;
        }

        var result = Sweep(stores, Now);

        Assert.NotNull(result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));
    }

    /// <summary>
    /// Known OS metadata (Finder's <c>.DS_Store</c> and AppleDouble <c>._*</c> files, Windows' <c>Thumbs.db</c> and
    /// <c>desktop.ini</c>) and the engine's own write leftovers aren't roots; every other file is scanned as text, JSON or
    /// not: a reference in a text editor's binary swap file, or escaped in a recovery file that isn't JSON, keeps its blob.
    /// </summary>
    [Fact]
    public void OsMetadataIsIgnored_AndEveryOtherFileIsScannedAsText()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var inSwap = Put(stores, "in a swap file", Now.AddDays(-30));
        var escaped = Put(stores, "escaped in a recovery file", Now.AddDays(-30));
        var gone = Put(stores, "gone", Now.AddDays(-30));
        stores.Documents.Create(A, Document(A, "a"));
        var folder = paths.ProfileFolder(A);
        Directory.CreateDirectory(Path.Combine(folder, "versions"));
        File.WriteAllBytes(Path.Combine(folder, ".DS_Store"), [0, 0, 0, 1, (byte)'B', (byte)'u', (byte)'d', 0xFF]);
        File.WriteAllBytes(Path.Combine(folder, "._document.json"), [0, 5, 22, 7, 0xFF]);
        File.WriteAllBytes(Path.Combine(folder, "versions", "Thumbs.db"), [0xD0, 0xCF, 0x11, 0xE0]);
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(folder, "versions", $"2.json.{new string('a', 32)}.tmp"), "{\"half");
        File.WriteAllBytes(Path.Combine(folder, ".document.json.swp"), [0xFF, 0x00, .. Encoding.ASCII.GetBytes(inSwap), 0x00, 0xFE]);
        File.WriteAllText(Path.Combine(folder, "versions", "3.json~"), "{\"src\": \"\\u0073ha256:" + escaped["sha256:".Length..] + "\", broken");

        var result = Sweep(stores, Now);

        Assert.Null(result.SkippedBecause);
        // document.json, the swap file and the recovery file.
        Assert.Equal((3, 1), (result.Documents, result.Deleted));
        Assert.True(stores.Blobs.Exists(inSwap));
        Assert.True(stores.Blobs.Exists(escaped));
        Assert.False(stores.Blobs.Exists(gone));
    }

    [Fact]
    public void OtherFilesInBlobsAreLeftAlone()
    {
        using var temp = new TempDirectory();
        var stores = new Stores(new DataPaths(temp.Path)).Open();
        Put(stores, "x", Now.AddDays(-30));
        var notes = Path.Combine(stores.Blobs.Paths.BlobsDirectory, "notes.txt");
        File.WriteAllText(notes, "x");
        File.SetLastWriteTimeUtc(notes, Now.AddDays(-30).UtcDateTime);

        Assert.Equal(1, Sweep(stores, Now).Deleted);
        Assert.True(File.Exists(notes));
    }

    /// <summary>
    /// The earlier saves the store keeps are roots: a blob only a kept version refers to stays; once its version is
    /// dropped (an eleventh save drops the first), the blob goes, but only past the grace period.
    /// </summary>
    [Fact]
    public void ABlobOnlyAKeptVersionRefersTo_IsKept_UntilTheVersionIsDropped()
    {
        using var temp = new TempDirectory();
        var stores = new Stores(new DataPaths(temp.Path)).Open();
        var blobs = Enumerable.Range(0, 12).Select(i => Put(stores, $"save {i}", Now.AddDays(-30))).ToArray();
        stores.Documents.Create(A, Document(A, "s0", blobs[0]));
        for (var i = 1; i <= 11; i++)
            stores.Documents.Save(A, Document(A, $"s{i}", blobs[i]));
        File.SetLastWriteTimeUtc(stores.Blobs.Paths.BlobFile(blobs[0]), Now.AddHours(-1).UtcDateTime);

        // Versions 2 to 11 hold s1 to s10; version 1 (s0) was dropped, but its blob is within the grace period.
        var result = Sweep(stores, Now);
        Assert.Null(result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.All(blobs, b => Assert.True(stores.Blobs.Exists(b)));

        result = Sweep(stores, Now.AddDays(2));
        Assert.Null(result.SkippedBecause);
        Assert.Equal(1, result.Deleted);
        Assert.False(stores.Blobs.Exists(blobs[0]));
        Assert.All(blobs[1..], b => Assert.True(stores.Blobs.Exists(b)));
    }

    /// <summary>A kept version that can't be read stops the sweep's deletions, as a document that can't be.</summary>
    [Fact]
    public void AKeptVersionThatCantBeRead_DeletesNothing()
    {
        if (OperatingSystem.IsWindows())
            return; // Unix permissions

        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var old = Put(stores, "old", Now.AddDays(-30));
        stores.Documents.Create(A, Document(A, "a"));
        stores.Documents.Save(A, Document(A, "b"));
        var version = Path.Combine(paths.ProfileFolder(A), "versions", "1.json");
        File.SetUnixFileMode(version, UnixFileMode.None);
        if (CanRead(version))
            return; // running with privileges that ignore the mode

        var result = Sweep(stores, Now);

        Assert.NotNull(result.SkippedBecause);
        Assert.Contains("1.json", result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));
    }

    /// <summary>
    /// A dropped version's removal is on disk before any blob is deleted: the sweep flushes each profile's versions folder
    /// first, and deletes nothing while that flush fails, so a dropped version that a power loss brings back (its folder
    /// on another drive than blobs/, say) still has its blobs.
    /// </summary>
    [Fact]
    public void ADroppedVersionsBlobs_GoOnlyOnceItsRemovalIsFlushed()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var blobs = Enumerable.Range(0, 12).Select(i => Put(stores, $"save {i}", Now.AddDays(-30))).ToArray();
        stores.Documents.Create(A, Document(A, "s0", blobs[0]));
        for (var i = 1; i <= 11; i++)
            stores.Documents.Save(A, Document(A, $"s{i}", blobs[i]));
        Assert.False(File.Exists(paths.VersionFile(A, 1))); // dropped: nothing refers to blobs[0] now

        var failing = new MatchFaults { Fail = (step, path) => step == StorageStep.SyncFolder && path == paths.VersionsFolder(A) };
        var result = SweepWith(paths, failing);
        Assert.NotNull(result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.All(blobs, b => Assert.True(stores.Blobs.Exists(b)));

        var flushing = new MatchFaults();
        result = SweepWith(paths, flushing);
        Assert.Null(result.SkippedBecause);
        Assert.Equal(1, result.Deleted);
        Assert.False(stores.Blobs.Exists(blobs[0]));
        AssertFlushedBefore(flushing.Steps, paths.VersionsFolder(A), paths.BlobFile(blobs[0]));
    }

    /// <summary>
    /// A save whose document's rename couldn't be flushed may come back after a power loss as the document it replaced:
    /// the sweep flushes each profile's folder before deleting anything, and deletes nothing while that fails, so the
    /// replaced document's blobs are kept.
    /// </summary>
    [Fact]
    public void ASavesUnflushedRename_KeepsTheReplacedDocumentsBlobs_UntilItsFolderIsFlushed()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var old = Put(stores, "old", Now.AddDays(-30));
        stores.Documents.Create(A, Document(A, "a", old));
        bool ProfileFlush(StorageStep step, string path) => step == StorageStep.SyncFolder && path == paths.ProfileFolder(A);
        // The profile's folder can't be flushed: no version is kept (versions/ is new), and the document's rename isn't on disk.
        var live = new Stores(paths, new MatchFaults { Fail = ProfileFlush }).Open();
        Assert.Throws<IOException>(() => live.Documents.Save(A, Document(A, "b")));
        Assert.Equal(Document(A, "b"), live.Documents.Read(A));
        Assert.False(File.Exists(paths.VersionFile(A, 1)));

        var result = SweepWith(paths, new MatchFaults { Fail = ProfileFlush });
        Assert.NotNull(result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));

        var flushing = new MatchFaults();
        result = SweepWith(paths, flushing);
        Assert.Null(result.SkippedBecause);
        Assert.Equal(1, result.Deleted);
        Assert.False(stores.Blobs.Exists(old));
        AssertFlushedBefore(flushing.Steps, paths.ProfileFolder(A), paths.BlobFile(old));
    }

    /// <summary>
    /// A delete whose flush of <c>profiles/</c> failed, then failed again at the next start, leaves the deleted profile's
    /// folder, which isn't a root, while its rename may not be on disk: the sweep flushes <c>profiles/</c> before it deletes
    /// anything, and deletes nothing while that fails, so a power loss can't bring the profile back without its blobs.
    /// </summary>
    [Fact]
    public void ADeletedProfilesBlobs_GoOnlyOnceItsRenameIsFlushed()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var stores = new Stores(paths).Open();
        var old = Put(stores, "old", Now.AddDays(-30));
        stores.Documents.Create(A, Document(A, "a", old));
        bool ProfilesFlush(StorageStep step, string path) => step == StorageStep.SyncFolder && path == paths.ProfilesDirectory;
        var live = new Stores(paths, new MatchFaults { Fail = ProfilesFlush }).Open();
        Assert.Throws<IOException>(() => live.Documents.Delete(A));
        var deleted = Assert.Single(Directory.GetDirectories(paths.ProfilesDirectory));
        Assert.StartsWith(A + ".deleted-", Path.GetFileName(deleted), StringComparison.Ordinal);

        // The next start can't flush profiles/ either: the deleted profile's folder is left, its rename maybe not on disk.
        var result = SweepWith(paths, new MatchFaults { Fail = ProfilesFlush });
        Assert.True(Directory.Exists(deleted));
        Assert.NotNull(result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));

        var flushing = new MatchFaults();
        result = SweepWith(paths, flushing);
        Assert.Null(result.SkippedBecause);
        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(deleted));
        Assert.False(stores.Blobs.Exists(old));
        AssertFlushedBefore(flushing.Steps, paths.ProfilesDirectory, paths.BlobFile(old));
    }

    /// <summary>Sweeps at the next start, on stores given <paramref name="faults"/>.</summary>
    private static BlobSweepResult SweepWith(DataPaths paths, MatchFaults faults)
    {
        var next = new Stores(paths, faults).Open();
        return BlobSweep.Run(next.Documents, next.Blobs, Now);
    }

    private static void AssertFlushedBefore(List<(StorageStep Step, string Path)> steps, string folder, string deleted)
    {
        var flushed = steps.FindIndex(s => s.Step == StorageStep.SyncFolder && s.Path == folder);
        var deletedAt = steps.FindIndex(s => s.Step == StorageStep.DeleteFile && s.Path == deleted);
        Assert.True(0 <= flushed && flushed < deletedAt, string.Join(", ", steps));
    }

    private static bool CanRead(string file)
    {
        try
        {
            File.ReadAllBytes(file);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool CanList(string folder)
    {
        try
        {
            _ = Directory.EnumerateFileSystemEntries(folder).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Sweeps at the next start, as the engine does.</summary>
    private static BlobSweepResult Sweep(Stores stores, DateTimeOffset now)
    {
        var next = stores.Reopen();
        return BlobSweep.Run(next.Documents, next.Blobs, now);
    }

    private static string Put(Stores stores, string content, DateTimeOffset written)
    {
        var id = stores.Blobs.Put(Encoding.UTF8.GetBytes(content));
        File.SetLastWriteTimeUtc(stores.Blobs.Paths.BlobFile(id), written.UtcDateTime);
        return id;
    }
}
