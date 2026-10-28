using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.StorageTestData;

namespace InfoPanel.Core.Tests.Documents;

public class DocumentStoreTests
{
    private const string A = "0a000000-0000-4000-8000-000000000000";
    private const string B = "0b000000-0000-4000-8000-000000000000";
    private const string C = "0c000000-0000-4000-8000-000000000000";

    [Fact]
    public void DataPaths_V2Layout()
    {
        var paths = new DataPaths("/data/InfoPanel/v2");
        var blob = "sha256:" + new string('a', 64);

        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/profiles.json"), paths.ProfileOrderFile);
        Assert.Equal(Path.GetFullPath($"/data/InfoPanel/v2/profiles/{A}"), paths.ProfileFolder(A));
        Assert.Equal(Path.GetFullPath($"/data/InfoPanel/v2/profiles/{A}/document.json"), paths.DocumentFile(A));
        Assert.Equal(Path.GetFullPath("/data/InfoPanel/v2/blobs"), paths.BlobsDirectory);
        Assert.Equal(Path.GetFullPath($"/data/InfoPanel/v2/blobs/{new string('a', 64)}"), paths.BlobFile(blob));
        Assert.Throws<ArgumentException>(() => paths.ProfileFolder("../x"));
        Assert.Throws<ArgumentException>(() => paths.DocumentFile(A.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => paths.BlobFile("sha256:../../etc/passwd"));
    }

    public static TheoryData<string> InvalidIds => new()
    {
        "", " ", "..", ".", "../" + A, A + "/..", "a/b", "a\\b", A.ToUpperInvariant(), "0A000000-0000-4000-8000-000000000000",
        "{" + A + "}", A + "\n", " " + A, A + " ", A + ".tmp", A + ".deleted-1", "0a0000000000400080000000000000000",
        "0a000000-0000-4000-8000-00000000000g", "0a000000-0000-4000-8000-0000000000000", "con", "profiles.json",
    };

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public void Ids_OnlyLowerCaseUuids(string id)
    {
        Assert.False(DocumentStore.IsValidId(id));

        using var temp = new TempDirectory();
        var store = new DocumentStore(new DataPaths(temp.Path));
        store.Open();
        Assert.Throws<ArgumentException>(() => store.Create(id, Document(A, "x")));
        Assert.Throws<KeyNotFoundException>(() => store.Read(id));
        Assert.Throws<KeyNotFoundException>(() => store.Save(id, Document(A, "x")));
        Assert.False(store.Delete(id));
        Assert.False(store.Contains(id));
        Assert.Equal(["profiles.json"], Tree(temp.Path));
    }

    [Fact]
    public void Ids_ValidForms()
    {
        Assert.True(DocumentStore.IsValidId(A));
        Assert.True(DocumentStore.IsValidId(NewId()));
        Assert.False(DocumentStore.IsValidId(null));
    }

    [Fact]
    public void CreateSaveReadDeleteReorder()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        Assert.Throws<InvalidOperationException>(() => store.List());
        Assert.Empty(store.Open());

        store.Create(A, Document(A, "a"));
        store.Create(B, Document(B, "b"));
        Assert.Equal([A, B], store.List());
        Assert.Equal(Document(A, "a"), store.Read(A));
        Assert.Equal(Document(A, "a"), File.ReadAllBytes(paths.DocumentFile(A)));
        Assert.Equal($"{{\"order\":[\"{A}\",\"{B}\"]}}", File.ReadAllText(paths.ProfileOrderFile));
        Assert.Throws<InvalidOperationException>(() => store.Create(A, Document(A, "again")));

        // Bytes in, bytes out: the store never parses what it stores.
        var opaque = Encoding.UTF8.GetBytes("not json at all é");
        store.Save(B, opaque);
        Assert.Equal(opaque, store.Read(B));
        Assert.Throws<KeyNotFoundException>(() => store.Save(C, opaque));

        store.Create(C, Document(C, "c"));
        store.Reorder([C, A, B]);
        Assert.Equal([C, A, B], store.List());
        Assert.Throws<ArgumentException>(() => store.Reorder([C, A]));
        Assert.Throws<ArgumentException>(() => store.Reorder([C, A, A]));
        Assert.Throws<ArgumentException>(() => store.Reorder([C, A, B, NewId()]));
        Assert.Equal([C, A, B], store.List());

        Assert.True(store.Delete(A));
        Assert.False(store.Delete(A));
        Assert.Equal([C, B], store.List());
        Assert.False(Directory.Exists(paths.ProfileFolder(A)));
        Assert.Throws<KeyNotFoundException>(() => store.Read(A));

        // A new store on the folder sees the same.
        var reopened = new DocumentStore(paths);
        Assert.Equal([C, B], reopened.Open());
        Assert.Equal(opaque, reopened.Read(B));
        Assert.Equal(new[] { "profiles", "profiles.json", $"profiles/{B}", $"profiles/{B}/document.json", $"profiles/{C}", $"profiles/{C}/document.json" }
            .Order(StringComparer.Ordinal), Tree(temp.Path).Select(p => p.Replace('\\', '/')).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Open_AppendsUnlistedFoldersAndDropsListedIdsWithoutOne()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var gone = NewId();
        WriteFolder(paths, C);
        WriteFolder(paths, A);
        WriteFolder(paths, B);
        File.WriteAllText(paths.ProfileOrderFile, $"{{\"order\":[\"{B}\",\"{gone}\",\"{B}\",\"Not-An-Id\",7]}}");

        var store = new DocumentStore(paths);
        Assert.Equal([B, A, C], store.Open());
        Assert.Equal($"{{\"order\":[\"{B}\",\"{A}\",\"{C}\"]}}", File.ReadAllText(paths.ProfileOrderFile));

        // Nothing to change: the order file is left as it is.
        var stamp = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(paths.ProfileOrderFile, stamp);
        Assert.Equal([B, A, C], new DocumentStore(paths).Open());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(paths.ProfileOrderFile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"order\":{}}")]
    public void Open_MissingOrUnreadableOrder_ListsFoldersById(string? order)
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, B);
        WriteFolder(paths, A);
        if (order != null)
            File.WriteAllText(paths.ProfileOrderFile, order);

