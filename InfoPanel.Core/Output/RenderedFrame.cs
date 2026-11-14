using InfoPanel.Spec;
using SkiaSharp;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace InfoPanel.Output;

/// <summary>
/// One frame of a profile at one scale, shared by every sink of that scale that receives it on a tick.
/// <para>
/// <b>Lifetime.</b> <see cref="Image"/> points at the render loop's reused bitmap, and <see cref="GetPng"/> encodes
/// from it, so call both only inside <see cref="IFrameSink.OnFrame"/>. A sink that needs the bytes later calls
/// <see cref="GetPng"/> inside <c>OnFrame</c> and keeps the returned memory (it stays valid: it is a managed copy).
/// After the last sink's <c>OnFrame</c> the frame is closed: <see cref="Image"/> throws, and so does
/// <see cref="GetPng"/> unless a sink already encoded it. Everything else may be read at any time.
/// </para>
/// <para>
/// <b>PNG.</b> Encoded once, on the first <see cref="GetPng"/> call, with zlib level 1, and the same bytes are
/// returned to every sink of the frame.
/// </para>
/// </summary>
public sealed class RenderedFrame
{
    /// <summary>
    /// zlib level 1 with the Up row filter only: lossless, about 3× faster than letting the encoder try every filter
    /// and about as small (see the performance log in the tests).
    /// </summary>
    public static readonly SKPngEncoderOptions PngOptions = new(SKPngEncoderFilterFlags.Up, 1);

    private readonly Lock _lock = new();
    private SKImage? _image;
    private ReadOnlyMemory<byte>? _png;
    private PngLease? _lease;
    private double _pngMs;
    private bool _closed;

    internal RenderedFrame(SKImage image)
    {
        _image = image;
    }

    public required string ProfileId { get; init; }

    /// <summary>The document revision the frame was drawn from (the tick snapshot's).</summary>
    public required long Rev { get; init; }

    /// <summary>Goes up by one for every frame the render service emits, across profiles and scales.</summary>
    public required long FrameId { get; init; }

    /// <summary>The tick of the snapshot the frame was drawn from (<see cref="InfoPanel.Scenes.TickSnapshot.Tick"/>).</summary>
    public required long Tick { get; init; }

    /// <summary>The scale actually rendered (<see cref="FrameScale.Normalize"/> of the request).</summary>
    public required float Scale { get; init; }

    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    public required int ProfileWidth { get; init; }

    public required int ProfileHeight { get; init; }

    /// <summary>The tick's item bounds in profile pixels and document order (<see cref="InfoPanel.Scenes.TickSnapshot.Bounds"/>); empty when no sink asked for bounds.</summary>
    public required IReadOnlyList<ItemBounds> Bounds { get; init; }

    /// <summary>XxHash3 of the pixels and the bounds.</summary>
    public required ulong ContentHash { get; init; }

    /// <summary>The pixels (Rgba8888, premultiplied). Valid only during <see cref="IFrameSink.OnFrame"/>.</summary>
    public SKImage Image
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                return _image!;
            }
        }
    }

    /// <summary>The frame as a PNG, encoded on first use and shared. Call it during <see cref="IFrameSink.OnFrame"/>.</summary>
    public ReadOnlyMemory<byte> GetPng()
    {
        lock (_lock)
        {
            if (_png is { } png)
                return png;

            if (_closed)
                throw new InvalidOperationException("GetPng() was called after OnFrame returned; call it inside OnFrame and keep the bytes.");

            png = Encoded().Memory.ToArray();
            _png = png;
            return png;
        }
    }

    /// <summary>The frame's PNG in a pooled array, encoded once (under the lock); the frame holds one reference until <see cref="Close"/>.</summary>
    private PngLease Encoded()
    {
        if (_lease != null)
            return _lease;

        var started = Stopwatch.GetTimestamp();
        using var pixmap = _image!.PeekPixels();
        using var data = pixmap.Encode(PngOptions)
            ?? throw new InvalidOperationException("PNG encoding failed.");
        var pool = PngPool;
        var length = checked((int)data.Size);
        var array = pool.Rent(length);
        data.AsSpan().CopyTo(array);
        _lease = new PngLease(array, length, a => pool.Return(a));
        _pngMs = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 2);
        return _lease;
    }

    /// <summary>The pool <see cref="LeasePng"/>'s storage comes from (tests).</summary>
    internal ArrayPool<byte> PngPool { get; init; } = ArrayPool<byte>.Shared;

    /// <summary>
    /// The frame as a PNG, encoded on first use and shared, as a reference the caller releases once (<see cref="PngLease.Release"/>)
    /// when it no longer reads the bytes. Call it during <see cref="IFrameSink.OnFrame"/>.
    /// </summary>
    public PngLease LeasePng()
    {
        lock (_lock)
        {
            if (_closed)
                throw new InvalidOperationException("LeasePng() was called after OnFrame returned; call it inside OnFrame and keep the lease.");
            return Encoded().AddRef();
        }
    }

    /// <summary>How long <see cref="GetPng"/>'s one encoding took, in milliseconds (0 before it has encoded the frame).</summary>
    public double PngMs
    {
        get { lock (_lock) return _pngMs; }
    }

    /// <summary>True once <see cref="GetPng"/> has encoded the frame (for the render loop's statistics and tests).</summary>
    internal bool IsEncoded
    {
        get { lock (_lock) return _lease != null; }
    }

    /// <summary>Called by the render loop after every sink's <c>OnFrame</c>: releases the image over the reused bitmap.</summary>
    internal void Close()
    {
        lock (_lock)
        {
            if (_closed)
                return;

            _closed = true;
            _image?.Dispose();
            _image = null;
            _lease?.Release(); // the frame's own reference: the array goes back once every holder has let go
        }
    }
}

/// <summary>
/// A frame's PNG in an array shared by reference: each holder releases its reference once, and the last release gives the
/// array back (to its pool). <see cref="Memory"/> is valid only while the reader holds a reference.
/// </summary>
public sealed class PngLease
{
    private readonly byte[] _array;
    private readonly int _length;
    private readonly Action<byte[]>? _returned;
    private int _references = 1;

    /// <param name="returned">Called with the array once the last reference is released (null: nothing to give back).</param>
    public PngLease(byte[] array, int length, Action<byte[]>? returned)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, array.Length);
        _array = array;
        _length = length;
        _returned = returned;
    }

    public int Length => _length;

    public ReadOnlyMemory<byte> Memory => _array.AsMemory(0, _length);

    /// <summary>The references held (tests).</summary>
    internal int References => Volatile.Read(ref _references);

    /// <summary>One more reference, for a holder that releases it once.</summary>
    public PngLease AddRef()
    {
        // Compare-and-swap: a count that reached 0 stays 0 (the array is back in its pool), however often this is asked.
        var current = Volatile.Read(ref _references);
        while (true)
        {
            if (current <= 0)
                throw new InvalidOperationException("The PNG was already given back.");
            var seen = Interlocked.CompareExchange(ref _references, current + 1, current);
            if (seen == current)
                return this;
            current = seen;
        }
    }

    /// <summary>Gives one reference back; the last one gives the array back.</summary>
    public void Release()
    {
        var left = Interlocked.Decrement(ref _references);
        if (left == 0)
            _returned?.Invoke(_array);
        else if (left < 0)
            throw new InvalidOperationException("The PNG was released more times than it was referenced.");
    }
}
