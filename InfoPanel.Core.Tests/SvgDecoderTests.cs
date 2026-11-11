using System.Text;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using SkiaSharp;
using Svg.Skia;
using Xunit;

namespace InfoPanel.Core.Tests;

/// <summary>
/// The SVG decoder (Svg.Skia) on the engine's SkiaSharp: what the engine decodes (a rebuilt document's gradients, clip
/// paths, masks and <c>use</c>) draws where it should, and what the profile refuses (text, filters, images) still draws
/// when the decoder is handed it directly, so a decoder built for another SkiaSharp fails here first, not as a missing
/// method or an empty picture in a frame.
/// </summary>
public class SvgDecoderTests
{
    private const string Head = "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' width='100' height='100'>";

    private static SKBitmap Draw(byte[] svg)
    {
        using var stream = new MemoryStream(svg, writable: false);
        using var sk = new SKSvg();
        sk.Load(stream);
        var picture = Assert.IsType<SKPicture>(sk.Picture);
        var bitmap = new SKBitmap(100, 100);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.DrawPicture(picture);
        return bitmap;
    }

    /// <summary>The document as the engine decodes it: accepted by the media check and rebuilt.</summary>
    private static byte[] Rebuilt(string svg)
    {
        var result = MediaValidator.Check(Encoding.UTF8.GetBytes(svg));
        Assert.Empty(result.Errors);
        return Assert.IsType<byte[]>(result.Sanitized);
    }

    private static void Near(SKColor expected, SKColor actual, int tolerance = 40)
    {
        Assert.True(Math.Abs(expected.Red - actual.Red) <= tolerance && Math.Abs(expected.Green - actual.Green) <= tolerance
            && Math.Abs(expected.Blue - actual.Blue) <= tolerance, $"{actual} is not near {expected}");
    }

    [Fact]
    public void Gradients_clip_paths_masks_and_uses_draw_through_the_engines_path()
    {
        using var bitmap = Draw(Rebuilt(Head
            + "<defs><linearGradient id='lin' x1='0' y1='0' x2='1' y2='0'><stop offset='0' stop-color='#ff0000'/><stop offset='1' stop-color='#0000ff'/></linearGradient>"
            + "<radialGradient id='rad'><stop offset='0' stop-color='#ffff00'/><stop offset='1' stop-color='#00ffff'/></radialGradient>"
            + "<clipPath id='clip'><circle cx='75' cy='25' r='10'/></clipPath>"
            + "<mask id='mask'><rect x='0' y='50' width='25' height='50' fill='#ffffff'/></mask>"
            + "<rect id='dot' width='4' height='4' fill='#ff00ff'/></defs>"
            + "<rect x='0' y='0' width='50' height='50' fill='url(#lin)'/>"
            + "<rect x='50' y='0' width='50' height='50' fill='#00ff00' clip-path='url(#clip)'/>"
            + "<rect x='0' y='50' width='50' height='50' fill='#000000' mask='url(#mask)'/>"
            + "<circle cx='75' cy='75' r='20' fill='url(#rad)'/>"
            + "<use href='#dot' x='90' y='90'/></svg>"));
        Near(SKColors.Red, bitmap.GetPixel(1, 25));
        Near(SKColors.Blue, bitmap.GetPixel(48, 25));
        Near(SKColors.Lime, bitmap.GetPixel(75, 25), 0); // inside the clip
        Near(SKColors.White, bitmap.GetPixel(52, 2), 0); // outside it
        Near(SKColors.Black, bitmap.GetPixel(10, 75), 0); // under the mask's white
        Near(SKColors.White, bitmap.GetPixel(40, 75), 0); // past it
        Near(SKColors.Yellow, bitmap.GetPixel(75, 75));
        Near(SKColors.Magenta, bitmap.GetPixel(92, 92), 0);
    }

    [Theory]
    [InlineData("<text x='5' y='40' font-size='30' fill='#000000'>Ag</text>")]
    [InlineData("<defs><filter id='f'><feGaussianBlur stdDeviation='3'/></filter></defs><rect x='20' y='20' width='20' height='20' fill='#000000' filter='url(#f)'/>")]
    public void Text_and_filters_are_refused_by_the_profile_and_still_draw_in_the_decoder(string content)
    {
        Assert.Null(MediaValidator.Check(Encoding.UTF8.GetBytes(Head + content + "</svg>")).Sanitized);
        using var bitmap = Draw(Encoding.UTF8.GetBytes(Head + content + "</svg>"));
        var drawn = 0;
        for (var y = 0; y < 100; y++)
            for (var x = 0; x < 100; x++)
                if (bitmap.GetPixel(x, y).Red < 128) drawn++;
        Assert.True(drawn > 20, $"{drawn} pixels drawn");
    }

    [Fact]
    public void A_blur_spreads_past_its_shape()
    {
        using var bitmap = Draw(Encoding.UTF8.GetBytes(Head
            + "<defs><filter id='f'><feGaussianBlur stdDeviation='3'/></filter></defs><rect x='20' y='20' width='20' height='20' fill='#000000' filter='url(#f)'/></svg>"));
        var edge = bitmap.GetPixel(18, 30);
        Assert.True(edge.Red is > 0 and < 250, $"{edge} at the blurred edge");
    }

    [Fact]
    public void An_image_is_refused_by_the_profile_and_still_draws_in_the_decoder()
    {
        using var red = new SKBitmap(2, 2);
        red.Erase(SKColors.Red);
        using var png = red.Encode(SKEncodedImageFormat.Png, 100);
        var svg = Head + "<image x='0' y='0' width='20' height='20' href='data:image/png;base64," + Convert.ToBase64String(png.ToArray()) + "'/></svg>";
        Assert.Null(MediaValidator.Check(Encoding.UTF8.GetBytes(svg)).Sanitized);
        using var bitmap = Draw(Encoding.UTF8.GetBytes(svg));
        Near(SKColors.Red, bitmap.GetPixel(10, 10), 0);
    }
}
