using System.Text;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Documents;
using InfoPanel.Persistence;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Documents.StorageTestData;

namespace InfoPanel.Core.Tests.Documents;

/// <summary>
/// Blobs stored from a stream (<see cref="BlobStore.PutAsync"/>, what <c>POST /api/blobs</c> uses): written to disk as
/// they arrive and hashed on the way, never held whole, refused past <c>videoBytes</c> with nothing left, crash-safe as
/// <see cref="BlobStore.Put(ReadOnlyMemory{byte})"/> is.
/// </summary>
public class BlobStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stream of <paramref name="length"/> bytes made as they are read (nothing is allocated for them), in reads of at most <paramref name="chunk"/>.</summary>
    internal sealed class Generated(long length, int chunk = 65536, Action<long>? reading = null) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public long Consumed => _position;

        public static byte At(long i) => (byte)(i * 31 + (i >> 9));

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            reading?.Invoke(_position);
            var n = (int)Math.Min(Math.Min(buffer.Length, chunk), length - _position);
            for (var i = 0; i < n; i++) buffer[i] = At(_position + i);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public static byte[] Bytes(int length)
        {
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = At(i);
            return bytes;
        }
    }

    [Fact]
    public async Task PutAsync_StoresTheStreamByItsHash_OnceOnly()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = Generated.Bytes(300_000);

        var first = await store.PutAsync(new Generated(content.Length), Ct);
        Assert.Equal(new BlobStore.StreamedBlob(BlobStore.IdOf(content), content.Length, true), first);
        Assert.Equal(content, store.ReadVerified(first!.Value.Id));

        var again = await store.PutAsync(new MemoryStream(content), Ct);
        Assert.Equal(first.Value with { Created = false }, again);
        Assert.Equal(store.Put(content), first.Value.Id);
        Assert.Equal(["blobs", Path.Combine("blobs", first.Value.Id[7..])], Tree(temp.Path));
    }

    [Fact]
    public async Task PutAsync_ReplacesAFileUnderTheNameThatDoesNotHashToIt()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var content = Encoding.UTF8.GetBytes("hello, stream");
        var id = BlobStore.IdOf(content);
        Directory.CreateDirectory(store.Paths.BlobsDirectory);
        File.WriteAllBytes(store.Paths.BlobFile(id), Encoding.UTF8.GetBytes("hello, other!"));

        Assert.True((await store.PutAsync(new MemoryStream(content), Ct))!.Value.Created);
        Assert.Equal(content, File.ReadAllBytes(store.Paths.BlobFile(id)));
    }

    [Fact]
    public async Task PutAsync_OverVideoBytes_StoresNothing_AndReadsNoFurther()
    {
        using var temp = new TempDirectory();
        var limits = SpecLimits.Default with { VideoBytes = 100_000 };
        var store = new BlobStore(new DataPaths(temp.Path), limits);
        Assert.Equal(100_000, store.MaxBytes);

        Assert.NotNull(await store.PutAsync(new Generated(100_000), Ct));
        var over = new Generated(10_000_000, chunk: 4096);
        Assert.Null(await store.PutAsync(over, Ct));
        Assert.InRange(over.Consumed, 100_001, 100_000 + 2 * 81920);
        Assert.Equal(["blobs", Path.Combine("blobs", BlobStore.IdOf(Generated.Bytes(100_000))[7..])], Tree(temp.Path));
    }

    [Fact]
    public void TheDefaultCap_IsVideoBytes() =>
        Assert.Equal(SpecLimits.Default.VideoBytes, new BlobStore(new DataPaths(Path.GetTempPath())).MaxBytes);

    [Fact]
    public async Task PutAsync_WritesToDiskAsTheBytesArrive()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        var seen = new List<long>();
        // Before each read after the first, the bytes read so far are in the upload's temp file already.
        var stream = new Generated(1_000_000, chunk: 65536, reading: position =>
        {
            if (position == 0) return;
            var uploads = Directory.GetFiles(store.Paths.BlobsDirectory, "upload.*.tmp");
            seen.Add(uploads.Length == 1 ? new FileInfo(uploads[0]).Length : -1);
        });

        await store.PutAsync(stream, Ct);
        Assert.NotEmpty(seen);
        Assert.All(seen, length => Assert.True(length > 0, "a read came before the bytes already read were on disk"));
        Assert.Empty(Directory.GetFiles(store.Paths.BlobsDirectory, "*.tmp"));
    }

    [Fact]
    public async Task PutAsync_Cancelled_LeavesNothing()
    {
        using var temp = new TempDirectory();
        var store = new BlobStore(new DataPaths(temp.Path));
        using var cancel = new CancellationTokenSource();
        var stream = new Generated(1_000_000, chunk: 65536, reading: position =>
        {
            if (position > 200_000) cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync(new CancellingStream(stream, cancel.Token), cancel.Token));
        Assert.Empty(Directory.GetFiles(store.Paths.BlobsDirectory));
    }

    /// <summary>Throws once its token is cancelled, as a request body does when the client goes away.</summary>
    private sealed class CancellingStream(Stream inner, CancellationToken token) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            return inner.Read(buffer, offset, count);
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            token.ThrowIfCancellationRequested();
            return inner.ReadAsync(buffer, cancellationToken);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Open_RemovesAnUploadsTempFile_AndNothingElse()
    {
        using var temp = new TempDirectory();
        var paths = new DataPaths(temp.Path);
        Directory.CreateDirectory(paths.BlobsDirectory);
        var leftover = Path.Combine(paths.BlobsDirectory, $"upload.{Guid.NewGuid():N}.tmp");
        var other = Path.Combine(paths.BlobsDirectory, $"uploads.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(leftover, "half an upload");
        File.WriteAllText(other, "not ours");

        new BlobStore(paths).Open();
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(other));
    }

    // ---- the media check of a stored file ---------------------------------------------------------------------------

    /// <summary>A seekable stream that counts the bytes read through it.</summary>
    private sealed class Counting(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long Total { get; private set; }

        // A derived MemoryStream reads spans through this overload.
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = base.Read(buffer, offset, count);
            Total += n;
            return n;
        }
    }

    [Fact]
    public void TheStreamCheck_ReadsAVideosContainerInPlace_AndAPictureOverMediaBytesNotAtAll()
    {
        var limits = SpecLimits.Default with { MediaBytes = 1024 };
        var video = new Counting(VideoFixtures.Read("clip-mpeg4.avi"));
        var check = MediaValidator.Check(video, limits);
        Assert.Empty(check.Errors);
        Assert.Equal(("mpeg4", MediaFormat.Avi), (check.Info!.Codec, check.Info.Format));
        Assert.True(video.Total < 4096, $"{video.Total} bytes read of a {video.Length}-byte video");
        Assert.Equal(MediaValidator.Check(VideoFixtures.Read("clip-mpeg4.avi"), limits), check, new CheckComparer());

        var picture = new Counting(new byte[2048]);
        Assert.Equal("limit.mediaBytes", Assert.Single(MediaValidator.Check(picture, limits).Errors).Code);
        Assert.True(picture.Total <= 12);

        var small = Generated.Bytes(1000);
        Assert.Equal(MediaValidator.Check(small, limits), MediaValidator.Check(new MemoryStream(small), limits), new CheckComparer());
        Assert.Equal("limit.videoBytes", Assert.Single(MediaValidator.Check(new MemoryStream(VideoFixtures.Read("clip-mpeg4.avi")),
            limits with { VideoBytes = 1000 }).Errors).Code);
    }

    private sealed class CheckComparer : IEqualityComparer<MediaCheck>
    {
        public bool Equals(MediaCheck? x, MediaCheck? y) =>
            x!.Info == y!.Info && x.Errors.Select(e => e.Code).SequenceEqual(y.Errors.Select(e => e.Code));

        public int GetHashCode(MediaCheck obj) => 0;
    }

    [Fact]
    public async Task PutBlobAsync_DescribesAVideo_AndStoresAPictureOverMediaBytesAsNoMedia()
    {
        using var temp = new TempDirectory();
        var limits = SpecLimits.Default with { MediaBytes = 1024 };
        var paths = new DataPaths(temp.Path);
        using var workspace = Workspace.Start(paths, new WorkspaceOptions { Limits = limits, Examples = [] }, DateTimeOffset.UtcNow);

        var video = (await workspace.PutBlobAsync(new MemoryStream(VideoFixtures.Read("clip-h264.mp4")), Ct))!;
        Assert.True(video.Created);
        Assert.Equal(("mp4", "h264", 64, 48), (video.Info.Media!.Format, video.Info.Media.Codec, video.Info.Media.Width, video.Info.Media.Height));

        var picture = (await workspace.PutBlobAsync(new Generated(5000), Ct))!;
        Assert.Equal(("limit.mediaBytes", 5000L), (picture.Info.MediaError!.Code, picture.Info.Bytes));
        Assert.True(workspace.Blobs.Exists(picture.Info.Id));

        Assert.Null(await workspace.PutBlobAsync(new Generated(limits.VideoBytes + 1L), Ct));
    }

    [Fact]
    public void ABlobOverMediaBytes_IsRefusedByThePictureDecoder_WithoutBeingRead()
    {
        using var temp = new TempDirectory();
        var limits = SpecLimits.Default with { MediaBytes = 1024 };
        var blobs = new BlobStore(new DataPaths(temp.Path), limits);
        var id = blobs.Put(VideoFixtures.Read("clip-mpeg4.avi"));
        var reads = 0;
        var store = new CountingStore(new InfoPanel.Documents.BlobMediaStore(blobs), () => reads++);
        using var cache = new InfoPanel.Images.MediaSampleCache(store, new InfoPanel.Images.MediaSampleCacheOptions { Synchronous = true, Limits = limits });

        var lookup = cache.Get(id, 0, InfoPanel.Images.MediaPlayback.Loop);
        Assert.Equal(InfoPanel.Images.MediaState.Failed, lookup.State);
        Assert.StartsWith("limit.mediaBytes", lookup.Problem);
        Assert.Equal(0, reads);
        Assert.Null(new InfoPanel.Documents.BlobMediaStore(blobs).Read(id));
    }

    private sealed class CountingStore(InfoPanel.Images.IMediaAssetStore inner, Action read) : InfoPanel.Images.IMediaAssetStore
    {
        public byte[]? Read(string hash)
        {
            read();
            return inner.Read(hash);
        }

        public long? Size(string hash) => inner.Size(hash);
    }

    /// <summary>A streamed upload failed at every step, as a crash and as an I/O error: the blob is whole or absent, and no temp file outlives the next start.</summary>
    [Theory]
    [MemberData(nameof(CrashSafetyTests.Modes), MemberType = typeof(CrashSafetyTests))]
    public void StreamedWrite_IsWholeOrAbsent(bool crash)
    {
        var content = Generated.Bytes(200_000);
        var id = BlobStore.IdOf(content);
        var steps = ForEveryStep(crash,
            arrange: _ => { },
            act: s => Assert.Equal(id, s.Blobs.PutAsync(new Generated(content.Length, chunk: 50_000)).GetAwaiter().GetResult()!.Value.Id),
            check: (faults, live, reopened, completed) =>
            {
                Assert.Equal(completed || faults.Completed(StorageStep.ReplaceFile, _ => true), reopened.Blobs.Exists(id));
                Assert.Equal(reopened.Blobs.Exists(id) ? content.Length : null, reopened.Blobs.Size(id));
                // The id is returned only once the blob's entry in blobs/ is flushed to disk.
                Assert.Equal(completed, faults.Completed(StorageStep.SyncFolder, p => p == reopened.Blobs.Paths.BlobsDirectory));
            });
        Assert.True(steps >= 5, $"{steps} steps");
    }

    /// <summary>The same blob streamed again over a stored copy, failed at every step: the stored blob stays whole.</summary>
    [Theory]
    [MemberData(nameof(CrashSafetyTests.Modes), MemberType = typeof(CrashSafetyTests))]
    public void StreamedWrite_OfAStoredBlob_KeepsIt(bool crash)
    {
        var content = Generated.Bytes(70_000);
        var id = BlobStore.IdOf(content);
        ForEveryStep(crash,
            arrange: s => s.Blobs.Put(content),
            act: s => Assert.False(s.Blobs.PutAsync(new MemoryStream(content)).GetAwaiter().GetResult()!.Value.Created),
            check: (_, _, reopened, _) => Assert.Equal(content, reopened.Blobs.ReadVerified(id)));
    }
}
