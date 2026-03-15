using AsyncKeyedLock;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Monitors.PluginProxies;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    internal static class Cache
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(Cache));
        private static readonly TypedMemoryCache<LockedImage> ImageCache = new(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(10)
        });

        private static readonly Timer _expirationTimer;
        private static readonly AsyncKeyedLocker<string> _locks = new();

        static Cache()
        {
            _expirationTimer = new Timer(
                callback: _ => ForceExpirationScan(),
                state: null,
                dueTime: TimeSpan.FromSeconds(10),
                period: TimeSpan.FromSeconds(10)
            );
        }
        private static void ForceExpirationScan()
        {
            _ = ImageCache.Get("__dummy_key_for_expiration__");
        }

        private const string PluginImageScheme = "plugin-image://";

        public static LockedImage? GetLocalImage(ImageDisplayItem imageDisplayItem, bool initialiseIfMissing = true)
        {
            LockedImage? result = null;
            if (imageDisplayItem is HttpImageDisplayItem httpImageDisplayItem)
            {
                var sensorReading = httpImageDisplayItem.GetValue();

                if (sensorReading.HasValue && !string.IsNullOrEmpty(sensorReading.Value.ValueText))
                {
                    var valueText = sensorReading.Value.ValueText;

                    if (valueText.StartsWith(PluginImageScheme, StringComparison.Ordinal))
                    {
                        result = GetPluginImageFromUri(valueText);
                    }
                    else if (valueText.IsUrl())
                    {
                        result = GetLocalImage(valueText, initialiseIfMissing, imageDisplayItem);
                    }
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(imageDisplayItem.CalculatedPath))
                {
                    result = GetLocalImage(imageDisplayItem.CalculatedPath, initialiseIfMissing, imageDisplayItem);
                }
            }

            result?.AddImageDisplayItem(imageDisplayItem);

            return result;
        }

        /// <summary>
        /// Parses a plugin-image://{pluginId}/{imageId} URI and returns the cached LockedImage.
        /// </summary>
        private static LockedImage? GetPluginImageFromUri(string uri)
        {
            // Parse "plugin-image://pluginId/imageId"
            var path = uri[PluginImageScheme.Length..];
            var slashIndex = path.IndexOf('/');
            if (slashIndex < 0) return null;

            var pluginId = path[..slashIndex];
            var imageId = path[(slashIndex + 1)..];

            return GetPluginImage(pluginId, imageId);
        }

        private static LockedImage? GetLocalImage(string path, bool initialiseIfMissing = true, ImageDisplayItem? imageDisplayItem = null)
        {
            if (string.IsNullOrEmpty(path) || path.Equals("NO_IMAGE"))
            {
                return null;
            }

            // Check cache first
            if (ImageCache.TryGetValue(path, out LockedImage? cachedImage))
            {
                return cachedImage;
            }

            if (!initialiseIfMissing)
            {
                return null;
            }

            // Start async initialization without blocking
            _ = Task.Run(() => InitializeImageSafe(path, imageDisplayItem));

            return null; // Return null immediately while initializing
        }

        private static void InitializeImageSafe(string path, ImageDisplayItem? imageDisplayItem)
        {
            // Try to acquire lock WITHOUT waiting (0ms timeout)
            using var semLock = _locks.LockOrNull(path, 0);

            if (semLock == null)
            {
                // Another thread is initializing - return immediately
                return;
            }

            try
            {
                InitializeImage(path, imageDisplayItem);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to load image '{Path}'" , path);
            }
        }

        private static void InitializeImage(string path, ImageDisplayItem? imageDisplayItem)
        {
            // Double-check cache after acquiring lock - another thread may have loaded it
            if (ImageCache.TryGetValue(path, out _))
            {
                return; // Already cached by another thread
            }

            var cachedImage = new LockedImage(path, imageDisplayItem);

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

            // Only set expiration for non-persistent images
            if (imageDisplayItem?.PersistentCache != true)
            {
                cacheOptions.SlidingExpiration = TimeSpan.FromSeconds(10);
            }

            ImageCache.Set(path, cachedImage, cacheOptions);

            Logger.Debug("Image '{Path}' loaded successfully (Persistent: {Persistent})", path, imageDisplayItem?.PersistentCache ?? false);
        }

        public static void TouchImage(ImageDisplayItem imageDisplayItem)
        {
            if (imageDisplayItem is HttpImageDisplayItem httpImageDisplayItem)
            {
                var sensorReading = httpImageDisplayItem.GetValue();
                if (sensorReading.HasValue && !string.IsNullOrEmpty(sensorReading.Value.ValueText))
                {
                    var valueText = sensorReading.Value.ValueText;
                    if (valueText.StartsWith(PluginImageScheme, StringComparison.Ordinal) || valueText.IsUrl())
                    {
                        ImageCache.TryGetValue(valueText, out _);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(imageDisplayItem.CalculatedPath))
            {
                ImageCache.TryGetValue(imageDisplayItem.CalculatedPath, out _);
            }
        }

        public static void InvalidateImage(ImageDisplayItem imageDisplayItem)
        {
            var path = imageDisplayItem.CalculatedPath;

            if (!string.IsNullOrEmpty(path))
            {
                InvalidateImage(path);
            }
        }

        public static void InvalidateImage(string path)
        {
            if (!string.IsNullOrEmpty(path))
            {
                using (_locks.Lock(path))
                {
                    try
                    {
                        ImageCache.Remove(path);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Failed to invalidate image from cache '{Path}'", path);
                    }
                }
            }
        }

        /// <summary>
        /// Gets or creates a LockedImage backed by a plugin image proxy (shared memory).
        /// Uses a persistent cache entry since the image is always live.
        /// Cache key format: plugin-image://{pluginId}/{imageId}
        /// </summary>
        public static LockedImage? GetPluginImage(string pluginId, string imageId)
        {
            var cacheKey = $"plugin-image://{pluginId}/{imageId}";

            if (ImageCache.TryGetValue(cacheKey, out LockedImage? cachedImage))
            {
                return cachedImage;
            }

            var proxy = PluginMonitor.Instance.GetImageProxy(pluginId, imageId);
            if (proxy == null) return null;

            var lockedImage = new LockedImage(cacheKey, proxy);

            var cacheOptions = new MemoryCacheEntryOptions
            {
                PostEvictionCallbacks = {
                    new PostEvictionCallbackRegistration
                    {
                        EvictionCallback = (key, value, reason, state) =>
                        {
                            Logger.Debug("Plugin image cache entry '{Key}' evicted due to {Reason}", key, reason);
                            // Don't dispose — the ProxyPluginImage lifecycle is managed by PluginHostConnection
                        }
                    }
                }
            };
            // No sliding expiration — persistent cache for plugin images

            ImageCache.Set(cacheKey, lockedImage, cacheOptions);

            Logger.Information("Plugin image cached: {CacheKey} ({W}x{H})", cacheKey, proxy.Width, proxy.Height);

            return lockedImage;
        }

        /// <summary>
        /// Invalidates plugin image cache entries for specific image IDs.
        /// Called when a plugin host disconnects or is reloaded.
        /// </summary>
        public static void InvalidatePluginImages(string pluginId, IEnumerable<string> imageIds)
        {
            foreach (var imageId in imageIds)
            {
                var cacheKey = $"plugin-image://{pluginId}/{imageId}";
                ImageCache.Remove(cacheKey);
                Logger.Debug("Invalidated plugin image cache: {CacheKey}", cacheKey);
            }
        }
    }
}
