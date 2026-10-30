using InfoPanel.Images;
using Xunit;

namespace InfoPanel.Core.Tests.Images;

/// <summary>
/// The engine's media store: fetched media by its own keys, every other key from the stored assets, each read from
/// that store. A video is read in place and sized without its bytes, so a size or a stream comes from the stored assets.
/// </summary>
public sealed class EngineMediaStoreTests
{
    private const string Asset = "sha256:0000000000000000000000000000000000000000000000000000000000000001";

    /// <summary>Stored assets that are sized and opened without reading: a blob over <c>mediaBytes</c>, as a video's is.</summary>
    private sealed class LargeAssets : IMediaAssetStore
    {
        public int Reads;

        public byte[]? Read(string hash)
        {
            Reads++;
            return null;
        }

        public long? Size(string hash) => hash == Asset ? 100_000_000 : null;

        public Stream? Open(string hash) => hash == Asset ? new MemoryStream([1, 2, 3], writable: false) : null;
    }

    [Fact]
    public void An_assets_size_and_stream_come_from_the_stored_assets_unread()
    {
        var assets = new LargeAssets();
        var store = new EngineMediaStore(assets, new FetchedMediaStore());
        IMediaAssetStore media = store;

        Assert.Equal(100_000_000, media.Size(Asset));
        using var stream = media.Open(Asset);
        Assert.NotNull(stream);
        Assert.Equal(3, stream.Length);
        Assert.Equal(0, assets.Reads);
    }

    [Fact]
    public void Fetched_media_comes_from_the_fetched_store()
    {
        var assets = new LargeAssets();
        var fetched = new FetchedMediaStore();
        var key = fetched.Put([4, 5]);
        IMediaAssetStore media = new EngineMediaStore(assets, fetched);

        Assert.Equal([4, 5], media.Read(key));
        using var stream = media.Open(key);
        Assert.Equal(2, stream!.Length);
        Assert.Null(media.Size(key));
        Assert.Equal(0, assets.Reads);
    }
}
