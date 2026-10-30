using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InfoPanel.Spec;
using Serilog;
using SkiaSharp;
using Svg.Skia;

namespace InfoPanel.Images
{
    /// <summary>Reads stored media assets by content hash (<c>sha256:…</c>). Called on decode workers only; may block on IO.</summary>
    public interface IMediaAssetStore
    {
        /// <summary>The asset's bytes, or null when the store has no such asset.</summary>
        byte[]? Read(string hash);

        /// <summary>The stored asset's size in bytes, unchecked, or null when unknown; a picture over <c>mediaBytes</c> isn't read.</summary>
        long? Size(string hash) => null;
    }

    /// <summary>
    /// Media a provider configured on the engine supplies (capability <c>media.provider</c>). Must not block: it returns
    /// the sample it has for <paramref name="reference"/> (retained for the caller, who releases it) or null while it has none.
    /// </summary>
    public interface IMediaProvider
    {
        MediaSample? TryGet(string reference, double seconds);
    }

    public enum MediaPlayback { Loop, Once, Still }

    public enum MediaState { Loading, Ready, Failed }

    /// <summary>
    /// What <see cref="MediaSampleCache.Get"/> found: a retained sample (the caller releases it) or why there is none.
    /// <see cref="Info"/> is the asset's header once a decode worker has inspected it, while it is still loading too;
    /// <see cref="MediaSampleCache.KnownSize"/> has the size measured when it was stored or last decoded.
    /// </summary>
    public readonly record struct MediaLookup(MediaState State, MediaSample? Sample, MediaInfo? Info, string? Problem);

    public sealed class MediaSampleCacheOptions
    {
        /// <summary>Load and decode on the calling thread (tests and one-off renders). A render loop leaves it off.</summary>
        public bool Synchronous { get; init; }

        /// <summary>Decode workers (a bounded pool, off the render thread).</summary>
        public int Workers { get; init; } = 2;

        /// <summary>The decoded frame budget; least recently used frames are dropped first (spec/docs/limits.md: 512 MiB).</summary>
        public long BudgetBytes { get; init; } = 512L * 1024 * 1024;

        /// <summary>Pending decode requests; a request that doesn't fit is dropped and asked for again on a later tick.</summary>
        public int QueueCapacity { get; init; } = 256;

