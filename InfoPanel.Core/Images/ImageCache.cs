using InfoPanel.Images;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class ImageCacheOptions
    {
        /// <summary>
        /// Load missing images on the calling thread instead of in the background. For tests and
        /// one-off renders; a render loop should leave it off so a slow disk never stalls a frame.
        /// </summary>
        public bool SynchronousLoad { get; init; }
    }

    /// <summary>
    /// Caches decoded local images (<see cref="LockedImage"/>) by resolved path. Images unused for
    /// 10 s are evicted and disposed unless they were requested as persistent.
    /// HTTP(S), RTSP(S), video files and <c>plugin-image://</c> sources are not supported yet and give null.
    /// </summary>
    public sealed class ImageCache : IImageSource, IDisposable
    {
        private static readonly ILogger Logger = Log.ForContext<ImageCache>();

        private static readonly TimeSpan SlidingExpiration = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan FailedRetryAfter = TimeSpan.FromSeconds(5);

        private static readonly string[] UnsupportedSchemes = ["http://", "https://", "rtsp://", "rtsps://", "plugin-image://"];
        private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".webm", ".avi", ".mov", ".m3u", ".m3u8"];

        private readonly ImageCacheOptions _options;
        private readonly TypedMemoryCache<LockedImage> _images = new(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(10)
        });
        private readonly ConcurrentDictionary<string, byte> _loading = new();
        private readonly ConcurrentDictionary<string, long> _generations = new();
        private readonly object _publishLock = new();
        private volatile bool _disposed;
        private readonly Timer _expirationTimer;

        /// <summary>Decodes a source; a seam so tests can act between decoding and publishing.</summary>
        internal Func<string, LockedImage> Decode = static source => new LockedImage(source);

        public ImageCache(ImageCacheOptions? options = null)
        {
            _options = options ?? new ImageCacheOptions();

            // MemoryCache only scans for expired entries when it is accessed; scan regularly so
            // unused images are released even when nothing is rendering.
            _expirationTimer = new Timer(
                callback: _ => ForceExpirationScan(),
                state: null,
                dueTime: TimeSpan.FromSeconds(10),
                period: TimeSpan.FromSeconds(10)
            );
        }

        private void ForceExpirationScan()
        {
            try
            {
                _images.TryGetValue("__dummy_key_for_expiration__", out _);
            }
            catch (ObjectDisposedException)
            {
                // The timer can fire once more while the cache is being disposed.
            }
        }

        public static bool IsSupported(string source)
        {
            if (string.IsNullOrEmpty(source) || source.Equals("NO_IMAGE"))
            {
                return false;
            }

            foreach (var scheme in UnsupportedSchemes)
            {
                if (source.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var queryIndex = source.IndexOf('?');
            var path = queryIndex >= 0 ? source[..queryIndex] : source;

            foreach (var extension in VideoExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        public IRenderImage? Get(string source, bool persistent)
        {
            if (!IsSupported(source) || _disposed)
            {
                return null;
            }

            // Check cache first
            if (_images.TryGetValue(source, out LockedImage? cachedImage) && cachedImage != null)
            {
                return cachedImage.Loaded ? cachedImage : null;
            }

            var generation = GetGeneration(source);

            if (_options.SynchronousLoad)
            {
                Load(source, persistent, generation);
                return !_disposed && _images.TryGetValue(source, out cachedImage) && cachedImage is { Loaded: true } ? cachedImage : null;
            }

            // Start loading without blocking; at most one load per source at a time.
            if (_loading.TryAdd(source, 0))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        Load(source, persistent, generation);
                    }
                    finally
                    {
                        _loading.TryRemove(source, out _);
                    }
                });
            }

            return null; // Return null immediately while loading
        }

        private long GetGeneration(string source) => _generations.TryGetValue(source, out var generation) ? generation : 0;

        /// <summary>
        /// Decodes <paramref name="source"/> and publishes it, unless the cache was disposed or the
        /// source invalidated since <paramref name="generation"/> was read; then the image is disposed.
        /// </summary>
        private void Load(string source, bool persistent, long generation)
        {
            LockedImage? image = null;

            try
            {
                if (_disposed)
                {
                    return;
                }

                // Double-check: another load may have finished first.
                if (_images.TryGetValue(source, out _))
                {
                    return;
                }

                image = Decode(source);

                var cacheOptions = new MemoryCacheEntryOptions
                {
                    PostEvictionCallbacks = {
                        new PostEvictionCallbackRegistration
                        {
                            EvictionCallback = (key, value, reason, state) =>
                            {
                                Logger.Debug("Cache entry '{Key}' evicted due to {Reason}", key, reason);
                                if (value is LockedImage lockedImage)
                                {
                                    lockedImage.Dispose();
                                }
                            }
                        }
                    }
                };

                if (!image.Loaded)
                {
                    // Remember the failure briefly so a missing file isn't re-read every frame,
                    // but pick the file up once it appears.
                    cacheOptions.AbsoluteExpirationRelativeToNow = FailedRetryAfter;
                }
                else if (!persistent)
                {
                    cacheOptions.SlidingExpiration = SlidingExpiration;
                }

                // Publishing and Invalidate/Dispose take the same lock, so an invalidation either
                // happens first (and this load is discarded) or removes what this load published.
                lock (_publishLock)
                {
                    if (_disposed || GetGeneration(source) != generation)
                    {
                        Logger.Debug("Image '{Path}' discarded: invalidated or cache disposed while loading", source);
                        return;
                    }

                    _images.Set(source, image, cacheOptions);
                    image = null; // owned by the cache now
                }

                Logger.Debug("Image '{Path}' loaded (Persistent: {Persistent})", source, persistent);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to load image '{Path}'", source);
            }
            finally
            {
                image?.Dispose();
            }
        }

        /// <summary>
        /// Drops a cached image so the next <see cref="Get"/> reloads it (e.g. after the file changed).
        /// A load already in flight for it is discarded.
        /// </summary>
        public void Invalidate(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return;
            }

            lock (_publishLock)
            {
                if (_disposed)
                {
                    return;
                }

                _generations.AddOrUpdate(source, 1, static (_, generation) => generation + 1);
                _images.Remove(source);
            }
        }

        public void Dispose()
        {
            lock (_publishLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _expirationTimer.Dispose();
            _images.Dispose();
        }
    }
}
