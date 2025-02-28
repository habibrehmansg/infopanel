using InfoPanel.Extensions;
using InfoPanel.Models;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace InfoPanel
{
    internal static class Cache
    {
        private static readonly IMemoryCache ImageCache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(1)
        });

        private static readonly object _imageLock = new();

        public static Stream ToStream(this Image image, ImageFormat format)
        {
            var stream = new MemoryStream();
            image.Save(stream, format);
            stream.Position = 0;
            return stream;
        }

        public static LockedImage? GetLocalImage(ImageDisplayItem imageDisplayItem, bool initialiseIfMissing = true)
        {
            if (imageDisplayItem.CalculatedPath != null)
            {
                return GetLocalImage(imageDisplayItem.CalculatedPath, initialiseIfMissing);
            }

            return null;
        }

        public static LockedImage? GetLocalImage(string path, bool initialiseIfMissing = true)
        {
            ImageCache.TryGetValue(path, out LockedImage? result);

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

                    ImageCache.Set(path, result, new MemoryCacheEntryOptions
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
