using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace InfoPanel
{
    public sealed class PanelDrawTask : BackgroundTask
    {
        private static readonly Lazy<PanelDrawTask> _instance = new(() => new PanelDrawTask());
        public static PanelDrawTask Instance => _instance.Value;
        private PanelDrawTask() { }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            var watch = new Stopwatch();
            watch.Start();

            try
            {
                var fpsCounter = new FpsCounter();
                var stopwatch = new Stopwatch();

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        stopwatch.Restart();

                        var profiles = ConfigModel.Instance.GetProfilesCopy().Where(profile =>
                        (profile.Active && !profile.Direct2DMode)).ToList();

                        try
                        {
                            var options = new ParallelOptions
                            {
                                CancellationToken = token
                            };
                            Parallel.ForEach(profiles, options, profile =>
                            {
                                using var bitmap = Render(profile);
                                SharedModel.Instance.SetPanelBitmap(profile, bitmap);
                            });
                        }
                        catch (Exception e)
                        {
                            Trace.WriteLine($"Exception during parallel execution: {e.Message}");
                        }

                        var frameTime = stopwatch.ElapsedMilliseconds;
                        //Trace.WriteLine($"Frametime: {frameTime}");

                        fpsCounter.Update();
                        SharedModel.Instance.CurrentFrameRate = fpsCounter.FramesPerSecond;
                        SharedModel.Instance.CurrentFrameTime = frameTime;

                        var targetFrameTime = 1000.0 / (ConfigModel.Instance.Settings.TargetFrameRate * 1.03);
                        if (frameTime < targetFrameTime)
                        {
                            var sleep = (int)(targetFrameTime - frameTime);
                            //Trace.WriteLine($"Sleep {sleep}ms");
                            await Task.Delay(sleep, token);
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Exception during task execution: {ex.Message}");
                    }
                }
            }
            catch (TaskCanceledException)
            {
                Trace.WriteLine("Task cancelled");
            }
        }

        public static Bitmap Render(Profile profile, bool drawSelected = true, double scale = 1, bool cache = true, bool videoBackgroundFallback = false, PixelFormat pixelFormat = PixelFormat.Format32bppArgb)
        {
            var bitmap = new Bitmap(profile.Width, profile.Height, pixelFormat);

            if (profile.OverrideDpi)
            {
                bitmap.SetResolution(96, 96);
            }

            using var g = CompatGraphics.FromBitmap(bitmap) as MyGraphics;
            PanelDraw.Run(profile, g, drawSelected, scale, cache, videoBackgroundFallback);

            return bitmap;
        }

        public static Bitmap RenderSplash(int width, int height, PixelFormat pixelFormat = PixelFormat.Format32bppArgb, RotateFlipType rotateFlipType = RotateFlipType.RotateNoneFlipNone)
        {
            var bitmap = new Bitmap(width, height, pixelFormat);
            using var g = CompatGraphics.FromBitmap(bitmap) as MyGraphics;
            g.Clear(Color.Black);

            using var logo = LoadBitmapFromResource("logo.png");
            logo.RotateFlip(rotateFlipType);

            var size = Math.Min(width, height) / 3;
            g.DrawBitmap(logo, width / 2 - size / 2, height / 2 - size / 2, size, size);
            
            return bitmap;
        }

        public static Bitmap LoadBitmapFromResource(string resourceName)
        {
            // Construct the URI for the image resource
            var uri = new Uri($"pack://application:,,,/Resources/Images/{resourceName}");

            // Load the image as a BitmapImage
            var bitmapImage = new BitmapImage(uri);

            // Convert the BitmapImage to a Bitmap
            using var memoryStream = new MemoryStream();
            // Create a new encoder to save the bitmap image to a memory stream
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
            encoder.Save(memoryStream);

            // Create a Bitmap from the memory stream
            using var tempBitmap = new Bitmap(memoryStream);
            // Clone the bitmap to ensure the stream is closed
            return new Bitmap(tempBitmap);
        }
    }
}
