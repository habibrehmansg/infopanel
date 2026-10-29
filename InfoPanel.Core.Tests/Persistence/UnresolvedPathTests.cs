using System.Text;
using InfoPanel.Core.Tests.Documents;
using InfoPanel.Documents;
using InfoPanel.Import;
using InfoPanel.Persistence;
using InfoPanel.Settings;
using Xunit;
using static InfoPanel.Core.Tests.Documents.StorageTestData;

namespace InfoPanel.Core.Tests.Persistence;

/// <summary>
/// What the engine opens or lists in its data folder goes through one layer (<see cref="RegularFile"/>,
/// docs/2.0/data-folder.md, "Special files and links"): a link whose target can't be reached, or something that isn't the
/// folder or file expected, is never read as empty or absent. The sweep then deletes nothing; a folder the stores need
/// stops start-up with nothing written; the engine's own files are unreadable and left as they are; and every listing
/// and walk is bounded. Symbolic links need privileges on Windows, so those cases run on macOS and Linux.
/// </summary>
public sealed class UnresolvedPathTests : IDisposable
{
    private const string A = "0a000000-0000-4000-8000-000000000000";
    private const string B = "0b000000-0000-4000-8000-000000000000";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();

    private DataPaths Paths => new(_temp.Combine("data"));

    public void Dispose() => _temp.Dispose();

    private static string Old(Stores stores, string content)
    {
        var id = stores.Blobs.Put(Encoding.UTF8.GetBytes(content));
        File.SetLastWriteTimeUtc(stores.Blobs.Paths.BlobFile(id), Now.AddDays(-30).UtcDateTime);
        return id;
    }

    /// <summary>
    /// <c>profiles/</c> is a link to a drive that is there when the stores open and gone when the sweep lists it: the
    /// profiles on it can't be read, so nothing is deleted, the blob only they refer to included.
    /// </summary>
    [Fact]
    public void TheSweep_WhenProfilesIsALinkToADriveThatIsGone_DeletesNothing()
    {
        if (OperatingSystem.IsWindows())
            return;
        var drive = _temp.Combine("drive");
        Directory.CreateDirectory(Path.Combine(drive, "profiles"));
        Directory.CreateDirectory(Paths.Root);
        Directory.CreateSymbolicLink(Paths.ProfilesDirectory, Path.Combine(drive, "profiles"));
        var stores = new Stores(Paths).Open();
        var onTheDrive = Old(stores, "only a profile on the drive refers to it");
        stores.Documents.Create(A, Document(A, "on the drive", onTheDrive));
        var opened = stores.Reopen();
        Directory.Move(drive, _temp.Combine("unplugged"));

        var result = BlobSweep.Run(opened.Documents, opened.Blobs, Now);

        Assert.NotNull(result.SkippedBecause);
        Assert.Contains("profiles", result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(onTheDrive));
    }

    /// <summary>
    /// Start-up with <c>profiles/</c> a link to nothing, a file or a FIFO: the profiles can't be listed, so start-up
    /// stops, and nothing in the folder is written: the order keeps the profile it lists and no blob is swept.
    /// </summary>
    [Theory]
    [InlineData("a link to nothing")]
    [InlineData("a file")]
    [InlineData("a FIFO")]
    public void StartUp_WhenProfilesIsntAFolder_Stops_AndWritesNothing(string what)
    {
        if (OperatingSystem.IsWindows())
            return;
        var stores = new Stores(Paths).Open();
        var referenced = Old(stores, "referenced by a profile that can't be listed");
        stores.Documents.Create(A, Document(A, "a", referenced));
        StoreMarker.Open(Paths);
        Directory.Move(Paths.ProfilesDirectory, _temp.Combine("moved away"));
        switch (what)
        {
            case "a link to nothing":
                Directory.CreateSymbolicLink(Paths.ProfilesDirectory, _temp.Combine("unmounted", "profiles"));
                break;
            case "a file":
                File.WriteAllText(Paths.ProfilesDirectory, "not a folder");
                break;
            case "a FIFO":
                Fifo.Create(Paths.ProfilesDirectory);
                break;
        }
        var order = File.ReadAllText(Paths.ProfileOrderFile);

        Assert.ThrowsAny<IOException>(() => Fifo.Within(15, () => Workspace.Start(Paths, null, Now).Dispose(), Paths.ProfilesDirectory));

        Assert.Equal(order, File.ReadAllText(Paths.ProfileOrderFile));
        Assert.True(stores.Blobs.Exists(referenced));
    }

    /// <summary><c>blobs/</c> a link to a drive that isn't there: the blobs can't be listed, so start-up stops, nothing written.</summary>
    [Fact]
    public void StartUp_WhenBlobsIsALinkToNothing_Stops_AndWritesNothing()
    {
        if (OperatingSystem.IsWindows())
            return;
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        StoreMarker.Open(Paths);
        Directory.CreateSymbolicLink(Paths.BlobsDirectory, _temp.Combine("unmounted", "blobs"));
        var before = Tree(Paths.Root);

        Assert.ThrowsAny<IOException>(() => Workspace.Start(Paths, null, Now).Dispose());

        Assert.Equal(before, Tree(Paths.Root));
        Assert.NotNull(new FileInfo(Paths.BlobsDirectory).LinkTarget);
    }

