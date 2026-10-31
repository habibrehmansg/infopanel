using SkiaSharp;

namespace InfoPanel.Desktop;

/// <summary>
/// Draws a <see cref="DesktopFrame"/> (RGBA, premultiplied) into a window's BGRA, premultiplied pixels, as a desktop
/// window shows it: the target first cleared to the floor, then the frame anchored top-left at one frame pixel per
/// pixel, or, for a frame rendered below scale 1 (over the frame pixel cap), stretched by 1 / <c>Scale</c> with linear
/// filtering. Skia swizzles and copies in one pass. A target larger than the frame keeps the floor outside it.
/// <para>
/// The floor: black at alpha 1/255 (invisible on any background) when <c>floor</c> is true, so no pixel of the
/// window is fully transparent and every pixel takes clicks, as on macOS (a layered window on Windows passes clicks
/// through pixels of alpha 0); fully transparent when false. An opaque frame pixel is copied unchanged either way.
/// </para>
/// </summary>
public static class DesktopFrameBlit
{
    /// <summary>The floor colour: black at alpha 1 (premultiplied, the same).</summary>
    public static readonly SKColor Floor = new(0, 0, 0, 1);

    /// <summary>
    /// Draws <paramref name="frame"/> into the <paramref name="width"/> × <paramref name="height"/> BGRA premultiplied
    /// pixels at <paramref name="target"/>, <paramref name="rowBytes"/> apart.
    /// </summary>
    public static unsafe void Draw(DesktopFrame frame, nint target, int width, int height, int rowBytes, bool floor)
    {
        if (width <= 0 || height <= 0)
            return;
        if (rowBytes < width * 4)
            throw new ArgumentOutOfRangeException(nameof(rowBytes), "A row holds fewer than width pixels.");
        if (frame.RowBytes < frame.Width * 4 || frame.Pixels.Length < (long)frame.RowBytes * frame.Height)
            throw new ArgumentException("The frame's pixels are shorter than its size.", nameof(frame));

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, target, rowBytes)
            ?? throw new InvalidOperationException($"A {width}×{height} window surface couldn't be made.");
        var canvas = surface.Canvas;
        canvas.Clear(floor ? Floor : SKColors.Transparent);
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        fixed (byte* pixels = frame.Pixels)
        {
            using var pixmap = new SKPixmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Rgba8888, SKAlphaType.Premul), (nint)pixels, frame.RowBytes);
            using var image = SKImage.FromPixels(pixmap);
            if (frame.Scale >= 1 || frame.Scale <= 0)
            {
                // One frame pixel per pixel. Over the transparent floor nothing needs blending: copied as it is.
                using var paint = new SKPaint { BlendMode = floor ? SKBlendMode.SrcOver : SKBlendMode.Src };
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
            }
            else
            {
                using var paint = new SKPaint { BlendMode = floor ? SKBlendMode.SrcOver : SKBlendMode.Src };
                var dest = new SKRect(0, 0, frame.Width / frame.Scale, frame.Height / frame.Scale);
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear), paint);
            }
        }

        canvas.Flush();
    }
}
