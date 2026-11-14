using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using InfoPanel.Images;
using InfoPanel.Images.Video;
using InfoPanel.Spec;
using SkiaSharp;

namespace InfoPanel.Output;

/// <summary>
/// The media of one recording (<see cref="LoopRecorder"/>): a media cache of its own over the stored assets, apart from
/// the one running outputs share. It decodes on the recording's own thread, before the frame that needs a picture is
/// drawn (an animated picture at the recording's own time for that frame), so a picture stored just before is drawn,
/// never a placeholder still loading; it has no decode workers and never touches the shared cache, so a recording
/// neither queues work there nor drops a running output's frames.
/// <para>
/// It is bounded by reservations made before anything is read or decoded, held until the recording is disposed: at
/// most <see cref="MaxPictures"/> pictures read; their stored bytes, known from the store before they are read, within
/// <see cref="MaxStoredBytes"/>; the pixels each may hold at once (from its header: width x height x 4 for each frame
/// buffer the cache may keep for it), within <see cref="BudgetBytes"/>. A picture past any bound is not read or not
/// decoded and draws as missing media. The cache's own budget has room for everything reserved, so it never drops a
/// decoded frame a tick may still hold and decodes it again: what the recording holds in pictures stays within its
/// reservations, whatever the ticks keep. It plays no video: it has no video decoder and opens no stored video, so a
/// video draws as one this engine can't play. Once the recording is cancelled it reads nothing more; disposed with the
/// recording, it keeps nothing.
/// </para>
/// </summary>
public sealed class LoopMedia : IDisposable
{
    /// <summary>Pictures a recording reads; past them, the rest draw as missing media.</summary>
    public const int MaxPictures = 32;

    /// <summary>Stored bytes of pictures a recording reads; past them, the rest draw as missing media.</summary>
    public const long MaxStoredBytes = 64L * 1024 * 1024;

    /// <summary>Decoded pixels a recording reserves for its pictures; past them, the rest draw as missing media.</summary>
    public const long BudgetBytes = 128L * 1024 * 1024;

    private readonly Bounded _store;

    /// <summary>
    /// The media of a recording over <paramref name="store"/> (stored assets only: nothing is fetched), within
    /// <paramref name="limits"/>' media limits; it reads nothing once <paramref name="cancel"/> is cancelled.
    /// </summary>
    public LoopMedia(IMediaAssetStore store, SpecLimits limits, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = new Bounded(store, cancel);
        Cache = new MediaSampleCache(_store, new MediaSampleCacheOptions
        {
            Synchronous = true,
            // Room for all that the reservations allow (decoded pixels and the stored bytes a decoder keeps), so the
            // cache never evicts a frame and decodes it again while a tick still holds the first copy.
            BudgetBytes = BudgetBytes + MaxStoredBytes,
            Limits = limits,
            // No decoder: a video is never opened, probed or decoded here (it draws as a video this engine can't play).
            Video = new VideoOptions(),
            // Nothing a recording decodes is logged: its media is the request's.
            Log = Serilog.Core.Logger.None,
        });
    }

    /// <summary>The recording's cache, for its runtime's <see cref="Scenes.SceneRuntimeOptions.Media"/>.</summary>
    public MediaSampleCache Cache { get; }

    /// <summary>Pictures read so far (each counted once).</summary>
    public int Read => _store.Count;

    /// <summary>Stored bytes charged so far.</summary>
    public long StoredBytes => _store.Stored;

    /// <summary>Decoded bytes reserved so far (never given back before the recording is disposed).</summary>
    public long ReservedBytes => _store.Reserved;

    /// <summary>Stored videos opened (always 0: a recording plays no video).</summary>
    public int Opened => _store.Opened;

    public void Dispose() => Cache.Dispose();

    /// <summary>
    /// The bytes a picture of these header values may hold at once in the cache: its composite and its copy for a still;
    /// for an animation also every frame and a copy of the canvas a frame restores. Null when it can't be counted (no
    /// size, or a size whose bytes overflow).
    /// </summary>
    internal static long? Pixels(int width, int height, int frames)
    {
        if (width < 1 || height < 1) return null;
        var buffers = frames > 1 ? (double)frames + 2 : 2;
        var bytes = (double)width * height * 4 * buffers;
        return bytes > long.MaxValue / 2 ? null : (long)bytes;
    }

    /// <summary>The decoded bytes to reserve for <paramref name="bytes"/>, from its header only; null when it can't be counted.</summary>
    private static long? Reserve(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var codec = SKCodec.Create(stream);
        // Not a raster the decoder reads (an SVG, recorded from its rebuilt document, or bytes the validator refuses):
        // counted at its stored size.
        if (codec is null) return bytes.Length;
        return Pixels(codec.Info.Width, codec.Info.Height, Math.Max(1, codec.FrameCount));
    }

    /// <summary>The store as a recording reads it: within its bounds, charged before each read, nothing once cancelled.</summary>
    private sealed class Bounded(IMediaAssetStore store, CancellationToken cancel) : IMediaAssetStore
    {
        private readonly Lock _lock = new();
        // Pictures read and admitted, with the decoded bytes reserved for each; and those refused once read.
        private readonly Dictionary<string, long> _held = new(StringComparer.Ordinal);
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
        private long _stored;
        private long _reserved;
        private int _opened;

        public int Count
        {
            get
            {
                lock (_lock) return _held.Count + _refused.Count;
            }
        }

        public long Stored
        {
            get
            {
                lock (_lock) return _stored;
            }
        }

        public long Reserved
        {
            get
            {
                lock (_lock) return _reserved;
            }
        }

        public int Opened => Volatile.Read(ref _opened);

        public byte[]? Read(string hash)
        {
            if (cancel.IsCancellationRequested) return null;
            bool again;
            lock (_lock)
            {
                if (_refused.Contains(hash)) return null;
                again = _held.ContainsKey(hash);
            }
            // A picture read before (its decoder closed and reopened) is already charged and reserved.
            if (again) return store.Read(hash);

            // Its stored length, known before it is read: a picture whose length the store can't tell is not read.
            if (store.Size(hash) is not { } size || size < 0) return null;
            lock (_lock)
            {
                if (_held.Count + _refused.Count >= MaxPictures || _stored + size > MaxStoredBytes) return null;
                _stored += size;
            }
            var bytes = store.Read(hash);
            if (bytes is null || bytes.Length > size || cancel.IsCancellationRequested)
            {
                lock (_lock) _refused.Add(hash);
                return null;
            }
            // Its pixels are reserved from its header before anything decodes it.
            var reserve = Reserve(bytes);
            lock (_lock)
            {
                if (reserve is not { } pixels || _reserved + pixels > BudgetBytes)
                {
                    _refused.Add(hash);
                    return null;
                }
                _reserved += pixels;
                _held[hash] = pixels;
            }
            return bytes;
        }

        public long? Size(string hash) => store.Size(hash);

        // A recording plays no video: a stored video is never opened (the cache has no decoder to ask for one).
        public Stream? Open(string hash)
        {
            Interlocked.Increment(ref _opened);
            return null;
        }
    }
}
