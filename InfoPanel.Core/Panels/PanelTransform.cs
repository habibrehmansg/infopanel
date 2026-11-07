using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace InfoPanel.Panels;

/// <summary>
/// The profile's frame as a panel shows it: cleared to opaque black, turned clockwise, scaled uniformly to fit and
/// centred with 1.4.x's integer offsets (<c>SKBitmapExtensions.EnsureBitmapSize</c>), drawn source-over, so a
/// transparent background is black. A frame already the panel's size with no rotation is copied without resampling.
/// Pure: no device, no state.
/// </summary>
public static class PanelTransform
{
    /// Bytes a frame of this size and format takes.
    public static int TargetLength(int panelWidth, int panelHeight, PanelPixelFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(panelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(panelHeight);
        return format switch
        {
            PanelPixelFormat.Rgb565Le => checked(panelWidth * panelHeight * 2),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format."),
        };
    }

    /// source: the frame's pixels, RGBA premultiplied (RenderedFrame's image read into a pixmap);
    /// target: exactly TargetLength(...) bytes; panelWidth/panelHeight: the panel in its own orientation.
    public static unsafe void Render(SKPixmap source, Span<byte> target, int panelWidth, int panelHeight,
                              PanelRotation rotation, PanelPixelFormat format)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0) throw new ArgumentException("The frame is empty.", nameof(source));
        if (source.GetPixels() == IntPtr.Zero) throw new ArgumentException("The frame has no pixels.", nameof(source));
        if (!Enum.IsDefined(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Unknown rotation.");
        var length = TargetLength(panelWidth, panelHeight, format);
        if (target.Length != length)
        {
            throw new ArgumentException($"The target holds {target.Length} bytes; a {panelWidth}×{panelHeight} {format} frame takes {length}.", nameof(target));
        }

        // Made before the target is touched: a frame Skia can't read leaves the target as it was.
        using var image = SKImage.FromPixels(source)
            ?? throw new ArgumentException("Skia could not read the frame's pixels.", nameof(source));

        // Skia's RGB565 is a native-endian 16-bit word, red in the top 5 bits: drawn straight into the target.
        var info = new SKImageInfo(panelWidth, panelHeight, SKColorType.Rgb565, SKAlphaType.Opaque);
        fixed (byte* pixels = target)
        {
            using (var surface = SKSurface.Create(info, (IntPtr)pixels, info.RowBytes)
                ?? throw new InvalidOperationException("Skia could not draw into the target."))
            {
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Black);
                Draw(canvas, image, panelWidth, panelHeight, rotation);
                canvas.Flush();
            }
        }

        if (!BitConverter.IsLittleEndian)
        {
            var words = MemoryMarshal.Cast<byte, ushort>(target);
            BinaryPrimitives.ReverseEndianness(words, words);
        }
    }

    /// <summary>1.4.x's geometry, over the black the canvas was cleared to.</summary>
    private static void Draw(SKCanvas canvas, SKImage image, int targetWidth, int targetHeight, PanelRotation rotation)
    {
        var turned = rotation is PanelRotation.Rotate90 or PanelRotation.Rotate270;
        int effectiveWidth = turned ? image.Height : image.Width, effectiveHeight = turned ? image.Width : image.Height;

        if (rotation == PanelRotation.None && effectiveWidth == targetWidth && effectiveHeight == targetHeight)
        {
            canvas.DrawImage(image, 0, 0);
            return;
        }

        var scale = Math.Min((float)targetWidth / effectiveWidth, (float)targetHeight / effectiveHeight);
        var scaledWidth = (int)(effectiveWidth * scale);
        var scaledHeight = (int)(effectiveHeight * scale);
        var x = (targetWidth - scaledWidth) / 2;
        var y = (targetHeight - scaledHeight) / 2;

        canvas.Translate(x + scaledWidth / 2f, y + scaledHeight / 2f);
        canvas.RotateDegrees((int)rotation);
        canvas.Scale(scale);
        canvas.Translate(-image.Width / 2f, -image.Height / 2f);

        using var paint = new SKPaint { IsAntialias = true };
        var bounds = SKRect.Create(image.Width, image.Height);
        canvas.DrawImage(image, bounds, bounds, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }
}
