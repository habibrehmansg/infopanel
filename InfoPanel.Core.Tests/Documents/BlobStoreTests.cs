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
    public void Put_RefusesMoreThanMediaBytes()
    {
        using var temp = new TempDirectory();
        Assert.Equal(SpecLimits.Default.MediaBytes, new BlobStore(new DataPaths(temp.Path)).MaxBytes);

        var store = new BlobStore(new DataPaths(temp.Path), SpecLimits.Default.With(new Dictionary<string, double> { ["mediaBytes"] = 4 }));
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
