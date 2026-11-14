using System.Runtime.InteropServices;
using InfoPanel.Desktop;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// A frame (RGBA, premultiplied) drawn into a window's BGRA pixels: swizzled byte for byte at one pixel per pixel,
/// rows and pooled buffers honoured, stretched when rendered below scale 1, a frame that doesn't fit the window
/// stretched over the whole of it, and the floor (alpha 1/255) where the frame is transparent so every pixel takes
/// clicks, or alpha 0 without it.
/// </summary>
public sealed class DesktopFrameBlitTests
{
    /// <summary>The target's pixels after drawing <paramref name="frame"/> into <paramref name="width"/> × <paramref name="height"/>.</summary>
    private static byte[] Draw(DesktopFrame frame, int width, int height, bool floor, int extraRowBytes = 0)
    {
        var rowBytes = width * 4 + extraRowBytes;
        var target = new byte[rowBytes * height];
        var handle = GCHandle.Alloc(target, GCHandleType.Pinned);
        try
        {
            DesktopFrameBlit.Draw(frame, handle.AddrOfPinnedObject(), width, height, rowBytes, floor);
        }
        finally
        {
            handle.Free();
        }

        return target;
    }

    /// <summary>The BGRA bytes of the target's pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static (byte B, byte G, byte R, byte A) At(byte[] target, int rowBytes, int x, int y)
    {
        var i = y * rowBytes + x * 4;
        return (target[i], target[i + 1], target[i + 2], target[i + 3]);
    }

