using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

                         Parallel.ForEach(profiles, profile =>
                         {
                             using var bitmap = Render(profile);
                             SharedModel.Instance.SetPanelBitmap(profile, bitmap);
                         });

                        fpsCounter.Update();
                        SharedModel.Instance.CurrentFrameRate = fpsCounter.FramesPerSecond;
                        SharedModel.Instance.CurrentFrameTime = stopwatch.ElapsedMilliseconds;

                        var targetFrameTime = 1000.0 / ConfigModel.Instance.Settings.TargetFrameRate;
                        if (stopwatch.ElapsedMilliseconds < targetFrameTime)
                        {
                            var sleep = (int)(targetFrameTime - stopwatch.ElapsedMilliseconds);
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

        public static Bitmap Render(Profile profile, bool drawSelected = true, PixelFormat pixelFormat = PixelFormat.Format32bppArgb)
        {
            var bitmap = new Bitmap(profile.Width, profile.Height, pixelFormat);

            if (profile.OverrideDpi)
            {
                bitmap.SetResolution(96, 96);
            }

            using var g = CompatGraphics.FromBitmap(bitmap) as MyGraphics;
            PanelDraw.Run(profile, g, drawSelected);

            return bitmap;
        }
    }
}
