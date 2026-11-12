using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using InfoPanel.Panels.Sdk;
using SkiaSharp;

namespace InfoPanel.Panels;

/// <summary>
/// The profile's frame as a panel shows it: cleared to opaque black, turned clockwise, scaled uniformly to fit and
/// centred with 1.4.x's integer offsets (<c>SKBitmapExtensions.EnsureBitmapSize</c>), drawn source-over, so a
/// transparent background is black. A frame already the panel's size with no rotation is copied without resampling.
/// Written as RGB565 (a raw stream) or as opaque RGBA (a JPEG stream's frame, before <see cref="PanelJpeg"/> encodes it).
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
            PanelPixelFormat.Rgba8888 => checked(panelWidth * panelHeight * 4),
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

        // Skia's RGB565 is a native-endian 16-bit word, red in the top 5 bits: drawn straight into the target. RGBA is
        // bytes in order, opaque: the frame a JPEG is encoded from.
        var info = format == PanelPixelFormat.Rgba8888
            ? new SKImageInfo(panelWidth, panelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque)
            : new SKImageInfo(panelWidth, panelHeight, SKColorType.Rgb565, SKAlphaType.Opaque);
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

        if (format == PanelPixelFormat.Rgb565Le && !BitConverter.IsLittleEndian)
        {
            var words = MemoryMarshal.Cast<byte, ushort>(target);
            BinaryPrimitives.ReverseEndianness(words, words);
        }
    }

    /// <summary>Bytes a <paramref name="width"/> × <paramref name="height"/> frame takes in a raw <paramref name="format"/> (not <see cref="PanelFrameFormat.Jpeg"/>).</summary>
    public static int FrameLength(int width, int height, PanelFrameFormat format)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return format switch
        {
            PanelFrameFormat.Rgb565Le or PanelFrameFormat.Rgb565Be or PanelFrameFormat.Rgb565LeTruncated => checked(width * height * 2),
            PanelFrameFormat.Rgb888 or PanelFrameFormat.Bgr888 => checked(width * height * 3),
            PanelFrameFormat.Rgba8888 => checked(width * height * 4),
            PanelFrameFormat.Jpeg => throw new ArgumentOutOfRangeException(nameof(format), format, "A JPEG frame has no fixed length."),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "A format this engine doesn't know."),
        };
    }

    /// <summary>
    /// Software brightness, as 1.4.x's families without a light of their own dimmed (Thermalright's, Thermaltake's and
    /// JL's <c>ApplyBrightness</c>): each colour channel of the opaque RGBA frame times <paramref name="percent"/> / 100
    /// through Skia's colour matrix, alpha kept. 100 or more leaves the frame as it is.
    /// </summary>
    public static unsafe void Dim(Span<byte> rgba, int width, int height, int percent)
    {
        CheckRgba(rgba, width, height);
        if (percent >= 100)
            return;
        var scale = Math.Clamp(percent, 0, 100) / 100f;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        fixed (byte* pixels = rgba)
        {
            using var copy = SKImage.FromPixelCopy(info, (IntPtr)pixels, info.RowBytes)
                ?? throw new InvalidOperationException("Skia could not copy the frame.");
            using var surface = SKSurface.Create(info, (IntPtr)pixels, info.RowBytes)
                ?? throw new InvalidOperationException("Skia could not draw into the frame.");
            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateColorMatrix(
                [
                    scale, 0, 0, 0, 0,
                    0, scale, 0, 0, 0,
                    0, 0, scale, 0, 0,
                    0, 0, 0, 1, 0,
                ]),
            };
            surface.Canvas.DrawImage(copy, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), paint);
            surface.Canvas.Flush();
        }
    }

    /// <summary>
    /// Draws <paramref name="overlay"/> (a decoded PNG, its alpha kept) over the opaque RGBA frame at its top left corner,
    /// as 1.4.x drew its display masks (<c>ApplyDisplayMask</c>).
    /// </summary>
    public static unsafe void Overlay(Span<byte> rgba, int width, int height, SKImage overlay)
    {
        CheckRgba(rgba, width, height);
        ArgumentNullException.ThrowIfNull(overlay);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        fixed (byte* pixels = rgba)
        {
            using var surface = SKSurface.Create(info, (IntPtr)pixels, info.RowBytes)
                ?? throw new InvalidOperationException("Skia could not draw into the frame.");
            surface.Canvas.DrawImage(overlay, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            surface.Canvas.Flush();
        }
    }

    /// <summary>The <paramref name="crop"/> of the RGBA frame (<paramref name="width"/> pixels a row) into <paramref name="target"/>, its rows packed.</summary>
    public static void Crop(ReadOnlySpan<byte> rgba, int width, int height, PanelCrop crop, Span<byte> target)
    {
        CheckRgba(rgba, width, height);
        if (CropProblem(crop, width, height) is { } problem)
            throw new ArgumentException(problem, nameof(crop));
        var row = crop.Width * 4;
        if (target.Length != row * crop.Height)
            throw new ArgumentException($"The target holds {target.Length} bytes; the crop takes {row * crop.Height}.", nameof(target));
        for (var y = 0; y < crop.Height; y++)
            rgba.Slice(((crop.Y + y) * width + crop.X) * 4, row).CopyTo(target.Slice(y * row, row));
    }

    /// <summary>Why <paramref name="crop"/> isn't a part of a <paramref name="width"/> × <paramref name="height"/> frame, or null when it is.</summary>
    public static string? CropProblem(PanelCrop crop, int width, int height) =>
        crop.X < 0 || crop.Y < 0 || crop.Width < 1 || crop.Height < 1 || (long)crop.X + crop.Width > width || (long)crop.Y + crop.Height > height
            ? $"the crop {crop.Width} x {crop.Height} at ({crop.X}, {crop.Y}) isn't inside the {width} x {height} frame"
            : null;

    /// <summary>
    /// The opaque RGBA frame in a raw <paramref name="format"/>. <see cref="PanelFrameFormat.Rgb565Be"/>,
    /// <see cref="PanelFrameFormat.Rgb565LeTruncated"/>, <see cref="PanelFrameFormat.Rgb888"/> and
    /// <see cref="PanelFrameFormat.Bgr888"/> by truncation, each channel's low bits dropped with no rounding or dithering
    /// (1.4.x's Thermalright <c>ConvertRgba8888ToRgb565</c>, both byte orders, matching the vendor's own tool);
    /// <see cref="PanelFrameFormat.Rgba8888"/> as it is; <see cref="PanelFrameFormat.Rgb565Le"/> as <see cref="Render"/>
    /// writes it (Skia's conversion, BeadaPanel's since 1.4).
    /// </summary>
    public static unsafe void Convert(ReadOnlySpan<byte> rgba, int width, int height, PanelFrameFormat format, Span<byte> target)
    {
        CheckRgba(rgba, width, height);
        var length = FrameLength(width, height, format);
        if (target.Length != length)
            throw new ArgumentException($"The target holds {target.Length} bytes; a {width}×{height} {format} frame takes {length}.", nameof(target));
        var pixels = width * height;
        switch (format)
        {
            case PanelFrameFormat.Rgba8888:
                rgba.CopyTo(target);
                return;
            case PanelFrameFormat.Rgb565Be:
            case PanelFrameFormat.Rgb565LeTruncated:
                // hi = RRRRRGGG, lo = GGGBBBBB; big-endian hi first, little-endian lo first.
                var (first, second) = format == PanelFrameFormat.Rgb565Be ? (0, 1) : (1, 0);
                for (var i = 0; i < pixels; i++)
                {
                    byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
                    target[i * 2 + first] = (byte)((r & 0xF8) | (g >> 5));
                    target[i * 2 + second] = (byte)(((g & 0x1C) << 3) | (b >> 3));
                }
                return;
            case PanelFrameFormat.Rgb888:
            case PanelFrameFormat.Bgr888:
                var bgr = format == PanelFrameFormat.Bgr888;
                for (var i = 0; i < pixels; i++)
                {
                    target[i * 3] = rgba[i * 4 + (bgr ? 2 : 0)];
                    target[i * 3 + 1] = rgba[i * 4 + 1];
                    target[i * 3 + 2] = rgba[i * 4 + (bgr ? 0 : 2)];
                }
                return;
            case PanelFrameFormat.Rgb565Le:
                var source = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                var info = new SKImageInfo(width, height, SKColorType.Rgb565, SKAlphaType.Opaque);
                fixed (byte* from = rgba)
                fixed (byte* to = target)
                {
                    using var image = SKImage.FromPixels(new SKPixmap(source, (IntPtr)from, source.RowBytes))
                        ?? throw new InvalidOperationException("Skia could not read the frame.");
                    using var surface = SKSurface.Create(info, (IntPtr)to, info.RowBytes)
                        ?? throw new InvalidOperationException("Skia could not draw into the target.");
                    surface.Canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
                    surface.Canvas.Flush();
                }
                if (!BitConverter.IsLittleEndian)
                {
                    var words = MemoryMarshal.Cast<byte, ushort>(target);
                    BinaryPrimitives.ReverseEndianness(words, words);
                }
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "A JPEG frame is PanelJpeg's.");
        }
    }

    private static void CheckRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        var length = TargetLength(width, height, PanelPixelFormat.Rgba8888);
        if (rgba.Length != length)
            throw new ArgumentException($"A {width}×{height} RGBA frame is {length} bytes; this one is {rgba.Length}.", nameof(rgba));
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
