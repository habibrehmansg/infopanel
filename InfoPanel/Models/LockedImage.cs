using ImageMagick;
using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace InfoPanel.Models
{
    public class LockedImage : IDisposable
    {
        private readonly string? ImagePath;
        private MagickImageCollection? ImageCollection;
        private readonly Bitmap? Bitmap;
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

        public LockedImage(Bitmap bitmap)
        {
            Bitmap = bitmap;
            Width = Bitmap.Width;
            Height = Bitmap.Height;
            Frames = 1;
        }

        private void LoadImage()
        {
            if (ImagePath != null && File.Exists(ImagePath))
            {
                lock (Lock) { 
                    try
                    {
                        ImageCollection?.Dispose();
                        ImageCollection = new MagickImageCollection(ImagePath);
                        ImageCollection.Coalesce();

                        Width = (int)ImageCollection[0].Width;
                        Height = (int)ImageCollection[0].Height;
                        Frames = ImageCollection.Count;

                        //only animate if there are multiple frames (gif)
                        if (Frames > 1)
                        {
                            var totalDuration = 0;
                            foreach (var image in ImageCollection)
                            {
                                totalDuration += (int)image.AnimationDelay * 10;
                            }

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

        private Bitmap? GetCurrentFrame()
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

                return ImageCollection?[currentFrame].ToBitmap();
            }
            else
            {
                return ImageCollection?.ToBitmap();
            }
        }

        public void Access(Action<Bitmap?> access)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException("LockedImage");
            }

            lock (Lock)
            {
                if (Bitmap != null)
                {
                    access(Bitmap);
                }
                else
                {
                    access(GetCurrentFrame());
                }
            }
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            lock (Lock)
            {
                if (!IsDisposed)
                {
                    ImageCollection?.Dispose();
                    IsDisposed = true;
                }
            }
        }
    }


}
