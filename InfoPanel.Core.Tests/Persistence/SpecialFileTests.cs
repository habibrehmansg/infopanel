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
/// The engine opens only regular files in its data folder (docs/2.0/data-folder.md): a FIFO, socket or device where it
/// reads is never opened in a way that waits (a FIFO's open waits for a writer), so start-up never blocks on one. As a
/// sweep root it stops deletion; as the marker it is refused; as one of the engine's own files it is unreadable and left
/// as it is. Each wait is bounded here (<see cref="Fifo.Within{T}"/>); the product never sleeps.
/// </summary>
public sealed class SpecialFileTests : IDisposable
{
    private const string A = "0a000000-0000-4000-8000-000000000000";
    private const string B = "0b000000-0000-4000-8000-000000000000";

    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly List<Workspace> _open = [];

    private DataPaths Paths => new(_temp.Path);

    public void Dispose()
    {
        foreach (var workspace in _open)
            workspace.Dispose();
        _temp.Dispose();
    }

    private string Old(Stores stores)
    {
        var id = stores.Blobs.Put("old"u8.ToArray());
        File.SetLastWriteTimeUtc(stores.Blobs.Paths.BlobFile(id), Now.AddDays(-30).UtcDateTime);
        return id;
    }

    [Fact]
    public void AFifoUnderAProfileFolder_StopsTheSweep_WithoutBlocking()
    {
        if (!Fifo.Supported)
            return;
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        var old = Old(stores);
        var fifo = Path.Combine(Paths.ProfileFolder(A), "versions", "live");
        Directory.CreateDirectory(Path.GetDirectoryName(fifo)!);
        Fifo.Create(fifo);

        var next = stores.Reopen();
        var result = Fifo.Within(10, () => BlobSweep.Run(next.Documents, next.Blobs, Now), fifo);

        Assert.NotNull(result.SkippedBecause);
        Assert.Contains("live", result.SkippedBecause);
        Assert.Equal(0, result.Deleted);
        Assert.True(stores.Blobs.Exists(old));
    }

    /// <summary>
    /// Start-up with a FIFO as one profile's document and another under a profile's folder: it doesn't wait, the profile
    /// is listed as unreadable, and nothing is swept.
    /// </summary>
    [Fact]
    public void StartUp_WithFifosForAProfile_DoesntBlock()
    {
        if (!Fifo.Supported)
            return;
        var stores = new Stores(Paths).Open();
        stores.Documents.Create(A, Document(A, "a"));
        stores.Documents.Create(B, Document(B, "b"));
        var old = Old(stores);
        File.Delete(Paths.DocumentFile(A));
        Fifo.Create(Paths.DocumentFile(A));
        var other = Path.Combine(Paths.ProfileFolder(B), "recovery");
        Fifo.Create(other);

        var workspace = Fifo.Within(15, () => Workspace.Start(Paths, null, Now), Paths.DocumentFile(A), other);
        _open.Add(workspace);
        var entries = Fifo.Within(10, () => workspace.List(), Paths.DocumentFile(A), other);

        Assert.Equal([A, B], entries.Select(e => e.Id));
        Assert.Equal("unreadable", entries[0].Status);
        Assert.True(stores.Blobs.Exists(old));
    }

    [Fact]
    public void AMarkerThatIsAFifo_IsRefused_WithoutBlocking()
    {
        if (!Fifo.Supported)
            return;
        var marker = Path.Combine(_temp.Path, "store.json");
        Fifo.Create(marker);

        var refused = Fifo.Within(10, () => Assert.Throws<StoreMarkerException>(() => StoreMarker.Open(Paths)), marker);

        Assert.Contains("store.json", refused.Message);
        Assert.Equal(["store.json"], Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName));
    }

    /// <summary>The engine's own files as FIFOs: each is unreadable, without waiting, and left as it is.</summary>
    [Fact]
    public void TheEnginesOwnFiles_AsFifos_AreUnreadable_WithoutBlocking()
    {
        if (!Fifo.Supported)
            return;
        Directory.CreateDirectory(Paths.ProfileFolder(A));
        File.WriteAllBytes(Paths.DocumentFile(A), Document(A, "a"));
        string[] fifos = [Paths.ProfileOrderFile, Paths.SettingsFile, Path.Combine(_temp.Path, V1Folder.RecordFileName)];
        foreach (var fifo in fifos)
            Fifo.Create(fifo);

        var order = Fifo.Within(10, () => new DocumentStore(Paths).Open(), fifos);
        var settings = Fifo.Within(10, () => SettingsStore.Open(Paths), fifos);
        var migration = Fifo.Within(10, () => new V1Folder(_temp.Combine("no 1.x here")).GetState(Paths), fifos);

        Assert.Equal([A], order);
        Assert.Equal(SettingsStatus.Unreadable, settings.Status);
        Assert.Equal("done", migration.State);
        Assert.Single(migration.Diagnostics!, d => d.Code == "import.record");
        // Each is still there, never replaced.
        foreach (var fifo in fifos)
            Assert.True(File.Exists(fifo) && new FileInfo(fifo).Length == 0, fifo);
        Assert.Equal(
            new[] { "profiles", "profiles.json", "settings.json", V1Folder.RecordFileName }.Order(StringComparer.Ordinal),
            Directory.GetFileSystemEntries(_temp.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}
