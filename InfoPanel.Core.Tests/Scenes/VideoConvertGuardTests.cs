using System.Runtime.InteropServices;
using InfoPanel.Images.Video;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>
/// The decoder's conversion of a decoded picture into a frame's pixels (<see cref="VideoDecoder.Layout"/> and
/// <see cref="VideoDecoder.Scale"/>, as <see cref="VideoDecoder"/> calls them) writes only inside the bytes the frame is
/// given, at every width from 1 to 64 and a few heights, at the picture's own size and scaled down and up: FFmpeg's
/// converters work on blocks of pixels, and a block past a row's end that lands past the frame's last byte writes into
/// memory something else owns. Guard bytes before and after the frame tell, by how many bytes; and the visible pixels are
/// the same bytes a tightly packed frame gets.
/// </summary>
public sealed unsafe class VideoConvertGuardTests
{
    /// <summary>The guard bytes on each side of a frame; the frame starts aligned as the decoder's frames start.</summary>
    private const int Guard = 16 * VideoDecoder.FrameAlignment;

    /// <summary>The room a reference frame has after its last row: far more than any block of pixels.</summary>
    private const int ReferenceTail = 64 * 1024;

    private static readonly int[] Heights = [1, 2, 3, 7, 40];

    [Fact]
    public void TheConversion_WritesOnlyTheFramesOwnBytes_AtEveryWidthFrom1To64()
    {
        var f = FFmpegForTests.Library;
        var outside = new List<string>();
        var differing = new List<string>();
        var cases = 0;
        foreach (var height in Heights)
        {
            for (var width = 1; width <= 64; width++)
            {
                foreach (var (kind, w, h) in new[]
                         {
                             ("same size", width, height),
                             ("scaled down", width * 2 + 3, height * 2 + 1),
                             ("scaled up", Math.Max(1, width / 2), Math.Max(1, height / 2)),
                         })
                {
                    cases++;
                    var (before, after, same) = Convert(f, w, h, width, height);
                    if (before > 0 || after > 0)
                        outside.Add($"{kind} {w} x {h} to {width} x {height}: {after} bytes past the end, {before} before the start");
                    if (!same)
                        differing.Add($"{kind} {w} x {h} to {width} x {height}");
                }
            }
        }
        Assert.True(outside.Count == 0,
            $"{outside.Count} of {cases} conversions wrote outside their frame ({f.Versions}):\n" + string.Join("\n", outside));
        Assert.True(differing.Count == 0,
            $"{differing.Count} of {cases} conversions differ from a tightly packed frame's pixels:\n" + string.Join("\n", differing));
    }

