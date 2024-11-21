using InfoPanel.Drawing;
using InfoPanel.Models;
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

        private async void DoWork(CancellationToken token)
        {
            int frameRateLimit = ConfigModel.Instance.Settings.TargetFrameRate;
            int currentFps = 0;
            long currentFrameTime = 0;

            DateTime lastFpsUpdate = DateTime.UtcNow;

            var watch = new Stopwatch();

            while (!token.IsCancellationRequested)
            {
                watch.Start();
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

                watch.Stop();

                currentFps++;
                currentFrameTime += watch.ElapsedMilliseconds;
                
                if (lastFpsUpdate.Second != DateTime.Now.Second)
                {
                    frameRateLimit = ConfigModel.Instance.Settings.TargetFrameRate;
                    SharedModel.Instance.CurrentFrameRate = currentFps;
                    SharedModel.Instance.CurrentFrameTime = currentFrameTime / currentFps;

                    currentFps = 0;
                    currentFrameTime = 0;
                    lastFpsUpdate = DateTime.Now;
                }

                var optimalFrameTime = 1000 / frameRateLimit;
                var delay = optimalFrameTime - watch.ElapsedMilliseconds;

                if (delay > 0)
                {
                    Thread.Sleep((int)delay);
                }

                watch.Reset();

                // Trace.WriteLine($"Draw Execution Time: {watch.ElapsedMilliseconds} ms");
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

                if(profile.OverrideDpi)
                {
                    bitmap.SetResolution(96, 96);
                }

                lockedBitmap = new LockedBitmap(bitmap, profile.OverrideDpi);
                BitmapCache[profile.Guid] = lockedBitmap;
            }

            lockedBitmap.Access(bitmap =>
            {
                
                using (var g = CompatGraphics.FromImage(bitmap) as MyGraphics)
                {
                    PanelDraw.Run(profile, g, drawSelected);
                }
            });

            return lockedBitmap;
        }
    }
}
