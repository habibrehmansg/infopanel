using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    internal static class Cache
    {
        private static readonly TypedMemoryCache<LockedImage> ImageCache = new(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(10)
        });

        private static readonly Timer _expirationTimer;
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = [];

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

            var semaphore = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

            // Try to acquire lock WITHOUT waiting (0ms timeout)
            if (!semaphore.Wait(0))
            {
                // Another thread is initializing - return null immediately
                return null;
            }

            // Start async initialization without blocking
            _ = Task.Run(() => InitializeImageSafe(path, imageDisplayItem, semaphore));

            return null; // Return null immediately while initializing
        }

        private static void InitializeImageSafe(string path, ImageDisplayItem? imageDisplayItem, SemaphoreSlim semaphore)
        {
            try
            {
                InitializeImage(path, imageDisplayItem);
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Failed to load image '{path}': {e}");
            }
            finally
            {
                semaphore.Release();

                // Safely clean up semaphore - check if we can remove it atomically
                if (_locks.TryGetValue(path, out var currentSemaphore) &&
                    ReferenceEquals(currentSemaphore, semaphore) &&
                    semaphore.CurrentCount == 1)
                {
                    _locks.TryRemove(path, out _);
                }
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

            ImageCache.Set(path, cachedImage, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromSeconds(10),
                PostEvictionCallbacks = {
                    new PostEvictionCallbackRegistration
                    {
                        EvictionCallback = (key, value, reason, state) =>
                        {
                            Trace.WriteLine($"Cache entry '{key}' evicted due to {reason}.");
                            if (value is LockedImage lockedImage)
                            {
                                lockedImage.Dispose();
                            }
                        }
                    }
                }
            });

            Trace.WriteLine($"Image '{path}' loaded successfully");
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
                var semaphore = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));

                try
                {
                    semaphore.Wait();
                    ImageCache.Remove(path);
                }
                catch (Exception e)
                {
                    Trace.WriteLine($"Failed to acquire lock for invalidating image '{path}': {e}");
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }
    }
}
