using System.Buffers.Binary;
using System.Runtime.InteropServices;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Panels;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels;

/// <summary>
/// <see cref="PanelTransform"/>: the profile's frame turned, fitted and centred as 1.4.x's
/// <c>SKBitmapExtensions.EnsureBitmapSize</c> did, over black, written as RGB565 little-endian.
/// </summary>
public sealed class PanelTransformTests
{
    /// <summary>An RGBA premultiplied frame whose pixel (x, y) is <paramref name="colour"/>(x, y).</summary>
    internal static SKBitmap Frame(int width, int height, Func<int, int, SKColor> colour)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = colour(x, y);
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(c.Red * c.Alpha / 255);
                pixels[i + 1] = (byte)(c.Green * c.Alpha / 255);
                pixels[i + 2] = (byte)(c.Blue * c.Alpha / 255);
                pixels[i + 3] = c.Alpha;
            }
        }

        return bitmap;
    }

    /// <summary>The panel's pixels, read back as 16-bit little-endian values (row by row).</summary>
    private static ushort[,] Render(SKBitmap frame, int panelWidth, int panelHeight, PanelRotation rotation)
    {
        var target = new byte[PanelTransform.TargetLength(panelWidth, panelHeight, PanelPixelFormat.Rgb565Le)];
        target.AsSpan().Fill(0xAA); // whatever the buffer held before: every byte must be written
        using var pixmap = frame.PeekPixels();
        PanelTransform.Render(pixmap, target, panelWidth, panelHeight, rotation, PanelPixelFormat.Rgb565Le);

        var result = new ushort[panelWidth, panelHeight];
        for (var y = 0; y < panelHeight; y++)
        {
            for (var x = 0; x < panelWidth; x++)
            {
                result[x, y] = BinaryPrimitives.ReadUInt16LittleEndian(target.AsSpan((y * panelWidth + x) * 2, 2));
            }
        }

        return result;
    }

    /// <summary>RGB565 of a colour whose channels are exact in 5/6/5 bits (any rounding gives the same value).</summary>
    private static ushort Rgb565(SKColor c) => (ushort)((c.Red >> 3) << 11 | (c.Green >> 2) << 5 | c.Blue >> 3);

    /// <summary>
    /// A colour for index <paramref name="k"/> (0–31), different for each, with channels exact in 5/6/5 bits
    /// (the 5- and 6-bit values with their top bits repeated), so a moved pixel can't be mistaken for another.
    /// </summary>
    private static SKColor Distinct(int k)
    {
        int r5 = k & 31, g6 = (k * 7 + 3) & 63, b5 = 31 - (k & 31);
        return new SKColor((byte)(r5 << 3 | r5 >> 2), (byte)(g6 << 2 | g6 >> 4), (byte)(b5 << 3 | b5 >> 2));
    }

    private const ushort Black = 0x0000;

    [Theory]
    [InlineData(PanelRotation.None)]
    [InlineData(PanelRotation.Rotate90)]
    [InlineData(PanelRotation.Rotate180)]
    [InlineData(PanelRotation.Rotate270)]
    public void Rotation_at_the_panel_size_moves_each_pixel_exactly(PanelRotation rotation)
    {
        const int w = 5, h = 3; // odd sizes: a half-pixel slip would blend neighbours
        using var frame = Frame(w, h, (x, y) => Distinct(y * w + x));
        var turned = rotation is PanelRotation.Rotate90 or PanelRotation.Rotate270;
        int panelWidth = turned ? h : w, panelHeight = turned ? w : h;

        var panel = Render(frame, panelWidth, panelHeight, rotation);

        for (var sy = 0; sy < h; sy++)
        {
            for (var sx = 0; sx < w; sx++)
            {
                // Clockwise, as Skia's RotateDegrees turns a y-down canvas.
                var (dx, dy) = rotation switch
                {
                    PanelRotation.None => (sx, sy),
                    PanelRotation.Rotate90 => (h - 1 - sy, sx),
                    PanelRotation.Rotate180 => (w - 1 - sx, h - 1 - sy),
                    _ => (sy, w - 1 - sx),
                };
                Assert.True(panel[dx, dy] == Rgb565(Distinct(sy * w + sx)),
                    $"source ({sx},{sy}) should be at ({dx},{dy}): expected {Rgb565(Distinct(sy * w + sx)):X4}, got {panel[dx, dy]:X4}");
            }
        }
    }

    [Fact]
    public void Exact_size_without_rotation_copies_the_pixels_unresampled()
    {
        const int w = 33, h = 17;
        var random = new Random(7);
        var colours = Enumerable.Range(0, w * h).Select(_ => Distinct(random.Next(32))).ToArray();
        using var frame = Frame(w, h, (x, y) => colours[y * w + x]);

        var panel = Render(frame, w, h, PanelRotation.None);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.True(panel[x, y] == Rgb565(colours[y * w + x]), $"({x},{y}): expected {Rgb565(colours[y * w + x]):X4}, got {panel[x, y]:X4}");
            }
        }
    }

    /// <summary>The columns and rows the colour fills, as [first, last] ranges; everything else must be black.</summary>
    private static void AssertFilled(ushort[,] panel, ushort colour, int left, int top, int right, int bottom)
    {
        for (var y = 0; y < panel.GetLength(1); y++)
        {
            for (var x = 0; x < panel.GetLength(0); x++)
            {
                var inside = x >= left && x <= right && y >= top && y <= bottom;
                var expected = inside ? colour : Black;
                Assert.True(panel[x, y] == expected, $"({x},{y}): expected {expected:X4}, got {panel[x, y]:X4}");
            }
        }
    }

    [Fact]
    public void Fits_down_and_letterboxes_with_black_bars()
    {
        // 800×480 on 480×480: scale 0.6, 480×288, centred at y = (480 − 288) / 2 = 96.
        using var frame = Frame(800, 480, (_, _) => SKColors.Red);
        AssertFilled(Render(frame, 480, 480, PanelRotation.None), 0xF800, 0, 96, 479, 383);
    }

    [Fact]
    public void Fits_up_and_letterboxes_with_black_bars()
    {
        // 100×50 on 300×200: scale 3, 300×150, centred at y = (200 − 150) / 2 = 25.
        using var frame = Frame(100, 50, (_, _) => SKColors.Lime);
        AssertFilled(Render(frame, 300, 200, PanelRotation.None), 0x07E0, 0, 25, 299, 174);
    }

    [Fact]
    public void Fits_after_rotating_and_pillarboxes_with_black_bars()
    {
        // 800×480 turned is 480×800; on 480×480: scale 0.6, 288×480, centred at x = (480 − 288) / 2 = 96.
        using var frame = Frame(800, 480, (_, _) => SKColors.Blue);
        AssertFilled(Render(frame, 480, 480, PanelRotation.Rotate90), 0x001F, 96, 0, 383, 479);
    }

    [Fact]
    public void Centres_on_the_truncated_size_with_integer_offsets_as_1_4_x()
    {
        // 1.4.x: scale = min(10/4, 10/3) = 2.5; scaledHeight = (int)(3 × 2.5) = 7; y = (10 − 7) / 2 = 1; the image
        // (7.5 high) is centred on y + 7 / 2 = 4.5, so it covers 0.75 to 8.25: row 0 partly, row 9 not at all.
        // Centred on the exact size it would cover 1.25 to 8.75: row 0 black, row 9 partly lit.
        using var frame = Frame(4, 3, (_, _) => SKColors.White);
        var panel = Render(frame, 10, 10, PanelRotation.None);

        for (var x = 0; x < 10; x++)
        {
            Assert.True(panel[x, 0] is not Black and not 0xFFFF, $"row 0 should be partly covered at x {x}: {panel[x, 0]:X4}");
            for (var y = 1; y <= 7; y++) Assert.True(panel[x, y] == 0xFFFF, $"({x},{y}) should be white: {panel[x, y]:X4}");
            Assert.True(panel[x, 8] is not Black and not 0xFFFF, $"row 8 should be partly covered at x {x}: {panel[x, 8]:X4}");
            Assert.True(panel[x, 9] == Black, $"row 9 should be black at x {x}: {panel[x, 9]:X4}");
        }
    }

    [Fact]
    public void Transparent_pixels_show_black_and_translucent_ones_are_drawn_over_black()
    {
        using var frame = Frame(4, 2, (x, y) => y == 0 ? SKColors.Transparent : new SKColor(255, 0, 0, 128));
        var panel = Render(frame, 4, 2, PanelRotation.None);

        for (var x = 0; x < 4; x++)
        {
            Assert.Equal(Black, panel[x, 0]);
            Assert.Equal(16 << 11, panel[x, 1]); // premultiplied (128, 0, 0) over black: red 128 → 16 of 31
        }
    }

    [Fact]
    public void Transparent_frame_rotated_and_fitted_is_all_black()
    {
        using var frame = Frame(8, 6, (_, _) => SKColors.Transparent);
        AssertFilled(Render(frame, 20, 9, PanelRotation.Rotate270), Black, 0, 0, -1, -1);
    }

    [Theory]
    [InlineData(255, 0, 0, 0x00, 0xF8)]
    [InlineData(0, 255, 0, 0xE0, 0x07)]
    [InlineData(0, 0, 255, 0x1F, 0x00)]
    [InlineData(255, 255, 255, 0xFF, 0xFF)]
    [InlineData(0, 0, 0, 0x00, 0x00)]
    public void Writes_rgb565_little_endian_with_red_in_the_top_bits(byte r, byte g, byte b, byte low, byte high)
    {
        using var frame = Frame(1, 1, (_, _) => new SKColor(r, g, b));
        var target = new byte[] { 0xAA, 0xAA };
        using var pixmap = frame.PeekPixels();

        PanelTransform.Render(pixmap, target, 1, 1, PanelRotation.None, PanelPixelFormat.Rgb565Le);

        Assert.Equal(new[] { low, high }, target);
    }

    [Fact]
    public void A_flat_colour_stays_flat_without_dithering()
    {
        using var frame = Frame(16, 16, (_, _) => new SKColor(100, 150, 200));
        var panel = Render(frame, 16, 16, PanelRotation.None);
        var first = panel[0, 0];
        Assert.All(panel.Cast<ushort>(), value => Assert.Equal(first, value));
    }

    [Fact]
    public void Target_length_is_two_bytes_a_pixel()
    {
        Assert.Equal(480 * 800 * 2, PanelTransform.TargetLength(480, 800, PanelPixelFormat.Rgb565Le));
    }

    [Fact]
    public void Rejects_a_target_of_the_wrong_length_and_empty_sizes()
    {
        using var frame = Frame(2, 2, (_, _) => SKColors.White);
        using var pixmap = frame.PeekPixels();

        Assert.Throws<ArgumentException>(() => PanelTransform.Render(pixmap, new byte[7], 2, 2, PanelRotation.None, PanelPixelFormat.Rgb565Le));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelTransform.TargetLength(0, 2, PanelPixelFormat.Rgb565Le));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PanelTransform.Render(pixmap, new byte[8], 2, 2, (PanelRotation)45, PanelPixelFormat.Rgb565Le));
    }

    [Fact]
    public void Rejects_a_target_longer_than_the_frame_and_leaves_it_untouched()
    {
        using var frame = Frame(2, 2, (_, _) => SKColors.White);
        using var pixmap = frame.PeekPixels();
        var target = new byte[PanelTransform.TargetLength(2, 2, PanelPixelFormat.Rgb565Le) + 2];
        target.AsSpan().Fill(0xAA);

        Assert.Throws<ArgumentException>(() => PanelTransform.Render(pixmap, target, 2, 2, PanelRotation.None, PanelPixelFormat.Rgb565Le));
        Assert.All(target, value => Assert.Equal(0xAA, value));
    }

    [Theory]
    [InlineData(SKColorType.Rgba8888, false)] // positive size, no pixels
    [InlineData(SKColorType.Unknown, true)]   // pixels Skia can't read as an image
    public void Rejects_a_frame_without_readable_pixels_before_writing_the_target(SKColorType colorType, bool withPixels)
    {
        var info = new SKImageInfo(2, 2, colorType, SKAlphaType.Premul);
        var pixels = new byte[2 * 2 * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, withPixels ? handle.AddrOfPinnedObject() : IntPtr.Zero, 2 * 4);
            var target = new byte[PanelTransform.TargetLength(2, 2, PanelPixelFormat.Rgb565Le)];
            target.AsSpan().Fill(0xAA);

            Assert.Throws<ArgumentException>(() => PanelTransform.Render(pixmap, target, 2, 2, PanelRotation.None, PanelPixelFormat.Rgb565Le));
            Assert.All(target, value => Assert.Equal(0xAA, value));
        }
        finally
        {
            handle.Free();
        }
    }

    [Theory]
    [InlineData(7, 5, PanelRotation.None)]      // the exact-size copy
    [InlineData(5, 7, PanelRotation.Rotate90)]  // turned at the panel size
    [InlineData(20, 9, PanelRotation.Rotate270)] // turned and fitted
    public void A_frame_with_padded_rows_renders_as_a_tight_one(int panelWidth, int panelHeight, PanelRotation rotation)
    {
        // A pooled buffer may hold rows longer than width × 4: the padding must be skipped, never drawn.
        const int w = 7, h = 5, rowBytes = w * 4 + 20;
        using var tight = Frame(w, h, (x, y) => Distinct(y * w + x));
        var padded = new byte[rowBytes * h];
        padded.AsSpan().Fill(0xFF);
        for (var y = 0; y < h; y++) tight.GetPixelSpan().Slice(y * w * 4, w * 4).CopyTo(padded.AsSpan(y * rowBytes));

        var expected = new byte[PanelTransform.TargetLength(panelWidth, panelHeight, PanelPixelFormat.Rgb565Le)];
        using (var pixmap = tight.PeekPixels())
        {
            PanelTransform.Render(pixmap, expected, panelWidth, panelHeight, rotation, PanelPixelFormat.Rgb565Le);
        }

        var actual = new byte[expected.Length];
        var handle = GCHandle.Alloc(padded, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(tight.Info, handle.AddrOfPinnedObject(), rowBytes);
            PanelTransform.Render(pixmap, actual, panelWidth, panelHeight, rotation, PanelPixelFormat.Rgb565Le);
        }
        finally
        {
            handle.Free();
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(65536, 32768)] // width × height overflows
    [InlineData(32768, 32768)] // width × height fits, the bytes don't
    public void Target_length_throws_instead_of_wrapping_past_int(int panelWidth, int panelHeight)
    {
        Assert.Throws<OverflowException>(() => PanelTransform.TargetLength(panelWidth, panelHeight, PanelPixelFormat.Rgb565Le));
    }
}