        /// <summary>An asset unused this long has its decoder and frames dropped.</summary>
        public TimeSpan IdleAfter { get; init; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Assets whose decoder (encoded bytes, codec and the animation's composite buffer) stays open at once; the least
        /// recently used is closed first. Open decoders count against <see cref="BudgetBytes"/> as frames do.
        /// </summary>
        public int MaxDecoders { get; init; } = 8;

        /// <summary>How long <see cref="MediaSampleCache.Dispose"/> waits for the workers; a decode still running then finishes on its own and frees what it holds.</summary>
        public TimeSpan ShutdownWait { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Measured sizes kept (an asset's decoded size, from its upload's inspection or a decode's), apart from the
        /// decoded frames and their budget; the least recently used is forgotten first.
        /// </summary>
        public int SizesKept { get; init; } = 4096;

        /// <summary>The clock that times idle assets.</summary>
        public TimeProvider Time { get; init; } = TimeProvider.System;

        public SpecLimits Limits { get; init; } = SpecLimits.Default;
    }

    /// <summary>
    /// Decoded media frames by asset hash, for the scene runtime. <see cref="Get"/> never blocks: an asset is inspected
    /// with <see cref="MediaValidator"/> (header only) before anything decodes it, then decoded on a small pool of
    /// workers, with the decoder the probed format names (an SVG from its rebuilt document only, never its stored bytes); until a frame is ready the caller gets the asset's latest decoded frame, or nothing (it draws the
    /// missing-media placeholder) with the asset's header once known (its size is the decoded frame's). Frames and open
    /// decoders are kept within one byte budget, least recently used dropped first; a dropped frame is disposed once no holder retains it.
    /// Measured sizes (<see cref="KnownSize"/>) are kept apart, in a small store bounded by its own entry count, so the
    /// size of media stored or decoded in this run outlives its frames. <see cref="Dispose"/> stops the workers (waiting a
    /// while for the frame being decoded) and releases the cache's references; samples others retain stay valid until
    /// they release them. Locks: an asset's <c>DecodeLock</c> is taken before the cache's lock, never after it (under
    /// the cache's lock a decode lock is only tried, never waited for).
    /// </summary>
    public sealed class MediaSampleCache : IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<MediaSampleCache>();

        private readonly IMediaAssetStore _store;
        private readonly MediaSampleCacheOptions _options;
        private readonly Lock _lock = new();
        private readonly Dictionary<string, Asset> _assets = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, int), LinkedListNode<Frame>> _frames = new();
        private readonly LinkedList<Frame> _lru = new();
        private readonly LinkedList<Asset> _decoders = new(); // assets with an open decoder, most recently used first
        private readonly HashSet<(string, int)> _pending = new();
        private readonly Channel<(Asset Asset, int Frame)> _queue;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task[] _workers;
        private long _bytes;
        private long _peak;
        private long _lastSweep;
        private volatile bool _disposed;
        private readonly long _start;
        private readonly SizeStore _sizes;

        public MediaSampleCache(IMediaAssetStore store, MediaSampleCacheOptions? options = null)
        {
            _store = store;
            _options = options ?? new MediaSampleCacheOptions();
            _start = _options.Time.GetTimestamp();
            _sizes = new SizeStore(Math.Max(0, _options.SizesKept));
            _queue = Channel.CreateBounded<(Asset, int)>(new BoundedChannelOptions(Math.Max(1, _options.QueueCapacity))
            {
                // Wait, so a full queue makes TryWrite return false (DropWrite reports success and discards the write).
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                SingleReader = false,
            });
            _workers = _options.Synchronous
                ? []
                : Enumerable.Range(0, Math.Clamp(_options.Workers, 1, 8)).Select(_ => Task.Run(WorkAsync)).ToArray();
        }

        /// <summary>Bytes of decoded frames and open decoders the cache holds.</summary>
        public long Bytes => Interlocked.Read(ref _bytes);

        /// <summary>The most <see cref="Bytes"/> has been after making room: frames, open decoders and the copies a decode makes.</summary>
        internal long PeakBytes => Interlocked.Read(ref _peak);

        /// <summary>Tests: called on the decoding thread at the end of each decode, while it still holds the asset's decode lock.</summary>
        internal Action<string>? HoldingDecodeLock;

        /// <summary>Test seam: called with the asset's hash and the exact bytes handed to the SVG decoder.</summary>
        internal Action<string, byte[]>? DecodingSvg;

        /// <summary>The frame index <paramref name="hash"/> shows at <paramref name="seconds"/>, from its published timing; -1 before it is opened.</summary>
        internal int FrameIndex(string hash, double seconds, MediaPlayback playback)
        {
            Asset? asset;
            lock (_lock) _assets.TryGetValue(hash, out asset);
            return asset is { Opened: true } ? asset.FrameAt(seconds, playback) : -1;
        }

        /// <summary>Milliseconds since the cache was made, on <see cref="MediaSampleCacheOptions.Time"/>.</summary>
        private long Now() => (long)_options.Time.GetElapsedTime(_start).TotalMilliseconds;

        /// <summary>Assets the cache holds (looked up and not yet swept).</summary>
        internal int AssetCount
        {
            get
            {
                lock (_lock) return _assets.Count;
            }
        }

        /// <summary>Measured sizes the cache keeps.</summary>
        internal int KnownSizes => _sizes.Count;

        /// <summary>Assets with an open decoder.</summary>
        internal int DecoderCount
        {
            get
            {
                lock (_lock) return _decoders.Count;
            }
        }

        /// <summary>Raised after a decode request has been processed (decoded, failed or dropped at shutdown); tests use it to wait.</summary>
        public event Action<string>? FrameDecoded;

        /// <summary>
        /// The frame of <paramref name="hash"/> at <paramref name="seconds"/> into its animation, retained for the
        /// caller. Never blocks (unless the cache is synchronous).
        /// </summary>
        public MediaLookup Get(string hash, double seconds, MediaPlayback playback)
        {
            if (_disposed) return new MediaLookup(MediaState.Failed, null, null, "The media cache is shut down.");
            Asset asset;
            lock (_lock)
            {
                if (!_assets.TryGetValue(hash, out asset!))
                {
                    asset = new Asset(hash);
                    _assets[hash] = asset;
                }
                asset.LastUsed = Now();
                SweepLocked();
            }

            if (asset.State == MediaState.Loading && !asset.Opened)
            {
                if (_options.Synchronous) Process(asset, 0);
                else Request(asset, 0);
            }
            if (asset.State == MediaState.Failed) return new MediaLookup(MediaState.Failed, null, asset.Info, asset.Problem);
            if (!asset.Opened) return new MediaLookup(MediaState.Loading, null, asset.Info, null);

            var index = asset.FrameAt(seconds, playback);
            lock (_lock)
            {
                if (_frames.TryGetValue((hash, index), out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    if (node.Value.Sample.Retain() is { } hit) return new MediaLookup(MediaState.Ready, hit, asset.Info, null);
                }
            }
            if (_options.Synchronous)
            {
                Process(asset, index);
                lock (_lock)
                {
                    if (_frames.TryGetValue((hash, index), out var node) && node.Value.Sample.Retain() is { } decoded)
                        return new MediaLookup(MediaState.Ready, decoded, asset.Info, null);
                }
                return asset.State == MediaState.Failed
                    ? new MediaLookup(MediaState.Failed, null, asset.Info, asset.Problem)
                    : new MediaLookup(MediaState.Loading, null, asset.Info, null);
            }
            Request(asset, index);
            lock (_lock)
            {
                // Until the frame is decoded, the asset's latest frame stands in (an animation keeps moving).
                if (asset.Latest is { } latest && _frames.TryGetValue((hash, latest), out var node) && node.Value.Sample.Retain() is { } fallback)
                    return new MediaLookup(MediaState.Ready, fallback, asset.Info, null);
            }
            return new MediaLookup(MediaState.Loading, null, asset.Info, null);
        }

        /// <summary>
        /// Forgets a lookup of <paramref name="hash"/> that failed or is still under way (its blob was stored since, say),
        /// so the next <see cref="Get"/> reads it again: a lookup under way may have read the store before the blob was
        /// there, and the failure it then publishes goes to the forgotten asset. An asset that loaded is left alone: its
        /// bytes are the same. <paramref name="header"/> is what the store's own inspection of those bytes found (the
        /// same check a decode worker makes): its size is kept (<see cref="KnownSize"/>), and no asset is made for it.
        /// </summary>
        public void Retry(string hash, MediaInfo? header = null)
        {
            if (header is not null) _sizes.Put(hash, header.Width, header.Height);
            lock (_lock)
            {
                if (!_assets.TryGetValue(hash, out var asset) || asset.State == MediaState.Ready) return;
                // As the idle sweep drops an asset; a decode still running on it frees what it holds when it sees Detached.
                foreach (var node in _frames.Where(f => f.Key.Item1 == hash).Select(f => f.Value).ToList()) Evict(node);
                asset.Detached = true;
                _assets.Remove(hash);
            }
        }

        /// <summary>
        /// The size <paramref name="hash"/> is drawn at once decoded, when it was measured in this run (stored, or
        /// decoded) and is still among the <see cref="MediaSampleCacheOptions.SizesKept"/> most recently used; null
        /// otherwise. Never reads or inspects the asset; takes only the size store's own lock.
        /// </summary>
        public (float Width, float Height)? KnownSize(string hash) => _sizes.Get(hash);

        private void Request(Asset asset, int frame)
        {
            lock (_lock)
            {
                if (!_pending.Add((asset.Hash, frame))) return;
            }
            if (!_queue.Writer.TryWrite((asset, frame)))
            {
                lock (_lock)
                {
                    _pending.Remove((asset.Hash, frame));
                }
            }
        }

        private async Task WorkAsync()
        {
            var token = _stop.Token;
            try
            {
                while (await _queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (_queue.Reader.TryRead(out var job))
                    {
                        if (token.IsCancellationRequested) return;
                        try
                        {
                            Process(job.Asset, job.Frame);
                        }
                        finally
                        {
                            lock (_lock)
                            {
                                _pending.Remove((job.Asset.Hash, job.Frame));
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>Opens the asset if needed and decodes <paramref name="index"/> (and the frame after it, for animations).</summary>
        private void Process(Asset asset, int index)
        {
            lock (asset.DecodeLock)
            {
                try
                {
                    if (_disposed || asset.Detached) return;
                    if (!asset.Opened && asset.State != MediaState.Failed) Open(asset);
                    if (asset.State == MediaState.Failed) return;
                    index = Math.Clamp(index, 0, asset.Frames - 1);
                    if (!Cached(asset, index))
                    {
                        // A still image's decoder is released after its frame is decoded, and an animation's may have
                        // been closed for the budget; open it again if the frame was dropped.
                        if (asset.Codec is null && asset.Vector is null) Open(asset);
                        if (asset.State == MediaState.Failed) return;
                        Decode(asset, index);
                    }
                    if (asset.Frames > 1 && index + 1 < asset.Frames && !Cached(asset, index + 1) && asset.Codec is not null) Decode(asset, index + 1);
                    HoldingDecodeLock?.Invoke(asset.Hash);
                }
                catch (Exception e)
                {
                    Logger.Warning(e, "Media {Hash} could not be decoded", asset.Hash);
                    Fail(asset, "media.malformed", "The media could not be decoded.");
                }
                finally
                {
                    // Shut down or swept while this decode ran: nobody else frees what the asset holds.
                    if (_disposed || asset.Detached)
                    {
                        CloseDecoder(asset);
                        asset.Vector?.Release();
                        asset.Vector = null;
                    }
                }
            }
            // Decoders opened while others were decoding couldn't close those (their locks were held): with more workers
            // than MaxDecoders, the excess is closed here, once this decode lock is free.
            lock (_lock)
            {
                while (_decoders.Count > Math.Max(1, _options.MaxDecoders) && CloseIdleDecoderLocked(null))
                {
                }
            }
            FrameDecoded?.Invoke(asset.Hash);
        }

        private void Fail(Asset asset, string code, string message)
        {
            asset.Problem = $"{code}: {message}";
            asset.State = MediaState.Failed;
            // The picture won't be drawn: its remembered size must not lay it out once the idle sweep has dropped this
            // asset. (A detached asset is skipped: an upload has replaced it and put the new bytes' size in.)
            if (!asset.Detached) _sizes.Remove(asset.Hash);
            CloseDecoder(asset);
        }

        /// <summary>Closes the asset's decoder and returns its bytes to the budget. The caller holds the asset's decode lock.</summary>
        private void CloseDecoder(Asset asset)
        {
            asset.ReleaseDecoder();
            lock (_lock) ForgetDecoderLocked(asset);
        }

        private void ForgetDecoderLocked(Asset asset)
        {
            _bytes -= asset.DecoderBytes;
            asset.DecoderBytes = 0;
            if (asset.DecoderNode is { } node)
            {
                _decoders.Remove(node);
                asset.DecoderNode = null;
            }
        }

        /// <summary>
        /// Counts <paramref name="bytes"/> more of the asset's decoder against the budget, marks it most recently used and
        /// makes room (other decoders and frames). The caller holds the asset's decode lock.
        /// </summary>
        private void ChargeDecoder(Asset asset, long bytes)
        {
            lock (_lock)
            {
                asset.DecoderBytes += bytes;
                _bytes += bytes;
                if (asset.DecoderNode is { } node) _decoders.Remove(node);
                asset.DecoderNode = _decoders.AddFirst(asset);
                TrimLocked(asset, null);
            }
        }

        /// <summary>
        /// Closes least recently used decoders past <see cref="MediaSampleCacheOptions.MaxDecoders"/>, then drops least
        /// recently used frames, then decoders, while the cache is over its budget. <paramref name="current"/> (whose
        /// decode lock this thread holds) and <paramref name="keep"/> are never dropped.
        /// </summary>
        private void TrimLocked(Asset? current, LinkedListNode<Frame>? keep)
        {
            while (_decoders.Count > Math.Max(1, _options.MaxDecoders) && CloseIdleDecoderLocked(current))
            {
            }
            while (_bytes > _options.BudgetBytes)
            {
                if (_lru.Last is { } last && last != keep) Evict(last);
                else if (!CloseIdleDecoderLocked(current)) break;
            }
            if (_bytes > _peak) Interlocked.Exchange(ref _peak, _bytes);
        }

        /// <summary>
        /// Returns <paramref name="bytes"/> reserved with <see cref="ChargeDecoder"/> for a copy that has been freed (or
        /// that a failed allocation never made). The caller holds the asset's decode lock; a decoder closed since (a
        /// failure) already returned everything.
        /// </summary>
        private void Unreserve(Asset asset, long bytes)
        {
            lock (_lock)
            {
                var returned = Math.Min(bytes, asset.DecoderBytes);
                asset.DecoderBytes -= returned;
                _bytes -= returned;
            }
        }

        /// <summary>Closes the least recently used decoder that no thread is using; false when there is none.</summary>
        private bool CloseIdleDecoderLocked(Asset? current)
        {
            for (var node = _decoders.Last; node is not null; node = node.Previous)
            {
                var asset = node.Value;
                if (ReferenceEquals(asset, current) || !Monitor.TryEnter(asset.DecodeLock)) continue;
                try
                {
                    asset.ReleaseDecoder();
                    ForgetDecoderLocked(asset);
                }
                finally
                {
                    Monitor.Exit(asset.DecodeLock);
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks what the decoder will allocate against the probe and the media limits: a decoder may read other
        /// dimensions than the header the probe checked (a GIF frame larger than its logical screen grows the canvas).
        /// Static and internal so a test can reach it with a probe result that disagrees.
        /// </summary>
        internal static string? CheckCodec(SKCodec codec, MediaInfo info, SpecLimits limits)
        {
            int w = codec.Info.Width, h = codec.Info.Height, frames = Math.Max(1, codec.FrameCount);
            if (w != info.Width || h != info.Height)
                return $"The decoder reads {w} x {h} pixels where the header says {info.Width} x {info.Height}.";
            if (frames > Math.Max(1, info.Frames))
                return $"The decoder reads {frames} frames where the header says {info.Frames}.";
            var area = (long)w * h;
            if (w < 1 || h < 1 || w > limits.MediaSide || h > limits.MediaSide || area > limits.MediaArea
                || frames > limits.MediaFrames || area * frames > limits.MediaFrameArea)
                return $"{w} x {h} pixels in {frames} frames is over the media limits.";
            return null;
        }

        private void Open(Asset asset)
        {
            byte[]? bytes;
            try
            {
                // A blob over mediaBytes (a video's, or a picture's refused as media) is never read whole to be refused.
                if (_store.Size(asset.Hash) is { } size && size > _options.Limits.MediaBytes)
                {
                    Fail(asset, "limit.mediaBytes", $"{size} bytes is over the limit of {_options.Limits.MediaBytes} (mediaBytes).");
                    return;
                }
                bytes = _store.Read(asset.Hash);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                bytes = null;
            }
            if (bytes is null)
            {
                Fail(asset, "media.missing", $"No asset {asset.Hash}.");
                return;
            }
            // The probe's format picks the decoder; nothing sniffs the bytes again. An SVG is decoded only from the
            // document the validator rebuilt from it (spec/docs/svg.md): the stored bytes never reach the SVG decoder.
            var check = MediaValidator.Check(bytes, _options.Limits);
            if (check.Info is not { } info || check.Errors.Count > 0)
            {
                var error = check.Errors.FirstOrDefault();
                Fail(asset, error?.Code ?? "media.malformed", error?.Message ?? "Unreadable media.");
                return;
            }
            asset.Info = info;
            if (check.Format == MediaFormat.Svg)
            {
                if (check.Sanitized is not { } rebuilt)
                {
                    Fail(asset, "media.malformed", "The SVG has no rebuilt form to draw.");
                    return;
                }
                OpenSvg(asset, info, rebuilt);
                return;
            }
            if (Encoded(info.Format) is not { } expected)
            {
                Fail(asset, "media.format", "Not a supported image.");
                return;
            }
            asset.Data = SKData.CreateCopy(bytes);
            ChargeDecoder(asset, bytes.Length);
            asset.Codec = SKCodec.Create(asset.Data);
            if (asset.Codec is null || asset.Codec.EncodedFormat != expected)
            {
                Fail(asset, "media.format", "Not a supported image.");
                return;
            }
            if (CheckCodec(asset.Codec, info, _options.Limits) is { } problem)
            {
                Fail(asset, "media.malformed", problem);
                return;
            }
            // Built aside and published as one reference: an animation reopened after its decoder was closed keeps
            // answering lookups from the previous (equal) timing until this one is complete.
            var frames = Math.Max(1, asset.Codec.FrameCount);
            var frameInfo = frames > 1 ? asset.Codec.FrameInfo : [];
            var cumulative = new long[frames];
            long total = 0;
            for (var i = 0; i < frames; i++)
            {
                // Like browsers, a frame of 10 ms or less shows for 100 ms.
                var duration = frames > 1 ? frameInfo[i].Duration : 0;
                if (frames > 1 && duration <= 10) duration = 100;
                total += duration;
                cumulative[i] = total;
            }
            asset.Timing = new Timing(frames, frameInfo, cumulative, total);
            asset.Opened = true;
            asset.State = MediaState.Ready;
            _sizes.Put(asset.Hash, info.Width, info.Height);
        }

        private static SKEncodedImageFormat? Encoded(MediaFormat format) => format switch
        {
            MediaFormat.Png => SKEncodedImageFormat.Png,
            MediaFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            MediaFormat.Gif => SKEncodedImageFormat.Gif,
            MediaFormat.WebP => SKEncodedImageFormat.Webp,
            _ => null,
        };

        /// <summary>
        /// Records an SVG's rebuilt document into a picture at the probed natural size. The rebuilt bytes count against
        /// the budget as the asset's decoder while they are parsed; the picture then counts as the asset's frame.
        /// </summary>
        private void OpenSvg(Asset asset, MediaInfo info, byte[] rebuilt)
        {
            ChargeDecoder(asset, rebuilt.Length);
            DecodingSvg?.Invoke(asset.Hash, rebuilt);
            SKPicture picture;
            try
            {
                using var stream = new MemoryStream(rebuilt, writable: false);
                using var svg = new SKSvg();
                svg.Load(stream);
                if (svg.Picture is not { } source)
                {
                    Fail(asset, "media.malformed", "The SVG could not be drawn.");
                    return;
                }
                var cull = source.CullRect;
                using var recorder = new SKPictureRecorder();
                var canvas = recorder.BeginRecording(cull);
                canvas.DrawPicture(source);
                picture = recorder.EndRecording();
                float w = info.Width > 0 ? info.Width : cull.Width, h = info.Height > 0 ? info.Height : cull.Height;
                if (cull.Width > 0 && cull.Height > 0 && (w != cull.Width || h != cull.Height))
                {
                    // Draw the picture's own cull rectangle at the probed natural size.
                    using var r2 = new SKPictureRecorder();
                    var c2 = r2.BeginRecording(new SKRect(0, 0, w, h));
                    c2.Scale(w / cull.Width, h / cull.Height);
                    c2.Translate(-cull.Left, -cull.Top);
                    c2.DrawPicture(picture);
                    picture.Dispose();
                    picture = r2.EndRecording();
                }
                asset.Vector = MediaSample.FromPicture(picture, w, h);
            }
            finally
            {
                // The parse is over: its bytes go back to the budget (a failure already returned them).
                CloseDecoder(asset);
            }
            asset.Timing = Timing.Still;
            asset.Opened = true;
            asset.State = MediaState.Ready;
            if (asset.Vector is { } drawn) _sizes.Put(asset.Hash, drawn.Width, drawn.Height);
        }

        private bool Cached(Asset asset, int index)
        {
            lock (_lock)
            {
                return _frames.ContainsKey((asset.Hash, index));
            }
        }

        private void Decode(Asset asset, int index)
        {
            if (Cached(asset, index)) return;
            if (asset.Vector is { } vector)
            {
                if (vector.Retain() is { } held) Store(asset, 0, held);
                return;
            }
            var codec = asset.Codec!;
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul);
            var size = info.BytesSize64;
            // Every pixel buffer is counted (and room made) before it is allocated, so the budget holds at the peak too:
            // the composite, the frame's own copy and, for a frame restored to the previous one, the copy of the canvas.
            if (asset.Composite is null)
            {
                ChargeDecoder(asset, size);
                asset.Composite = new SKBitmap(info);
                if (asset.Composite.GetPixels() == IntPtr.Zero)
                {
                    Fail(asset, "media.malformed", "No memory for the image.");
                    return;
                }
            }
            else
            {
                ChargeDecoder(asset, 0);
            }
            var composite = asset.Composite;
            if (asset.Frames == 1)
            {
                var result = codec.GetPixels(info, composite.GetPixels());
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    Fail(asset, "media.malformed", $"The image could not be decoded ({result}).");
                    return;
                }
                if (StoreCopy(asset, 0, info, composite)) CloseDecoder(asset);
                return;
            }

            // The composite holds frame CompositeFrame as shown (before its disposal), which is what Skia's prior-frame
            // option means: Skia then applies that frame's disposal to its own rectangle and draws this one. Without
            // a usable prior frame Skia decodes the frames this one depends on into the buffer first.
            var frameInfo = asset.Timing.FrameInfo[index];
            var required = frameInfo.RequiredFrame;
            var prior = required >= 0 && asset.CompositeFrame >= required && asset.CompositeFrame < index ? asset.CompositeFrame : -1;
            // A frame disposed by restoring the previous one leaves the canvas as it was before it: keep a copy.
            SKBitmap? previous = null;
            if (frameInfo.DisposalMethod == SKCodecAnimationDisposalMethod.RestorePrevious && asset.CompositeFrame >= 0)
            {
                ChargeDecoder(asset, size);
                previous = composite.Copy();
                if (previous is null || previous.GetPixels() == IntPtr.Zero)
                {
                    previous?.Dispose();
                    Fail(asset, "media.malformed", "No memory for the image.");
                    return;
                }
            }
            var decoded = codec.GetPixels(info, composite.GetPixels(), new SKCodecOptions(index, prior));
            if (decoded is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                previous?.Dispose();
                Fail(asset, "media.malformed", $"Frame {index} could not be decoded ({decoded}).");
                return;
            }
            if (!StoreCopy(asset, index, info, composite))
            {
                previous?.Dispose();
                return;
            }
            if (frameInfo.DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious)
            {
                asset.CompositeFrame = index;
            }
            else if (previous is not null)
            {
                // The copy becomes the composite (still frame CompositeFrame); the old buffer's charge is the copy's now.
                composite.Dispose();
                asset.Composite = previous;
                Unreserve(asset, size);
            }
            else
            {
                asset.CompositeFrame = -1;
            }
        }

        /// <summary>
        /// Stores a copy of <paramref name="pixels"/> as frame <paramref name="index"/>, its bytes counted before the copy
        /// is made. False when there was no memory for it (the asset failed).
        /// </summary>
        private bool StoreCopy(Asset asset, int index, SKImageInfo info, SKBitmap pixels)
        {
            var size = info.BytesSize64;
            ChargeDecoder(asset, size);
            var image = SKImage.FromPixelCopy(info, pixels.GetPixels(), pixels.RowBytes);
            if (image is null)
            {
                Fail(asset, "media.malformed", "No memory for the image.");
                return false;
            }
            Store(asset, index, MediaSample.FromImage(image), size);
            return true;
        }

        /// <summary>
        /// Adds a frame to the cache. <paramref name="reserved"/> bytes the decode reserved for it on the asset's decoder
        /// become the frame's.
        /// </summary>
        private void Store(Asset asset, int index, MediaSample sample, long reserved = 0)
        {
            lock (_lock)
            {
                var moved = Math.Min(reserved, asset.DecoderBytes);
                asset.DecoderBytes -= moved;
                _bytes -= moved;
                if (_disposed || asset.Detached || _frames.ContainsKey((asset.Hash, index)))
                {
                    sample.Release();
                    return;
                }
                var node = _lru.AddFirst(new Frame(asset.Hash, index, sample));
                _frames[(asset.Hash, index)] = node;
                _bytes += sample.Bytes;
                asset.Latest = index;
                TrimLocked(asset, node);
            }
        }

        private void Evict(LinkedListNode<Frame> node)
        {
            _lru.Remove(node);
            _frames.Remove((node.Value.Hash, node.Value.Index));
            _bytes -= node.Value.Sample.Bytes;
            if (_assets.TryGetValue(node.Value.Hash, out var asset) && asset.Latest == node.Value.Index) asset.Latest = null;
            node.Value.Sample.Release();
        }

        private void SweepLocked()
        {
            var now = Now();
            if (now - _lastSweep < 5000) return;
            _lastSweep = now;
            var idle = (long)_options.IdleAfter.TotalMilliseconds;
            foreach (var asset in _assets.Values.Where(a => now - a.LastUsed > idle).ToList())
            {
                foreach (var node in _frames.Where(f => f.Key.Item1 == asset.Hash).Select(f => f.Value).ToList()) Evict(node);
                if (Monitor.TryEnter(asset.DecodeLock))
                {
                    try
                    {
                        asset.ReleaseDecoder();
                        ForgetDecoderLocked(asset);
                        asset.Vector?.Release();
                        asset.Vector = null;
                        asset.Detached = true;
                        _assets.Remove(asset.Hash);
                    }
                    finally
                    {
                        Monitor.Exit(asset.DecodeLock);
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queue.Writer.TryComplete();
            _stop.Cancel();
            var stopped = true;
            try
            {
                stopped = Task.WaitAll(_workers, _options.ShutdownWait);
            }
            catch (AggregateException)
            {
            }
            // Detach everything under the cache's lock, then free each asset outside it: a decode still running holds
            // its asset's decode lock and takes the cache's lock to store a frame, so waiting for one under the other
            // would deadlock. An asset still being decoded is freed by its worker when it leaves (it sees _disposed).
            List<Asset> assets;
            lock (_lock)
            {
                while (_lru.Last is { } last) Evict(last);
                assets = _assets.Values.ToList();
                foreach (var asset in assets) asset.Detached = true;
                _assets.Clear();
            }
            foreach (var asset in assets)
            {
                if (!Monitor.TryEnter(asset.DecodeLock)) continue;
                try
                {
                    CloseDecoder(asset);
                    asset.Vector?.Release();
                    asset.Vector = null;
                }
                finally
                {
                    Monitor.Exit(asset.DecodeLock);
                }
            }
            if (stopped) _stop.Dispose();
        }

        private sealed record Frame(string Hash, int Index, MediaSample Sample);

        /// <summary>
        /// Measured sizes by asset hash, apart from the decoded frames and their budget: put by an upload's inspection
        /// (<see cref="Retry"/>) and a finished open, read by <see cref="KnownSize"/>, forgotten past the entry cap
        /// (least recently used first) or when the asset's decode fails, never by the idle sweep, the frame budget or a retry. Its own lock, never taken
        /// with the cache's: held for one dictionary read or write, and when a new size finds the store full, one pass
        /// over its entries for the least recently used. An entry is the hash's 32 bytes, the size and a use stamp
        /// (about 60 bytes with the dictionary's own); a hash that isn't <c>sha256:</c> and 64 hex digits isn't kept.
        /// </summary>
        private sealed class SizeStore(int cap)
        {
            private readonly Lock _lock = new();
            private readonly Dictionary<HashKey, (float Width, float Height, long Used)> _sizes = new();
            private long _stamp;

            public int Count
            {
                get
                {
                    lock (_lock) return _sizes.Count;
                }
            }

            public void Put(string hash, float width, float height)
            {
                if (cap == 0 || !(width > 0 && height > 0) || !HashKey.TryParse(hash, out var key)) return;
                lock (_lock)
                {
                    if (_sizes.Count >= cap && !_sizes.ContainsKey(key))
                    {
                        HashKey oldest = default;
                        var used = long.MaxValue;
                        foreach (var (k, v) in _sizes)
                        {
                            if (v.Used < used)
                            {
                                used = v.Used;
                                oldest = k;
                            }
                        }
                        _sizes.Remove(oldest);
                    }
                    _sizes[key] = (width, height, ++_stamp);
                }
            }

            public void Remove(string hash)
            {
                if (cap == 0 || !HashKey.TryParse(hash, out var key)) return;
                lock (_lock) _sizes.Remove(key);
            }

            public (float Width, float Height)? Get(string hash)
            {
                if (cap == 0 || !HashKey.TryParse(hash, out var key)) return null;
                lock (_lock)
                {
                    if (!_sizes.TryGetValue(key, out var size)) return null;
                    _sizes[key] = size with { Used = ++_stamp };
                    return (size.Width, size.Height);
                }
            }
        }

        /// <summary>A <c>sha256:</c> hash's 32 bytes.</summary>
        private readonly record struct HashKey(ulong A, ulong B, ulong C, ulong D)
        {
            private const string Prefix = "sha256:";

            public static bool TryParse(string hash, out HashKey key)
            {
                key = default;
                if (hash.Length != Prefix.Length + 64 || !hash.StartsWith(Prefix, StringComparison.Ordinal)) return false;
                Span<byte> bytes = stackalloc byte[32];
                if (Convert.FromHexString(hash.AsSpan(Prefix.Length), bytes, out _, out var written) != OperationStatus.Done || written != 32)
                    return false;
                key = new HashKey(BinaryPrimitives.ReadUInt64LittleEndian(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
                return true;
            }
        }

        private sealed class Asset(string hash)
        {
            public readonly string Hash = hash;
            public readonly object DecodeLock = new();
            public volatile MediaState State = MediaState.Loading;
            public volatile bool Opened;
            /// <summary>Removed from the cache (swept or shut down): a worker that still has it frees what it holds and stores nothing.</summary>
            public volatile bool Detached;
            /// <summary>The header, from the asset's inspection when it is opened. Read by lookups without the lock.</summary>
            public volatile MediaInfo? Info;
            public string? Problem;
            public long LastUsed;
            /// <summary>Frame count, durations and disposal, published whole when the asset is opened (lookups read it without the lock).</summary>
            public volatile Timing Timing = Timing.Still;
            public int Frames => Timing.Frames;
            public int? Latest;
            public SKData? Data;
            public SKCodec? Codec;
            public SKBitmap? Composite;
            /// <summary>The frame the composite shows (before that frame's disposal); -1 when it holds none.</summary>
            public int CompositeFrame = -1;
            /// <summary>What the open decoder (bytes and composite) counts against the budget; changed under the cache's lock.</summary>
            public long DecoderBytes;
            public LinkedListNode<Asset>? DecoderNode;
            public MediaSample? Vector;

            public void ReleaseDecoder()
            {
                Codec?.Dispose();
                Codec = null;
                Data?.Dispose();
                Data = null;
                Composite?.Dispose();
                Composite = null;
                CompositeFrame = -1;
            }

            /// <summary>The frame shown <paramref name="seconds"/> into the animation (spec/docs/smart-nodes.md, media playback).</summary>
            public int FrameAt(double seconds, MediaPlayback playback)
            {
                var timing = Timing;
                int frames = timing.Frames;
                var total = timing.TotalMs;
                if (frames <= 1 || total <= 0 || playback == MediaPlayback.Still) return 0;
                var t = Math.Max(0, double.IsFinite(seconds) ? seconds : 0) * 1000;
                if (playback == MediaPlayback.Once && t >= total) return frames - 1;
                var ms = (long)Math.Floor(t % total);
                var i = Array.BinarySearch(timing.Cumulative, ms);
                i = i < 0 ? ~i : i + 1;
                return Math.Clamp(i, 0, frames - 1);
            }
        }

        /// <summary>An opened asset's frames: each frame's codec info and the time its display ends, in ms from the start.</summary>
        private sealed record Timing(int Frames, SKCodecFrameInfo[] FrameInfo, long[] Cumulative, long TotalMs)
        {
            public static readonly Timing Still = new(1, [], [0], 0);
        }
    }
}
