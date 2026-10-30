using System.Security.Cryptography;
using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Documents;

public class BlobStoreTests
{
    [Fact]
    public void Put_StoresByContentHash()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = Encoding.UTF8.GetBytes("hello");
        var hex = Convert.ToHexStringLower(SHA256.HashData(content));

        var id = store.Put(content);

        Assert.Equal("sha256:" + hex, id);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(temp.Path, "blobs", hex)));
        Assert.True(store.Exists(id));
        Assert.Equal(5, store.Size(id));
        using (var stream = store.OpenRead(id)!)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            Assert.Equal(content, buffer.ToArray());
        }

        var absent = BlobStore.IdOf("other"u8);
        Assert.False(store.Exists(absent));
        Assert.Null(store.Size(absent));
        Assert.Null(store.OpenRead(absent));
        Assert.Equal([id], store.List());
        Assert.Equal(["blobs", Path.Combine("blobs", hex)], StorageTestData.Tree(temp.Path));
    }

    [Fact]
    public void Put_Again_KeepsTheFileAndRenewsItsTime()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var id = store.Put("same"u8.ToArray());
        var file = store.Paths.BlobFile(id);
        var old = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(file, old);
        Assert.Equal(id, store.Put("same"u8.ToArray()));

        Assert.True(File.GetLastWriteTimeUtc(file) > old.AddDays(2));
        Assert.Single(Directory.GetFiles(store.Paths.BlobsDirectory));
    }

    /// <summary>A Put whose flush of blobs/ failed left the blob in place; a retry flushes blobs/ before returning its id.</summary>
    [Fact]
    public void Put_BlobsFlushFailed_RetryFlushesBeforeReturning()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var faults = new MatchFaults { Fail = (step, path) => step == StorageStep.SyncFolder && path == paths.BlobsDirectory };
        var store = new BlobStore(paths, null, faults);
        store.Open();
        var content = "retried"u8.ToArray();

        Assert.Throws<IOException>(() => store.Put(content));
        Assert.True(store.Exists(BlobStore.IdOf(content)));

        faults.Fail = null;
        faults.Steps.Clear();
        Assert.Equal(BlobStore.IdOf(content), store.Put(content));
        Assert.Equal((StorageStep.SyncFolder, paths.BlobsDirectory), faults.Steps[^1]);
    }

    [Fact]
    public void Put_RewritesAFileOfTheWrongSize()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = "whole content"u8.ToArray();
        var id = BlobStore.IdOf(content);
        Directory.CreateDirectory(store.Paths.BlobsDirectory);
        File.WriteAllText(store.Paths.BlobFile(id), "whole");

        Assert.Equal(id, store.Put(content));
        Assert.Equal(content, File.ReadAllBytes(store.Paths.BlobFile(id)));
    }

    [Fact]
    public void Put_ReplacesAFileOfTheRightSizeWithOtherContent()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = "whole content"u8.ToArray();
        var id = BlobStore.IdOf(content);
        var file = store.Paths.BlobFile(id);
        Directory.CreateDirectory(store.Paths.BlobsDirectory);
        File.WriteAllText(file, "other content");
        Assert.Equal(content.Length, new FileInfo(file).Length);

        Assert.Equal(id, store.Put(content));
        Assert.Equal(content, File.ReadAllBytes(file));
        Assert.Single(Directory.GetFiles(store.Paths.BlobsDirectory));
    }

    [Fact]
    public void Put_RefusesMoreThanVideoBytes()
    {
        using var temp = new TempDirectory();
        Assert.Equal(SpecLimits.Default.VideoBytes, new BlobStore(new DataPaths(temp.Path)).MaxBytes);

        var store = new BlobStore(new DataPaths(temp.Path), SpecLimits.Default.With(new Dictionary<string, double> { ["videoBytes"] = 4 }));
        Assert.Equal(BlobStore.IdOf("four"u8), store.Put("four"u8.ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Put("five!"u8.ToArray()));
        Assert.Single(Directory.GetFiles(store.Paths.BlobsDirectory));
    }

    public static TheoryData<string> InvalidIds => new()
    {
        "", "sha256:", new string('a', 64), "sha256:" + new string('a', 63), "sha256:" + new string('a', 65),
        "sha256:" + new string('A', 64), "SHA256:" + new string('a', 64), "sha512:" + new string('a', 64),
        "sha256:" + new string('g', 64), "sha256:" + new string('a', 64) + "\n", " sha256:" + new string('a', 64),
        "sha256:../../" + new string('a', 58), "sha256:" + new string('a', 32) + "/" + new string('a', 31),
        "sha256:" + new string('a', 32) + "\\" + new string('a', 31),
    };

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public void Ids_OnlySha256WithLowerCaseHex(string id)
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));

        Assert.False(BlobStore.IsValidId(id));
        Assert.Throws<ArgumentException>(() => store.Exists(id));
        Assert.Throws<ArgumentException>(() => store.Size(id));
        Assert.Throws<ArgumentException>(() => store.OpenRead(id));
        Assert.Throws<ArgumentException>(() => store.ReadVerified(id));
        Assert.Throws<ArgumentException>(() => store.OpenVerified(id));
        Assert.Throws<ArgumentException>(() => store.StoredSize(id));
    }

    private static byte[]? ReadAll(Stream? stream)
    {
        if (stream is null)
            return null;
        using (stream)
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    /// <summary>Overwrites <paramref name="file"/> in place with <paramref name="bytes"/> and sets its time.</summary>
    private static void Overwrite(string file, byte[] bytes, DateTime written)
    {
        File.WriteAllBytes(file, bytes);
        File.SetLastWriteTimeUtc(file, written);
    }

    [Fact]
    public void VerifiedReads_GiveAStoredBlob_AndNothingForAMissingOne()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = "verified"u8.ToArray();
        var id = store.Put(content);

        Assert.Equal(content, store.ReadVerified(id));
        Assert.Equal(content, ReadAll(store.OpenVerified(id)));
        Assert.Equal(content.Length, store.StoredSize(id));

        var absent = BlobStore.IdOf("absent"u8);
        Assert.Null(store.ReadVerified(absent));
        Assert.Null(store.OpenVerified(absent));
        Assert.Null(store.StoredSize(absent));
    }

    /// <summary>
    /// A file changed on disk to other bytes of the same length, with its time set back, is not the blob: a cold store
    /// (a new run) hashes it on its first read and counts it as not stored.
    /// </summary>
    [Fact]
    public void VerifiedReads_InAColdStore_RefuseAFileThatDoesNotHashToItsId()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var content = "the stored bytes"u8.ToArray();
        var id = new BlobStore(paths).Put(content);
        var file = paths.BlobFile(id);
        var written = File.GetLastWriteTimeUtc(file);
        Overwrite(file, "other bytes, same"u8.ToArray()[..content.Length], written);
        Assert.Equal((content.Length, written), (new FileInfo(file).Length, File.GetLastWriteTimeUtc(file)));

        foreach (var read in new Func<BlobStore, object?>[] { s => s.ReadVerified(id), s => ReadAll(s.OpenVerified(id)), s => s.StoredSize(id) })
        {
            var cold = new BlobStore(paths);
            Assert.Null(read(cold));
            Assert.Equal(1, cold.Hashes);
            Assert.True(cold.Exists(id));
        }
    }

    /// <summary>
    /// A verdict is kept for one file version, its length and last-write time: a read at the same version doesn't hash,
    /// and a replaced file, a changed time or a changed length is hashed again, so a good verdict never covers bytes it
    /// didn't check.
    /// </summary>
    [Fact]
    public void Verdicts_AreKeptPerFileVersion()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var content = "kept per file version"u8.ToArray();
        var id = new BlobStore(paths).Put(content);
        var file = paths.BlobFile(id);
        var store = new BlobStore(paths);
        var t0 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, t0);

        Assert.Equal(content, store.ReadVerified(id));
        Assert.Equal(1, store.Hashes);
        Assert.Equal(content, store.ReadVerified(id));
        Assert.Equal(content, ReadAll(store.OpenVerified(id)));
        Assert.Equal(content.Length, store.StoredSize(id));
        Assert.Equal(1, store.Hashes);

        // Other bytes of the same length at another time: hashed again, refused.
        var other = new byte[content.Length];
        Overwrite(file, other, t0.AddSeconds(1));
        Assert.Null(store.ReadVerified(id));
        Assert.Null(store.OpenVerified(id));
        Assert.Null(store.StoredSize(id));
        Assert.Equal(2, store.Hashes);

        // The right bytes back, by a replacing rename, at yet another time: hashed again, good.
        var replacement = file + ".new";
        File.WriteAllBytes(replacement, content);
        File.SetLastWriteTimeUtc(replacement, t0.AddSeconds(2));
        File.Move(replacement, file, overwrite: true);
        Assert.Equal(content, ReadAll(store.OpenVerified(id)));
        Assert.Equal(3, store.Hashes);

        // Only the time changed (a touch): hashed again.
        File.SetLastWriteTimeUtc(file, t0.AddSeconds(3));
        Assert.Equal(content.Length, store.StoredSize(id));
        Assert.Equal(4, store.Hashes);

        // Only the length changed, at the same time: hashed again, refused.
        Overwrite(file, [.. content, (byte)'!'], t0.AddSeconds(3));
        Assert.Null(store.ReadVerified(id));
        Assert.Equal(5, store.Hashes);
    }

    /// <summary>A corrupt file is replaced by the next Put of the right bytes; reads then hash the new file once.</summary>
    [Fact]
    public void ACorruptFile_IsRepairedByPut_AndThenReadsAsTheBlob()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = "repaired"u8.ToArray();
        var id = BlobStore.IdOf(content);
        Directory.CreateDirectory(store.Paths.BlobsDirectory);
        File.WriteAllBytes(store.Paths.BlobFile(id), "damaged!"u8.ToArray());
        Assert.Null(store.ReadVerified(id));
        Assert.Equal(1, store.Hashes);

        // Written again, without a verdict: its first read hashes the new file.
        Assert.Equal(id, store.Put(content, out var created));
        Assert.True(created);
        Assert.Equal(content, store.ReadVerified(id));
        Assert.Equal(content.Length, store.StoredSize(id));
        Assert.Equal(2, store.Hashes);

        // Stored again (a touch): the Put's own check is the verdict.
        Assert.Equal(id, store.Put(content, out created));
        Assert.False(created);
        Assert.Equal(content, ReadAll(store.OpenVerified(id)));
        Assert.Equal(2, store.Hashes);
    }

    /// <summary>
    /// Put remembers a verdict only for the bytes it compared, with the length and time of the file it read them through,
    /// and none for a blob it wrote: a file renamed into the blob's place meanwhile (other bytes of the same length; here at Put's touch of a
    /// stored blob, and right after a new blob's rename) is checked on its next read and refused.
    /// </summary>
    [Fact]
    public void Put_RemembersTheFileItChecked_NotOneThatReplacedIt()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        StorageStep? replaceAt = null;
        string? target = null;
        var faults = new MatchFaults
        {
            Fail = (step, path) =>
            {
                if (step == replaceAt && path == (step == StorageStep.TouchFile ? target : paths.BlobsDirectory))
                {
                    replaceAt = null;
                    var other = Path.Combine(temp.Path, "other");
                    File.WriteAllBytes(other, new byte[new FileInfo(target!).Length]);
                    File.Move(other, target!, overwrite: true);
                }
                return false;
            },
        };

        // Stored again: the touch lands on the file that replaced the one Put checked.
        var stored = "stored, then checked again"u8.ToArray();
        var storedId = new BlobStore(paths).Put(stored);
        target = paths.BlobFile(storedId);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-1));
        var store = new BlobStore(paths, null, faults);
        replaceAt = StorageStep.TouchFile;
        Assert.Equal(storedId, store.Put(stored, out var created));
        Assert.False(created);
        Assert.Null(replaceAt);
        Assert.Null(store.ReadVerified(storedId));
        Assert.Equal(1, store.Hashes);

        // New: replaced after the write's rename, before blobs/ is flushed.
        var fresh = "written, then replaced"u8.ToArray();
        var freshId = BlobStore.IdOf(fresh);
        target = paths.BlobFile(freshId);
        replaceAt = StorageStep.SyncFolder;
        Assert.Equal(freshId, store.Put(fresh, out created));
        Assert.True(created);
        Assert.Null(replaceAt);
        Assert.Null(store.ReadVerified(freshId));
        Assert.Null(store.StoredSize(freshId));
    }

    /// <summary>
    /// A new blob is written and left without a verdict (Put doesn't read back what it wrote): its first checked read
    /// hashes it, once. Stored again, it is compared, and that verdict is remembered.
    /// </summary>
    [Fact]
    public void Put_OfANewBlob_RemembersNothing_ItsFirstReadHashesIt()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = "new, not read back"u8.ToArray();

        var id = store.Put(content, out var created);
        Assert.True(created);
        Assert.Equal(0, store.VerdictCount);
        Assert.Equal(content, store.ReadVerified(id));
        Assert.Equal(content.Length, store.StoredSize(id));
        Assert.Equal(1, store.Hashes);

        var again = new BlobStore(store.Paths);
        Assert.Equal(id, again.Put(content, out created));
        Assert.False(created);
        Assert.Equal(1, again.VerdictCount);
        Assert.Equal(content, again.ReadVerified(id));
        Assert.Equal(0, again.Hashes);
    }

    /// <summary>
    /// Put of a stored blob touches its file before comparing it, and remembers a verdict only for the version it
    /// compared: a file changed in place before the comparison (here at the touch) is written again, and one changed in
    /// place during it (its time changed) is left without a verdict, so its next read hashes it and refuses it.
    /// </summary>
    [Fact]
    public void Put_OfAStoredBlob_RemembersOnlyTheVersionItCompared()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows refuses an in-place write while Put holds the file open.");
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var content = "stored, then changed in place"u8.ToArray();
        var other = new byte[content.Length];
        var id = new BlobStore(paths).Put(content);
        var file = paths.BlobFile(id);
        var earlier = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        // Changed at the touch: the comparison sees the change, and the blob is written again.
        var faults = new MatchFaults
        {
            Fail = (step, path) =>
            {
                if (step == StorageStep.TouchFile)
                    Overwrite(path, other, earlier);
                return false;
            },
        };
        var store = new BlobStore(paths, null, faults);
        Assert.Equal(id, store.Put(content, out var created));
        Assert.True(created);
        Assert.Equal(content, File.ReadAllBytes(file));
        Assert.Equal(content, store.ReadVerified(id));

        // Changed during the comparison: nothing remembered; the next read hashes the file and refuses it.
        store = new BlobStore(paths) { Compared = () => Overwrite(file, other, earlier) };
        Assert.Equal(id, store.Put(content, out created));
        Assert.False(created);
        Assert.Equal(0, store.VerdictCount);
        Assert.Null(store.ReadVerified(id));
        Assert.Equal(1, store.Hashes);
    }

    /// <summary>
    /// A file over <c>videoBytes</c> under a blob's name (only a file changed on disk can be) is not stored, refused from
    /// its length before it is read: no checked read hashes it, even when its bytes do hash to its name.
    /// </summary>
    [Fact]
    public void VerifiedReads_RefuseAFileOverVideoBytes_WithoutHashingIt()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var limits = SpecLimits.Default.With(new Dictionary<string, double> { ["videoBytes"] = 4 });
        var large = "five!"u8.ToArray();
        var id = BlobStore.IdOf(large);
        Directory.CreateDirectory(paths.BlobsDirectory);
        File.WriteAllBytes(paths.BlobFile(id), large);
        Assert.Equal(large.Length, new BlobStore(paths).StoredSize(id));

        foreach (var read in new Func<BlobStore, object?>[] { s => s.ReadVerified(id), s => s.ReadHashed(id), s => ReadAll(s.OpenVerified(id)), s => s.StoredSize(id) })
        {
            var store = new BlobStore(paths, limits);
            Assert.Null(read(store));
            Assert.Equal(0, store.Hashes);
        }
    }

    /// <summary>
    /// At most <see cref="BlobStore.VerdictCapacity"/> verdicts are remembered, reads' and Puts' of stored blobs alike;
    /// past it the least recently used is dropped, and that blob's file is hashed again on its next read.
    /// </summary>
    [Fact]
    public void Verdicts_AreBounded_TheLeastRecentlyUsedIsDropped_AndCheckedAgain()
    {
        Assert.Equal(4096, BlobStore.VerdictCapacity);
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var writer = new BlobStore(paths);
        var (a, b, c) = (writer.Put("a"u8.ToArray()), writer.Put("b"u8.ToArray()), writer.Put("c"u8.ToArray()));
        var store = new BlobStore(paths, null, faults: null, verdictCapacity: 2);

        long Read(string id)
        {
            Assert.NotNull(store.StoredSize(id));
            return store.Hashes;
        }

        Assert.Equal(1, Read(a));
        Assert.Equal(2, Read(b));
        Assert.Equal(2, Read(a));
        Assert.Equal(3, Read(c)); // drops b, the least recently used
        Assert.Equal(2, store.VerdictCount);
        Assert.Equal(3, Read(a));
        Assert.Equal(3, Read(c));
        Assert.Equal(4, Read(b)); // checked again; drops a
        Assert.Equal(5, Read(a));

        // Puts of stored blobs, each compared and remembered.
        for (var i = 0; i < 10; i++)
        {
            var bytes = Encoding.UTF8.GetBytes($"put {i}");
            writer.Put(bytes);
            store.Put(bytes);
        }
        Assert.Equal(2, store.VerdictCount);
        Assert.Equal(5, Read(BlobStore.IdOf("put 9"u8)));
        Assert.Equal(6, Read(c));
    }

    [Fact]
    public void ConcurrentFirstReads_AllGetTheSameVerdict()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        var good = new byte[256 * 1024];
        new Random(7).NextBytes(good);
        var goodId = new BlobStore(paths).Put(good);
        var bad = (byte[])good.Clone();
        bad[^1] ^= 1;
        var badId = BlobStore.IdOf(bad);
        File.WriteAllBytes(paths.BlobFile(badId), good);

        // Dedicated threads, so the readers meet at the barrier without holding the thread pool other tests run on.
        var store = new BlobStore(paths);
        const int Readers = 12;
        using var start = new Barrier(Readers);
        var results = new (bool Good, bool Bad)[Readers];
        var threads = Enumerable.Range(0, Readers).Select(i => new Thread(() =>
        {
            start.SignalAndWait();
            results[i] = (i % 3) switch
            {
                0 => (store.ReadVerified(goodId) is { } g && g.AsSpan().SequenceEqual(good), store.ReadVerified(badId) is null),
                1 => (ReadAll(store.OpenVerified(goodId)) is { } g && g.AsSpan().SequenceEqual(good), store.OpenVerified(badId) is null),
                _ => (store.StoredSize(goodId) == good.Length, store.StoredSize(badId) is null),
            };
        })).ToArray();
        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.All(results, r => Assert.Equal((true, true), r));
    }

    [Fact]
    public void List_IgnoresOtherFiles()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var id = store.Put("x"u8.ToArray());
        File.WriteAllText(Path.Combine(store.Paths.BlobsDirectory, "notes.txt"), "x");
        File.WriteAllText(Path.Combine(store.Paths.BlobsDirectory, new string('A', 64)), "x");
        File.WriteAllText(Path.Combine(store.Paths.BlobsDirectory, new string('a', 64) + ".png"), "x");

        Assert.Equal([id], store.List());
    }
}
