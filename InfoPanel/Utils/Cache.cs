using InfoPanel.Extensions;
using InfoPanel.Models;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace InfoPanel
{
    internal static class Cache
    {
        private static readonly IMemoryCache ImageCache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(10)
        });

        private static readonly Timer _expirationTimer;
        private static readonly object _imageLock = new();

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
            if(imageDisplayItem is HttpImageDisplayItem httpImageDisplayItem)
            {
                var sensorReading = httpImageDisplayItem.GetValue();

                if (sensorReading.HasValue && sensorReading.Value.ValueText != null)
                {
                    return GetLocalImage(sensorReading.Value.ValueText, initialiseIfMissing, imageDisplayItem.Guid.ToString());
                }
            }else
            {
                if (imageDisplayItem.CalculatedPath != null)
                {
                    return GetLocalImage(imageDisplayItem.CalculatedPath, initialiseIfMissing, imageDisplayItem.Guid.ToString());
                }
            }

            return null;
        }

        public static LockedImage? GetLocalImage(string path, bool initialiseIfMissing = true, string tag = "default")
        {
            var cacheKey = $"{tag}-{path}";
            ImageCache.TryGetValue(cacheKey, out LockedImage? result);

            if (result != null || !initialiseIfMissing)
            {
                return result;
            }

            lock (_imageLock)
            {
                try
                {
                    if (!path.Equals("NO_IMAGE") && (path.IsUrl() || File.Exists(path)))
                    {
                        result = new LockedImage(path);
                    }

                    ImageCache.Set(cacheKey, result, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromSeconds(5),
                        PostEvictionCallbacks = { new PostEvictionCallbackRegistration
                        {
                            EvictionCallback = (key, value, reason, state) =>
                            {
                                if(value is LockedImage lockedImage)
                                {
                                   lockedImage.Dispose();
                                }
                            }
                        } }
                    });

                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.ToString());
                }
            }
            return result;
        }
    }


}
