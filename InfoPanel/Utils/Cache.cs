using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Utils;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

        public static Stream ToStream(this Image image, ImageFormat format)
        {
            var stream = new MemoryStream();
            image.Save(stream, format);
            stream.Position = 0;
            return stream;
        }

        public static LockedImage? GetLocalImage(ImageDisplayItem imageDisplayItem, bool initialiseIfMissing = true)
        {
            LockedImage? result = null;
            if (imageDisplayItem is HttpImageDisplayItem httpImageDisplayItem)
            {
                var sensorReading = httpImageDisplayItem.GetValue();

                if (sensorReading.HasValue && !string.IsNullOrEmpty(sensorReading.Value.ValueText) && sensorReading.Value.ValueText.IsUrl())
                {
                    result = GetLocalImage(sensorReading.Value.ValueText, initialiseIfMissing);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(imageDisplayItem.CalculatedPath))
                {
                    result = GetLocalImage(imageDisplayItem.CalculatedPath, initialiseIfMissing);
                }
            }

            if (result != null)
            {
                if (imageDisplayItem.Hidden)
                {
                    result.Volume = 0; // Set volume to 0 if the image is hidden
                }
                else
                {
                    result.Volume = imageDisplayItem.Volume / 100.0f;
                }
            }

            return result;
        }

        private static LockedImage? GetLocalImage(string path, bool initialiseIfMissing = true)
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
            Task.Run(() => InitializeImage(path, semaphore));

            return null; // Return null immediately while initializing
        }

        private static void InitializeImage(string path, SemaphoreSlim semaphore)
        {
            try
            {
                // Double-check cache after we start (but we already hold the lock)
                if (ImageCache.TryGetValue(path, out _))
                {
                    return; // Already cached somehow
                }

                var cachedImage = new LockedImage(path);

                if (cachedImage != null)
                {
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
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Failed to load image '{path}': {e}");
            }
            finally
            {
                // NOW release the semaphore
                semaphore.Release();

                // Clean up semaphore if no one else is waiting
                if (semaphore.CurrentCount == 1)
                {
                    _locks.TryRemove(path, out _);
                }
            }
        }


    }


}