    /// <summary>
    /// A yuv420p picture of <paramref name="w"/> x <paramref name="h"/>, laid out as FFmpeg's frame pool lays one out
    /// (rows 64-byte aligned, padding after), converted as the decoder converts it to <paramref name="width"/> x
    /// <paramref name="height"/>: the bytes written before and after the frame, and whether its visible pixels are those
    /// of a tightly packed frame with room to spare.
    /// </summary>
    private static (int Before, int After, bool Same) Convert(FFmpeg f, int w, int h, int width, int height)
    {
        var target = SkiaSharp.SKImageInfo.PlatformColorType == SkiaSharp.SKColorType.Bgra8888 ? FFmpegAbi.PixFmtBgra : FFmpegAbi.PixFmtRgba;
        var sws = f.sws_getContext(w, h, FFmpegAbi.PixFmtYuv420p, width, height, target, VideoDecoder.ScaleFlags(w, h, width, height), null, null, null);
        Assert.True(sws is not null, $"No scaler for {w} x {h} to {width} x {height}.");
        int cw = (w + 1) / 2, ch = (h + 1) / 2;
        var strides = stackalloc int[4] { Align(w, 64), Align(cw, 64), Align(cw, 64), 0 };
        var planes = stackalloc byte*[4];
        int[] rows = [h, ch, ch];
        try
        {
            for (var p = 0; p < 3; p++)
            {
                var size = (nuint)(strides[p] * (rows[p] + 16) + 64);
                planes[p] = (byte*)NativeMemory.AlignedAlloc(size, 64);
                for (nuint i = 0; i < size; i++)
                    planes[p][i] = p == 0 ? (byte)(16 + i * 7 % 220) : (byte)(64 + (i * 13 + (nuint)p * 50) % 128);
            }

            var (rowBytes, bytes) = VideoDecoder.Layout(width, height);
            Assert.True(rowBytes >= width * 4 && bytes >= (long)rowBytes * (height - 1) + width * 4, "The layout holds the picture.");
            var total = (nuint)(Guard + bytes + Guard);
            var buffer = (byte*)NativeMemory.AlignedAlloc(total, 64);
            try
            {
                int before = 0, after = 0;
                foreach (var pattern in new byte[] { 0x5A, 0xA5 })
                {
                    new Span<byte>(buffer, (int)total).Fill(pattern);
                    var converted = VideoDecoder.Scale(f, sws, planes, strides, h, buffer + Guard, rowBytes);
                    Assert.True(converted > 0, $"{w} x {h} to {width} x {height} wasn't converted ({converted}).");
                    for (var i = 0; i < Guard; i++)
                    {
                        if (buffer[i] != pattern)
                        {
                            before = Math.Max(before, Guard - i);
                            break;
                        }
                    }
                    var tail = buffer + Guard + bytes;
                    for (var i = Guard - 1; i >= 0; i--)
                    {
                        if (tail[i] != pattern)
                        {
                            after = Math.Max(after, i + 1);
                            break;
                        }
                    }
                }

                return (before, after, SameAsTight(f, sws, planes, strides, h, buffer + Guard, rowBytes, width, height));
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }
        finally
        {
            for (var p = 0; p < 3; p++)
                if (planes[p] is not null) NativeMemory.AlignedFree(planes[p]);
            f.sws_freeContext(sws);
        }
    }

    private static int Align(int value, int to) => (value + to - 1) / to * to;

    [Theory]
    [InlineData("small-h264-40x40.mp4", 40, 40)]
    [InlineData("small-h264-26x14.mp4", 26, 14)]
    [InlineData("camera-h264-640x480.mp4", 640, 480)]
    [InlineData("camera-h264-640x480.mp4", 200, 150)]
    public void DecodedFrames_ShowTheBytesOfATightlyPackedFrame_InTheLayoutThatHoldsTheScalersBlocks(string clip, int width, int height)
    {
        var f = FFmpegForTests.Library;
        var bytes = VideoFixtures.Read(clip);
        var probe = VideoProbe.Inspect(bytes, out _)!;
        using var decoder = VideoDecoder.Open(f, new MemoryStream(bytes), probe, width, height, SpecLimits.Default);
        var (rowBytes, _) = VideoDecoder.Layout(width, height);
        var frames = 0;
        while (decoder.Next() is not null)
        {
            using var image = decoder.Convert();
            using var pixels = image.PeekPixels();
            Assert.Equal(rowBytes, pixels.RowBytes);
            Assert.Equal(0, (long)pixels.GetPixels() % VideoDecoder.FrameAlignment);
            var frame = decoder.Frame;
            int w = *(int*)((byte*)frame + FFmpegAbi.FrameWidth), h = *(int*)((byte*)frame + FFmpegAbi.FrameHeight);
            var format = *(int*)((byte*)frame + FFmpegAbi.FrameFormat);
            var sws = f.sws_getContext(w, h, format, width, height, Target, VideoDecoder.ScaleFlags(w, h, width, height), null, null, null);
            Assert.True(sws is not null);
            try
            {
                Assert.True(SameAsTight(f, sws, (byte**)((byte*)frame + FFmpegAbi.FrameData), (int*)((byte*)frame + FFmpegAbi.FrameLinesize), h,
                    (byte*)pixels.GetPixels(), rowBytes, width, height), $"frame {frames} differs from a tightly packed frame's pixels");
            }
            finally
            {
                f.sws_freeContext(sws);
            }
            frames++;
        }
        Assert.True(frames >= 10, $"{frames} frames decoded");
    }

    private static int Target => SkiaSharp.SKImageInfo.PlatformColorType == SkiaSharp.SKColorType.Bgra8888 ? FFmpegAbi.PixFmtBgra : FFmpegAbi.PixFmtRgba;

    /// <summary>
    /// Whether the visible pixels of <paramref name="frame"/> (rows <paramref name="rowBytes"/> apart) are the bytes the
    /// same picture gets in a tightly packed frame (rows 4 bytes a pixel apart, as frames were laid out before, with room
    /// to spare after), wherever that frame's conversion writes: converted twice, over two fillings, a byte that keeps
    /// its filling was never written there (FFmpeg's x86 converters leave a row's last pixels unconverted when the next
    /// block wouldn't fit in a tightly packed row), and is left out.
    /// </summary>
    private static bool SameAsTight(FFmpeg f, void* sws, byte** srcData, int* srcStride, int srcHeight, byte* frame, int rowBytes, int width, int height)
    {
        var tightBytes = width * 4 * height;
        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)(tightBytes + ReferenceTail), 64);
        try
        {
            var dst = stackalloc byte*[4] { buffer, null, null, null };
            var dstStride = stackalloc int[4] { width * 4, 0, 0, 0 };
            new Span<byte>(buffer, tightBytes + ReferenceTail).Fill(0x5A);
            Assert.True(f.sws_scale(sws, srcData, srcStride, 0, srcHeight, dst, dstStride) > 0);
            var first = new ReadOnlySpan<byte>(buffer, tightBytes).ToArray();
            new Span<byte>(buffer, tightBytes + ReferenceTail).Fill(0xA5);
            Assert.True(f.sws_scale(sws, srcData, srcStride, 0, srcHeight, dst, dstStride) > 0);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width * 4; x++)
                {
                    var at = y * width * 4 + x;
                    if (first[at] == buffer[at] && frame[(long)y * rowBytes + x] != buffer[at])
                        return false;
                }
            }
            return true;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }
}
