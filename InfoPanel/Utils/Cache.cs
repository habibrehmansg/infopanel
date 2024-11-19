using InfoPanel.Models;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;

namespace InfoPanel
{
    internal static class Cache
    {
        private static ConcurrentDictionary<string, LockedImage> ImageDictionary = new ConcurrentDictionary<string, LockedImage>();
        private static readonly IMemoryCache ImageCache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(5)
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
            LockedImage result;
            ImageCache.TryGetValue(path, out result);

            if(result != null)
            {
                //ImageCache.Set(path, result, TimeSpan.FromSeconds(5));
                return result;
            }

            lock (_imageLock)
            {
                try
                {
                    if (!path.Equals("NO_IMAGE") && File.Exists(path))
                    {
                        using (var temp = Image.FromFile(path))
                        {
                            //todo fix DPI to lower
                            //FrameDimension dimension = new FrameDimension(temp.FrameDimensionsList[0]);
                            //if (temp.GetFrameCount(dimension) == 1 
                            //    && temp.Width > 4096 || temp.Height > 4096
                            //    || temp.HorizontalResolution > 96 || temp.VerticalResolution > 96)
                            //{
                            //    //var width = (int)(temp.Width * (96.0 / temp.HorizontalResolution));
                            //    //var height = (int)(temp.Height * (96.0 / temp.VerticalResolution));
                            //    var width = temp.Width;
                            //    var height = temp.Height;



                            //    using (var resized = new Bitmap(width, height, temp.PixelFormat))
                            //    {
                            //        resized.SetResolution(96, 96);
                            //        using (Graphics g = Graphics.FromImage(resized))
                            //        {
                            //            g.DrawImage(temp, new Rectangle(0, 0, temp.Width, temp.Height));
                            //        }

                            //        var stream = ToStream(resized, temp.RawFormat);
                            //        var image = Image.FromStream(stream);
                            //        result = new LockedImage(image);
                            //    }
                            //}
                            //else
                            //{
                            //    //var stream = ToStream(temp, temp.RawFormat);
                            //    //result = new LockedImage(Image.FromStream(stream));
                            //    result = new LockedImage(path);
                            //}

                            result = new LockedImage(path);

                        }
                    }
                    else
                    {
                        using (var stream = Application.GetResourceStream(new Uri("Images/no_image.png", UriKind.Relative)).Stream)
                        {
                           
                            result = new LockedImage((Bitmap) Image.FromStream(stream));
                        }
                    }

                    ImageCache.Set(path, result, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = TimeSpan.FromSeconds(5)
                    });
                    return result;
                }
                catch (Exception e)
                {
                    Trace.WriteLine(e.ToString());
                }
            }

            return null;
        }
    }


}
