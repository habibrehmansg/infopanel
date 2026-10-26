using System.Diagnostics;
using InfoPanel.Images;
using SkiaSharp;
using Xunit;

namespace InfoPanel.Core.Tests.Scenes;

/// <summary>Writes small GIFs for tests: one palette (index 0 black, 1 red, 2 green, 3 blue, 4 white, 7 transparent), each frame a solid rectangle.</summary>
internal static class TestGif
{
    public sealed record Frame(int X, int Y, int W, int H, byte Color, int Disposal = 1, int DelayCs = 10, bool Pixels = true);

    public static byte[] Build(int width, int height, params Frame[] frames)
    {
        var o = new List<byte>();
        void U16(int v)
        {
            o.Add((byte)v);
            o.Add((byte)(v >> 8));
        }
        o.AddRange("GIF89a"u8.ToArray());
        U16(width);
        U16(height);
        o.Add(0xF2); // global colour table, 8 entries
        o.Add(0);
        o.Add(0);
        byte[][] palette = [[0, 0, 0], [255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 255], [0, 0, 0], [0, 0, 0], [0, 0, 0]];
        foreach (var c in palette) o.AddRange(c);
        foreach (var f in frames)
        {
            o.AddRange([0x21, 0xF9, 4, (byte)((f.Disposal << 2) | 1)]); // graphic control: disposal, transparent index 7
            U16(f.DelayCs);
            o.AddRange([7, 0]);
            o.Add(0x2C);
            U16(f.X);
            U16(f.Y);
            U16(f.W);
            U16(f.H);
            o.Add(0);
            o.Add(3); // LZW minimum code size: 4-bit codes, clear 8, end 9
            var data = new List<byte>();
            int acc = 0, bits = 0;
            void Emit(int code)
            {
                acc |= code << bits;
                bits += 4;
                while (bits >= 8)
                {
                    data.Add((byte)acc);
                    acc >>= 8;
                    bits -= 8;
                }
            }
            Emit(8);
            if (f.Pixels)
            {
                var count = (long)f.W * f.H;
                for (long i = 0; i < count; i++)
                {
                    Emit(f.Color);
                    if (i % 2 == 1 && i + 1 < count) Emit(8); // clear often, so codes stay 4 bits wide
                }
            }
            Emit(9);
            if (bits > 0) data.Add((byte)acc);
            for (var i = 0; i < data.Count; i += 255)
            {
                var n = Math.Min(255, data.Count - i);
                o.Add((byte)n);
                o.AddRange(data.GetRange(i, n));
            }
            o.Add(0);
        }
        o.Add(0x3B);
        return o.ToArray();
    }
}

