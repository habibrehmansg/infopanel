using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Golden;

/// <summary>
/// Writes the image fixtures the goldens draw (<c>Fixtures/assets/</c>): a PNG, an SVG, a 3-frame GIF and
/// five gauge frames. The files are committed; regenerate them only on purpose, with
/// <c>GENERATE_FIXTURE_ASSETS=1 dotnet test InfoPanel.slnx --filter FullyQualifiedName~FixtureAssets</c>,
/// then regenerate the goldens.
/// </summary>
public class FixtureAssets
{
    public const int GaugeFrames = 5;

    [Fact]
    public void Generate()
    {
        if (Environment.GetEnvironmentVariable("GENERATE_FIXTURE_ASSETS") != "1")
        {
            Assert.Skip("Set GENERATE_FIXTURE_ASSETS=1 to regenerate Fixtures/assets.");
        }

        var directory = Path.Combine(TestPaths.ProjectDirectory, "Fixtures", "assets");
        Directory.CreateDirectory(directory);

        File.WriteAllBytes(Path.Combine(directory, "logo.png"), EncodePng(DrawLogo()));
        File.WriteAllText(Path.Combine(directory, "icon.svg"), Svg);
        File.WriteAllBytes(Path.Combine(directory, "blink.gif"), Gif.Encode(48, 32, Gif.Palette, [GifFrame(0), GifFrame(1), GifFrame(2)], delayCentiseconds: 20));

        for (var i = 0; i < GaugeFrames; i++)
        {
            File.WriteAllBytes(Path.Combine(directory, $"gauge-{i}.png"), EncodePng(DrawGaugeFrame(i / (float)(GaugeFrames - 1))));
        }
    }