        Assert.Equal([A, B], new DocumentStore(paths).Open());
        Assert.Equal($"{{\"order\":[\"{A}\",\"{B}\"]}}", File.ReadAllText(paths.ProfileOrderFile));
    }

    [Fact]
    public void Open_RemovesLeftoversAndLeavesEverythingElse()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        var hex = Guid.NewGuid().ToString("N");

        // Left by interrupted writes, creates and deletes.
        File.WriteAllText(paths.ProfileOrderFile + $".{hex}.tmp", "{\"order\":[]}");
        File.WriteAllText(paths.DocumentFile(A) + $".{hex}.tmp", "{\"partial\":");
        var staging = Path.Combine(paths.ProfilesDirectory, $"{B}.{hex}.tmp");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "document.json"), Document(B, "staged"));
        var deleted = Path.Combine(paths.ProfilesDirectory, $"{C}.deleted-{hex}");
        Directory.CreateDirectory(deleted);
        File.WriteAllBytes(Path.Combine(deleted, "document.json"), Document(C, "deleted"));
        Directory.CreateDirectory(paths.BlobsDirectory);
        File.WriteAllText(Path.Combine(paths.BlobsDirectory, new string('a', 64) + $".{hex}.tmp"), "half");

        // 1.x and step-1 data, and folders that aren't profiles.
        var kept = new[]
        {
            "profiles.xml", $"profiles.xml.{hex}.tmp", "profiles/6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7.xml",
            "assets/6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7/a.png", "profiles/0D000000-0000-4000-8000-00000000000F/document.json",
            "profiles/Backup/document.json", "blobs/readme.txt",
        };
        foreach (var file in kept)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temp.Path, file))!);
            File.WriteAllText(Path.Combine(temp.Path, file), "x");
        }

        var stores = new Stores(paths).Open();
        Assert.Equal([A], stores.Documents.List());
        Assert.Equal(Document(A, "w"), stores.Documents.Read(A));

        var tree = Tree(temp.Path).Select(p => p.Replace('\\', '/')).ToArray();
        Assert.DoesNotContain(tree, p => p.EndsWith(".tmp", StringComparison.Ordinal) && !p.StartsWith("profiles.xml", StringComparison.Ordinal));
        Assert.DoesNotContain(tree, p => p.Contains(".deleted-", StringComparison.Ordinal));
        Assert.All(kept, file => Assert.Contains(file, tree));
    }

    /// <summary>Only the exact names the stores write are leftovers; anything close to them is someone else's.</summary>
    [Fact]
    public void Open_RemovesOnlyTheNamesTheStoresWrite()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        var hex = Guid.NewGuid().ToString("N");
        var kept = new[]
        {
            "profiles/Backup.deleted-manually/document.json", $"profiles/{B}.deleted-{hex[..31]}/document.json",
            $"profiles/{B}.deleted-{hex}x/document.json", $"profiles/Backup.{hex}.tmp/document.json", $"profiles/{B}.tmp/document.json",
            $"profiles/{A}/foo.tmp", $"profiles/{A}/document.json.tmp", $"profiles/{A}/other.json.{hex}.tmp",
            "blobs/notes.tmp", $"blobs/{new string('a', 64)}.tmp", $"blobs/{new string('a', 63)}.{hex}.tmp",
            "profiles.json.tmp", $"notes.json.{hex}.tmp",
        };
        foreach (var file in kept)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temp.Path, file))!);
            File.WriteAllText(Path.Combine(temp.Path, file), "x");
        }

        var stores = new Stores(paths).Open();

        Assert.Equal([A], stores.Documents.List());
        var tree = Tree(temp.Path).Select(p => p.Replace('\\', '/')).ToArray();
        Assert.All(kept, file => Assert.Contains(file, tree));
    }

    [Fact]
    public void Read_LargerThanTheReadCap_Throws()
    {
        // The raw cap is documentBytes plus the read allowance (the canonical size is the reader's to check): a file of
        // exactly that many bytes is read, one more is not.
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var limits = Limit(Document(A, "a").Length);
        var store = new DocumentStore(paths, limits);
        store.Open();
        var atCap = Document(A, "a" + new string('b', DocumentStore.ReadAllowanceBytes));
        Assert.Equal(DocumentStore.ReadCap(limits), atCap.Length);
        store.Create(A, atCap);
        Assert.Equal(atCap, store.Read(A));

        store.Save(A, Document(A, "a" + new string('b', DocumentStore.ReadAllowanceBytes + 1)));

        Assert.Throws<InvalidDataException>(() => store.Read(A));
    }

    [Fact]
    public void Open_OrderLargerThanTheReadCap_ListsFoldersById()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, B);
        WriteFolder(paths, A);
        File.WriteAllText(paths.ProfileOrderFile, $"{{\"order\":[\"{B}\",\"{A}\"]}}" + new string(' ', 200 + DocumentStore.ReadAllowanceBytes));

        Assert.Equal([A, B], new DocumentStore(paths, Limit(200)).Open());
        Assert.Equal([A, B], new DocumentStore(paths).Open());
    }

    [Fact]
    public void Read_MissingDocumentInAProfileFolder_Throws()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        Directory.CreateDirectory(paths.ProfileFolder(A));

        var store = new DocumentStore(paths);
        Assert.Equal([A], store.Open());
        Assert.Throws<FileNotFoundException>(() => store.Read(A));
        Assert.True(store.Delete(A));
        Assert.Empty(new DocumentStore(paths).Open());
    }

    [Fact]
    public void Delete_FolderLink_RemovesTheLinkNotItsTarget()
    {
        if (OperatingSystem.IsWindows())
            return; // directory links need privileges there

        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        File.WriteAllBytes(Path.Combine(outside.Path, "document.json"), Document(A, "outside"));
        var paths = new DataPaths(temp.Path);
        Directory.CreateDirectory(paths.ProfilesDirectory);
        Directory.CreateSymbolicLink(paths.ProfileFolder(A), outside.Path);

        var store = new DocumentStore(paths);
        Assert.Equal([A], store.Open());
        Assert.Equal(Document(A, "outside"), store.Read(A));
        Assert.True(store.Delete(A));
        Assert.Empty(store.List());
        Assert.True(File.Exists(Path.Combine(outside.Path, "document.json")));
    }

    private static SpecLimits Limit(int documentBytes) =>
        SpecLimits.Default.With(new Dictionary<string, double> { ["documentBytes"] = documentBytes });

    private static void WriteFolder(DataPaths paths, string id)
    {
        Directory.CreateDirectory(paths.ProfileFolder(id));
        File.WriteAllBytes(paths.DocumentFile(id), Document(id, "w"));
    }
}
