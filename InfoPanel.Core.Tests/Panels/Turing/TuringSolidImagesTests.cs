using InfoPanel.Panels.Turing;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// Lian Li's start clears its two layers with a transparent PNG and a black JPEG of the panel's size
/// (<c>Services/LianLiPanelDeviceTask.cs:87-95,107-112</c>, encoded by SkiaSharp at quality 100 and 95). The package has
/// no image library, so it writes both itself: not Skia's bytes, the same pictures, which a decoder (Skia's here) reads
/// back as the panel's size, every pixel transparent, every pixel black.
/// </summary>
public sealed class TuringSolidImagesTests
{
    public static TheoryData<int, int> Sizes() => new()
    {
        { 480, 1920 },
        { 464, 1920 },
        { 2288, 1080 },
        { 480, 480 },
        { 13, 7 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheClearPng_IsTransparent_AtThePanelsSize(int width, int height)
    {
        using var bitmap = SKBitmap.Decode(SolidImages.TransparentPng(width, height));

        Assert.NotNull(bitmap);
        Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
        Assert.True(bitmap.Pixels.All(p => p.Alpha == 0));
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheClearJpeg_IsBlack_AtThePanelsSize(int width, int height)
    {
        var jpeg = SolidImages.BlackJpeg(width, height);
        using var codec = SKCodec.Create(new MemoryStream(jpeg));
        using var bitmap = SKBitmap.Decode(jpeg);

        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.NotNull(bitmap);
        Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
        Assert.True(bitmap.Pixels.All(p => p.Red == 0 && p.Green == 0 && p.Blue == 0 && p.Alpha == 255));
    }
}
