using InfoPanel.Drawing;
using InfoPanel.Models;
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
        private static readonly object _lock = new object();

        private static ConcurrentDictionary<Guid, LockedBitmap> BitmapCache = new ConcurrentDictionary<Guid, LockedBitmap>();
        public static PanelDrawTask Instance => _instance.Value;
        private PanelDrawTask() { }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            int currentFps = 0;
            long currentFrameTime = 0;

            var watch = new Stopwatch();
            watch.Start();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var frameRateLimit = ConfigModel.Instance.Settings.TargetFrameRate;
                        var optimalFrameTime = 1000.0 / frameRateLimit; // Target frame time in ms

                        // Start timing the frame
                        var frameStart = watch.ElapsedMilliseconds;

                        var profiles = ConfigModel.Instance.GetProfilesCopy().Where(profile =>
                        (profile.Active && !profile.Direct2DMode)
                        || (ConfigModel.Instance.Settings.BeadaPanel && ConfigModel.Instance.Settings.BeadaPanelProfile == profile.Guid)
                        || (ConfigModel.Instance.Settings.TuringPanelA && ConfigModel.Instance.Settings.TuringPanelAProfile == profile.Guid)
                        || (ConfigModel.Instance.Settings.TuringPanelC && ConfigModel.Instance.Settings.TuringPanelCProfile == profile.Guid)
                        || (ConfigModel.Instance.Settings.TuringPanelE && ConfigModel.Instance.Settings.TuringPanelEProfile == profile.Guid))
                            .ToList();

                         Parallel.ForEach(profiles, profile =>
                         {
                            var lockedBitmap = Render(profile);

                            lockedBitmap.Access(bitmap =>
                            {
                                SharedModel.Instance.SetPanelBitmap(profile, bitmap);
                            });
                        });

                        // Calculate the elapsed time for this frame
                        var frameTime = watch.ElapsedMilliseconds - frameStart;

                        // Update FPS and average frame time once per second
                        currentFrameTime += frameTime;
                        currentFps++;

                        if (frameStart >= 1000)
                        {
                            SharedModel.Instance.CurrentFrameRate = currentFps;
                            SharedModel.Instance.CurrentFrameTime = currentFrameTime / currentFps;

                            currentFps = 0;
                            currentFrameTime = 0;
                            watch.Restart();
                        }

                        // Delay to maintain target frame rate
                        var delay = optimalFrameTime - frameTime;
                        if (delay > 0) await Task.Delay((int)delay, token);

                        //Trace.WriteLine($"Draw Execution Time: {frameTime} ms");
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

        public static LockedBitmap Render(Profile profile, bool drawSelected = true)
        {
            if (!BitmapCache.TryGetValue(profile.Guid, out var lockedBitmap)
                || lockedBitmap.Width != profile.Width || lockedBitmap.Height != profile.Height
                || lockedBitmap.OverrideDpi != profile.OverrideDpi)
            {
                lockedBitmap?.Dispose();
                var bitmap = new Bitmap(profile.Width, profile.Height, PixelFormat.Format32bppArgb);

                if (profile.OverrideDpi)
                {
                    bitmap.SetResolution(96, 96);
                }

                lockedBitmap = new LockedBitmap(bitmap, profile.OverrideDpi);
                BitmapCache[profile.Guid] = lockedBitmap;
            }

            lockedBitmap.Access(bitmap =>
            {
                using var g = CompatGraphics.FromBitmap(bitmap) as MyGraphics;
                PanelDraw.Run(profile, g, drawSelected);
            });

            return lockedBitmap;
        }
    }
}
