using System.Runtime.InteropServices;
using InfoPanel.Desktop;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// A frame (RGBA, premultiplied) drawn into a window's BGRA pixels: swizzled byte for byte at one pixel per pixel,
/// rows and pooled buffers honoured, anchored top-left over the floor, stretched when rendered below scale 1, and the
/// floor (alpha 1/255) where the frame is transparent so every pixel takes clicks, or alpha 0 without it.
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
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void AFrameSmallerThanTheWindow_IsAnchoredTopLeft_TheRestAtTheFloor(bool floor, byte floorAlpha)
    {
        var frame = Frame(2, 1, (_, _) => (200, 100, 50, 255));
        var target = Draw(frame, 4, 3, floor);

        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var expected = x < 2 && y < 1 ? ((byte)50, (byte)100, (byte)200, (byte)255) : ((byte)0, (byte)0, (byte)0, floorAlpha);
                Assert.Equal(expected, At(target, 16, x, y));
            }
        }
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
