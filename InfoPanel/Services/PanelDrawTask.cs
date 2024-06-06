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
                    if (profile.Active 
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
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(ColorTranslator.FromHtml(profile.BackgroundColor));
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;

                    DisplayItem? selectedItem = SharedModel.Instance.SelectedItem;
                    List<Rectangle> selectedRectangles = new List<Rectangle>();

                    foreach (var displayItem in SharedModel.Instance.GetProfileDisplayItemsCopy(profile))
                    {
                        if (displayItem.Hidden) continue;

                        switch (displayItem)
                        {
                            case TextDisplayItem textDisplayItem:
                                {
                                    (var text, var color) = textDisplayItem.EvaluateTextAndColor();
                                    
                                    FontStyle fontStyle =
                                        (textDisplayItem.Bold ? FontStyle.Bold : FontStyle.Regular) |
                                        (textDisplayItem.Italic ? FontStyle.Italic : FontStyle.Regular) |
                                        (textDisplayItem.Underline ? FontStyle.Underline : FontStyle.Regular) |
                                        (textDisplayItem.Strikeout ? FontStyle.Strikeout : FontStyle.Regular);

                                    using Font font = new Font(textDisplayItem.Font, textDisplayItem.FontSize, fontStyle);
                                    using Brush brush = new SolidBrush(ColorTranslator.FromHtml(color));
                                    StringFormat format = new StringFormat();
                                    if (textDisplayItem.RightAlign)
                                    {
                                        format.Alignment = StringAlignment.Far;
                                    }
                                    else
                                    {
                                        format.Alignment = StringAlignment.Near;
                                    }

                                    g.DrawString(text, font, brush, new PointF(textDisplayItem.X, textDisplayItem.Y), format);

                                    if (displayItem.Selected)
                                    {
                                        var textSize = g.MeasureString(text, font);
                                        if (textDisplayItem.RightAlign)
                                        {
                                            selectedRectangles.Add(new Rectangle((int)(textDisplayItem.X - textSize.Width), textDisplayItem.Y - 2, (int)textSize.Width, (int)(textSize.Height - 4)));
                                        }
                                        else
                                        {
                                            selectedRectangles.Add(new Rectangle(textDisplayItem.X, textDisplayItem.Y - 2, (int)textSize.Width, (int)(textSize.Height - 4)));
                                        }
                                    }

                                    break;
                                }
                            case ImageDisplayItem imageDisplayItem:
                                if (imageDisplayItem.CalculatedPath != null)
                                {
                                    var cachedImage = Cache.GetLocalImage(imageDisplayItem.CalculatedPath);
                                    var frame = cachedImage?.GetCurrentTimeFrame() ?? 0;

                                    cachedImage?.Access(image =>
                                    {
                                        if (frame > 0 && frame < cachedImage.Frames)
                                        {
                                            FrameDimension dimension = new FrameDimension(image.FrameDimensionsList[0]);
                                            image.SelectActiveFrame(dimension, frame);
                                        }

                                        var width = image.Width;
                                        var height = image.Height;

                                        if(imageDisplayItem.Scale > 0)
                                        {
                                            width = (int)(width * imageDisplayItem.Scale / 100.0f);
                                            height = (int)(height * imageDisplayItem.Scale / 100.0f);
                                        }

                                        g.DrawImage(image, new Rectangle(imageDisplayItem.X, imageDisplayItem.Y, width, height));

                                        if (imageDisplayItem.Layer)
                                        {
                                            using (var brush = new SolidBrush(ColorTranslator.FromHtml(imageDisplayItem.LayerColor)))
                                            {
                                                g.FillRectangle(brush, imageDisplayItem.X, imageDisplayItem.Y, width, height);
                                            }
                                        }

                                        if (displayItem.Selected)
                                        {
                                            selectedRectangles.Add(new Rectangle(imageDisplayItem.X + 2, imageDisplayItem.Y + 2, width - 4, height - 4));
                                        }
                                    });
                                }
                                break;
                            case GaugeDisplayItem gaugeDisplayItem:
                                {
                                    var imageDisplayItem = gaugeDisplayItem.EvaluateImage(1.0 / frameRateLimit);

                                    if (imageDisplayItem?.CalculatedPath != null)
                                    {
                                        var cachedImage = Cache.GetLocalImage(imageDisplayItem.CalculatedPath);
                                        var frame = cachedImage?.GetCurrentTimeFrame() ?? 0;

                                        cachedImage?.Access(image =>
                                        {
                                            if (frame > 0 && frame < cachedImage.Frames)
                                            {
                                                FrameDimension dimension = new FrameDimension(image.FrameDimensionsList[0]);
                                                image.SelectActiveFrame(dimension, frame);
                                            }

                                            int width = (int)(image.Width * imageDisplayItem.Scale / 100.0f);
                                            int height = (int)(image.Height * imageDisplayItem.Scale / 100.0f);

                                            g.DrawImage(image, new Rectangle(gaugeDisplayItem.X, gaugeDisplayItem.Y, width, height));

                                            if (displayItem.Selected)
                                            {
                                                selectedRectangles.Add(new Rectangle(gaugeDisplayItem.X + 2, gaugeDisplayItem.Y + 2, width - 4, height - 4));
                                            }
                                        });
                                    }
                                    break;
                                }
                            case ChartDisplayItem chartDisplayItem:

                                var chartBitmap = GraphDrawTask.Instance.GetBitmap(chartDisplayItem.Guid);

                                if (chartBitmap != null)
                                {
                                    chartBitmap.Access(chartBitmap =>
                                    {
                                        g.DrawImage(chartBitmap, chartDisplayItem.X, chartDisplayItem.Y);
                                    });

                                    if (displayItem.Selected)
                                    {
                                        selectedRectangles.Add(new Rectangle(chartDisplayItem.X - 2, chartDisplayItem.Y - 2, chartDisplayItem.Width + 4, chartDisplayItem.Height + 4));
                                    }
                                }
                                break;
                        }
                    }

                    if (drawSelected && SharedModel.Instance.SelectedProfile == profile && selectedRectangles.Any())
                    {
                        if (currentFps >= 0 && currentFps < frameRateLimit / 2)
                        {
                            using var pen = new Pen(Color.FromArgb(255, 0, 255, 0), 2);
                            foreach (var rectangle in selectedRectangles)
                            {
                                g.DrawRectangle(pen, rectangle);
                            }
                        }
                    }
                }
            });

            return lockedBitmap;
        }
    }
}