/// <summary>Assets by name from bytes in memory, with an optional gate that blocks reads of one asset.</summary>
internal sealed class MemoryAssets : IMediaAssetStore
{
    public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);
    public string? Gated;
    public ManualResetEventSlim Gate { get; } = new();
    public ManualResetEventSlim Entered { get; } = new();

    public byte[]? Read(string hash)
    {
        if (hash == Gated)
        {
            Entered.Set();
            Gate.Wait(TimeSpan.FromSeconds(20));
        }
        return Assets.TryGetValue(hash, out var b) ? b : null;
    }

    public static byte[] Png(SKColor color, int size = 4)
    {
        using var bmp = new SKBitmap(size, size);
        bmp.Erase(color);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>The decode cache's bounds and lifecycle: what decoders allocate, the budget, the queue, shutdown, GIF disposal.</summary>
public sealed class MediaSampleCacheTests
{
    private static SKColor Pixel(MediaSample sample, int x, int y)
    {
        using var bmp = SKBitmap.FromImage(sample.Image!);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void A_gif_frame_larger_than_its_screen_is_refused_before_anything_is_allocated()
    {
        // A 1 x 1 logical screen (what the probe checks) with a 16384 x 16384 frame: a decoder that grows the canvas to
        // the frame would allocate 1 GiB.
        var store = new MemoryAssets();
        store.Assets["big"] = TestGif.Build(1, 1, new TestGif.Frame(0, 0, 16384, 16384, 1, Pixels: false));
        using var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true });
        var lookup = media.Get("big", 0, MediaPlayback.Loop);
        if (lookup.Sample is { } sample)
        {
            // A decoder that keeps the logical screen is fine too, as long as that is all it allocates.
            Assert.Equal(1, sample.Width);
            Assert.Equal(1, sample.Height);
            sample.Release();
        }
        else
        {
            Assert.Equal(MediaState.Failed, lookup.State);
            Assert.Contains("header says 1 x 1", lookup.Problem);
        }
        Assert.True(media.Bytes < 1024 * 1024, $"{media.Bytes} bytes");
    }

    [Fact]
    public void Animation_decoders_and_their_composites_count_against_the_budget()
    {
        var store = new MemoryAssets();
        for (var i = 0; i < 16; i++)
            store.Assets["a" + i] = TestGif.Build(256, 256, new TestGif.Frame(0, 0, 256, 256, (byte)(1 + i % 4)), new TestGif.Frame(0, 0, 128, 128, 4));
        const long frame = 256 * 256 * 4;
        using (var exact = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true }))
        {
            // The encoded bytes, the composite buffer, and frames 0 and 1 (decoded ahead).
            exact.Get("a0", 0, MediaPlayback.Loop).Sample!.Release();
            Assert.Equal(store.Assets["a0"].Length + frame + 2 * frame, exact.Bytes);
            Assert.Equal(1, exact.DecoderCount);
        }

        const long budget = 2 * 1024 * 1024; // eight 256 x 256 frames
        var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true, BudgetBytes = budget, MaxDecoders = 2 });
        for (var round = 0; round < 2; round++)
            for (var i = 0; i < 16; i++)
            {
                var lookup = media.Get("a" + i, round * 0.15, MediaPlayback.Loop);
                Assert.NotNull(lookup.Sample);
                lookup.Sample!.Release();
                Assert.True(media.Bytes <= budget, $"{media.Bytes} bytes after asset {i}");
                Assert.True(media.DecoderCount <= 2, $"{media.DecoderCount} decoders open");
            }
        // A closed decoder opens again on demand, and the frames are still right.
        var again = media.Get("a0", 0.15, MediaPlayback.Loop);
        Assert.Equal(new SKColor(255, 255, 255), Pixel(again.Sample!, 10, 10));
        Assert.Equal(new SKColor(255, 0, 0), Pixel(again.Sample!, 200, 200));
        again.Sample!.Release();
        media.Dispose();
        Assert.Equal(0, media.Bytes);
    }

    [Fact]
    public void Shutting_down_while_a_decode_outlasts_the_wait_does_not_deadlock()
    {
        var store = new MemoryAssets { Gated = "slow" };
        store.Assets["slow"] = TestGif.Build(64, 64, new TestGif.Frame(0, 0, 64, 64, 1), new TestGif.Frame(0, 0, 32, 32, 2));
        var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Workers = 1, ShutdownWait = TimeSpan.FromMilliseconds(100) });
        var processed = new ManualResetEventSlim();
        media.FrameDecoded += _ => processed.Set();
        Assert.Equal(MediaState.Loading, media.Get("slow", 0, MediaPlayback.Loop).State);
        Assert.True(store.Entered.Wait(TimeSpan.FromSeconds(10)), "the worker never read the asset");

        // The worker holds the asset's decode lock; once released it needs the cache's lock to store its frames. Dispose
        // returns after its wait while the decode is still blocked.
        var dispose = Task.Run(media.Dispose);
        Assert.True(dispose.Wait(TimeSpan.FromSeconds(10)), "Dispose waited for the blocked decode");
        Assert.False(processed.IsSet, "the decode finished before the gate opened");
        store.Gate.Set();
        Assert.True(processed.Wait(TimeSpan.FromSeconds(10)), "the worker never finished");
        var watch = Stopwatch.StartNew();
        while (media.Bytes != 0 && watch.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(10);
        Assert.Equal(0, media.Bytes); // the worker freed what it took after the shutdown
        Assert.Equal(MediaState.Failed, media.Get("slow", 0, MediaPlayback.Loop).State);
    }

    [Fact]
    public void A_request_that_does_not_fit_the_queue_is_asked_for_again_later()
    {
        var store = new MemoryAssets { Gated = "a" };
        store.Assets["a"] = MemoryAssets.Png(SKColors.Red);
        store.Assets["b"] = MemoryAssets.Png(SKColors.Green);
        store.Assets["c"] = MemoryAssets.Png(SKColors.Blue);
        using var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Workers = 1, QueueCapacity = 1 });
        media.Get("a", 0, MediaPlayback.Loop);
        Assert.True(store.Entered.Wait(TimeSpan.FromSeconds(10)));
        media.Get("b", 0, MediaPlayback.Loop); // queued: the queue is full
        media.Get("c", 0, MediaPlayback.Loop); // doesn't fit
        store.Gate.Set();

        MediaLookup c = default;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            c = media.Get("c", 0, MediaPlayback.Loop);
            if (c.State == MediaState.Ready) break;
            Thread.Sleep(10);
        }
        Assert.Equal(MediaState.Ready, c.State);
        c.Sample!.Release();
    }

    [Fact]
    public void Copies_a_decode_makes_are_counted_before_they_are_allocated()
    {
        // Frame 1 is disposed by restoring the previous canvas, so decoding it keeps a copy of the composite as well as
        // making the frame's own copy.
        var store = new MemoryAssets();
        store.Assets["r"] = TestGif.Build(256, 256,
            new TestGif.Frame(0, 0, 256, 256, 1, Disposal: 1),
            new TestGif.Frame(0, 0, 64, 64, 2, Disposal: 3),
            new TestGif.Frame(0, 0, 64, 64, 3, Disposal: 1));
        long encoded = store.Assets["r"].Length, frame = 256 * 256 * 4;
        using (var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true }))
        {
            media.Get("r", 0, MediaPlayback.Loop).Sample!.Release(); // frame 0, and frame 1 ahead
            // At the peak: the composite, frame 0, the copy of the canvas and frame 1's copy.
            Assert.Equal(encoded + 4 * frame, media.PeakBytes);
            Assert.Equal(encoded + 3 * frame, media.Bytes); // the canvas copy went back to being the composite
        }
        var budget = encoded + 3 * frame;
        using (var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true, BudgetBytes = budget }))
        {
            media.Get("r", 0, MediaPlayback.Loop).Sample?.Release();
            var one = media.Get("r", 0.15, MediaPlayback.Loop);
            Assert.Equal(new SKColor(0, 255, 0), Pixel(one.Sample!, 10, 10));
            one.Sample!.Release();
            Assert.True(media.PeakBytes <= budget, $"peak {media.PeakBytes} over {budget}");
        }
    }

    [Fact]
    public void An_animation_reopened_while_it_is_looked_up_keeps_its_timing()
    {
        // 1000 frames of 100 ms: 50.05 s in is frame 500. One decoder and no room for frames, so every lookup of the
        // other asset closes this one's decoder and the next lookup of it opens it again, while another thread reads
        // its timing.
        var store = new MemoryAssets();
        store.Assets["x"] = TestGif.Build(1, 1, Enumerable.Range(0, 1000).Select(i => new TestGif.Frame(0, 0, 1, 1, (byte)(i % 4))).ToArray());
        store.Assets["y"] = TestGif.Build(1, 1, new TestGif.Frame(0, 0, 1, 1, 1), new TestGif.Frame(0, 0, 1, 1, 2));
        using var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true, MaxDecoders = 1, BudgetBytes = 1 });
        media.Get("x", 0, MediaPlayback.Loop).Sample?.Release();
        Assert.Equal(500, media.FrameIndex("x", 50.05, MediaPlayback.Loop));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var reopen = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                media.Get("y", 0, MediaPlayback.Loop).Sample?.Release();
                media.Get("x", 0, MediaPlayback.Loop).Sample?.Release();
            }
        });
        reopen.Start();
        var wrong = new List<int>();
        long lookups = 0;
        while (!stop.IsCancellationRequested)
        {
            var index = media.FrameIndex("x", 50.05, MediaPlayback.Loop);
            if (index != 500) wrong.Add(index);
            lookups++;
        }
        reopen.Join();
        Assert.True(wrong.Count == 0, $"{wrong.Count} of {lookups} lookups read frame {string.Join(", ", wrong.Distinct().Take(5))}");
    }

    [Fact]
    public void Decoders_opened_by_more_workers_than_the_limit_are_closed_once_the_decodes_finish()
    {
        var store = new MemoryAssets();
        store.Assets["a"] = TestGif.Build(8, 8, new TestGif.Frame(0, 0, 8, 8, 1), new TestGif.Frame(0, 0, 4, 4, 2));
        store.Assets["b"] = TestGif.Build(8, 8, new TestGif.Frame(0, 0, 8, 8, 3), new TestGif.Frame(0, 0, 4, 4, 4));
        using var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Workers = 2, MaxDecoders = 1 });
        // Both decodes hold their decode locks until both have opened their decoders, so neither can close the other.
        using var both = new Barrier(2);
        var held = 0;
        media.HoldingDecodeLock = _ =>
        {
            if (Interlocked.Increment(ref held) <= 2) both.SignalAndWait(TimeSpan.FromSeconds(10));
        };
        var finished = 0;
        using var done = new ManualResetEventSlim();
        media.FrameDecoded += _ =>
        {
            if (Interlocked.Increment(ref finished) == 2) done.Set();
        };
        media.Get("a", 0, MediaPlayback.Loop);
        media.Get("b", 0, MediaPlayback.Loop);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the decodes never finished");
        Assert.Equal(2, Volatile.Read(ref held));
        Assert.Equal(1, media.DecoderCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gif_disposal_restores_only_the_disposed_frame(bool direct)
    {
        // 8 x 8 in quadrants: red everywhere (kept); green top left, restored to the background; blue bottom right,
        // restored to the previous frame; white top right.
        var store = new MemoryAssets();
        store.Assets["g"] = TestGif.Build(8, 8,
            new TestGif.Frame(0, 0, 8, 8, 1, Disposal: 1),
            new TestGif.Frame(0, 0, 4, 4, 2, Disposal: 2),
            new TestGif.Frame(4, 4, 4, 4, 3, Disposal: 3),
            new TestGif.Frame(4, 0, 4, 4, 4, Disposal: 1));
        using var media = new MediaSampleCache(store, new MediaSampleCacheOptions { Synchronous = true });
        SKColor clear = SKColors.Transparent, red = new(255, 0, 0), green = new(0, 255, 0), blue = new(0, 0, 255), white = new(255, 255, 255);
        // Top left, top right, bottom left, bottom right of each frame as shown.
        SKColor[][] expected =
        [
            [red, red, red, red],
            [green, red, red, red],
            [clear, red, red, blue],
            [clear, white, red, red],
        ];
        var order = direct ? new[] { 3, 2, 1, 0 } : [0, 1, 2, 3];
        foreach (var frame in order)
        {
            var lookup = media.Get("g", frame * 0.1 + 0.05, MediaPlayback.Loop);
            var sample = lookup.Sample!;
            var actual = new[] { Pixel(sample, 1, 1), Pixel(sample, 6, 1), Pixel(sample, 1, 6), Pixel(sample, 6, 6) };
            sample.Release();
            for (var q = 0; q < 4; q++)
            {
                if (expected[frame][q].Alpha == 0) Assert.True(actual[q].Alpha == 0, $"frame {frame} quadrant {q}: {actual[q]}");
                else Assert.True(expected[frame][q] == actual[q], $"frame {frame} quadrant {q}: {actual[q]}, expected {expected[frame][q]}");
            }
        }
    }
}
