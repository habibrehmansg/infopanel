using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Testing;

/// <summary>
/// The frames the conformance suite writes (<see cref="ConformanceFrames"/>): a raw frame is its format's bytes a pixel
/// times the size, and the JPEG is one a decoder reads, of the size asked for, mid-grey.
/// </summary>
public sealed class ConformanceFramesTests
{
    [Theory]
    [InlineData(PanelFrameFormat.Rgb565Le, 2)]
    [InlineData(PanelFrameFormat.Rgb565Be, 2)]
    [InlineData(PanelFrameFormat.Rgb888, 3)]
    [InlineData(PanelFrameFormat.Bgr888, 3)]
    [InlineData(PanelFrameFormat.Rgba8888, 4)]
    public void A_raw_frame_is_its_formats_bytes_a_pixel(PanelFrameFormat format, int bytesPerPixel)
    {
        var frame = ConformanceFrames.Of(format, 37, 11);

        Assert.Equal(37 * 11 * bytesPerPixel, frame.Length);
        Assert.True(frame.Distinct().Count() > 1, "a frame that isn't uniform");
    }

    [Fact]
    public void An_opaque_frame_has_every_alpha_at_255()
    {
        var frame = ConformanceFrames.Of(PanelFrameFormat.Rgba8888, 5, 3);

        Assert.All(Enumerable.Range(0, 15), pixel => Assert.Equal(255, frame[pixel * 4 + 3]));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(320, 240)]
    [InlineData(1920, 462)]
    [InlineData(13, 7)]
    public void The_JPEG_decodes_to_a_grey_frame_of_its_size(int width, int height)
    {
        var jpeg = ConformanceFrames.Of(PanelFrameFormat.Jpeg, width, height);

        using var bitmap = SKBitmap.Decode(jpeg);
        Assert.NotNull(bitmap);
        Assert.Equal((width, height), (bitmap.Width, bitmap.Height));
        var pixel = bitmap.GetPixel(width / 2, height / 2);
        Assert.InRange(pixel.Red, 126, 130);
        Assert.Equal(pixel.Red, pixel.Green);
        Assert.Equal(pixel.Red, pixel.Blue);
    }
}