    private const string Svg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">
          <rect x="2" y="2" width="60" height="60" rx="12" fill="#1E88E5"/>
          <circle cx="32" cy="32" r="18" fill="none" stroke="#FFFFFF" stroke-width="6"/>
          <path d="M32 14 L32 32 L46 40" fill="none" stroke="#FDD835" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>
        </svg>
        """;

    private static SKBitmap DrawLogo()
    {
        var bitmap = new SKBitmap(new SKImageInfo(96, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        using var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(96, 64),
            [new SKColor(0xFF, 0x70, 0x43), new SKColor(0x8E, 0x24, 0xAA)], SKShaderTileMode.Clamp);
        using var fill = new SKPaint { IsAntialias = true, Shader = gradient };
        canvas.DrawRoundRect(new SKRect(0, 0, 96, 64), 12, 12, fill);

        using var white = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawCircle(30, 32, 16, white);

        using var triangle = new SKPath();
        triangle.MoveTo(56, 16);
        triangle.LineTo(84, 32);
        triangle.LineTo(56, 48);
        triangle.Close();
        canvas.DrawPath(triangle, white);

        return bitmap;
    }

    private static SKBitmap DrawGaugeFrame(float fraction)
    {
        var bitmap = new SKBitmap(new SKImageInfo(80, 80, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var arc = new SKRect(10, 10, 70, 70);
        using var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8, Color = new SKColor(0x45, 0x4A, 0x55), StrokeCap = SKStrokeCap.Round };
        canvas.DrawArc(arc, 135, 270, false, track);

        using var value = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8, Color = new SKColor(0x26, 0xC6, 0xDA), StrokeCap = SKStrokeCap.Round };
        if (fraction > 0)
        {
            canvas.DrawArc(arc, 135, 270 * fraction, false, value);
        }

        var angle = (135 + 270 * fraction) * MathF.PI / 180;
        using var needle = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(0xFF, 0x52, 0x52), StrokeCap = SKStrokeCap.Round };
        canvas.DrawLine(40, 40, 40 + 24 * MathF.Cos(angle), 40 + 24 * MathF.Sin(angle), needle);

        using var hub = new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawCircle(40, 40, 4, hub);

        return bitmap;
    }

    /// <summary>A 48 × 32 frame: dark background, one coloured block whose position and colour change per frame.</summary>
    private static byte[] GifFrame(int frame)
    {
        const int width = 48, height = 32;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)1);

        var left = 4 + frame * 14;
        for (var y = 8; y < 24; y++)
        {
            for (var x = left; x < left + 12; x++)
            {
                pixels[y * width + x] = (byte)(2 + frame);
            }
        }

        // A white frame-number tick mark per frame along the bottom.
        for (var i = 0; i <= frame; i++)
        {
            for (var x = 4 + i * 6; x < 8 + i * 6; x++)
            {
                pixels[28 * width + x] = 6;
            }
        }

        return pixels;
    }

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using (bitmap)
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        {
            return data.ToArray();
        }
    }

    /// <summary>
    /// A minimal GIF89a encoder (Skia decodes GIF but can't encode it): 8-colour global palette, full-size
    /// frames, looping. The LZW stream holds only literal codes and resets the code table every four codes,
    /// so every code stays 4 bits wide; valid for any decoder, and simple enough to check by eye.
    /// </summary>
    internal static class Gif
    {
        public static readonly SKColor[] Palette =
        [
            new(0x00, 0x00, 0x00), new(0x20, 0x28, 0x30), new(0xE5, 0x39, 0x35), new(0x43, 0xA0, 0x47),
            new(0x1E, 0x88, 0xE5), new(0xFD, 0xD8, 0x35), new(0xFF, 0xFF, 0xFF), new(0x80, 0x80, 0x80),
        ];

        private const int MinCodeSize = 3;          // 8 colours
        private const int ClearCode = 1 << MinCodeSize;
        private const int EndCode = ClearCode + 1;
        private const int CodeWidth = MinCodeSize + 1;
        private const int LiteralsPerClear = 4;

        public static byte[] Encode(int width, int height, SKColor[] palette, IReadOnlyList<byte[]> frames, int delayCentiseconds)
        {
            using var output = new MemoryStream();

            output.Write("GIF89a"u8);
            WriteUInt16(output, width);
            WriteUInt16(output, height);
            output.WriteByte(0xF2);                 // global colour table, 8-bit resolution, 2^(2+1) = 8 entries
            output.WriteByte(0);                    // background colour index
            output.WriteByte(0);                    // no aspect ratio

            foreach (var colour in palette)
            {
                output.WriteByte(colour.Red);
                output.WriteByte(colour.Green);
                output.WriteByte(colour.Blue);
            }

            // NETSCAPE2.0 application extension: loop forever.
            output.Write([0x21, 0xFF, 0x0B]);
            output.Write("NETSCAPE2.0"u8);
            output.Write([0x03, 0x01, 0x00, 0x00, 0x00]);

            foreach (var frame in frames)
            {
                // Graphic control extension: disposal "leave in place", no transparency.
                output.Write([0x21, 0xF9, 0x04, 0x04]);
                WriteUInt16(output, delayCentiseconds);
                output.Write([0x00, 0x00]);

                // Image descriptor: full canvas, no local colour table, not interlaced.
                output.WriteByte(0x2C);
                WriteUInt16(output, 0);
                WriteUInt16(output, 0);
                WriteUInt16(output, width);
                WriteUInt16(output, height);
                output.WriteByte(0);

                output.WriteByte(MinCodeSize);
                WriteSubBlocks(output, Lzw(frame));
            }

            output.WriteByte(0x3B);
            return output.ToArray();
        }

        private static byte[] Lzw(byte[] pixels)
        {
            var bytes = new List<byte>();
            var buffer = 0;
            var bits = 0;

            void Emit(int code)
            {
                buffer |= code << bits;
                bits += CodeWidth;
                while (bits >= 8)
                {
                    bytes.Add((byte)(buffer & 0xFF));
                    buffer >>= 8;
                    bits -= 8;
                }
            }

            for (var i = 0; i < pixels.Length; i++)
            {
                if (i % LiteralsPerClear == 0)
                {
                    Emit(ClearCode);
                }

                Emit(pixels[i]);
            }

            Emit(EndCode);

            if (bits > 0)
            {
                bytes.Add((byte)(buffer & 0xFF));
            }

            return [.. bytes];
        }

        private static void WriteSubBlocks(Stream output, byte[] data)
        {
            for (var offset = 0; offset < data.Length; offset += 255)
            {
                var length = Math.Min(255, data.Length - offset);
                output.WriteByte((byte)length);
                output.Write(data, offset, length);
            }

            output.WriteByte(0);
        }

        private static void WriteUInt16(Stream output, int value)
        {
            output.WriteByte((byte)(value & 0xFF));
            output.WriteByte((byte)(value >> 8));
        }
    }
}

public class FixtureAssetTests
{
    /// <summary>The hand-written GIF encoder's output decodes, through Skia, to three frames with the expected colours.</summary>
    [Fact]
    public void Gif_DecodesToThreeFrames()
    {
        using var codec = SKCodec.Create(Path.Combine(FixtureAssetLocator.Directory, "blink.gif"));
        Assert.NotNull(codec);
        Assert.Equal(3, codec.FrameCount);

        var info = new SKImageInfo(48, 32, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (var frame = 0; frame < 3; frame++)
        {
            using var bitmap = new SKBitmap(info);
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(frame)));

            var block = FixtureAssets.Gif.Palette[2 + frame];
            Assert.Equal(block, bitmap.GetPixel(4 + frame * 14 + 6, 16));
            Assert.Equal(FixtureAssets.Gif.Palette[1], bitmap.GetPixel(1, 1));
        }
    }
}

public class SmallSvgTests
{
    /// <summary>The fixture SVG is under 512 bytes; it must still be read as an SVG, not handed to the raster codec.</summary>
    [Fact]
    public void SvgUnder512Bytes_LoadsAsVector()
    {
        var path = Path.Combine(FixtureAssetLocator.Directory, "icon.svg");
        Assert.True(new FileInfo(path).Length < 512);

        using var cache = new ImageCache(new ImageCacheOptions { SynchronousLoad = true });
        var image = cache.Get(path, persistent: false);

        Assert.NotNull(image);
        Assert.True(image.IsVector);
        Assert.Equal((64, 64), (image.Width, image.Height));
    }
}