    /// <summary>
    /// A profile's folder that is a link to a drive that isn't there may still be a profile: it stays in the order (it
    /// can't be read), the order isn't rewritten without it, and deleting it removes the link, never what it leads to.
    /// </summary>
    [Fact]
    public void AProfileFolderLinkToNothing_StaysListed_AndDeletingItRemovesTheLink()
    {
        if (OperatingSystem.IsWindows())
            return;
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        stores.Documents.Create(B, Document(B, "b"));
        var drive = _temp.Combine("drive", B);
        Directory.CreateDirectory(Path.GetDirectoryName(drive)!);
        Directory.Move(Paths.ProfileFolder(B), drive);
        Directory.CreateSymbolicLink(Paths.ProfileFolder(B), drive);
        Directory.Move(_temp.Combine("drive"), _temp.Combine("unplugged"));
        var order = File.ReadAllText(Paths.ProfileOrderFile);

        var reopened = stores.Reopen();

        Assert.Equal([A, B], reopened.Documents.List());
        Assert.Equal(order, File.ReadAllText(Paths.ProfileOrderFile));
        Assert.ThrowsAny<IOException>(() => reopened.Documents.Read(B));

        Assert.True(reopened.Documents.Delete(B));
        Assert.Equal([A], reopened.Documents.List());
        Assert.False(File.Exists(Paths.ProfileFolder(B)) || Directory.Exists(Paths.ProfileFolder(B)), "the link is removed");
        Assert.True(File.Exists(_temp.Combine("unplugged", B, "document.json")));
        Assert.Equal([A], stores.Reopen().Documents.List());
    }

    /// <summary>
    /// A sidecar named <c>NUL</c> (made through <c>\\?\</c>) in a profile's folder holds the only reference to an old blob:
    /// the sweep reads that file, by its literal name, never the null device, and keeps the blob. Windows only: elsewhere
    /// <c>NUL</c> is an ordinary name, and no device has a file's name in a folder.
    /// </summary>
    [Fact]
    public void ASidecarNamedLikeADevice_IsReadAsTheFile_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows device names (NUL, CON, COM1) exist only on Windows.");
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var inNul = Old(stores, "named only in a file called NUL");
        File.WriteAllText(@"\\?\" + Path.Combine(Paths.ProfileFolder(A), "NUL"), inNul);

        var next = stores.Reopen();
        var result = BlobSweep.Run(next.Documents, next.Blobs, Now);

        Assert.Null(result.SkippedBecause);
        Assert.True(stores.Blobs.Exists(inNul));
    }

    /// <summary>
    /// The sweep's walk is bounded: a profile's <c>versions</c> link to a tree with more entries than the sweep's budget
    /// (or more bytes to read than its budget) stops that run's deletions, rather than listing or reading on and on.
    /// </summary>
    [Theory]
    [InlineData("entries")]
    [InlineData("bytes")]
    public void TheSweep_OverItsBudget_DeletesNothing(string budget)
    {
        if (OperatingSystem.IsWindows())
            return;
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var old = Old(stores, "old");
        var tree = _temp.Combine("big tree");
        for (var i = 0; i < 20; i++)
        {
            Directory.CreateDirectory(Path.Combine(tree, $"d{i}"));
            File.WriteAllText(Path.Combine(tree, $"d{i}", "v.json"), new string('x', 1000));
        }
        Directory.CreateSymbolicLink(Path.Combine(Paths.ProfileFolder(A), "versions"), tree);
        var within = stores.Reopen();
        Assert.Null(BlobSweep.Run(within.Documents, within.Blobs, Now.AddDays(-60)).SkippedBecause);

        var next = stores.Reopen();
        var result = budget == "entries" ? SweepWithin(next, maxEntries: 30, maxBytes: long.MaxValue) : SweepWithin(next, maxEntries: int.MaxValue, maxBytes: 10_000);

        Assert.NotNull(result.SkippedBecause);
        Assert.Contains(budget == "entries" ? "entries" : "bytes", result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));
    }

    /// <summary>
    /// The engine's own files as links to nothing (a drive that isn't there): each is unreadable, never "absent", and left
    /// as the link it is: the order is kept in memory, the settings refuse changes, the record isn't replaced.
    /// </summary>
    [Fact]
    public void TheEnginesOwnFiles_AsLinksToNothing_AreUnreadable_AndLeftAsLinks()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(Paths.ProfileFolder(A));
        File.WriteAllBytes(Paths.DocumentFile(A), Document(A, "a"));
        string[] links = [Paths.ProfileOrderFile, Paths.SettingsFile, Path.Combine(Paths.Root, V1Folder.RecordFileName)];
        foreach (var link in links)
            File.CreateSymbolicLink(link, _temp.Combine("unmounted", Path.GetFileName(link)));

        var order = new DocumentStore(Paths).Open();
        var settings = SettingsStore.Open(Paths);
        var migration = new V1Folder(_temp.Combine("no 1.x here")).GetState(Paths);

        Assert.Equal([A], order);
        Assert.Equal(SettingsStatus.Unreadable, settings.Status);
        Assert.Equal("done", migration.State);
        foreach (var link in links)
            Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    /// <summary>The sweep with its budget set: entries looked at, and bytes read.</summary>
    private static BlobSweepResult SweepWithin(Stores stores, int maxEntries, long maxBytes) =>
        BlobSweep.Run(stores.Documents, stores.Blobs, Now, maxEntries, maxBytes);
}