/// <summary>
/// A landscape 800×480 frame on three panels (<c>Golden/panels/{os}/</c>, macOS the reference): turned onto a portrait
/// one, pillarboxed on a wide one and letterboxed on a square one. Regenerate with
/// <c>UPDATE_GOLDENS=1 dotnet test InfoPanel.slnx --filter Category=Golden</c>.
/// </summary>
[Collection(GoldenCollection.Name)]
[Trait("Category", "Golden")]
public sealed class PanelTransformGoldenTests
{
    [Theory]
    [InlineData("landscape-on-480x800-rotate90", 480, 800, PanelRotation.Rotate90)]
    [InlineData("landscape-on-1280x480", 1280, 480, PanelRotation.None)]
    [InlineData("landscape-on-480x480", 480, 480, PanelRotation.None)]
    public void Matches_golden(string name, int panelWidth, int panelHeight, PanelRotation rotation)
    {
        using var frame = Landscape();
        using var pixmap = frame.PeekPixels();
        using var panel = new SKBitmap(new SKImageInfo(panelWidth, panelHeight, SKColorType.Rgb565, SKAlphaType.Opaque));

        PanelTransform.Render(pixmap, panel.GetPixelSpan(), panelWidth, panelHeight, rotation, PanelPixelFormat.Rgb565Le);

        GoldenImage.Verify(name, panel, folder: "panels");
    }

