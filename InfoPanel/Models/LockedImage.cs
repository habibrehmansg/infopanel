using ImageMagick;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using unvell.D2DLib;

namespace InfoPanel.Models
{
    public partial class LockedImage : IDisposable
    {
        public readonly string ImagePath;
        private Image? Image;
        private Bitmap?[] BitmapCache;
        private FrameDimension FrameDimension;
        //private MagickImageCollection? ImageCollection;
        private IntPtr? D2DHandle;
        private D2DBitmap[]? D2DBitmapCache;
        public int Width = 0;
        public int Height = 0;

        public int Frames = 0;
        public int FrameTime = 0;

        private readonly object Lock = new();
        private bool IsDisposed = false;

        private readonly Stopwatch Stopwatch = new Stopwatch();

        public LockedImage(string imagePath)
        {
            ImagePath = imagePath;
            LoadImage();
        }

        private void LoadImage()
        {
            if (ImagePath != null && File.Exists(ImagePath))
            {
                lock (Lock) { 
                    try
                    {
                        DisposeAssets();

                        Image?.Dispose();

                        //double convert to release lock on file
                        using var temp = Image.FromFile(ImagePath);

                        var stream = new MemoryStream();
                        temp.Save(stream, temp.RawFormat);
                        stream.Position = 0;

                        Image = Image.FromStream(stream);

                        Width = Image.Width;
                        Height = Image.Height;

                        FrameDimension = new FrameDimension(Image.FrameDimensionsList[0]);
                        Frames = Image.GetFrameCount(FrameDimension);

                        BitmapCache = new Bitmap[Frames];

                        //ImageCollection = new MagickImageCollection(ImagePath);
                        //ImageCollection.Coalesce();

                        //Width = (int)ImageCollection[0].Width;
                        //Height = (int)ImageCollection[0].Height;
                        //Frames = ImageCollection.Count;

                        DisposeD2DAssets();

                        D2DBitmapCache = new D2DBitmap[Frames];

                        //only animate if there are multiple frames (gif)
                        if (Frames > 1)
                        {
                            var totalDuration = 0;

                            for (int i = 0; i < Frames; i++)
                            {
                                Image.SelectActiveFrame(FrameDimension, i);
                                var delayPropertyBytes = Image.GetPropertyItem(20736)?.Value;

                                if (delayPropertyBytes != null)
                                {
                                    var frameDelay = BitConverter.ToInt32(delayPropertyBytes, i * 4) * 10;
                                    totalDuration += frameDelay;
                                }
                            }

                            //foreach (var image in ImageCollection)
                            //{
                            //    totalDuration += (int)image.AnimationDelay * 10;
                            //}

                            //default to 1 second if no delay is found
                            if (totalDuration == 0)
                            {
                                totalDuration = 1000;
                            }

                            FrameTime = totalDuration / Frames;

                            //start the stopwatch
                            Stopwatch.Start();
                        } else
                        {
                            Stopwatch.Stop();
                        }
                    }
                    catch { }
                }
            }
        }

        private int GetCurrentFrameCount()
        {
            if (Frames > 1)
            {
                var elapsedTime = Stopwatch.ElapsedMilliseconds;
                int elapsedFrames = 0;

                if (FrameTime > 0)
                {
                    elapsedFrames = (int)(elapsedTime / FrameTime);
                }

                //reset every day
                if (elapsedTime > 86400000)
                {
                    Stopwatch.Restart();
                }

                var currentFrame = elapsedFrames % Frames;

                //ensure the frame is within bounds
                if (currentFrame >= Frames)
                {
                    currentFrame = Frames - 1;
                }

                return currentFrame;
            }
            else
            {
                return 0;
            }
        }

        public void AccessD2D(D2DDevice device, IntPtr handle, Action<D2DBitmap?> action, bool cache = true)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            lock (Lock)
            {
                if (D2DHandle == null)
                {
                    D2DHandle = handle;
                }
                else
                if (D2DHandle != handle)
                {
                    Trace.WriteLine("D2DDevice changed, disposing assets");
                    DisposeD2DAssets();
                    D2DHandle = handle;
                }

                D2DBitmap? d2dbitmap = null;

                if (Image != null && D2DBitmapCache != null) {
                    var frame = GetCurrentFrameCount();
                    d2dbitmap = D2DBitmapCache[frame];

                    if (d2dbitmap == null)
                    {
                        if(Frames == 1)
                        {
                            d2dbitmap = device.CreateBitmapFromFile(ImagePath);
                        } else
                        {
                            //var bitmap = ImageCollection?[frame].ToBitmap();
                            Image.SelectActiveFrame(FrameDimension, frame);
                            d2dbitmap = device.CreateBitmapFromGDIBitmap((Bitmap)Image, true);
                        }

                        if (d2dbitmap != null)
                        {
                            D2DBitmapCache[frame] = d2dbitmap;
                        }
                    }
                }

                action(d2dbitmap);
            }
        }

        public void Access(Action<Bitmap?> access, bool cache = true)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            lock (Lock)
            {
                if (Image != null && BitmapCache != null)
                {
                    var frame = GetCurrentFrameCount();
                    var bitmap = BitmapCache[frame];

                    if (bitmap == null && Image != null)
                    {
                        Image.SelectActiveFrame(FrameDimension, frame);
                        bitmap = new Bitmap(Image);

                        if (cache)
                        {
                            BitmapCache[frame] = bitmap;
                        }
                    }

                    access(bitmap);
                }
            }
        }

        private void DisposeD2DAssets()
        {
            Trace.WriteLine("DisposeD2DAssets");
            if (D2DBitmapCache != null)
            {
                for(int i = 0; i< D2DBitmapCache.Length; i++)
                {
                    D2DBitmapCache[i]?.Dispose();
                    D2DBitmapCache[i] = null;
                }
            }
        }

        private void DisposeAssets()
        {
            Trace.WriteLine("DisposeAssets");
            if(BitmapCache != null)
            {
                for (int i = 0; i < BitmapCache.Length; i++)
                {
                    BitmapCache[i]?.Dispose();
                    BitmapCache[i] = null;
                }
            }

            //ImageCollection?.Dispose();
            //ImageCollection = null;
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            lock (Lock)
            {
                if (!IsDisposed)
                {
                    DisposeAssets();
                    DisposeD2DAssets();
                    IsDisposed = true;
                }
            }

            GC.SuppressFinalize(this);
        }
    }


}
