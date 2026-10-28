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
