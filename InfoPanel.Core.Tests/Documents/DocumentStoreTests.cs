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
        Assert.Equal(new[] { "profiles", "profiles.json", $"profiles/{B}", $"profiles/{B}/document.json", $"profiles/{B}/versions", $"profiles/{B}/versions/1.json", $"profiles/{C}", $"profiles/{C}/document.json" }
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

    /// <summary>
    /// The start-up removal of staging and deleted profiles' folders is bounded, all of them together: past its budget it
    /// stops, leaves the rest for the next start, and the store opens. The next start with room removes what is left.
    /// </summary>
    [Fact]
    public void Open_RemovesLeftoverFolders_WithinItsBudget_AndLeavesTheRestForTheNextStart()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        var hex = Guid.NewGuid().ToString("N");
        var leftovers = new[]
        {
            Path.Combine(paths.ProfilesDirectory, $"{B}.{hex}.tmp"),
            Path.Combine(paths.ProfilesDirectory, $"{C}.deleted-{hex}"),
        };
        foreach (var leftover in leftovers)
        {
            for (var i = 0; i < 10; i++)
            {
                Directory.CreateDirectory(Path.Combine(leftover, $"d{i}"));
                for (var j = 0; j < 4; j++)
                    File.WriteAllText(Path.Combine(leftover, $"d{i}", $"f{j}.json"), "x");
            }
        }
        int Entries() => leftovers.Sum(l => Directory.Exists(l) ? Directory.GetFileSystemEntries(l, "*", SearchOption.AllDirectories).Length + 1 : 0);
        Assert.Equal(102, Entries());

        var store = new DocumentStore(paths) { CleanupEntries = 30 };
        Assert.Equal([A], store.Open());

        Assert.InRange(Entries(), 102 - 30, 101);
        Assert.Equal(Document(A, "w"), store.Read(A));

        Assert.Equal([A], new DocumentStore(paths).Open());
        Assert.Equal(0, Entries());
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

    /// <summary>The order isn't a document: a file over a document's read cap is read, within its own bound.</summary>
    [Fact]
    public void Open_TheOrderIsNotHeldToTheDocumentLimit()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, B);
        WriteFolder(paths, A);
        File.WriteAllText(paths.ProfileOrderFile, $"{{\"order\":[\"{B}\",\"{A}\"]}}" + new string(' ', 200 + DocumentStore.ReadAllowanceBytes));

        Assert.Equal([B, A], new DocumentStore(paths, Limit(200)).Open());
        Assert.Equal([B, A], new DocumentStore(paths).Open());
    }

    private static SpecLimits ListBound(int bytes) => SpecLimits.Default.With(new Dictionary<string, double> { ["profileListBytes"] = bytes });

    private static int OrderBytes(params string[] ids) => Encoding.UTF8.GetByteCount($"{{\"order\":[{string.Join(",", ids.Select(id => $"\"{id}\""))}]}}");

    /// <summary>
    /// The profile list has its own byte bound (<c>profileListBytes</c>), enforced before writing: a create that would take
    /// <c>profiles.json</c> over it is refused before anything is written, so every list the store writes reads back.
    /// </summary>
    [Fact]
    public void Create_PastTheProfileListBound_IsRefused_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var limits = ListBound(OrderBytes(A, B));
        var store = new DocumentStore(paths, limits);
        store.Open();
        store.Create(A, Document(A, "a"));
        store.Create(B, Document(B, "b"));
        var before = File.ReadAllBytes(paths.ProfileOrderFile);
        Assert.Equal(OrderBytes(A, B), before.Length);

        var refused = Assert.ThrowsAny<IOException>(() => store.Create(C, Document(C, "c")));

        Assert.Contains("profileListBytes", refused.Message);
        Assert.Equal([A, B], store.List());
        Assert.Equal(before, File.ReadAllBytes(paths.ProfileOrderFile));
        Assert.Equal([paths.ProfileFolder(A), paths.ProfileFolder(B)], Directory.GetFileSystemEntries(paths.ProfilesDirectory).Order(StringComparer.Ordinal));
        Assert.Equal([A, B], new DocumentStore(paths, limits).Open());
        // A delete makes room again.
        Assert.True(store.Delete(A));
        store.Create(C, Document(C, "c"));
        Assert.Equal([B, C], store.List());
    }

    /// <summary>
    /// The bound holds while reading too: <c>profiles.json</c> over it is not read (the profiles are listed by id), one at
    /// it is; an order rebuilt over it is kept for the run but never written.
    /// </summary>
    [Fact]
    public void Open_TheProfileListIsReadWithinItsBound()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "a"));
        store.Create(B, Document(B, "b"));
        store.Reorder([B, A]);
        var size = File.ReadAllBytes(paths.ProfileOrderFile).Length;

        Assert.Equal([B, A], new DocumentStore(paths, ListBound(size)).Open());
        Assert.Equal([A, B], new DocumentStore(paths, ListBound(size - 1)).Open());

        File.Delete(paths.ProfileOrderFile);
        var small = new DocumentStore(paths, ListBound(OrderBytes(A)));
        Assert.Equal([A, B], small.Open());
        Assert.False(File.Exists(paths.ProfileOrderFile));
        Assert.ThrowsAny<IOException>(() => small.Create(C, Document(C, "c")));
        Assert.False(Directory.Exists(paths.ProfileFolder(C)));
    }

    /// <summary>
    /// <c>profiles.json</c> is read tolerantly: members this engine doesn't know are ignored, kept when the order is
    /// rewritten, and count towards the bound.
    /// </summary>
    [Fact]
    public void TheOrderFile_KeepsMembersThisEngineDoesntKnow()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "a"));
        File.WriteAllText(paths.ProfileOrderFile, $$"""{"later": {"x": [1, "two"]}, "order": ["{{A}}"], "note": "kept"}""");

        var next = new DocumentStore(paths);
        Assert.Equal([A], next.Open());
        next.Create(B, Document(B, "b"));
        next.Reorder([B, A]);
        next.Create(C, Document(C, "c"));
        next.Delete(C);

        var stored = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.ProfileOrderFile))!.AsObject();
        Assert.Equal(["later", "order", "note"], stored.Select(p => p.Key));
        Assert.Equal("""{"x":[1,"two"]}""", stored["later"]!.ToJsonString());
        Assert.Equal("kept", stored["note"]!.GetValue<string>());
        Assert.Equal($"""["{B}","{A}"]""", stored["order"]!.ToJsonString());

        // The members it keeps are part of the list's size.
        var size = File.ReadAllBytes(paths.ProfileOrderFile).Length;
        var bounded = new DocumentStore(paths, ListBound(size + 38));
        Assert.Equal([B, A], bounded.Open());
        Assert.ThrowsAny<IOException>(() => bounded.Create(C, Document(C, "c")));
    }

    /// <summary>
    /// A member this engine doesn't know is kept as JSON text, whatever it holds (a string or a name that holds no text, a
    /// value nested deeper than a default reader goes, within jsonDepth): the order is read, and each member is written
    /// back as it was, compacted. (Changed on purpose: such a member made the file unreadable, and the rewrite then
    /// dropped the order and every other member.)
    /// </summary>
    [Fact]
    public void TheOrderFile_KeepsAMemberItCantDecode_AsItWas()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "a"));
        store.Create(B, Document(B, "b"));
        var deep = new string('[', 100) + new string(']', 100);
        File.WriteAllText(paths.ProfileOrderFile, $$"""{"order": ["{{B}}", "{{A}}"], "keep": {"x": 1}, "later": "\uD800", "\uD800": [ 1, 2 ], "deep": {{deep}}}""");

        var next = new DocumentStore(paths);
        Assert.Equal([B, A], next.Open());
        next.Create(C, Document(C, "c"));

        Assert.Equal([B, A, C], new DocumentStore(paths).Open());
        Assert.Equal($$"""{"order":["{{B}}","{{A}}","{{C}}"],"keep":{"x":1},"later":"\uD800","\uD800":[1,2],"deep":{{deep}}}""",
            File.ReadAllText(paths.ProfileOrderFile));

        // A damaged order is rebuilt by id, and the members beside it are still kept.
        File.WriteAllText(paths.ProfileOrderFile, """{"keep": 1, "order": {"not": "a list"}}""");
        Assert.Equal([A, B, C], new DocumentStore(paths).Open());
        Assert.Equal($$"""{"keep":1,"order":["{{A}}","{{B}}","{{C}}"]}""", File.ReadAllText(paths.ProfileOrderFile));
    }

    /// <summary>
    /// A well-formed order file the store can't hold whole (over its bound, or nested deeper than jsonDepth) is left as
    /// it is: the profiles are listed by id and the run keeps the order in memory, writing nothing, so nothing in the
    /// file is lost. A damaged one (not JSON) is rebuilt as before (<see cref="Open_MissingOrUnreadableOrder_ListsFoldersById"/>).
    /// </summary>
    [Theory]
    [InlineData("over its bound")]
    [InlineData("deeper than jsonDepth")]
    public void TheOrderFile_ThatCantBeHeldWhole_IsLeftAsItIs_AndTheRunKeepsTheOrderInMemory(string why)
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        WriteFolder(paths, B);
        var text = why == "over its bound"
            ? $$"""{"order":["{{B}}","{{A}}"],"later":"{{new string('x', 4096)}}"}"""
            : $$"""{"order":["{{B}}","{{A}}"],"later":{{new string('[', 300)}}{{new string(']', 300)}}}""";
        File.WriteAllText(paths.ProfileOrderFile, text);

        var store = new DocumentStore(paths, why == "over its bound" ? ListBound(1024) : null);
        Assert.Equal([A, B], store.Open());
        store.Create(C, Document(C, "c"));
        store.Reorder([C, B, A]);
        Assert.True(store.Delete(B));

        Assert.Equal([C, A], store.List());
        Assert.Equal(text, File.ReadAllText(paths.ProfileOrderFile));
        Assert.Equal([A, C], new DocumentStore(paths, why == "over its bound" ? ListBound(1024) : null).Open());
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

    // Earlier saves: profiles/{id}/versions/{n}.json.

    /// <summary>A save keeps the bytes it replaces, byte for byte, as the next version; a new profile has none.</summary>
    [Fact]
    public void Save_KeepsThePreviousSavedBytesAsAVersion()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        var first = Encoding.UTF8.GetBytes("first, not even JSON é");
        store.Create(A, first);
        Assert.Empty(VersionNames(paths, A));

        store.Save(A, Document(A, "second"));
        Assert.Equal(["1.json"], VersionNames(paths, A));
        Assert.Equal(first, File.ReadAllBytes(paths.VersionFile(A, 1)));

        store.Save(A, Document(A, "third"));
        Assert.Equal(["1.json", "2.json"], VersionNames(paths, A));
        Assert.Equal(Document(A, "second"), File.ReadAllBytes(paths.VersionFile(A, 2)));
        Assert.Equal(Document(A, "third"), store.Read(A));
        Assert.Equal(Path.Combine(paths.ProfileFolder(A), "versions", "2.json"), paths.VersionFile(A, 2));
    }

    /// <summary>A save of the bytes already saved keeps no version, so saving a clean document pushes none out.</summary>
    [Fact]
    public void Save_OfTheSameBytes_KeepsNoVersion()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "a"));

        store.Save(A, Document(A, "a"));
        Assert.Empty(VersionNames(paths, A));

        store.Save(A, Document(A, "b"));
        store.Save(A, Document(A, "b"));
        store.Save(A, Document(A, "b"));
        Assert.Equal(["1.json"], VersionNames(paths, A));
        Assert.Equal(Document(A, "a"), File.ReadAllBytes(paths.VersionFile(A, 1)));
    }

    /// <summary>At most ten versions are kept; the oldest goes first.</summary>
    [Fact]
    public void Save_KeepsTenVersions_TheOldestDroppedFirst()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "s0"));
        for (var i = 1; i <= 12; i++)
            store.Save(A, Document(A, $"s{i}"));

        // Versions 1 to 12 held s0 to s11; the ten newest are kept.
        Assert.Equal(Enumerable.Range(3, 10).Select(n => $"{n}.json"), VersionNames(paths, A));
        for (var n = 3; n <= 12; n++)
            Assert.Equal(Document(A, $"s{n - 1}"), File.ReadAllBytes(paths.VersionFile(A, (ulong)n)));
        Assert.Equal(Document(A, "s12"), store.Read(A));
        Assert.Equal((10, 64L * 1024 * 1024), (DocumentStore.VersionsKept, DocumentStore.VersionsKeptBytes));
    }

    /// <summary>
    /// The versions kept stay within their bytes, the oldest dropped first, but the newest is always kept, however large.
    /// </summary>
    [Fact]
    public void Save_KeepsVersionsWithinTheirBytes_ButAlwaysTheNewest()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths) { MaxVersionBytes = 1000 };
        store.Open();
        static byte[] Of(int size, char c) => Encoding.UTF8.GetBytes(new string(c, size));

        store.Create(A, Of(500, 'a'));
        store.Save(A, Of(500, 'b'));
        store.Save(A, Of(500, 'c'));
        Assert.Equal(["1.json", "2.json"], VersionNames(paths, A)); // 1,000 bytes: within

        store.Save(A, Of(1500, 'd'));
        Assert.Equal(["2.json", "3.json"], VersionNames(paths, A)); // 1,500: the oldest goes

        store.Save(A, Of(10, 'e'));
        Assert.Equal(["4.json"], VersionNames(paths, A)); // the newest alone is over: kept, the rest go
        Assert.Equal(Of(1500, 'd'), File.ReadAllBytes(paths.VersionFile(A, 4)));

        store.Save(A, Of(10, 'f'));
        Assert.Equal(["5.json"], VersionNames(paths, A));
        store.Save(A, Of(10, 'g'));
        Assert.Equal(["5.json", "6.json"], VersionNames(paths, A));
    }

    /// <summary>A delete removes the versions with the folder; a new profile, a duplicate's copy included, starts with none.</summary>
    [Fact]
    public void Versions_GoWithTheirProfile_AndANewProfileHasNone()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "a"));
        store.Save(A, Document(A, "b"));
        Assert.Equal(["1.json"], VersionNames(paths, A));

        store.Create(B, store.Read(A)); // as a duplicate stores its copy
        Assert.Empty(VersionNames(paths, B));

        Assert.True(store.Delete(A));
        Assert.False(Directory.Exists(paths.ProfileFolder(A)));
        store.Create(A, Document(A, "again"));
        Assert.Empty(VersionNames(paths, A));
        store.Save(A, Document(A, "again, saved"));
        Assert.Equal(["1.json"], VersionNames(paths, A));
        Assert.Equal(Document(A, "again"), File.ReadAllBytes(paths.VersionFile(A, 1)));
    }

    /// <summary>
    /// The next version is numbered after the highest present (in any letter case, so none is written over on a
    /// case-insensitive drive); every other name in the folder is left alone and doesn't count.
    /// </summary>
    [Fact]
    public void Save_NumbersAfterTheHighestVersion_AndLeavesOtherNamesAlone()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        WriteFolder(paths, B);
        var hex = Guid.NewGuid().ToString("N");
        var others = new[]
        {
            "notes.txt", "01.json", "0.json", "7.json.bak", "x.json", "1.json.tmp", $"9.json.{hex}.tmp.old", "-3.json", "+3.json",
            "20000000000000000000.json", "3.json~", "٣.json",
        };
        foreach (var name in others)
        {
            Directory.CreateDirectory(paths.VersionsFolder(A));
            File.WriteAllText(Path.Combine(paths.VersionsFolder(A), name), "x");
        }
        File.WriteAllBytes(paths.VersionFile(A, 7), Document(A, "seven"));
        Directory.CreateDirectory(paths.VersionsFolder(B));
        File.WriteAllText(Path.Combine(paths.VersionsFolder(B), "12.JSON"), "x");

        var store = new DocumentStore(paths);
        store.Open();
        store.Save(A, Document(A, "next"));
        store.Save(B, Document(B, "next"));

        Assert.Equal(Document(A, "w"), File.ReadAllBytes(paths.VersionFile(A, 8)));
        Assert.Equal(Document(A, "seven"), File.ReadAllBytes(paths.VersionFile(A, 7)));
        Assert.All(others, name => Assert.Equal("x", File.ReadAllText(Path.Combine(paths.VersionsFolder(A), name))));
        Assert.Equal(others.Length + 2, Directory.GetFileSystemEntries(paths.VersionsFolder(A)).Length);
        Assert.Equal(Document(B, "w"), File.ReadAllBytes(paths.VersionFile(B, 13)));
        Assert.Equal("x", File.ReadAllText(Path.Combine(paths.VersionsFolder(B), "12.JSON")));
    }

    /// <summary>A version's temp file (<c>{n}.json.{32 hex}.tmp</c>) is removed at start-up; nothing else in the folder is, and a linked versions folder isn't entered.</summary>
    [Fact]
    public void Open_RemovesVersionTempFiles_AndLeavesTheRest()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        WriteFolder(paths, B);
        var hex = Guid.NewGuid().ToString("N");
        var versions = paths.VersionsFolder(A);
        Directory.CreateDirectory(versions);
        var leftover = Path.Combine(versions, $"3.json.{hex}.tmp");
        File.WriteAllText(leftover, "{\"half");
        var kept = new[]
        {
            $"notes.json.{hex}.tmp", $"03.json.{hex}.tmp", $"3.json.{hex}.tmp.x", "3.json", $"3.json.{hex[..31]}.tmp", $"3.json.{hex}.tmp.tmp",
            $"document.json.{hex}.tmp.x",
        };
        foreach (var name in kept)
            File.WriteAllText(Path.Combine(versions, name), "x");

        using var outside = new TempDirectory();
        var linked = Path.Combine(outside.Path, $"4.json.{hex}.tmp");
        File.WriteAllText(linked, "x");
        if (!OperatingSystem.IsWindows())
            Directory.CreateSymbolicLink(paths.VersionsFolder(B), outside.Path);

        Assert.Equal([A, B], new DocumentStore(paths).Open());

        Assert.False(File.Exists(leftover));
        Assert.All(kept, name => Assert.True(File.Exists(Path.Combine(versions, name)), name));
        Assert.True(File.Exists(linked));
    }

    /// <summary>
    /// A recovery file's temp file (<c>recovery.json.{32 hex}.tmp</c>) is removed at start-up, as the document's is; the
    /// recovery file itself and every other name are left.
    /// </summary>
    [Fact]
    public void Open_RemovesRecoveryTempFiles_AndLeavesTheRest()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        var hex = Guid.NewGuid().ToString("N");
        var folder = paths.ProfileFolder(A);
        var leftover = Path.Combine(folder, $"recovery.json.{hex}.tmp");
        File.WriteAllText(leftover, "{\"half");
        var kept = new[]
        {
            "recovery.json", $"recovery.json.{hex}.tmp.x", $"recovery.json.{hex[..31]}.tmp", "recovery.json.tmp", $"Recovery.json.{Guid.NewGuid():N}.tmp",
            $"recovery.jsonx.{hex}.tmp",
        };
        foreach (var name in kept)
            File.WriteAllText(Path.Combine(folder, name), "x");

        Assert.Equal([A], new DocumentStore(paths).Open());

        Assert.False(File.Exists(leftover));
        Assert.All(kept, name => Assert.True(File.Exists(Path.Combine(folder, name)), name));
    }

    /// <summary>
    /// The recovery file is written only while the profile is listed (a write after its delete writes nothing, and never
    /// makes its folder again), read within the read cap, and removed with nothing left.
    /// </summary>
    [Fact]
    public void Recovery_WrittenOnlyWhileListed_ReadAndRemoved()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "a"));
        Assert.Null(store.ReadRecovery(A));

        store.WriteRecovery(A, "{\"kept\":1}"u8.ToArray());
        Assert.Equal("{\"kept\":1}"u8.ToArray(), store.ReadRecovery(A));
        store.RemoveRecovery(A);
        Assert.False(File.Exists(paths.RecoveryFile(A)));
        store.RemoveRecovery(A);

        store.Delete(A);
        Assert.Throws<KeyNotFoundException>(() => store.WriteRecovery(A, "{}"u8.ToArray()));
        Assert.False(Directory.Exists(paths.ProfileFolder(A)));
        store.RemoveRecovery(A);
        Assert.Throws<KeyNotFoundException>(() => store.ReadRecovery(A));
    }

    /// <summary>
    /// A <c>versions</c> entry that isn't a folder of the profile's own (a link, to a folder or to nothing, or a file) is
    /// never followed or written: the save keeps no version and saves.
    /// </summary>
    [Fact]
    public void Save_WithVersionsALinkOrAFile_KeepsNoVersion_AndSaves()
    {
        if (OperatingSystem.IsWindows())
            return; // directory links need privileges there

        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        WriteFolder(paths, A);
        WriteFolder(paths, B);
        WriteFolder(paths, C);
        Directory.CreateSymbolicLink(paths.VersionsFolder(A), outside.Path);
        File.WriteAllText(paths.VersionsFolder(B), "not a folder");
        Directory.CreateSymbolicLink(paths.VersionsFolder(C), Path.Combine(outside.Path, "nowhere"));

        var store = new DocumentStore(paths);
        store.Open();
        foreach (var id in new[] { A, B, C })
        {
            store.Save(id, Document(id, "next"));
            Assert.Equal(Document(id, "next"), store.Read(id));
        }

        Assert.Empty(Directory.GetFileSystemEntries(outside.Path));
        Assert.Equal("not a folder", File.ReadAllText(paths.VersionsFolder(B)));
        Assert.NotNull(new FileInfo(paths.VersionsFolder(C)).LinkTarget);
    }

    /// <summary>A saved document that can't be read (over the read cap) keeps no version; the save goes on.</summary>
    [Fact]
    public void Save_WhenThePreviousDocumentCantBeRead_KeepsNoVersion_AndSaves()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths, Limit(1000));
        WriteFolder(paths, A);
        File.WriteAllText(paths.DocumentFile(A), new string('x', store.MaxBytes + 1));
        store.Open();

        store.Save(A, Document(A, "next"));

        Assert.Equal(Document(A, "next"), store.Read(A));
        Assert.Empty(VersionNames(paths, A));
    }

    /// <summary>A version that can't be written is logged; the save itself goes on.</summary>
    [Fact]
    public void Save_WhoseVersionCantBeWritten_StillSaves()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var faults = new MatchFaults
        {
            Fail = (step, path) => step == StorageStep.CreateTemp && path.StartsWith(paths.VersionsFolder(A), StringComparison.Ordinal),
        };
        var store = new DocumentStore(paths, null, faults);
        store.Open();
        store.Create(A, Document(A, "a"));

        store.Save(A, Document(A, "b"));

        Assert.Equal(Document(A, "b"), store.Read(A));
        Assert.Contains(faults.Steps, s => s.Step == StorageStep.CreateTemp && s.Path.StartsWith(paths.VersionsFolder(A), StringComparison.Ordinal));
        Assert.Empty(VersionNames(paths, A));
    }

    /// <summary>
    /// The version is whole on disk, its file and its folder flushed, before the saved document is replaced; and versions
    /// are dropped only once the new document is in place.
    /// </summary>
    [Fact]
    public void Save_PutsTheVersionOnDisk_BeforeTheDocumentIsReplaced()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var faults = new MatchFaults();
        var store = new DocumentStore(paths, null, faults) { MaxVersions = 1 };
        store.Open();
        store.Create(A, Document(A, "a"));
        store.Save(A, Document(A, "b"));

        foreach (var (save, version) in new[] { ("c", 2UL), ("d", 3UL) })
        {
            faults.Steps.Clear();
            store.Save(A, Document(A, save));
            var steps = faults.Steps;
            int Index(StorageStep step, Func<string, bool> path) => steps.FindIndex(s => s.Step == step && path(s.Path));
            var versionFile = paths.VersionFile(A, version);
            var flushed = Index(StorageStep.FlushTemp, p => p.StartsWith(versionFile + ".", StringComparison.Ordinal));
            var replaced = Index(StorageStep.ReplaceFile, p => p == versionFile);
            var folderFlushed = Index(StorageStep.SyncFolder, p => p == paths.VersionsFolder(A));
            var documentTemp = Index(StorageStep.CreateTemp, p => p.StartsWith(paths.DocumentFile(A) + ".", StringComparison.Ordinal));
            var documentReplaced = Index(StorageStep.ReplaceFile, p => p == paths.DocumentFile(A));
            var dropped = Index(StorageStep.DeleteFile, p => p == paths.VersionFile(A, version - 1));
            Assert.True(0 <= flushed && flushed < replaced && replaced < folderFlushed && folderFlushed < documentTemp && documentTemp < documentReplaced && documentReplaced < dropped,
                string.Join(", ", steps));
            StepFaults.AssertEachChangeFlushedFirst(steps);
            Assert.Equal([$"{version}.json"], VersionNames(paths, A));
        }
    }

    /// <summary>
    /// Saves that fail after keeping the document they replace keep it once: a retry finds it as the newest version, with
    /// the same bytes, and reuses it (its folder flushed again before the document's write) instead of keeping it again,
    /// so ten failed saves and a successful one leave the earlier distinct saves, not ten copies of one.
    /// </summary>
    [Fact]
    public void FailedSaves_KeepTheDocumentTheyReplaceOnce()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var faults = new MatchFaults();
        var store = new DocumentStore(paths, null, faults);
        store.Open();
        store.Create(A, Document(A, "s0"));
        for (var i = 1; i <= 10; i++)
            store.Save(A, Document(A, $"s{i}"));

        bool DocumentTemp(StorageStep step, string path) =>
            step == StorageStep.CreateTemp && path.StartsWith(paths.DocumentFile(A) + ".", StringComparison.Ordinal);
        faults.Fail = DocumentTemp;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            faults.Steps.Clear();
            Assert.Throws<IOException>(() => store.Save(A, Document(A, "s11")));
            if (attempt == 0)
                continue;
            var steps = faults.Steps;
            Assert.DoesNotContain(steps, s => s.Step == StorageStep.CreateTemp && s.Path.StartsWith(paths.VersionsFolder(A), StringComparison.Ordinal));
            var flushed = steps.FindIndex(s => s.Step == StorageStep.SyncFolder && s.Path == paths.VersionsFolder(A));
            Assert.True(0 <= flushed && flushed < steps.FindIndex(s => DocumentTemp(s.Step, s.Path)), string.Join(", ", steps));
        }
        Assert.Equal(Document(A, "s10"), store.Read(A));

        faults.Fail = null;
        store.Save(A, Document(A, "s11"));

        // Versions 2 to 11 hold s1 to s10, each earlier save once; version 1 (s0) was dropped.
        Assert.Equal(Enumerable.Range(2, 10).Select(n => $"{n}.json"), VersionNames(paths, A));
        for (var n = 2; n <= 11; n++)
            Assert.Equal(Document(A, $"s{n - 1}"), File.ReadAllBytes(paths.VersionFile(A, (ulong)n)));
        Assert.Equal(Document(A, "s11"), store.Read(A));
    }

    /// <summary>
    /// Dropping runs after every save that writes the document, whether or not it kept a version: an old version a save
    /// couldn't drop goes at the next save, a save of the same bytes included.
    /// </summary>
    [Fact]
    public void AVersionASaveCouldntDrop_GoesAtTheNextSave_EvenOneThatKeepsNone()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var faults = new MatchFaults();
        var store = new DocumentStore(paths, null, faults);
        store.Open();
        store.Create(A, Document(A, "s0"));
        for (var i = 1; i <= 10; i++)
            store.Save(A, Document(A, $"s{i}"));

        faults.Fail = (step, path) => step == StorageStep.DeleteFile && path == paths.VersionFile(A, 1);
        store.Save(A, Document(A, "s11"));
        Assert.Equal(Enumerable.Range(1, 11).Select(n => $"{n}.json"), VersionNames(paths, A));

        faults.Fail = null;
        store.Save(A, Document(A, "s11"));

        Assert.Equal(Enumerable.Range(2, 10).Select(n => $"{n}.json"), VersionNames(paths, A));
        Assert.Equal(Document(A, "s11"), store.Read(A));
    }

    /// <summary>
    /// Only regular files count as versions and are dropped, the type checked on an open handle (RegularFile): a FIFO with
    /// a version's name is neither counted nor removed, though numbering still steps past it.
    /// </summary>
    [Fact]
    public void Save_NeverCountsOrDropsAFifoWithAVersionName()
    {
        if (!Fifo.Supported)
            return;

        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var store = new DocumentStore(paths);
        store.Open();
        store.Create(A, Document(A, "s0"));
        Directory.CreateDirectory(paths.VersionsFolder(A));
        var fifo = paths.VersionFile(A, 1);
        Fifo.Create(fifo);

        Fifo.Within(30, () =>
        {
            for (var i = 1; i <= 11; i++)
                store.Save(A, Document(A, $"s{i}"));
        }, fifo);

        // Versions 2 to 12 held s0 to s10; the ten newest regular files are kept, and the FIFO is left alone.
        Assert.Equal(new[] { "1.json" }.Concat(Enumerable.Range(3, 10).Select(n => $"{n}.json")), VersionNames(paths, A));
        Assert.Equal(PathState.Other, RegularFile.StateOf(fifo));
        Assert.Throws<NotARegularFileException>(() => RegularFile.OpenRead(fifo));
        for (var n = 3; n <= 12; n++)
            Assert.Equal(Document(A, $"s{n - 2}"), File.ReadAllBytes(paths.VersionFile(A, (ulong)n)));
    }

    /// <summary>
    /// The version's write flushes what its crash rule needs (data-folder.md, "Earlier saves"): its file and
    /// <c>versions/</c> on every save; the profile's folder when <c>versions/</c> is created; the data folder and
    /// <c>profiles/</c> only until a flush of them for the profile's versions has succeeded in this run, since a folder
    /// that exists isn't known to be on disk after a flush failed.
    /// </summary>
    [Fact]
    public void Save_TheVersionsWrite_FlushesOnlyWhatItNeeds()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var setup = new DocumentStore(paths);
        setup.Open();
        setup.Create(A, Document(A, "a"));

        // The version's steps: a save's steps up to the version's rename and the flush that follows it.
        List<(StorageStep Step, string Path)> VersionSteps(MatchFaults faults)
        {
            var replaced = faults.Steps.FindIndex(s => s.Step == StorageStep.ReplaceFile && s.Path.StartsWith(paths.VersionsFolder(A), StringComparison.Ordinal));
            Assert.True(replaced >= 0, string.Join(", ", faults.Steps));
            return faults.Steps.Take(replaced + 2).ToList();
        }
        string[] Flushed(List<(StorageStep Step, string Path)> steps) =>
            steps.Where(s => s.Step == StorageStep.SyncFolder).Select(s => s.Path).ToArray();
        string[] Everything = [paths.Root, paths.ProfilesDirectory, paths.ProfileFolder(A), paths.VersionsFolder(A)];

        var first = new MatchFaults();
        var store = new DocumentStore(paths, null, first);
        store.Open();
        store.Save(A, Document(A, "b"));
        var steps = VersionSteps(first);
        Assert.Equal(Everything, Flushed(steps));
        var created = steps.FindIndex(s => s.Step == StorageStep.CreateFolder && s.Path == paths.VersionsFolder(A));
        Assert.True(created >= 0 && steps[created + 1] == (StorageStep.SyncFolder, paths.ProfileFolder(A)), string.Join(", ", steps));
        StepFaults.AssertEachChangeFlushedFirst(steps);

        first.Steps.Clear();
        store.Save(A, Document(A, "c"));
        steps = VersionSteps(first);
        Assert.Equal([paths.VersionsFolder(A)], Flushed(steps));
        Assert.Contains((StorageStep.ReplaceFile, paths.VersionFile(A, 2)), steps);
        StepFaults.AssertEachChangeFlushedFirst(steps);

        // The next run, whose first flush of the data folder fails: that save keeps no version, and the next one flushes
        // everything above its version again before relying on it.
        var rootFlushes = 0;
        var next = new MatchFaults { Fail = (step, path) => step == StorageStep.SyncFolder && path == paths.Root && ++rootFlushes == 1 };
        store = new DocumentStore(paths, null, next);
        store.Open();
        store.Save(A, Document(A, "d"));
        Assert.Equal(["1.json", "2.json"], VersionNames(paths, A));
        next.Steps.Clear();
        store.Save(A, Document(A, "e"));
        steps = VersionSteps(next);
        Assert.Equal(Everything, Flushed(steps));
        Assert.Equal(["1.json", "2.json", "3.json"], VersionNames(paths, A));

        next.Steps.Clear();
        store.Save(A, Document(A, "f"));
        Assert.Equal([paths.VersionsFolder(A)], Flushed(VersionSteps(next)));
    }

    /// <summary>
    /// A version is reused only once its content is on disk: a newest version that holds the replaced document's bytes,
    /// but was written without a flush (overwritten before this start, say), is flushed on the handle it was compared
    /// through before the document is replaced; when that flush fails, the replaced document is kept as a new version.
    /// </summary>
    [Fact]
    public void Save_ReusesAVersionOnlyOnceItsContentIsFlushed()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var setup = new DocumentStore(paths);
        setup.Open();
        setup.Create(A, Document(A, "s0"));
        setup.Save(A, Document(A, "s1"));
        // Before this start, the newest version was overwritten with the saved document's bytes and never flushed.
        File.WriteAllBytes(paths.VersionFile(A, 1), Document(A, "s1"));

        var faults = new MatchFaults();
        var store = new DocumentStore(paths, null, faults);
        store.Open();
        store.Save(A, Document(A, "s2"));
        var steps = faults.Steps;
        var flushed = steps.IndexOf((StorageStep.FlushFile, paths.VersionFile(A, 1)));
        var documentTemp = steps.FindIndex(s => s.Step == StorageStep.CreateTemp && s.Path.StartsWith(paths.DocumentFile(A) + ".", StringComparison.Ordinal));
        Assert.True(0 <= flushed && flushed < documentTemp, string.Join(", ", steps));
        Assert.DoesNotContain(steps, s => s.Step == StorageStep.CreateTemp && s.Path.StartsWith(paths.VersionsFolder(A), StringComparison.Ordinal));
        Assert.Equal(["1.json"], VersionNames(paths, A));
        Assert.Equal(Document(A, "s2"), store.Read(A));

        // Its flush fails: the replaced document is kept as a new version, as without reuse.
        File.WriteAllBytes(paths.VersionFile(A, 1), Document(A, "s2"));
        faults.Fail = (step, path) => step == StorageStep.FlushFile && path == paths.VersionFile(A, 1);
        store.Save(A, Document(A, "s3"));
        Assert.Equal(["1.json", "2.json"], VersionNames(paths, A));
        Assert.Equal(Document(A, "s2"), File.ReadAllBytes(paths.VersionFile(A, 2)));
        Assert.Equal(Document(A, "s3"), store.Read(A));
    }

    /// <summary>
    /// The reused version's flush is the native one, checked: <c>EINVAL</c> and <c>ENOTSUP</c>, which .NET's own file flush
    /// ignores, are failures too, and any failure keeps the replaced document as a new version.
    /// </summary>
    [Fact]
    public void Save_DoesNotReuseAVersionWhoseNativeFlushFails()
    {
        if (OperatingSystem.IsWindows())
            return; // the flush there is .NET's, which reports every failure
        foreach (var errno in new[] { 22, OperatingSystem.IsMacOS() ? 45 : 95, 5 })
        {
            using var temp = new TempDirectory();
            var paths = new DataPaths(temp.Path);
            var setup = new DocumentStore(paths);
            setup.Open();
            setup.Create(A, Document(A, "s0"));
            setup.Save(A, Document(A, "s1"));
            File.WriteAllBytes(paths.VersionFile(A, 1), Document(A, "s1"));

            var faults = new MatchFaults { FileSyncErrno = errno };
            var store = new DocumentStore(paths, null, faults);
            store.Open();
            store.Save(A, Document(A, "s2"));

            Assert.Equal(["1.json", "2.json"], VersionNames(paths, A));
            Assert.Equal(Document(A, "s1"), File.ReadAllBytes(paths.VersionFile(A, 2)));
            Assert.Equal(Document(A, "s2"), store.Read(A));
        }
    }

    /// <summary>The names in a profile's versions folder, by length and then in ordinal order (so by number); none when it isn't there.</summary>
    private static string[] VersionNames(DataPaths paths, string id)
    {
        var folder = Path.Combine(paths.ProfileFolder(id), "versions");
        return Directory.Exists(folder)
            ? Directory.GetFileSystemEntries(folder).Select(p => Path.GetFileName(p)).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToArray()
            : [];
    }

    private static SpecLimits Limit(int documentBytes) =>
        SpecLimits.Default.With(new Dictionary<string, double> { ["documentBytes"] = documentBytes });

    private static void WriteFolder(DataPaths paths, string id)
    {
        Directory.CreateDirectory(paths.ProfileFolder(id));
        File.WriteAllBytes(paths.DocumentFile(id), Document(id, "w"));
    }
}