    /// <summary>
    /// 800×480: a gradient with a 1-pixel white border, a corner square of its own colour in each corner, an arrow
    /// pointing up, a circle and fine lines (to show the scaling), and a band at the bottom that is transparent on the
    /// left and half-transparent on the right.
    /// </summary>
    private static SKBitmap Landscape()
    {
        var bitmap = new SKBitmap(new SKImageInfo(800, 480, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(800, 400),
                   [new SKColor(20, 40, 120), new SKColor(20, 160, 150)], SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.DrawRect(0, 0, 800, 400, paint);
        }

        using var fill = new SKPaint { IsAntialias = true };
        fill.Color = new SKColor(255, 255, 255, 128);
        canvas.DrawRect(400, 400, 400, 80, fill);

        foreach (var (x, y, colour) in new[] { (0, 0, SKColors.Red), (720, 0, SKColors.Lime), (0, 400, SKColors.Blue), (720, 400, SKColors.Yellow) })
        {
            fill.Color = colour;
            canvas.DrawRect(x, y, 80, 80, fill);
        }

        fill.Color = SKColors.White;
        using (var arrow = new SKPath())
        {
            arrow.MoveTo(400, 60);
            arrow.LineTo(470, 160);
            arrow.LineTo(425, 160);
            arrow.LineTo(425, 300);
            arrow.LineTo(375, 300);
            arrow.LineTo(375, 160);
            arrow.LineTo(330, 160);
            arrow.Close();
            canvas.DrawPath(arrow, fill);
        }

        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = SKColors.White };
        stroke.StrokeWidth = 6;
        canvas.DrawCircle(600, 200, 90, stroke);
        stroke.StrokeWidth = 1;
        for (var k = 0; k < 12; k++) canvas.DrawLine(120 + k * 12, 120, 120 + k * 12, 320, stroke);
        canvas.DrawRect(0.5f, 0.5f, 799, 479, stroke);

        return bitmap;
    }
}
