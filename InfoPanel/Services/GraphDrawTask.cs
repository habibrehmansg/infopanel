using InfoPanel.Models;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel
{
    public sealed class GraphDrawTask
    {
        private static volatile GraphDrawTask? _instance;
        private static readonly object _lock = new object();

        private CancellationTokenSource _cts;
        private Task _task;

        private Dictionary<(UInt32, UInt32, UInt32), Queue<Double>> ValuesCache = new Dictionary<(UInt32, UInt32, UInt32), Queue<Double>>();
        private ConcurrentDictionary<Guid, LockedImage> BitmapCache = new ConcurrentDictionary<Guid, LockedImage>();

        private GraphDrawTask()
        { }

        public static GraphDrawTask Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new GraphDrawTask();
                    }
                }
                return _instance;
            }
        }

        public LockedImage? GetBitmap(Guid guid)
        {
            BitmapCache.TryGetValue(guid, out var bitmap);
            return bitmap;
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
            var watch = new Stopwatch();

            while (!token.IsCancellationRequested)
            {
                watch.Start();

                var processedList = new List<(UInt32, UInt32, UInt32)>();

                var profiles = ConfigModel.Instance.GetProfilesCopy();
                profiles.ForEach(profile =>
                {
                    if (ConfigModel.Instance.Settings.WebServer
                   || profile.Active
                   || (ConfigModel.Instance.Settings.BeadaPanel && ConfigModel.Instance.Settings.BeadaPanelProfile == profile.Guid)
                   || ConfigModel.Instance.Settings.TuringPanelA && ConfigModel.Instance.Settings.TuringPanelAProfile == profile.Guid
                   || ConfigModel.Instance.Settings.TuringPanelC && ConfigModel.Instance.Settings.TuringPanelCProfile == profile.Guid)
                    {
                        foreach (var displayItem in SharedModel.Instance.GetProfileDisplayItemsCopy(profile))
                        {
                            if (displayItem is ChartDisplayItem chartDisplayItem)
                            {
                                if (displayItem.Hidden) { continue; }

                                if (HWHash.SENSORHASH.TryGetValue((chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId), out HWHash.HWINFO_HASH hash))
                                {
                                    Queue<Double> queue;

                                    if (!ValuesCache.ContainsKey((chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId)))
                                    {
                                        queue = new Queue<Double>();
                                        ValuesCache.Add((chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId), queue);
                                    }
                                    else
                                    {
                                        queue = ValuesCache[(chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId)];
                                    }

                                    if (!processedList.Contains((chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId)))
                                    {
                                        queue.Enqueue(hash.ValueNow);
                                        processedList.Add((chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId));
                                    }

                                    if (queue.Count > 1000)
                                    {
                                        queue.Dequeue();
                                    }

                                    if (!BitmapCache.TryGetValue(chartDisplayItem.Guid, out LockedImage? bitmap) || bitmap.Width != chartDisplayItem.Width || bitmap.Height != chartDisplayItem.Height)
                                    {
                                        bitmap?.Dispose();
                                        bitmap = new LockedImage(new Bitmap(chartDisplayItem.Width, chartDisplayItem.Height, PixelFormat.Format32bppArgb));
                                        BitmapCache[chartDisplayItem.Guid] = bitmap;
                                    }

                                    bitmap.Access(bitmap =>
                                    {
                                        using (Graphics g = Graphics.FromImage(bitmap))
                                        {
                                            g.Clear(Color.Transparent);

                                            var frameRect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);

                                            g.SmoothingMode = SmoothingMode.AntiAlias;

                                            var values = queue.ToArray();
                                            Array.Reverse(values);

                                            var minValue = chartDisplayItem.MinValue;
                                            var maxValue = chartDisplayItem.MaxValue;

                                            switch (chartDisplayItem)
                                            {
                                                case GraphDisplayItem graphDisplayItem:

                                                    if (chartDisplayItem.Background)
                                                    {
                                                        using (var brush = new SolidBrush(ColorTranslator.FromHtml(chartDisplayItem.BackgroundColor)))
                                                        {
                                                            g.FillRectangle(brush, frameRect);
                                                        }
                                                    }

                                                    switch (graphDisplayItem.Type)
                                                    {
                                                        case GraphDisplayItem.GraphType.LINE:
                                                            {
                                                                var size = Math.Min((frameRect.Width / graphDisplayItem.Step) + 1, values.Length);

                                                                if (chartDisplayItem.AutoValue)
                                                                {
                                                                    var valueSlice = values[0..Math.Min(size * 3, values.Length)];
                                                                    minValue = (int)valueSlice.Min();
                                                                    maxValue = (int)valueSlice.Max();
                                                                }

                                                                var points = new List<Point>();

                                                                for (int i = 0; i < size; i++)
                                                                {
                                                                    var value = Math.Max(values[i] - minValue, 0);
                                                                    value = Math.Min(value, maxValue);

                                                                    var scale = maxValue - minValue;
                                                                    if (scale <= 0)
                                                                    {
                                                                        value = 0;
                                                                    }
                                                                    else
                                                                    {
                                                                        value = value / (maxValue - minValue);
                                                                    }

                                                                    value = value * (frameRect.Height - graphDisplayItem.Thickness - 1);
                                                                    value = Math.Round(value, 0, MidpointRounding.AwayFromZero);

                                                                    var newPoint = new Point(frameRect.Width - (i * graphDisplayItem.Step) - graphDisplayItem.Step, (int)(frameRect.Height - (value + graphDisplayItem.Thickness)));

                                                                    if (newPoint.Y == frameRect.Height - 1)
                                                                    {
                                                                        newPoint.Y = newPoint.Y - 1;
                                                                    }

                                                                    points.Insert(0, newPoint);
                                                                }

                                                                if (points.Count > 0)
                                                                {
                                                                    using (var path = new GraphicsPath())
                                                                    {
                                                                        Point previousPoint = new Point(points.First().X, graphDisplayItem.Height);
                                                                        foreach (var point in points)
                                                                        {
                                                                            path.AddLine(previousPoint, point);
                                                                            previousPoint = point;
                                                                        }

                                                                        path.AddLine(previousPoint, new Point(graphDisplayItem.Width + graphDisplayItem.Thickness, previousPoint.Y));
                                                                        path.AddLine(new Point(graphDisplayItem.Width + graphDisplayItem.Thickness, previousPoint.Y), new Point(chartDisplayItem.Width, chartDisplayItem.Height));

                                                                        if (graphDisplayItem.Fill)
                                                                        {
                                                                            using (var brush = new SolidBrush(ColorTranslator.FromHtml(graphDisplayItem.FillColor)))
                                                                            {
                                                                                g.FillPath(brush, path);
                                                                            }
                                                                        }

                                                                        using (var pen = new Pen(ColorTranslator.FromHtml(graphDisplayItem.Color), graphDisplayItem.Thickness))
                                                                        {
                                                                            g.DrawPath(pen, path);
                                                                        }
                                                                    }
                                                                }

                                                                break;
                                                            }
                                                        case GraphDisplayItem.GraphType.HISTOGRAM:
                                                            {
                                                                var penSize = Math.Max(graphDisplayItem.Thickness / 4, 1);
                                                                var size = Math.Min((frameRect.Width / (graphDisplayItem.Thickness + graphDisplayItem.Step + penSize * 2)) + 1, values.Length);

                                                                if (chartDisplayItem.AutoValue)
                                                                {
                                                                    var valueSlice = values[0..Math.Min(size * 3, values.Length)];
                                                                    minValue = (int)valueSlice.Min();
                                                                    maxValue = (int)valueSlice.Max();
                                                                }

                                                                for (int i = 0; i < size; i++)
                                                                {
                                                                    var value = Math.Max(values[i] - minValue, 0);
                                                                    value = Math.Min(value, maxValue);
                                                                    value = value / (maxValue - minValue);
                                                                    value = value * (frameRect.Height - 2 - 1);
                                                                    value = Math.Round(value, 0, MidpointRounding.AwayFromZero);

                                                                    var chartRect = new Rectangle(frameRect.Width - (i * (graphDisplayItem.Thickness + graphDisplayItem.Step + penSize * 2)) - graphDisplayItem.Thickness - penSize, (int)(frameRect.Height - (value)), graphDisplayItem.Thickness, (int)value + penSize);

                                                                    if (graphDisplayItem.Fill)
                                                                    {
                                                                        using (var brush = new SolidBrush(ColorTranslator.FromHtml(graphDisplayItem.FillColor)))
                                                                        {
                                                                            g.FillRectangle(brush, chartRect);
                                                                        }
                                                                    }

                                                                    using (var pen = new Pen(ColorTranslator.FromHtml(graphDisplayItem.Color), graphDisplayItem.Thickness / 4))
                                                                    {
                                                                        g.DrawRectangle(pen, chartRect);
                                                                    }
                                                                }
                                                                break;
                                                            }
                                                    }
                                                    break;

                                                case BarDisplayItem barDisplayItem:
                                                    {
                                                        if (chartDisplayItem.AutoValue)
                                                        {
                                                            minValue = (int)values.Min();
                                                            maxValue = (int)values.Max();
                                                        }

                                                        var value = Math.Max(values.FirstOrDefault(0.0) - minValue, 0);
                                                        value = Math.Min(value, maxValue);
                                                        value = value / (maxValue - minValue);
                                                        value = value * Math.Max(frameRect.Width, frameRect.Height);
                                                        value = Math.Round(value, 0, MidpointRounding.AwayFromZero);

                                                        var usageRect = new Rectangle(frameRect.X, frameRect.Y, (int)value, frameRect.Height);
                                                        if (frameRect.Height > frameRect.Width)
                                                        {
                                                            //top draw
                                                            //usageRect = new Rectangle(frameRect.X, frameRect.Y, frameRect.Width, (int)value);
                                                            //bottom draw
                                                            usageRect = new Rectangle(frameRect.X, (int)(frameRect.Y + frameRect.Height - value), frameRect.Width, (int)value);
                                                        }

                                                        using (var brush = new SolidBrush(ColorTranslator.FromHtml(barDisplayItem.Color)))
                                                        {
                                                            g.FillRectangle(brush, usageRect);
                                                        }

                                                        if (barDisplayItem.Gradient)
                                                        {
                                                            if (usageRect.Width > 0 && usageRect.Height > 0)
                                                            {
                                                                using (var brush = new LinearGradientBrush(usageRect, ColorTranslator.FromHtml(barDisplayItem.Color), ColorTranslator.FromHtml(barDisplayItem.GradientColor), LinearGradientMode.Vertical))
                                                                {
                                                                    g.FillRectangle(brush, usageRect);
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            using (var brush = new SolidBrush(ColorTranslator.FromHtml(chartDisplayItem.Color)))
                                                            {
                                                                g.FillRectangle(brush, usageRect);
                                                            }
                                                        }

                                                        if (chartDisplayItem.Background)
                                                        {
                                                            var remainderRect = new Rectangle(frameRect.X + (int)value, frameRect.Y, frameRect.Width - (int)value, frameRect.Height);

                                                            if (frameRect.Height > frameRect.Width)
                                                            {
                                                                //top draw
                                                                remainderRect = new Rectangle(frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height - (int)value);
                                                                //bottom draw
                                                                // remainderRect = new Rectangle(frameRect.X, (int)(frameRect.Y + frameRect.Height - value), frameRect.Width, (int)value);
                                                            }

                                                            if (barDisplayItem.Gradient)
                                                            {
                                                                using (var brush = new LinearGradientBrush(frameRect, ColorTranslator.FromHtml(barDisplayItem.BackgroundColor), ColorTranslator.FromHtml(barDisplayItem.GradientColor), LinearGradientMode.Vertical))
                                                                {
                                                                    g.FillRectangle(brush, remainderRect);
                                                                }
                                                            }
                                                            else
                                                            {
                                                                using (var brush = new SolidBrush(ColorTranslator.FromHtml(chartDisplayItem.BackgroundColor)))
                                                                {
                                                                    g.FillRectangle(brush, remainderRect);
                                                                }
                                                            }
                                                        }

                                                        break;
                                                    }
                                            }

                                            if (chartDisplayItem.Frame)
                                            {
                                                using (var pen = new Pen(ColorTranslator.FromHtml(chartDisplayItem.FrameColor), 1))
                                                {
                                                    g.DrawRectangle(pen, new Rectangle(0, 0, bitmap.Width - 1, bitmap.Height - 1));
                                                }
                                            }
                                        }
                                    });
                                }
                            }
                        }
                    }
                });



                watch.Stop();
                //Trace.WriteLine($"Graph Execution Time: {watch.ElapsedMilliseconds} ms");
                int delay = (int)(ConfigModel.Instance.Settings.TargetGraphUpdateRate - watch.ElapsedMilliseconds);
                if (delay > 0)
                {
                    Task.Delay(delay).Wait();
                }
                watch.Reset();
            }
        }
    }
}

