using System.Security.Cryptography;
using System.Text;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using Xunit;
using static InfoPanel.Core.Tests.Documents.StorageTestData;

namespace InfoPanel.Core.Tests.Persistence;

/// <summary>
/// The data folder's marker, <c>store.json</c> (docs/2.0/data-folder.md): written when absent, read before anything
/// repairs, sweeps or writes in the folder, and a start-up stopped by it, with nothing touched, when this engine can't
/// honour it.
/// </summary>
public sealed class StoreMarkerTests : IDisposable
{
    private const string A = "0a000000-0000-4000-8000-000000000000";
    private const string B = "0b000000-0000-4000-8000-000000000000";
    private const string Written = """{"format":"infopanel.store/1","requires":[]}""";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly List<Workspace> _open = [];

    private DataPaths Paths => new(_temp.Path);

    private string Marker => Path.Combine(_temp.Path, "store.json");

    public void Dispose()
    {
        foreach (var workspace in _open)
            workspace.Dispose();
        _temp.Dispose();
    }

    private Workspace StartWorkspace()
    {
        var workspace = Workspace.Start(Paths, null, Now);
        _open.Add(workspace);
        return workspace;
    }

    [Fact]
    public void AFirstStart_WritesTheMarker()
    {
        StartWorkspace();

        Assert.Equal(Written, File.ReadAllText(Marker));
    }

    [Fact]
    public void AStoreWithoutAMarker_GetsOne_AndKeepsItsProfiles()
    {
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        Assert.False(File.Exists(Marker));

        var workspace = StartWorkspace();

        Assert.Equal(Written, File.ReadAllText(Marker));
        Assert.Equal([A], workspace.Documents.List());
    }

    /// <summary>A marker is read tolerantly: members this engine doesn't know are ignored, and the file is never rewritten.</summary>
    [Theory]
    [InlineData("""{"requires": [], "format": "infopanel.store/1", "note": {"x": [1, 2]}}""")]
    [InlineData("""{"format":"infopanel.store/1"}""")]
    public void AMarkerThisEngineHonours_IsLeftAsItIs(string marker)
    {
        File.WriteAllText(Marker, marker);

        StartWorkspace();

        Assert.Equal(marker, File.ReadAllText(Marker));
    }

    public static TheoryData<string, string> Refused => new()
    {
        // What it says: the capability it lacks, the other major version, or that the file can't be read.
        { """{"format":"infopanel.store/1","requires":["test.unknown"]}""", "test.unknown" },
        { """{"format":"infopanel.store/1","requires":["test.one","test.two"]}""", "test.one, test.two" },
        { """{"format":"infopanel.store/2","requires":[]}""", "infopanel.store/2" },
        { "{ not json", "can't be read" },
        { "", "can't be read" },
        { "[]", "can't be read" },
        { """{"requires":[]}""", "can't be read" },
        { """{"format":"infopanel.document/1","requires":[]}""", "can't be read" },
        { """{"format":"infopanel.store/1","requires":"test.unknown"}""", "can't be read" },
        { """{"format":"infopanel.store/1","requires":[1]}""", "can't be read" },
        { """{"format":"infopanel.store/1","requires":[],"requires":["test.unknown"]}""", "can't be read" },
        { """{"format":"infopanel.store/1","format":"infopanel.store/2"}""", "can't be read" },
        { "\"" + new string('x', 65_536) + "\"", "can't be read" },
        { "(folder)", "can't be read" },
    };

    /// <summary>
    /// A marker this engine can't honour stops start-up with a message that names why, before anything repairs, sweeps or
    /// writes: the leftovers of an interrupted write, an unlisted profile and an old unreferenced blob are all as they were.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public void AMarkerThisEngineCantHonour_StopsStartUp_AndNothingIsTouched(string marker, string named)
    {
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var old = stores.Blobs.Put("old"u8.ToArray());
        File.SetLastWriteTimeUtc(Paths.BlobFile(old), Now.AddDays(-30).UtcDateTime);
        Directory.CreateDirectory(Paths.ProfileFolder(B));
        File.WriteAllBytes(Paths.DocumentFile(B), Document(B, "unlisted"));
        Directory.CreateDirectory(Path.Combine(Paths.ProfilesDirectory, $"{B}.{new string('1', 32)}.tmp"));
        File.WriteAllText(Path.Combine(_temp.Path, $"profiles.json.{new string('2', 32)}.tmp"), "x");
        File.WriteAllText(Path.Combine(_temp.Path, $"store.json.{new string('3', 32)}.tmp"), "x");
        if (marker == "(folder)")
            Directory.CreateDirectory(Path.Combine(Marker, "inside"));
        else
            File.WriteAllText(Marker, marker);
        var before = TreeHash(_temp.Path);

        var refused = Assert.Throws<StoreMarkerException>(StartWorkspace);

        Assert.Contains(named, refused.Message);
        Assert.Contains(Marker, refused.Message);
        Assert.Equal(before, TreeHash(_temp.Path));
    }

    /// <summary>Every file and folder under <paramref name="root"/>: names, kinds, bytes and write times.</summary>
    internal static string TreeHash(string root)
    {
        var text = new StringBuilder();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(entry);
            text.Append(Path.GetRelativePath(root, entry)).Append('|').Append(info.Attributes).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|');
            if (File.Exists(entry))
                text.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))));
            text.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