    /// <summary>A frame of <paramref name="pixels"/> (RGBA per pixel, premultiplied), rows <paramref name="rowBytes"/> apart.</summary>
    private static DesktopFrame Frame(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> pixel, int rowBytes = 0, int spare = 0, float scale = 1)
    {
        rowBytes = Math.Max(rowBytes, width * 4);
        var bytes = new byte[rowBytes * height + spare];
        Array.Fill(bytes, (byte)0xEE); // padding and the spare tail must never be read as pixels
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = pixel(x, y);
                var i = y * rowBytes + x * 4;
                (bytes[i], bytes[i + 1], bytes[i + 2], bytes[i + 3]) = (r, g, b, a);
            }
        }

        return new DesktopFrame(bytes, width, height, rowBytes, scale);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOpaqueFrame_IsSwizzledIntoBgra_ByteForByte_FloorOrNot(bool floor)
    {
        var frame = Frame(3, 2, (x, y) => ((byte)(10 + x), (byte)(20 + y), (byte)(30 + x + y), 255));
        var target = Draw(frame, 3, 2, floor);

        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 3; x++)
                Assert.Equal(((byte)(30 + x + y), (byte)(20 + y), (byte)(10 + x), (byte)255), At(target, 12, x, y));
        }
    }

    [Fact]
    public void WideRows_AndAPooledBufferLongerThanTheFrame_AreHonoured()
    {
        // Rows 32 bytes apart for 3 pixels (12 bytes), and 100 spare bytes after the last row; the target's rows are wider too.
        var frame = Frame(3, 3, (x, y) => ((byte)(x * 50), (byte)(y * 50), 7, 255), rowBytes: 32, spare: 100);
        var target = Draw(frame, 3, 3, floor: true, extraRowBytes: 8);

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
                Assert.Equal(((byte)7, (byte)(y * 50), (byte)(x * 50), (byte)255), At(target, 20, x, y));
        }
    }

    [Theory]
    [InlineData(20, 10, true, 1)] // smaller than the window
    [InlineData(20, 10, false, 0)]
    [InlineData(80, 40, true, 1)] // larger
    [InlineData(80, 40, false, 0)]
    [InlineData(60, 10, true, 1)] // wider, shorter: each axis on its own
    public void AFrameOfAnotherSizeThanTheWindow_IsStretchedOverTheWholeWindow(int frameWidth, int frameHeight, bool floor, byte floorAlpha)
    {
        // Top half: the left half orange, the right half blue; bottom half transparent. In a 40×20 window each quarter is
        // sampled well inside, away from the edges linear filtering blends.
        var frame = Frame(frameWidth, frameHeight, (x, y) =>
            y >= frameHeight / 2 ? ((byte)0, (byte)0, (byte)0, (byte)0)
            : x < frameWidth / 2 ? ((byte)200, (byte)100, (byte)50, (byte)255)
            : ((byte)10, (byte)20, (byte)230, (byte)255));
        var target = Draw(frame, 40, 20, floor);

        (byte, byte, byte, byte) orange = (50, 100, 200, 255), blue = (230, 20, 10, 255), bare = (0, 0, 0, floorAlpha);
        foreach (var (x, y) in new[] { (2, 2), (16, 7) })
            Assert.Equal(orange, At(target, 160, x, y));
        foreach (var (x, y) in new[] { (24, 2), (37, 7) })
            Assert.Equal(blue, At(target, 160, x, y));
        foreach (var (x, y) in new[] { (2, 13), (37, 17), (39, 19) })
            Assert.Equal(bare, At(target, 160, x, y));
    }

    [Theory]
    [InlineData(400, 200, 1f, 400, 200, true)] // exact
    [InlineData(399, 200, 1f, 400, 200, false)] // a pixel off, either side, either axis
    [InlineData(401, 200, 1f, 400, 200, false)]
    [InlineData(400, 199, 1f, 400, 200, false)]
    [InlineData(400, 201, 1f, 400, 200, false)]
    [InlineData(200, 100, 0.5f, 400, 200, true)] // scale 0.5, exact
    [InlineData(400, 200, 0.5f, 400, 200, false)] // the window's size, but rendered at half: it needs a stretch
    [InlineData(201, 100, 0.5f, 400, 200, false)]
    [InlineData(166, 100, 0.5f, 333, 201, true)] // 166.5 and 100.5 round as the frame's size did (to even)
    [InlineData(167, 101, 0.5f, 333, 201, false)]
    [InlineData(76, 76, 0.75f, 101, 101, true)] // 75.75 rounds up
    [InlineData(75, 75, 0.75f, 101, 101, false)]
    [InlineData(400, 200, 0f, 400, 200, true)] // a scale out of range is 1, as Draw takes it
    public void Fits_IsTheFrameAtItsScale_BeingTheWindowsPixelSize(int frameWidth, int frameHeight, float scale, int width, int height, bool fits)
    {
        Assert.Equal(fits, DesktopFrameBlit.Fits(frameWidth, frameHeight, scale, width, height));
    }

    [Fact]
    public void AFrameRenderedAtHalfScale_IsStretchedToFillTheWindow()
    {
        // 2×2 pixels at scale 0.5 for a 4×4 window: every window pixel is the frame's colour.
        var frame = Frame(2, 2, (_, _) => (0, 128, 255, 255), scale: 0.5f);
        var target = Draw(frame, 4, 4, floor: true);

        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
                Assert.Equal(((byte)255, (byte)128, (byte)0, (byte)255), At(target, 16, x, y));
        }
    }

    [Fact]
    public void TheFloor_LeavesNoPixelFullyTransparent_AndWithoutItTransparentStaysZero()
    {
        // Transparent, half transparent (premultiplied) and opaque pixels.
        var frame = Frame(3, 1, (x, _) => x switch
        {
            0 => (0, 0, 0, 0),
            1 => (60, 30, 0, 128),
            _ => (9, 8, 7, 255),
        });

        var floored = Draw(frame, 3, 1, floor: true);
        Assert.All(Enumerable.Range(0, 3), x => Assert.NotEqual(0, At(floored, 12, x, 0).A));
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)1), At(floored, 12, 0, 0));
        Assert.Equal(((byte)7, (byte)8, (byte)9, (byte)255), At(floored, 12, 2, 0)); // opaque: unchanged by the floor

        var bare = Draw(frame, 3, 1, floor: false);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)0), At(bare, 12, 0, 0));
        Assert.Equal(((byte)0, (byte)30, (byte)60, (byte)128), At(bare, 12, 1, 0)); // copied as it is
        Assert.Equal(((byte)7, (byte)8, (byte)9, (byte)255), At(bare, 12, 2, 0));
    }
}
