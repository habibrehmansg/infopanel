using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels;

/// <summary>
/// What the engine makes of a frame for the families 1.4.x drove beside BeadaPanel, in 1.4.x's order (dim, overlay,
/// crop, encode; <c>ThermalrightPanelDeviceTask.cs:158-199</c>): the raw formats by truncation (Thermalright's
/// <c>ConvertRgba8888ToRgb565</c>, <c>:343-394</c>), software dimming (each channel times the brightness, 1.4.x's
/// <c>ApplyBrightness</c>), a driver's PNG overlay for the rotation, the crop, and a JPEG over its cap encoded again a step
/// lower down to the floor.
/// </summary>
public sealed class PanelFrameWriterTests
{
    private readonly KeptLog _log = new();

    /// <summary>An opaque RGBA frame of <paramref name="width"/> × <paramref name="height"/>, pixel i given by <paramref name="color"/>.</summary>
    private static byte[] Rgba(int width, int height, Func<int, SKColor> color)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var c = color(i);
            (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (c.Red, c.Green, c.Blue, 255);
        }
        return rgba;
    }

    /// <summary>A premultiplied source frame Skia draws from, for the writer.</summary>
    private static SKBitmap Source(int width, int height, Func<int, int, SKColor> color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, color(x, y));
        return bitmap;
    }

    private static byte[] Png(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = Source(width, height, color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static PanelCapabilities Caps(int width, int height, PanelFrameFormat format) => new(width, height, [format], MaxFps: 30);

    private byte[] Written(PanelCapabilities capabilities, PanelFrameFormat format, SKBitmap source, PanelRotation rotation = PanelRotation.None, int brightness = 100)
    {
        using var writer = new PanelFrameWriter(capabilities, format, _log.Logger, "test panel");
        writer.Write(source.PeekPixels(), rotation, brightness);
        return writer.Frame.ToArray();
    }

    [Fact]
    public void One_pixel_in_each_new_format_is_its_channels_in_order_with_their_low_bits_dropped()
    {
        // 0xFF 0x80 0x3F: RGB565 keeps 11111, 100000, 00111.
        var rgba = Rgba(1, 1, _ => new SKColor(0xFF, 0x80, 0x3F));
        var be = new byte[2];
        var rgb = new byte[3];
        var bgr = new byte[3];
        var copy = new byte[4];

        PanelTransform.Convert(rgba, 1, 1, PanelFrameFormat.Rgb565Be, be);
        PanelTransform.Convert(rgba, 1, 1, PanelFrameFormat.Rgb888, rgb);
        PanelTransform.Convert(rgba, 1, 1, PanelFrameFormat.Bgr888, bgr);
        PanelTransform.Convert(rgba, 1, 1, PanelFrameFormat.Rgba8888, copy);

        Assert.Equal(new byte[] { 0b11111_100, 0b000_00111 }, be);
        Assert.Equal(new byte[] { 0xFF, 0x80, 0x3F }, rgb);
        Assert.Equal(new byte[] { 0x3F, 0x80, 0xFF }, bgr);
        Assert.Equal(new byte[] { 0xFF, 0x80, 0x3F, 0xFF }, copy);
    }

    [Fact]
    public void A_gradient_row_in_RGB565_big_endian_is_truncated_never_rounded_or_dithered()
    {
        // Every grey level 0 to 255 in a row: truncation maps each to level >> 3 (red, blue) and >> 2 (green), so 7 is
        // still 0 (rounding would give 1) and neighbours in a run of 8 are equal (dithering would vary them).
        var rgba = Rgba(256, 1, i => new SKColor((byte)i, (byte)i, (byte)i));
        var be = new byte[256 * 2];

        PanelTransform.Convert(rgba, 256, 1, PanelFrameFormat.Rgb565Be, be);

        for (var level = 0; level < 256; level++)
        {
            var word = (be[level * 2] << 8) | be[level * 2 + 1];
            Assert.Equal((level >> 3, level >> 2, level >> 3), (word >> 11, (word >> 5) & 0x3F, word & 0x1F));
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(50)]
    [InlineData(0)]
    public void Dimming_scales_each_channel_by_the_brightness_and_keeps_alpha(int percent)
    {
        var rgba = Rgba(2, 1, i => i == 0 ? new SKColor(200, 100, 50) : new SKColor(255, 255, 255));

        PanelTransform.Dim(rgba, 2, 1, percent);

        foreach (var (pixel, (r, g, b)) in new[] { (0, (200, 100, 50)), (1, (255, 255, 255)) })
        {
            Assert.Equal(255, rgba[pixel * 4 + 3]);
            var expected = new[] { r, g, b }.Select(c => c * percent / 100.0).ToArray();
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange(rgba[pixel * 4 + channel], Math.Floor(expected[channel]) - 1, Math.Ceiling(expected[channel]) + 1);
        }
        if (percent == 100)
            Assert.Equal(Rgba(2, 1, i => i == 0 ? new SKColor(200, 100, 50) : new SKColor(255, 255, 255)), rgba);
    }

    [Theory]
    [InlineData(PanelRotation.None, 0xFF0000)]
    [InlineData(PanelRotation.Rotate90, 0x00FF00)]
    [InlineData(PanelRotation.Rotate180, 0x0000FF)]
    [InlineData(PanelRotation.Rotate270, 0xFFFF00)]
    public void The_overlay_for_the_rotation_is_drawn_over_the_turned_frame_its_alpha_kept(PanelRotation rotation, int rgb)
    {
        // A 4 × 4 panel with an overlay per rotation, each opaque at its top left pixel and clear elsewhere.
        var overlays = new Dictionary<int, byte[]>
        {
            [0] = Png(4, 4, (x, y) => x == 0 && y == 0 ? new SKColor(0xFF, 0, 0) : SKColors.Transparent),
            [90] = Png(4, 4, (x, y) => x == 0 && y == 0 ? new SKColor(0, 0xFF, 0) : SKColors.Transparent),
            [180] = Png(4, 4, (x, y) => x == 0 && y == 0 ? new SKColor(0, 0, 0xFF) : SKColors.Transparent),
            [270] = Png(4, 4, (x, y) => x == 0 && y == 0 ? new SKColor(0xFF, 0xFF, 0) : SKColors.Transparent),
        };
        using var source = Source(4, 4, (_, _) => new SKColor(0x40, 0x40, 0x40));

        var frame = Written(Caps(4, 4, PanelFrameFormat.Rgba8888) with { Overlays = overlays }, PanelFrameFormat.Rgba8888, source, rotation);

        Assert.Equal(new byte[] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255 }, frame[..4]);
        Assert.Equal(new byte[] { 0x40, 0x40, 0x40, 255 }, frame[^4..]); // the clear part shows the frame
    }

    [Fact]
    public void A_rotation_without_an_overlay_is_drawn_without_one()
    {
        var overlays = new Dictionary<int, byte[]> { [0] = Png(2, 2, (_, _) => SKColors.Red) };
        using var source = Source(2, 2, (_, _) => SKColors.White);

        var frame = Written(Caps(2, 2, PanelFrameFormat.Rgb888) with { Overlays = overlays }, PanelFrameFormat.Rgb888, source, PanelRotation.Rotate180);

        Assert.All(frame, b => Assert.Equal(255, b));
    }

    [Fact]
    public void The_crop_sends_only_its_part_of_the_frame_as_rendered_at_full_size()
    {
        // 1.4.x's flicker fix: render at 480 rows, send the top 462 (here 4 × 6, the top 4 rows of columns 1 to 3).
        using var source = Source(4, 6, (x, y) => new SKColor((byte)(x * 10), (byte)(y * 10), 0));

        var frame = Written(Caps(4, 6, PanelFrameFormat.Rgb888) with { Crop = new PanelCrop(1, 0, 3, 4) }, PanelFrameFormat.Rgb888, source);

        Assert.Equal(3 * 4 * 3, frame.Length);
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 3; x++)
                Assert.Equal(new byte[] { (byte)((x + 1) * 10), (byte)(y * 10), 0 }, frame.AsSpan((y * 3 + x) * 3, 3).ToArray());
    }

    [Fact]
    public void The_overlay_is_drawn_after_dimming_and_before_the_crop()
    {
        // An opaque overlay pixel isn't dimmed (it is drawn after), and the crop takes the overlaid frame.
        var overlays = new Dictionary<int, byte[]> { [0] = Png(4, 2, (x, y) => x == 3 && y == 1 ? new SKColor(0xFF, 0xFF, 0xFF) : SKColors.Transparent) };
        using var source = Source(4, 2, (_, _) => new SKColor(200, 200, 200));
        var capabilities = Caps(4, 2, PanelFrameFormat.Rgb888) with { DimInSoftware = true, Overlays = overlays, Crop = new PanelCrop(2, 0, 2, 2) };

        var frame = Written(capabilities, PanelFrameFormat.Rgb888, source, brightness: 50);

        Assert.InRange(frame[0], 99, 101); // a dimmed frame pixel
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, frame[^3..]); // the overlay's, undimmed, at the crop's bottom right
    }

    [Fact]
    public void A_panel_that_doesnt_dim_in_software_is_sent_its_frame_at_full_brightness()
    {
        using var source = Source(1, 1, (_, _) => new SKColor(200, 200, 200));

        var frame = Written(Caps(1, 1, PanelFrameFormat.Rgb888), PanelFrameFormat.Rgb888, source, brightness: 10);

        Assert.Equal(new byte[] { 200, 200, 200 }, frame);
    }

    [Fact]
    public void A_jpeg_over_its_cap_is_encoded_again_a_step_lower_down_to_the_floor_and_logged_once_a_session()
    {
        // Noise no JPEG of it fits in 100 bytes: 90, then 65, then 40, the floor, and no lower.
        var random = new Random(7);
        using var source = Source(64, 64, (_, _) => new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        var capabilities = Caps(64, 64, PanelFrameFormat.Jpeg) with { MaxFrameBytes = 100, JpegQuality = 90, MinJpegQuality = 40, JpegQualityStep = 25 };
        using var writer = new PanelFrameWriter(capabilities, PanelFrameFormat.Jpeg, _log.Logger, "test panel");

        writer.Write(source.PeekPixels(), PanelRotation.None, 100);
        writer.Write(source.PeekPixels(), PanelRotation.None, 100);

        Assert.Equal(40, writer.Quality);
        Assert.Single(_log.Lines, l => l.Contains("encoded again at q40"));
    }

    [Fact]
    public void A_jpeg_over_its_cap_is_encoded_again_until_it_fits_and_that_one_is_sent()
    {
        // A plain frame over 600 bytes at 95 fits a few steps down: the first that fits is the frame.
        using var source = Source(64, 64, (x, y) => new SKColor((byte)(x * 4), (byte)(y * 4), 128));
        var unlimited = Caps(64, 64, PanelFrameFormat.Jpeg) with { JpegQuality = 95 };
        using var probe = new PanelFrameWriter(unlimited, PanelFrameFormat.Jpeg, _log.Logger, "test panel");
        probe.Write(source.PeekPixels(), PanelRotation.None, 100);
        var cap = probe.Frame.Length - 1;
        using var writer = new PanelFrameWriter(unlimited with { MaxFrameBytes = cap, MinJpegQuality = 5, JpegQualityStep = 10 }, PanelFrameFormat.Jpeg, _log.Logger, "test panel");

        writer.Write(source.PeekPixels(), PanelRotation.None, 100);

        Assert.InRange(writer.Quality, 5, 85);
        Assert.InRange(writer.Frame.Length, 1, cap);
        using var decoded = SKBitmap.Decode(writer.Frame.ToArray());
        Assert.Equal((64, 64), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void A_jpeg_still_over_its_cap_at_the_floor_isnt_handed_on_and_the_last_frame_stays_logged_once()
    {
        // A plain frame fits 1200 bytes; noise doesn't even at the floor: the driver never gets more than its cap (a
        // hosted session's slot holds no more), and the panel keeps showing the last frame that fitted.
        using var plain = Source(64, 64, (_, _) => SKColors.SteelBlue);
        var random = new Random(11);
        using var noise = Source(64, 64, (_, _) => new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        var capabilities = Caps(64, 64, PanelFrameFormat.Jpeg) with { MaxFrameBytes = 1200, JpegQuality = 90, MinJpegQuality = 70, JpegQualityStep = 10 };
        using var writer = new PanelFrameWriter(capabilities, PanelFrameFormat.Jpeg, _log.Logger, "test panel");

        writer.Write(plain.PeekPixels(), PanelRotation.None, 100);
        var fitted = writer.Frame.ToArray();
        writer.Write(noise.PeekPixels(), PanelRotation.None, 100);
        writer.Write(noise.PeekPixels(), PanelRotation.None, 100);

        Assert.InRange(fitted.Length, 1, 1200);
        Assert.Equal(fitted, writer.Frame.ToArray());
        Assert.Single(_log.Lines, l => l.Contains("isn't sent"));
    }

    [Fact]
    public void A_jpeg_within_its_cap_is_encoded_once_at_its_quality()
    {
        using var source = Source(16, 16, (_, _) => SKColors.SteelBlue);
        var capabilities = Caps(16, 16, PanelFrameFormat.Jpeg) with { MaxFrameBytes = 100_000, JpegQuality = 85 };
        using var writer = new PanelFrameWriter(capabilities, PanelFrameFormat.Jpeg, _log.Logger, "test panel");

        writer.Write(source.PeekPixels(), PanelRotation.None, 100);

        Assert.Equal(85, writer.Quality);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("encoded again"));
    }

    [Fact]
    public void Capabilities_the_writer_cant_follow_are_named()
    {
        Assert.Contains("crop", PanelFrameWriter.Problem(Caps(4, 4, PanelFrameFormat.Rgb888) with { Crop = new PanelCrop(2, 0, 3, 4) }));
        Assert.Contains("45", PanelFrameWriter.Problem(Caps(4, 4, PanelFrameFormat.Rgb888) with { Overlays = new Dictionary<int, byte[]> { [45] = [1] } }));
        Assert.Contains("step", PanelFrameWriter.Problem(Caps(4, 4, PanelFrameFormat.Jpeg) with { JpegQualityStep = 0 }));
        Assert.Null(PanelFrameWriter.Problem(Caps(4, 4, PanelFrameFormat.Rgb888) with { Crop = new PanelCrop(0, 0, 4, 3) }));
    }

    [Fact]
    public void BeadaPanels_RGB565_little_endian_stays_Skias_conversion_which_isnt_truncation()
    {
        // Left as it is (1.4's BeadaPanel stream): Skia rounds each channel to its 5 or 6 bits, so a grey of 7 is 1 where
        // truncation (the new formats', Thermalright's) gives 0. Written down so a family needing truncated little-endian
        // RGB565 knows to ask for it.
        var rgba = Rgba(256, 1, i => new SKColor((byte)i, (byte)i, (byte)i));
        var le = new byte[256 * 2];

        PanelTransform.Convert(rgba, 256, 1, PanelFrameFormat.Rgb565Le, le);

        var differ = Enumerable.Range(0, 256).Count(level => (le[level * 2] | (le[level * 2 + 1] << 8)) >> 11 != level >> 3);
        Assert.Equal(1, (le[7 * 2] | (le[7 * 2 + 1] << 8)) >> 11);
        Assert.True(differ > 0);
    }

    /// <summary>A PNG's signature and header alone, declaring <paramref name="width"/> × <paramref name="height"/> (its CRC left 0), then its end.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange([(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width]);
        bytes.AddRange([(byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height]);
        bytes.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0]);
        bytes.AddRange([0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82]);
        return [.. bytes];
    }

    [Fact]
    public void An_overlay_is_checked_by_the_size_its_header_declares_before_anything_decodes_it()
    {
        // A few dozen bytes declaring 60000 × 60000: refused by its header, never handed to Skia.
        var huge = Caps(4, 4, PanelFrameFormat.Rgba8888) with { Overlays = new Dictionary<int, byte[]> { [0] = PngHeader(60000, 60000) } };
        Assert.Contains("60000", PanelFrameWriter.Problem(huge));
        Assert.Throws<ArgumentException>(() => new PanelFrameWriter(huge, PanelFrameFormat.Rgba8888, _log.Logger, "test panel"));

        // Not the panel's size at its rotation (the frame it is drawn on is the panel's own, whatever the rotation).
        Assert.NotNull(PanelFrameWriter.Problem(Caps(4, 4, PanelFrameFormat.Rgba8888) with { Overlays = new Dictionary<int, byte[]> { [90] = Png(4, 2, (_, _) => SKColors.Red) } }));
        Assert.NotNull(PanelFrameWriter.Problem(Caps(4, 2, PanelFrameFormat.Rgba8888) with { Overlays = new Dictionary<int, byte[]> { [270] = Png(2, 4, (_, _) => SKColors.Red) } }));
        // Not a PNG at all.
        Assert.NotNull(PanelFrameWriter.Problem(Caps(4, 4, PanelFrameFormat.Rgba8888) with { Overlays = new Dictionary<int, byte[]> { [0] = [1, 2, 3, 4] } }));
        // The panel's own size, but more decoded bytes than an overlay may take.
        Assert.Contains("bytes", PanelFrameWriter.Problem(Caps(10000, 10000, PanelFrameFormat.Rgba8888) with { Overlays = new Dictionary<int, byte[]> { [0] = PngHeader(10000, 10000) } }));
        // The panel's size: taken.
        Assert.Null(PanelFrameWriter.Problem(Caps(4, 2, PanelFrameFormat.Rgba8888) with { Overlays = new Dictionary<int, byte[]> { [180] = Png(4, 2, (_, _) => SKColors.Red) } }));
    }

    [Fact]
    public void Truncated_RGB565_little_endian_is_1_4_Thermalrights_conversion_byte_for_byte()
    {
        // 1.4.x ThermalrightPanelDeviceTask.ConvertRgba8888ToRgb565: hi = (r & 0xF8) | (g >> 5), lo = ((g & 0x1C) << 3) | (b >> 3),
        // little-endian lo then hi. 0xFF 0x80 0x3F: 11111, 100000, 00111.
        var one = new byte[2];
        PanelTransform.Convert(Rgba(1, 1, _ => new SKColor(0xFF, 0x80, 0x3F)), 1, 1, PanelFrameFormat.Rgb565LeTruncated, one);
        Assert.Equal(new byte[] { 0b000_00111, 0b11111_100 }, one);

        var rgba = Rgba(256, 1, i => new SKColor((byte)i, (byte)(255 - i), (byte)(i ^ 0x5A)));
        var le = new byte[256 * 2];
        PanelTransform.Convert(rgba, 256, 1, PanelFrameFormat.Rgb565LeTruncated, le);
        for (var i = 0; i < 256; i++)
        {
            byte r = (byte)i, g = (byte)(255 - i), b = (byte)(i ^ 0x5A);
            Assert.Equal(new byte[] { (byte)(((g & 0x1C) << 3) | (b >> 3)), (byte)((r & 0xF8) | (g >> 5)) }, le[(i * 2)..(i * 2 + 2)]);
        }
        Assert.Equal(0, (le[7 * 2] | (le[7 * 2 + 1] << 8)) >> 11); // grey 7 is 0, where BeadaPanel's Rgb565Le gives 1

        // Through the writer, turned like any frame.
        using var source = Source(2, 1, (x, _) => x == 0 ? new SKColor(0xFF, 0x80, 0x3F) : new SKColor(7, 7, 7));
        Assert.Equal(new byte[] { 0b000_00111, 0b11111_100, 0b001_00000, 0 },Written(Caps(2, 1, PanelFrameFormat.Rgb565LeTruncated), PanelFrameFormat.Rgb565LeTruncated, source));
    }

    [Fact]
    public void A_BeadaPanel_frame_with_none_of_them_is_the_transform_it_always_was()
    {
        using var source = Source(8, 4, (x, y) => new SKColor((byte)(x * 30), (byte)(y * 60), 128));
        var expected = new byte[8 * 4 * 2];
        PanelTransform.Render(source.PeekPixels(), expected, 8, 4, PanelRotation.Rotate180, PanelPixelFormat.Rgb565Le);

        Assert.Equal(expected, Written(Caps(8, 4, PanelFrameFormat.Rgb565Le), PanelFrameFormat.Rgb565Le, source, PanelRotation.Rotate180));
    }
}
