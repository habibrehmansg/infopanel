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
            if (imageDisplayItem is HttpImageDisplayItem httpImageDisplayItem)
            {
                var sensorReading = httpImageDisplayItem.GetValue();

                if (sensorReading.HasValue && !string.IsNullOrEmpty(sensorReading.Value.ValueText))
                {
                    return GetLocalImage(sensorReading.Value.ValueText, initialiseIfMissing);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(imageDisplayItem.CalculatedPath))
                {
                    return GetLocalImage(imageDisplayItem.CalculatedPath, initialiseIfMissing);
                }
            }

            return null;
        }

        public static LockedImage? GetLocalImage(string path, bool initialiseIfMissing = true)
        {
            if (string.IsNullOrEmpty(path) || path.Equals("NO_IMAGE"))
            {
                return null;
            }

            if (ImageCache.TryGetValue(path, out LockedImage? cachedImage) || !initialiseIfMissing)
            {
                return cachedImage;
            }

            var semaphore = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            semaphore.Wait();

            try
            {
                cachedImage = new LockedImage(path);

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
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine(e.ToString());
            }
            finally
            {
                semaphore.Release();
                _locks.TryRemove(path, out _);
            }

            return cachedImage;
        }


    }


}
