using InfoPanel.Models;
using InfoPanel.Properties;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace InfoPanel
{
    internal static class Cache
    {
        private static ConcurrentDictionary<string, LockedImage> ImageDictionary = new ConcurrentDictionary<string, LockedImage>();
        private static IMemoryCache ScaledImageCache = new MemoryCache(new MemoryCacheOptions()
        {
            SizeLimit = 250 * 1024 * 1024, // 250 MB
            ExpirationScanFrequency = TimeSpan.FromMinutes(1)
        });

        private static object _imageLock = new object();

        public static void PurgeImageCache(ImageDisplayItem imageDisplayItem)
        {
            if (imageDisplayItem.CalculatedPath != null)
            {
                PurgeImageCache(imageDisplayItem.CalculatedPath);
            }
        }

        public static void PurgeImageCache(string path)
        {
            lock (_imageLock)
            {
                if (ImageDictionary.ContainsKey(path))
                {
                    ImageDictionary.Remove(path, out var Image);
                    Image?.Dispose();
                }
            }
        }

        public static Stream ToStream(this Image image, ImageFormat format)
        {
            var stream = new MemoryStream();
            image.Save(stream, format);
            stream.Position = 0;
            return stream;
        }

        public static LockedImage? GetLocalImage(ImageDisplayItem imageDisplayItem)
        {
            if (imageDisplayItem.CalculatedPath != null)
            {
                return GetLocalImage(imageDisplayItem.CalculatedPath);
            }

            return null;
        }

        public static LockedImage? GetLocalImage(string path)
        {
            if (ImageDictionary.ContainsKey(path))
            {
                return ImageDictionary[path];
            }

            lock (_imageLock)
            {
                try
                {
                    LockedImage? result;
                    if (!path.Equals("NO_IMAGE") && File.Exists(path))
                    {
                        using (var temp = Image.FromFile(path))
                        {
                            //todo fix DPI to lower
                            FrameDimension dimension = new FrameDimension(temp.FrameDimensionsList[0]);
                            if (temp.GetFrameCount(dimension) == 1 
                                && temp.Width > 4096 || temp.Height > 4096
                                || temp.HorizontalResolution > 96 || temp.VerticalResolution > 96)
                            {
                                //var width = (int)(temp.Width * (96.0 / temp.HorizontalResolution));
                                //var height = (int)(temp.Height * (96.0 / temp.VerticalResolution));
                                var width = temp.Width;
                                var height = temp.Height;



                                using (var resized = new Bitmap(width, height, temp.PixelFormat))
                                {
                                    resized.SetResolution(96, 96);
                                    using (Graphics g = Graphics.FromImage(resized))
                                    {
                                        g.DrawImage(temp, new Rectangle(0, 0, temp.Width, temp.Height));
                                    }

                                    var stream = ToStream(resized, temp.RawFormat);
                                    var image = Image.FromStream(stream);
                                    result = new LockedImage(image);
                                }
                            }
                            else
                            {
                                var stream = ToStream(temp, temp.RawFormat);
                                result = new LockedImage(Image.FromStream(stream));
                            }

                        }
                    }
                    else
                    {
                        using (var stream = Application.GetResourceStream(new Uri("Images/no_image.png", UriKind.Relative)).Stream)
                        {
                            result = new LockedImage(Image.FromStream(stream));
                        }
                    }

                    ImageDictionary[path] = result;
                    return result;
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.ToString());
                }
            }

            return null;
        }

        public static LockedImage? GetScaledImage(ImageDisplayItem imageDisplayItem, int frame = 0)
        {
            LockedImage? result = null;

            if (imageDisplayItem.CalculatedPath == null)
            {
                return result;
            }

            var key = imageDisplayItem.Guid.ToString() + "-" + frame;

            if (ScaledImageCache.TryGetValue(key, out result))
            {
                if (result?.Scale == imageDisplayItem.Scale)
                {
                    return result;
                }

                result?.Dispose();
            }

            var originalImage = GetLocalImage(imageDisplayItem.CalculatedPath);

            originalImage?.Access(image =>
            {
                int width = (int)(image.Width * imageDisplayItem.Scale / 100.0f);
                int height = (int)(image.Height * imageDisplayItem.Scale / 100.0f);

                var bitmap = new Bitmap(width, height);

                using (var g = Graphics.FromImage(bitmap))
                {
                    if (frame > 0 && frame < originalImage.Frames)
                    {
                        FrameDimension dimension = new FrameDimension(image.FrameDimensionsList[0]);
                        image.SelectActiveFrame(dimension, frame);
                    }
                    g.DrawImage(image, 0, 0, width, height);
                }

                result = new LockedImage(bitmap, imageDisplayItem.Scale);
                var size = bitmap.Width * bitmap.Height * 4;
                if (size <= 1000 * 1000)
                {
                    ScaledImageCache.Set(key, result,
                   new MemoryCacheEntryOptions()
                   .SetSlidingExpiration(TimeSpan.FromMinutes(1))
                   .SetSize(size));
                    Trace.WriteLine($"Cache inserted {imageDisplayItem.Name}-{frame} {size / 1000} kb");
                }
                else
                {
                    Trace.WriteLine($"Cache skipped {imageDisplayItem.Name}-{frame} {size / 1000} kb");
                }

            });

            return result;
        }
    }


}
