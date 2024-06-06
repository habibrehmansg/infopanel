using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace InfoPanel.Models
{
    public class LockedImage : IDisposable
    {
        private readonly System.Drawing.Image image;
        public readonly BitmapImage bitmapImage;
        public readonly int Scale;
        public readonly int Width;
        public readonly int Height;
        public readonly int Frames;
        public readonly int FrameTime;

        private readonly object bitmapLock = new object();
        private bool isDisposed = false;

        private Stopwatch Stopwatch = new Stopwatch();

        public LockedImage(System.Drawing.Image image)
        {
            this.image = image;
            this.Width = image.Width;
            this.Height = image.Height;
            this.Scale = 100;

            using (MemoryStream stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
                bitmapImage.Freeze();
            }

            FrameDimension dimension = new FrameDimension(image.FrameDimensionsList[0]);
            Frames = image.GetFrameCount(dimension);

            if (Frames > 1)
            {
                Stopwatch.Start();

                var totalDuration = 0;
                for (int i = 0; i < Frames; i++)
                {
                    var delayPropertyBytes = image.GetPropertyItem(20736)?.Value;
                    if (delayPropertyBytes != null)
                    {
                        var frameDelay = BitConverter.ToInt32(delayPropertyBytes, i * 4) * 10;
                        totalDuration += frameDelay;
                    }
                }

                if(totalDuration == 0)
                {
                    totalDuration = 1000;
                }

                FrameTime = totalDuration / Frames;
            }
            else
            {
                FrameTime = 0;
            }
        }

        public int GetCurrentTimeFrame()
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
                if(elapsedTime > 86400000)
                {
                    Stopwatch.Restart();
                }

                return elapsedFrames % Frames;
            }
            else
            {
                return 0;
            }
        }

        public LockedImage(System.Drawing.Image image, int scale)
        {
            this.image = image;
            this.Width = image.Width;
            this.Height = image.Height;
            this.Scale = scale;
            FrameDimension dimension = new FrameDimension(image.FrameDimensionsList[0]);
            Frames = image.GetFrameCount(dimension);
        }

        public void Access(Action<System.Drawing.Image> access)
        {
            if (isDisposed)
                throw new ObjectDisposedException("LockedImage");

            lock (bitmapLock)
            {
                access(image);
            }
        }
        public void Dispose()
        {
            if (isDisposed)
                return;

            lock (bitmapLock)
            {
                if (!isDisposed)
                {
                    image.Dispose();
                    isDisposed = true;
                }
            }
        }
    }


}
