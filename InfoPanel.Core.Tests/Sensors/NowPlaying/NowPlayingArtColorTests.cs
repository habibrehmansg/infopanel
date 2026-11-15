using InfoPanel.Sensors.NowPlaying;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.NowPlaying;

/// <summary>
/// The cover's main colour (<c>infopanel:/now-playing/art-color</c>) by its rule, over fixed pictures: the picture
/// decoded to 32 × 32, each pixel at least half opaque quantised to 4 bits a channel, the most frequent bucket (a tie to
/// the lowest bucket, red's bits first), read as the mean of that bucket's pixels as <c>#RRGGBB</c>; none for a picture
/// that doesn't decode, is over the media limits, isn't a raster image or has no pixel at least half opaque.
/// </summary>
public sealed class NowPlayingArtColorTests
{
    /// <summary>A PNG of <paramref name="width"/> × <paramref name="height"/> pixels, each <paramref name="pixel"/>'s (unpremultiplied).</summary>
    internal static byte[] Png(int width, int height, Func<int, int, SKColor> pixel)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, pixel(x, y));
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    internal static byte[] Png(SKColor color, int width = 32, int height = 32) => Png(width, height, (_, _) => color);

    [Theory]
    [InlineData(32, 32)]
    [InlineData(100, 60)]
    [InlineData(7, 3)]
    public void A_picture_of_one_colour_reads_that_colour_whatever_its_size(int width, int height)
    {
        Assert.Equal("#3A7BD5", NowPlayingArtColor.Of(Png(new SKColor(0x3A, 0x7B, 0xD5), width, height)));
    }

    [Fact]
    public void The_most_frequent_bucket_wins_however_many_colours_share_the_picture()
    {
        // 640 pixels of one colour, 384 of another.
        var picture = Png(32, 32, (_, y) => y < 20 ? new SKColor(0xC0, 0x40, 0x20) : new SKColor(0x20, 0x80, 0xF0));
        Assert.Equal("#C04020", NowPlayingArtColor.Of(picture));
    }

    [Fact]
    public void The_bucket_reads_as_the_mean_of_its_pixels_not_its_first_one()
    {
        // 512 pixels of #102030 and 192 of #1C2C3C share a bucket (1, 2, 3); 320 of #F0F0F0 are another.
        // The mean: (512 × 16 + 192 × 28) / 704 = 19.3 → 0x13; 35.3 → 0x23; 51.3 → 0x33.
        var picture = Png(32, 32, (_, y) => y < 16 ? new SKColor(0x10, 0x20, 0x30)
            : y < 22 ? new SKColor(0x1C, 0x2C, 0x3C)
            : new SKColor(0xF0, 0xF0, 0xF0));
        Assert.Equal("#132333", NowPlayingArtColor.Of(picture));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_tie_goes_to_the_lowest_bucket_wherever_its_pixels_are(bool blueFirst)
    {
        // 512 red pixels (bucket 0xF00) and 512 blue (0x00F): blue's bucket comes first, on either side.
        SKColor red = new(0xFF, 0, 0), blue = new(0, 0, 0xFF);
        var picture = Png(32, 32, (x, _) => (x < 16) == blueFirst ? blue : red);
        Assert.Equal("#0000FF", NowPlayingArtColor.Of(picture));
    }

    [Fact]
    public void A_pixel_less_than_half_opaque_is_skipped()
    {
        // 512 transparent red pixels and 320 green at alpha 127 outnumber 192 opaque blue ones, and count for nothing.
        var picture = Png(32, 32, (_, y) => y < 16 ? new SKColor(0xFF, 0, 0, 0)
            : y < 26 ? new SKColor(0, 0xFF, 0, 127)
            : new SKColor(0, 0, 0xFF));
        Assert.Equal("#0000FF", NowPlayingArtColor.Of(picture));
    }

    [Fact]
    public void A_pixel_at_least_half_opaque_counts_by_its_colour_as_an_opaque_one_does()
    {
        // 640 green pixels at alpha 128 against 384 opaque blue ones.
        var picture = Png(32, 32, (_, y) => y < 20 ? new SKColor(0, 0xFF, 0, 128) : new SKColor(0, 0, 0xFF));
        Assert.Equal("#00FF00", NowPlayingArtColor.Of(picture));
    }

    [Fact]
    public void A_fully_transparent_picture_has_no_colour()
    {
        Assert.Null(NowPlayingArtColor.Of(Png(new SKColor(0xFF, 0x20, 0x20, 0))));
    }

    public static TheoryData<string, byte[]> Undecodable() => new()
    {
        { "empty", [] },
        { "not a picture", [1, 2, 3, 4, 5] },
        { "a PNG's signature with nothing after it", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13] },
        { "an SVG (not a raster picture)", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"red\"/></svg>"u8.ToArray() },
        { "a picture wider than the media limits' 8,192 pixels", Png(new SKColor(0xFF, 0, 0), 8193, 1) },
    };

    [Theory]
    [MemberData(nameof(Undecodable))]
    public void A_picture_that_does_not_decode_within_the_media_limits_has_no_colour_and_throws_nothing(string what, byte[] bytes)
    {
        Assert.NotNull(what);
        Assert.Null(NowPlayingArtColor.Of(bytes));
    }

    [Fact]
    public void A_picture_whose_pixels_are_cut_short_after_its_headers_has_no_colour()
    {
        // A JPEG cut halfway through its scan: its headers read whole, so its decode starts and ends short.
        using var bitmap = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)x, (byte)y, (byte)(x ^ y)));
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        var whole = encoded.ToArray();
        var scan = whole.AsSpan().IndexOf(new byte[] { 0xFF, 0xDA });
        Assert.True(scan > 0);
        var cut = whole[..(scan + (whole.Length - scan) / 2)];

        Assert.NotNull(NowPlayingArtColor.Of(whole));
        using (var codec = SKCodec.Create(new MemoryStream(cut)))
        {
            Assert.NotNull(codec);
            Assert.Equal(SKCodecResult.IncompleteInput, codec.GetPixels(codec.Info, out _));
        }
        Assert.Null(NowPlayingArtColor.Of(cut));
    }
}
