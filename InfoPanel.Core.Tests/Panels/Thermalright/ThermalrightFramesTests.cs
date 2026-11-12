using System.Security.Cryptography;
using InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle;
using InfoPanel.Panels.Thermalright;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The pixel work 1.4.x did on a frame before sending it, against its own code (the V1 oracle,
/// <c>Services/ThermalrightPanelDeviceTask.cs</c> at c71f0f00): RGB565 by truncation (TRCC's, never dithered), the flicker
/// fix's crop, the JPEG cap and the quality steps, and the display masks' overlays.
/// </summary>
public sealed class ThermalrightFramesTests
{
    private static ThermalrightCatalog Catalog => ThermalrightCatalog.Default;

    private static SKBitmap Noise(int width, int height, int rowBytes, int seed)
    {
        var bitmap = new SKBitmap();
        Assert.True(bitmap.TryAllocPixels(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque), rowBytes));
        var pixels = new byte[rowBytes * height];
        new Random(seed).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255; // opaque, as every frame 1.4.x rendered
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }

    [Theory]
    [InlineData(37, 13, 37 * 4)]
    [InlineData(37, 13, 37 * 4 + 12)]
    [InlineData(320, 320, 320 * 4)]
    public void Rgb565_TruncatesAs1_4_BothByteOrders(int width, int height, int rowBytes)
    {
        // :343-391 (TRCC's FormCZTV.ImageTo565, no dithering)
        using var bitmap = Noise(width, height, rowBytes, width + rowBytes);
        foreach (var bigEndian in new[] { false, true })
        {
            var port = new byte[width * height * 2];
            ThermalrightFrames.ToRgb565(bitmap.GetPixelSpan(), width, height, rowBytes, bigEndian, port);
            Assert.Equal(V1DeviceTask.ConvertRgba8888ToRgb565(bitmap, bigEndian), port);
        }
    }

    [Theory]
    [InlineData(37, 13)]
    [InlineData(320, 320)]
    [InlineData(240, 320)]
    public void TheEnginesConversion_OfEachModelsFormat_IsThe1_4Truncation(int width, int height)
    {
        // :350-385: what the engine's transform sends these panels (PanelTransform.Convert, the formats their sessions
        // declare) against 1.4's own conversion: RGB565 little-endian truncated for the HID and ALi models, big-endian for
        // ChiZhu's 320 × 320 and HID PM 32.
        using var bitmap = Noise(width, height, width * 4, width * height);
        foreach (var (format, bigEndian) in new[] { (InfoPanel.Panels.Sdk.PanelFrameFormat.Rgb565LeTruncated, false), (InfoPanel.Panels.Sdk.PanelFrameFormat.Rgb565Be, true) })
        {
            var engine = new byte[width * height * 2];
            InfoPanel.Panels.PanelTransform.Convert(bitmap.GetPixelSpan(), width, height, format, engine);
            Assert.Equal(V1DeviceTask.ConvertRgba8888ToRgb565(bitmap, bigEndian), engine);
        }
        Assert.Equal(
            [InfoPanel.Panels.Sdk.PanelFrameFormat.Rgb565LeTruncated, InfoPanel.Panels.Sdk.PanelFrameFormat.Rgb565Be, InfoPanel.Panels.Sdk.PanelFrameFormat.Jpeg],
            new[] { ThermalrightPixels.Rgb565Le, ThermalrightPixels.Rgb565Be, ThermalrightPixels.Jpeg }.Select(ThermalrightDriver.FormatOf));
    }

    [Fact]
    public void FlickerFix_CropsTheTop462Rows_Of480()
    {
        // :177-185
        using var bitmap = Noise(1920, 480, 1920 * 4, 9);
        using var cropped = V1DeviceTask.Crop(bitmap, 1920, 462);
        var port = ThermalrightFrames.CropTop(bitmap.GetPixelSpan(), 1920, bitmap.RowBytes, 462);
        Assert.Equal(cropped.GetPixelSpan().ToArray(), port);
    }

    [Theory]
    [InlineData(1_073_741_824, 4, 1, 4)]   // width × 4 wraps to 0: once an empty frame, "cropped"
    [InlineData(0, 0, 1, 0)]                // no width
    [InlineData(2, 4, 1, 8)]                // a stride shorter than a row
    [InlineData(2, 8, 2, 15)]               // the source ends inside the last row
    [InlineData(2, 8, -1, 16)]              // a negative row count
    [InlineData(65_536, 262_144, 65_536, 1)] // width × 4 × rows past int
    public void CropTop_RefusesSizesThatWrapOrOverrunTheSource(int width, int stride, int rows, int sourceLength)
    {
        Assert.ThrowsAny<ArgumentException>(() => ThermalrightFrames.CropTop(new byte[sourceLength], width, stride, rows));
    }

    [Fact]
    public void CropTop_OfZeroRows_IsEmpty_AndOfAnExactSource_IsItsRows()
    {
        Assert.Empty(ThermalrightFrames.CropTop(new byte[16], 2, 8, 0));
        var source = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Assert.Equal(source[..8], ThermalrightFrames.CropTop(source, 2, 8, 1));
        Assert.Equal(source, ThermalrightFrames.CropTop(source, 2, 8, 2));
    }

    [Fact]
    public void JpegCap_IsTheOneEachModelsSessionSet()
    {
        // :1817-1827 (HID JPEG, 400 KB: 315ab6f), :1129 and :1369 (TrofeoBulk and LY1, 230 KB)
        foreach (var v1 in ThermalrightPanelModelDatabase.Models.Values)
        {
            var model = Catalog.ByV1Model(v1.Model.ToString())!;
            var cap = V1DeviceTask.MaxJpegSize(v1);
            Assert.Equal(cap == 0 ? null : cap, ThermalrightFrames.MaxJpegBytes(model));
        }
    }

    [Theory]
    [InlineData(95)]
    [InlineData(90)]
    [InlineData(56)]
    [InlineData(55)]
    [InlineData(54)]
    [InlineData(50)]
    [InlineData(10)]
    public void JpegQuality_StepsDownBy5_To50(int quality)
    {
        // :201-218: every lower quality 1.4.x tries while a frame stays over the cap
        var v1 = new V1DeviceTask(new V1Device()) { _maxJpegSize = 1 };
        Assert.Equal(v1.AdaptiveQualities(quality, _ => 2), ThermalrightFrames.LowerQualities(quality));
    }

    private static readonly Dictionary<string, string> MaskSha256 = new()
    {
        // shasum -a 256 of 1.4.x c71f0f00's InfoPanel/Resources/Overlays/*.png
        ["mask_rounded_all_0.png"] = "945125fc83c6f638560f4e06fa27c717308bff2864fddab5241532bcbb4c28fe",
        ["mask_rounded_all_90.png"] = "416f7ebbea7863c37b59c8e7e8e37192fcdefe765e7b70a16e88d28b92dc8e9d",
        ["mask_rounded_all_180.png"] = "945125fc83c6f638560f4e06fa27c717308bff2864fddab5241532bcbb4c28fe",
        ["mask_rounded_all_270.png"] = "416f7ebbea7863c37b59c8e7e8e37192fcdefe765e7b70a16e88d28b92dc8e9d",
        ["mask_rounded_left_0.png"] = "4bad38b7f32c95511208b5ee1b702ac39b8971ccf1becf16e4020e4f200c20e2",
        ["mask_rounded_left_90.png"] = "142e687eb38c84b1fee0bf3b317452decebb332ca9b2d423b57056ae40528ac1",
        ["mask_rounded_left_180.png"] = "7996e877d52bbd8dae1be2faabae8ab38a01299d42ddec5945311f32b07f6c1c",
        ["mask_rounded_left_270.png"] = "fb12c5c9d9bb36f866c5a2bccf4a186754f8a8cd2c8314ddeadcaafe2f281246",
    };

    [Fact]
    public void Masks_AreThe1_4Overlays_AtFourRotations_TheLevitaTurned180()
    {
        // :294-341: the overlay for the rotation plus the model's turn (:173), from the eight PNGs
        foreach (var model in Catalog.Models.Where(m => m.DisplayMask))
            foreach (var mask in new[] { ThermalrightMask.RoundedLeft, ThermalrightMask.RoundedAll })
                foreach (var rotation in new[] { 0, 90, 180, 270 })
                {
                    var v1Mask = mask == ThermalrightMask.RoundedLeft ? ThermalrightDisplayMask.RoundedLeft : ThermalrightDisplayMask.RoundedAll;
                    var v1Name = V1DeviceTask.MaskResourceName(v1Mask, rotation, model.MaskTurn);
                    var degrees = ThermalrightFrames.MaskDegrees(rotation, model.MaskTurn);
                    var name = ThermalrightFrames.MaskResource(mask, degrees);
                    Assert.Equal(v1Name, "InfoPanel.Resources.Overlays." + name);

                    using var stream = ThermalrightFrames.OpenMask(mask, degrees)!;
                    Assert.Equal(MaskSha256[name!], Convert.ToHexStringLower(SHA256.HashData(stream)));
                }
        Assert.Null(ThermalrightFrames.MaskResource(ThermalrightMask.None, 0));
        Assert.Null(ThermalrightFrames.OpenMask(ThermalrightMask.None, 0));
    }

    /// <summary>
    /// What 1.4 drew of a portrait (720 × 1600) mask on the 6.67" panels' landscape frame (1600 × 720): the PNG at (0, 0),
    /// clipped to the frame, so its top 720 rows at the frame's left and nothing elsewhere. Made from the embedded 1.4 PNG
    /// as a panel-size PNG, its pixels copied as stored (unpremultiplied, so nothing is rounded), the rest transparent.
    /// </summary>
    private static byte[] ClippedMask(Stream portraitPng, int width, int height)
    {
        using var codec = SKCodec.Create(portraitPng) ?? throw new InvalidOperationException("the 1.4 mask isn't a picture Skia reads");
        using var portrait = SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var (columns, rows) = (Math.Min(width, portrait.Width), Math.Min(height, portrait.Height));
        var source = portrait.GetPixelSpan();
        var pixels = new byte[width * height * 4]; // transparent
        for (var y = 0; y < rows; y++)
            source.Slice(y * portrait.RowBytes, columns * 4).CopyTo(pixels.AsSpan(y * width * 4));
        using var clipped = new SKBitmap();
        Assert.True(clipped.TryAllocPixels(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), width * 4));
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, clipped.GetPixels(), pixels.Length);
        using var pixmap = clipped.PeekPixels();
        using var png = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9))
            ?? throw new InvalidOperationException("Skia couldn't encode the clipped mask");
        return png.ToArray();
    }

    /// <summary>
    /// Writes the 6.67" panels' 90° and 270° masks as panel-size PNGs (<c>mask_rounded_*_{90,270}_clipped.png</c>, beside
    /// 1.4's own in the package's <c>Overlays/</c>) from 1.4's portrait PNGs embedded there. Committed; made again only
    /// on purpose, with <c>GENERATE_THERMALRIGHT_MASKS=1 dotnet test InfoPanel.slnx --filter
    /// FullyQualifiedName~ThermalrightFramesTests.ClippedMasks_Generate</c>; the oracle test below proves them.
    /// </summary>
    [Fact]
    public void ClippedMasks_Generate()
    {
        if (Environment.GetEnvironmentVariable("GENERATE_THERMALRIGHT_MASKS") != "1")
            Assert.Skip("Set GENERATE_THERMALRIGHT_MASKS=1 to make the Thermalright package's clipped masks again.");
        var directory = Path.GetFullPath(Path.Combine(Golden.TestPaths.ProjectDirectory, "..", "InfoPanel.Panels.Thermalright", "Overlays"));
        Assert.True(Directory.Exists(directory), directory);
        foreach (var mask in new[] { ThermalrightMask.RoundedLeft, ThermalrightMask.RoundedAll })
            foreach (var degrees in new[] { 90, 270 })
            {
                using var portrait = ThermalrightFrames.OpenMask(mask, degrees)!;
                var name = ThermalrightFrames.MaskResource(mask, degrees)!.Replace(".png", "_clipped.png");
                File.WriteAllBytes(Path.Combine(directory, name), ClippedMask(portrait, 1600, 720));
            }
    }

    [Fact]
    public void Overlays_AtEveryRotation_DrawWhat1_4Drew_PixelForPixel()
    {
        // :169-175, :294-341: 1.4 drew the mask's PNG for the rotation plus the model's turn at (0, 0) over the panel's
        // landscape frame, its 90° and 270° PNGs (portrait) clipped to it. The engine draws the driver's overlay for the
        // rotation at (0, 0) (PanelTransform.Overlay). Over a frame of noise, every rotation's overlay draws what 1.4 drew,
        // pixel for pixel, for each mask and each model's turn (the Levita's 180°). 1.4's clipped rounded-left 270° PNG
        // draws nothing (none of its opaque pixels is in the part the frame keeps, its top 720 rows): the engine's neither.
        using var background = Noise(1600, 720, 1600 * 4, 667);
        var drawsNothing = new List<string>();
        foreach (var turn in Catalog.Models.Where(m => m.DisplayMask).Select(m => m.MaskTurn).Distinct())
            foreach (var mask in new[] { ThermalrightMask.RoundedLeft, ThermalrightMask.RoundedAll })
            {
                var overlays = ThermalrightDriver.Overlays(mask, turn);
                Assert.Equal([0, 90, 180, 270], overlays.Keys.Order());
                foreach (var rotation in new[] { 0, 90, 180, 270 })
                {
                    // 1.4: the 1.4 PNG (the embedded one is 1.4's, by its hash above), decoded and drawn at (0, 0).
                    using var v1 = background.Copy();
                    using (var stream = ThermalrightFrames.OpenMask(mask, ThermalrightFrames.MaskDegrees(rotation, turn))!)
                    using (var v1Mask = SKBitmap.Decode(stream))
                    using (var canvas = new SKCanvas(v1))
                        canvas.DrawBitmap(v1Mask, 0, 0);

                    var engine = background.GetPixelSpan().ToArray();
                    using (var overlay = SKImage.FromEncodedData(overlays[rotation]))
                        InfoPanel.Panels.PanelTransform.Overlay(engine, 1600, 720, overlay);

                    var expected = v1.GetPixelSpan().ToArray();
                    var differ = Enumerable.Range(0, expected.Length / 4).Count(p => !expected.AsSpan(p * 4, 4).SequenceEqual(engine.AsSpan(p * 4, 4)));
                    Assert.True(differ == 0, $"{mask} at {rotation}° (turn {turn}): {differ} pixels differ from 1.4's drawing");
                    if (expected.AsSpan().SequenceEqual(background.GetPixelSpan()))
                        drawsNothing.Add($"{mask} {ThermalrightFrames.MaskDegrees(rotation, turn)}");
                }
            }
        Assert.Equal(["RoundedLeft 270", "RoundedLeft 270"], drawsNothing); // at 270° (turn 0) and at 90° (the Levita's)
    }
}
