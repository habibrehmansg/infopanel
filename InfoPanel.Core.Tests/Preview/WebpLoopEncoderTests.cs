using System.Buffers.Binary;
using System.Text;
using InfoPanel.Output;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

public class WebpLoopEncoderTests(ITestOutputHelper output)
{
    private const int Width = 360;
    private const int Height = 200;
    private const int Fps = 10;
    private const int FramesPerState = 20; // four states, 8 s at 10 fps
    private const int WidgetCap = 256 * 1024;

    /// <summary>
    /// A ring gauge's loop drawn straight with Skia: normal (green, rising), warning (amber), critical (red, pulsing),
    /// no reading (grey, dashed). Stands in for a recorded scene in the size check.
    /// </summary>
    private static List<LoopFrame> RingLoop()
    {
        var frames = new List<LoopFrame>();
        var states = new (SKColor Colour, float From, float To)[]
        {
            (new SKColor(0x3d, 0xd6, 0x8c), 0.20f, 0.55f),
            (new SKColor(0xf5, 0xb8, 0x2e), 0.70f, 0.85f),
            (new SKColor(0xef, 0x44, 0x44), 0.90f, 0.98f),
            (new SKColor(0x6b, 0x72, 0x80), 0f, 0f),
        };
        using var font = new SKFont(SKTypeface.Default, 36);
        for (var s = 0; s < states.Length; s++)
        {
            var (colour, from, to) = states[s];
            for (var i = 0; i < FramesPerState; i++)
            {
                var t = i / (float)(FramesPerState - 1);
                var value = from + (to - from) * t;
                var bitmap = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(new SKColor(0x12, 0x14, 0x1a));
                var rect = new SKRect(Width / 2f - 80, 20, Width / 2f + 80, 180);
                using var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 18, Color = new SKColor(0x2a, 0x2e, 0x38) };
                canvas.DrawArc(rect, 135, 270, false, track);
                using var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 18, StrokeCap = SKStrokeCap.Round, Color = colour };
                if (s == 2)
                    arc.Color = colour.WithAlpha((byte)(180 + 75 * MathF.Abs(MathF.Sin(i * 0.6f))));
                if (s < 3)
                    canvas.DrawArc(rect, 135, 270 * value, false, arc);
                using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
                var label = s < 3 ? $"{value * 100:0}%" : "--";
                canvas.DrawText(label, Width / 2f, 112, SKTextAlign.Center, font, text);
                frames.Add(new LoopFrame(bitmap, TimeSpan.FromMilliseconds(1000 / Fps)));
            }
        }
        return frames;
    }

    /// <summary>The top-level RIFF chunks: each one's four-character id and where its payload starts.</summary>
    private static List<(string Id, int Payload)> TopLevelChunks(byte[] webp)
    {
        Assert.True(webp.Length >= 12);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(webp, 8, 4));
        Assert.Equal((uint)webp.Length - 8, BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4)));
        var chunks = new List<(string, int)>();
        var at = 12;
        while (at + 8 <= webp.Length)
        {
            var id = Encoding.ASCII.GetString(webp, at, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(at + 4));
            chunks.Add((id, at + 8));
            at += 8 + size + (size & 1);
        }
        Assert.Equal(webp.Length, at);
        return chunks;
    }

    /// <summary>
    /// How many frames the file holds: libwebp's encoder folds a frame identical to the one before into it (its
    /// duration added), so a run of identical frames is one ANMF.
    /// </summary>
    private static int DistinctNeighbours(List<LoopFrame> frames)
    {
        var count = 1;
        for (var i = 1; i < frames.Count; i++)
        {
            if (!frames[i].Bitmap.GetPixelSpan().SequenceEqual(frames[i - 1].Bitmap.GetPixelSpan()))
                count++;
        }
        return count;
    }

    private static void AssertAnimatedWebp(byte[] webp, int frameCount, TimeSpan total)
    {
        var chunks = TopLevelChunks(webp);
        Assert.Equal("VP8X", chunks[0].Id);
        Assert.Equal(1, chunks.Count(c => c.Id == "ANIM"));
        var frames = chunks.Where(c => c.Id == "ANMF").ToList();
        Assert.Equal(frameCount, frames.Count);
        Assert.True((webp[20] & 0x02) != 0, "VP8X's animation flag is set");
        // An ANMF payload: X/2, Y/2, width − 1, height − 1 and the duration in ms, 24 bits each, then the flags.
        var ms = frames.Sum(f => webp[f.Payload + 12] | webp[f.Payload + 13] << 8 | webp[f.Payload + 14] << 16);
        Assert.Equal((int)total.TotalMilliseconds, ms);

        using var codec = SKCodec.Create(new SKMemoryStream(webp));
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(frameCount, codec.FrameCount);
        Assert.Equal(new SKSizeI(Width, Height), codec.Info.Size);
    }

    [Fact]
    public void ARingLoop_FitsTheWidgetCap_AndIsAValidAnimatedWebp()
    {
        var frames = RingLoop();
        try
        {
            var lossless = WebpLoopEncoder.Encode(frames, null);
            var lossy85 = WebpLoopEncoder.Encode(frames, 85);
            var lossy70 = WebpLoopEncoder.Encode(frames, 70);
            output.WriteLine($"ring {Width}x{Height}, {Fps} fps, {frames.Count} frames: lossless {lossless.Length} B, lossy 85 {lossy85.Length} B, lossy 70 {lossy70.Length} B; cap {WidgetCap} B");

            var distinct = DistinctNeighbours(frames);
            Assert.Equal(61, distinct); // the no-reading state's 20 frames are one picture
            var total = TimeSpan.FromSeconds(frames.Count / (double)Fps);
            AssertAnimatedWebp(lossless, distinct, total);
            AssertAnimatedWebp(lossy85, distinct, total);
            AssertAnimatedWebp(lossy70, distinct, total);

            // The setting a widget's loop would take: lossless when it fits, else the best lossy quality that does.
            var chosen = new (string Name, byte[] Bytes)[] { ("lossless", lossless), ("lossy 85", lossy85), ("lossy 70", lossy70) }
                .FirstOrDefault(c => c.Bytes.Length <= WidgetCap);
            output.WriteLine($"chosen: {chosen.Name ?? "none"}");
            Assert.NotNull(chosen.Name);
        }
        finally
        {
            foreach (var frame in frames)
                frame.Bitmap.Dispose();
        }
    }

    [Fact]
    public void ARingLoopsStates_JoinedAsRuns_FitTheCap_WithEveryStatesFirstFrameWhole()
    {
        var frames = RingLoop();
        try
        {
            var runs = Enumerable.Range(0, 4).Select(s => (IReadOnlyList<LoopFrame>)frames.Skip(s * FramesPerState).Take(FramesPerState).ToList()).ToList();
            var (lossless, keys) = WebpLoopEncoder.EncodeRuns(runs, null);
            var (lossy85, _) = WebpLoopEncoder.EncodeRuns(runs, 85);
            output.WriteLine($"ring as four runs: lossless {lossless.Length} B, lossy 85 {lossy85.Length} B; key frames {string.Join(",", keys)}");

            Assert.Equal([0, 20, 40, 60], keys); // the no-reading run is one still frame
            var total = TimeSpan.FromSeconds(frames.Count / (double)Fps);
            AssertAnimatedWebp(lossless, 61, total);
            AssertAnimatedWebp(lossy85, 61, total);
            Assert.True(lossless.Length <= WidgetCap);
            var anmf = TopLevelChunks(lossless).Where(c => c.Id == "ANMF").Select(c => c.Payload).ToList();
            foreach (var k in keys)
            {
                var p = anmf[k];
                Assert.True(lossless.AsSpan(p, 6).ToArray().All(b => b == 0), $"frame {k} at 0,0");
                Assert.Equal(Width - 1, lossless[p + 6] | lossless[p + 7] << 8 | lossless[p + 8] << 16);
                Assert.Equal(Height - 1, lossless[p + 9] | lossless[p + 10] << 8 | lossless[p + 11] << 16);
                Assert.True((lossless[p + 15] & 0x02) != 0, $"frame {k} isn't blended");
            }
        }
        finally
        {
            foreach (var frame in frames)
                frame.Bitmap.Dispose();
        }
    }

    [Fact]
    public void Encode_RefusesNoFrames_AndAFrameOfAnotherSize()
    {
        Assert.Throws<ArgumentException>(() => WebpLoopEncoder.Encode([], null));
        using var a = new SKBitmap(10, 10);
        using var b = new SKBitmap(10, 11);
        var frames = new List<LoopFrame> { new(a, TimeSpan.FromMilliseconds(100)), new(b, TimeSpan.FromMilliseconds(100)) };
        Assert.Throws<ArgumentException>(() => WebpLoopEncoder.Encode(frames, 80));
    }
}
