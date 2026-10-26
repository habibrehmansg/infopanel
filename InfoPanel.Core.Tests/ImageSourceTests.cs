using InfoPanel.Drawing;
using InfoPanel.Enums;
using InfoPanel.Sensors;
using InfoPanel.Models;
using Xunit;

namespace InfoPanel.Core.Tests;

public class ImageSourceTests
{
    [Fact]
    public void RelativePath_ResolvesInsideProfileAssets()
    {
        var profile = new Profile();
        var image = new ImageDisplayItem("Logo", profile, "logo.png", relativePath: true);
        var ctx = TestContexts.Create();

        var expected = Path.Combine(ctx.Assets.GetProfileAssetsDirectory(profile.Guid), "logo.png");

        Assert.Equal(Path.GetFullPath(expected), image.ResolveSource(ctx));
    }

    [Theory]
    [InlineData("../other/logo.png")]
    [InlineData("../../../etc/passwd")]
    public void RelativePath_OutsideProfileAssets_IsRejected(string filePath)
    {
        var profile = new Profile();
        var image = new ImageDisplayItem("Logo", profile, filePath, relativePath: true);

        Assert.Null(image.ResolveSource(TestContexts.Create()));
    }

    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("HTTPS://example.com/a.png")]
    [InlineData("rtsp://camera/stream")]
    [InlineData("rtsps://camera/stream")]
    [InlineData("plugin-image://weather/icon")]
    [InlineData("/videos/clip.mp4")]
    [InlineData("/videos/clip.MKV?x=1")]
    [InlineData("")]
    [InlineData("NO_IMAGE")]
    public void ImageCache_ReturnsNull_ForUnsupportedSources(string source)
    {
        using var cache = new ImageCache(new ImageCacheOptions { SynchronousLoad = true });
        var decoded = 0;
        cache.Decode = path => { decoded++; return new LockedImage(path); };

        Assert.Null(cache.Get(source, persistent: false));
        Assert.Equal(0, decoded); // rejected before any file access, not because the file is missing
    }

    [Fact]
    public void ImageCache_RejectsVideoExtension_EvenForLoadableImageBytes()
    {
        var png = WritePng();
        var mp4 = Path.ChangeExtension(png, ".mp4");
        File.Copy(png, mp4);
        try
        {
            using var cache = new ImageCache(new ImageCacheOptions { SynchronousLoad = true });

            Assert.NotNull(cache.Get(png, persistent: false));
            Assert.Null(cache.Get(mp4, persistent: false));
        }
        finally
        {
            File.Delete(png);
            File.Delete(mp4);
        }
    }

    [Fact]
    public void ImageCache_Invalidate_DuringLoad_DiscardsAndDisposesTheLoad()
    {
        var path = WritePng();
        try
        {
            using var cache = new ImageCache(new ImageCacheOptions { SynchronousLoad = true });
            LockedImage? loaded = null;
            cache.Decode = source =>
            {
                loaded = new LockedImage(source);
                cache.Invalidate(source); // the file changes while the old bytes are being decoded
                return loaded;
            };

            Assert.Null(cache.Get(path, persistent: false));
            Assert.NotNull(loaded);
            Assert.True(loaded.IsDisposed);

            // The next request loads afresh and is published.
            cache.Decode = source => new LockedImage(source);
            Assert.NotNull(cache.Get(path, persistent: false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImageCache_Dispose_DuringLoad_DisposesTheLoad()
    {
        var path = WritePng();
        try
        {
            var cache = new ImageCache(new ImageCacheOptions { SynchronousLoad = true });
            LockedImage? loaded = null;
            cache.Decode = source =>
            {
                loaded = new LockedImage(source);
                cache.Dispose();
                return loaded;
            };

            Assert.Null(cache.Get(path, persistent: false));
            Assert.NotNull(loaded);
            Assert.True(loaded.IsDisposed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("/Users/alice/private.png")]
    [InlineData(@"\\host\share\x.png")]
    [InlineData(@"C:\secret\x.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a.png")]
    [InlineData("logo.png")]
    [InlineData(" http://example.com/a.png")]
    [InlineData("")]
    public void HttpImage_RejectsNonHttpSources(string text)
    {
        Assert.Null(HttpImageSource(text));
    }

    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("HTTPS://example.com/a.png?x=1")]
    [InlineData("plugin-image://weather/icon")]
    public void HttpImage_AcceptsHttpAndPluginImageSources(string text)
    {
        Assert.Equal(text, HttpImageSource(text));
    }

    private static string? HttpImageSource(string text)
    {
        var sensors = new FakeSensorReader();
        sensors.Readings[SensorKey.Plugin("/test/url")] = new SensorReading(text);
        var item = new HttpImageDisplayItem("Url", new Profile()) { SensorType = SensorType.Plugin, PluginSensorId = "/test/url" };

        return item.ResolveSource(TestContexts.Create(sensors));
    }

    private static string WritePng()
    {
        var path = Path.Combine(Path.GetTempPath(), $"infopanel-{Guid.NewGuid():N}.png");
        using var bitmap = new SkiaSharp.SKBitmap(8, 4);
        using var stream = File.Create(path);
        bitmap.Erase(SkiaSharp.SKColors.Red);
        bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
        return path;
    }

    [Fact]
    public void ImageCache_LoadsLocalPng_Synchronously()
    {
        var path = Path.Combine(Path.GetTempPath(), $"infopanel-{Guid.NewGuid():N}.png");
        try
        {
            using (var bitmap = new SkiaSharp.SKBitmap(8, 4))
            using (var stream = File.Create(path))
            {
                bitmap.Erase(SkiaSharp.SKColors.Red);
                bitmap.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
            }

            using var cache = new ImageCache(new ImageCacheOptions { SynchronousLoad = true });
            var image = cache.Get(path, persistent: false);

            Assert.NotNull(image);
            Assert.Equal(8, image.Width);
            Assert.Equal(4, image.Height);
            Assert.False(image.IsVector);
            Assert.Null(cache.Get(path + ".missing.png", persistent: false));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
