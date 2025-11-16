using AsyncKeyedLock;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using Serilog;
using System;
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

        public static LockedImage? GetLocalImage(ImageDisplayItem imageDisplayItem, bool initialiseIfMissing = true)
        {
            LockedImage? result = null;
            if (imageDisplayItem is HttpImageDisplayItem httpImageDisplayItem)
            {
                var sensorReading = httpImageDisplayItem.GetValue();

                if (sensorReading.HasValue && !string.IsNullOrEmpty(sensorReading.Value.ValueText) && sensorReading.Value.ValueText.IsUrl())
                {
                    result = GetLocalImage(sensorReading.Value.ValueText, initialiseIfMissing, imageDisplayItem);
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

            // Try to acquire lock WITHOUT waiting (0ms timeout)
            var semLock = _locks.LockOrNull(path, 0);

            if (semLock == null)
            {
                // Another thread is initializing - return null immediately
                return null;
            }

            // Start async initialization without blocking
            _ = Task.Run(() => InitializeImageSafe(path, imageDisplayItem, semLock));

            return null; // Return null immediately while initializing
        }

        private static void InitializeImageSafe(string path, ImageDisplayItem? imageDisplayItem, IDisposable semLock)
        {
            try
            {
                InitializeImage(path, imageDisplayItem);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failed to load image '{Path}'" , path);
            }
            finally
            {
                semLock.Dispose();
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
                    ImageCache.Remove(path);
                }
            }
        }
    }
}
