using System.Buffers;
using InfoPanel.Output;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Preview;

/// <summary>
/// A frame's PNG lives in a pooled array shared by reference: a frame stream on the PNG path doesn't allocate an array of
/// the PNG's length per frame, and the array goes back to its pool exactly once, after the frame and every holder let go.
/// </summary>
public class RenderedFramePngTests
{
    private const int W = 960;
    private const int H = 540;

    /// <summary>Noise: a PNG of about 2 MB, far over the large object heap's 85 KB.</summary>
    private static SKBitmap Noise()
    {
        var bitmap = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
        var pixels = bitmap.GetPixelSpan();
        new Random(7).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        return bitmap;
    }

    private static RenderedFrame Frame(SKBitmap bitmap, long id, ArrayPool<byte>? pool = null) =>
        new(SKImage.FromPixels(bitmap.PeekPixels()))
        {
            ProfileId = "a5d7c0de-0000-4000-8000-000000000001", Rev = 1, FrameId = id, Tick = id, Scale = 1,
            PixelWidth = W, PixelHeight = H, ProfileWidth = W, ProfileHeight = H, Bounds = [], ContentHash = (ulong)id,
            PngPool = pool ?? ArrayPool<byte>.Shared,
        };

    [Fact]
    public void AStreamOfPngFrames_DoesNotAllocateAnArrayPerFrame()
    {
        using var bitmap = Noise();
        var length = 0;
        for (var i = 0; i < 3; i++)
        {
            var frame = Frame(bitmap, i);
            var lease = frame.LeasePng();
            length = lease.Length;
            frame.Close();
            lease.Release();
        }

        Assert.True(length > 85_000, $"the PNG is {length} B");
        const int frames = 100;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++)
        {
            var frame = Frame(bitmap, 10 + i);
            var lease = frame.LeasePng();
            frame.Close();
            lease.Release();
        }

        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)frames;
        TestContext.Current.TestOutputHelper?.WriteLine($"MEASURED {perFrame:F0} B per frame, PNG {length} B");
        Assert.True(perFrame < 8 * 1024, $"{perFrame:F0} B allocated per frame for a {length} B PNG");
    }

    [Fact]
    public void TwoHolders_TheArrayGoesBackOnce_AfterTheFrameAndBothLetGo()
    {
        using var bitmap = Noise();
        var pool = new PoisoningPool();
        var frame = Frame(bitmap, 1, pool);
        var a = frame.LeasePng();
        var b = frame.LeasePng();
        Assert.Same(a, b);
        Assert.Equal(1, pool.Rented);

        frame.Close();
        a.Release();
        Assert.Equal(0, pool.Returned);
        Assert.True(b.Memory.Span.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }), "still readable while held");
        b.Release();
        Assert.Equal(1, pool.Returned);
        Assert.Throws<InvalidOperationException>(() => b.Release());
    }

    [Fact]
    public void GetPng_StaysAManagedCopy_ThatOutlivesTheLeases()
    {
        using var bitmap = Noise();
        var pool = new PoisoningPool();
        var frame = Frame(bitmap, 1, pool);
        var lease = frame.LeasePng();
        var png = frame.GetPng();
        Assert.Equal(lease.Memory.ToArray(), png.ToArray());
        frame.Close();
        lease.Release();
        Assert.Equal(1, pool.Returned);
        Assert.True(png.Span.StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }));
        Assert.Equal(png.ToArray(), frame.GetPng().ToArray());
    }

    [Fact]
    public void GetPng_AfterClose_Throws_EvenWhenALeaseEncodedTheFrame()
    {
        using var bitmap = Noise();
        var frame = Frame(bitmap, 1, new PoisoningPool());
        var lease = frame.LeasePng();
        frame.Close();
        Assert.Throws<InvalidOperationException>(() => frame.GetPng());
        lease.Release();
    }

    [Fact]
    public void AddRef_AfterTheLastRelease_Throws_EveryTime_AndLeavesTheCountAtZero()
    {
        var returned = 0;
        var array = new byte[8];
        var lease = new PngLease(array, array.Length, _ => returned++);
        lease.Release();
        Assert.Equal(1, returned);
        Assert.Equal(0, lease.References);

        Assert.Throws<InvalidOperationException>(() => lease.AddRef());
        Assert.Throws<InvalidOperationException>(() => lease.AddRef());
        Assert.Equal(0, lease.References);
        Assert.Equal(1, returned);
    }

    /// <summary>Counts rents and returns, and overwrites an array as it comes back, so a holder reading it after sees garbage.</summary>
    internal sealed class PoisoningPool : ArrayPool<byte>
    {
        private int _rented, _returned;

        public int Rented => Volatile.Read(ref _rented);

        public int Returned => Volatile.Read(ref _returned);

        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref _rented);
            return new byte[minimumLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Interlocked.Increment(ref _returned);
            array.AsSpan().Fill(0xDD);
        }
    }
}
