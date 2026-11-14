using InfoPanel.Output;
using SkiaSharp;

namespace InfoPanel.Desktop;

/// <summary>
/// Draws a <see cref="DesktopFrame"/> (RGBA, premultiplied) into a window's BGRA, premultiplied pixels, as a desktop
/// window shows it: the target first cleared to the floor, then a frame that <see cref="Fits"/> the target at one frame
/// pixel per pixel, or, for a frame rendered below scale 1 (over the frame pixel cap), stretched by 1 / <c>Scale</c>
/// with linear filtering. A frame of any other size (one rendered before the window was resized) is stretched over the
/// whole target, each axis on its own, filtered linearly, until one that fits arrives. Skia swizzles and copies in one
/// pass.
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
    /// Whether a <paramref name="frameWidth"/> × <paramref name="frameHeight"/> frame rendered at
    /// <paramref name="frameScale"/> is exactly a <paramref name="width"/> × <paramref name="height"/> target at
    /// 1 / <c>Scale</c>: the size <see cref="FrameScale.PixelSize"/> gives the target at that scale (a scale of 1 or over,
    /// or not above 0, is 1). Such a frame is drawn one frame pixel per pixel, or stretched by 1 / <c>Scale</c>; any
    /// other is stretched over the whole target.
    /// </summary>
    public static bool Fits(int frameWidth, int frameHeight, float frameScale, int width, int height)
    {
        if (!(frameScale > 0 && frameScale < 1))
            return frameWidth == width && frameHeight == height;
        var (w, h) = FrameScale.PixelSize(frameScale, width, height);
        return frameWidth == w && frameHeight == h;
    }

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
            // Over the transparent floor nothing needs blending: copied as it is.
            using var paint = new SKPaint { BlendMode = floor ? SKBlendMode.SrcOver : SKBlendMode.Src };
            if (!Fits(frame.Width, frame.Height, frame.Scale, width, height))
            {
                // Another size than the window (a frame from before a resize): stretched over all of it, each axis on its own.
                canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear), paint);
            }
            else if (frame.Scale >= 1 || frame.Scale <= 0)
            {
                // One frame pixel per pixel.
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
            }
            else
            {
                var dest = new SKRect(0, 0, frame.Width / frame.Scale, frame.Height / frame.Scale);
                canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear), paint);
            }
        }

        canvas.Flush();
    }
}
