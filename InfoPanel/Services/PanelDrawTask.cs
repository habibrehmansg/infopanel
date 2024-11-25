using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class PanelDrawTask
    {
        private static volatile PanelDrawTask? _instance;
        private static readonly object _lock = new object();

        private CancellationTokenSource _cts;
        private Task _task;

        private static ConcurrentDictionary<Guid, LockedBitmap> BitmapCache = new ConcurrentDictionary<Guid, LockedBitmap>();

        private PanelDrawTask() { }

        public static PanelDrawTask Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new PanelDrawTask();
                    }
                }
                return _instance;
            }
        }

        public bool IsRunning()
        {
            return _cts != null && !_cts.IsCancellationRequested;
        }

        public void Start()
        {
            if (_task != null && !_task.IsCompleted) return;
            _cts = new CancellationTokenSource();
            _task = Task.Factory.StartNew(() => DoWork(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        private void DoWork(CancellationToken token)
        {
            int currentFps = 0;
            long currentFrameTime = 0;

            var watch = new Stopwatch();
            watch.Start();

            while (!token.IsCancellationRequested)
            {
                var frameRateLimit = ConfigModel.Instance.Settings.TargetFrameRate;
                var optimalFrameTime = 1000.0 / frameRateLimit; // Target frame time in ms

                // Start timing the frame
                var frameStart = watch.ElapsedMilliseconds;

                var profiles = ConfigModel.Instance.GetProfilesCopy();

                profiles.ForEach(profile =>
                {
                    if ((profile.Active && profile.CompatMode)
                    || (ConfigModel.Instance.Settings.BeadaPanel && ConfigModel.Instance.Settings.BeadaPanelProfile == profile.Guid)
                    || ConfigModel.Instance.Settings.TuringPanelA && ConfigModel.Instance.Settings.TuringPanelAProfile == profile.Guid
                    || ConfigModel.Instance.Settings.TuringPanelC && ConfigModel.Instance.Settings.TuringPanelCProfile == profile.Guid)
                    {
                        var lockedBitmap = Render(profile, currentFps, frameRateLimit);

                        lockedBitmap.Access(bitmap =>
                        {
                            SharedModel.Instance.SetPanelBitmap(profile, bitmap);
                        });
                    }
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
                if (delay > 0) Thread.Sleep((int)delay);

                //Trace.WriteLine($"Draw Execution Time: {frameTime} ms");
            }
        }

        public static LockedBitmap Render(Profile profile, int currentFps, int frameRateLimit, bool drawSelected = true)
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
