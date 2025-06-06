using InfoPanel.Models;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;

namespace InfoPanel.Drawing
{
    internal class GraphDraw
    {
        private static readonly IMemoryCache GraphDataSmoothCache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(5)
        });
        private static readonly ConcurrentDictionary<(UInt32, UInt32, UInt32), Queue<double>> GraphDataCache = [];
        private static readonly ConcurrentDictionary<string, Queue<double>> GraphDataCache2 = [];
        private static readonly ConcurrentDictionary<string, Queue<double>> GraphDataCache3 = [];
        private static readonly Stopwatch Stopwatch = new();

        static GraphDraw()
        {
            Stopwatch.Start();
        }

        private static Queue<double> GetGraphDataQueue(UInt32 id, UInt32 instance, UInt32 entryId)
        {
            var key = (id, instance, entryId);

            GraphDataCache.TryGetValue(key, out Queue<double>? result);

            if (result == null)
            {
                result = new Queue<double>();
                GraphDataCache.TryAdd(key, result);
            }

            return result;
        }

        private static Queue<double> GetGraphDataQueue(string libreSensorId)
        {
            var key = libreSensorId;

            GraphDataCache2.TryGetValue(key, out Queue<double>? result);

            if (result == null)
            {
                result = new Queue<double>();
                GraphDataCache2.TryAdd(key, result);
            }

            return result;
        }

        private static Queue<double> GetGraphPluginDataQueue(string pluginSensorId)
        {
            var key = pluginSensorId;

            GraphDataCache3.TryGetValue(key, out Queue<double>? result);

            if (result == null)
            {
                result = new Queue<double>();
                GraphDataCache3.TryAdd(key, result);
            }

            return result;
        }

        public static void Run(ChartDisplayItem chartDisplayItem, MyGraphics g)
        {
            var elapsedMilliseconds = Stopwatch.ElapsedMilliseconds;

            if (elapsedMilliseconds > ConfigModel.Instance.Settings.TargetGraphUpdateRate)
            {
                foreach (var key in GraphDataCache.Keys)
                {
                    GraphDataCache.TryGetValue(key, out Queue<double>? queue);
                    if (queue != null)
                    {
                        HWHash.SENSORHASH.TryGetValue(key, out HWHash.HWINFO_HASH value);

                        lock (queue)
                        {
                            queue.Enqueue(value.ValueNow);

                            if (queue.Count > 4096)
                            {
                                queue.Dequeue();
                            }
                        }
                    }
                }

                foreach (var key in GraphDataCache2.Keys)
                {
                    GraphDataCache2.TryGetValue(key, out Queue<double>? queue);
                    if (queue != null)
                    {
                        LibreMonitor.SENSORHASH.TryGetValue(key, out ISensor? value);
                        lock (queue)
                        {
                            queue.Enqueue(value?.Value ?? 0);

                            if (queue.Count > 4096)
                            {
                                queue.Dequeue();
                            }
                        }
                    }
                }

                foreach (var key in GraphDataCache3.Keys)
                {
                    GraphDataCache3.TryGetValue(key, out Queue<double>? queue);
                    if (queue != null)
                    {
                        if(PluginMonitor.SENSORHASH.TryGetValue(key, out PluginMonitor.PluginReading value) && value.Data is PluginSensor sensor)
                        {
                            lock (queue)
                            {
                                queue.Enqueue(sensor.Value);

                                if (queue.Count > 4096)
                                {
                                    queue.Dequeue();
                                }
                            }
                        }
                       
                    }
                }

                Stopwatch.Restart();
            }

            {
                g.Clear(Color.Transparent);

                var frameRect = new Rectangle(0, 0, chartDisplayItem.Width, chartDisplayItem.Height);

                Queue<double> queue;

                if (chartDisplayItem.SensorType == Enums.SensorType.Libre)
                {
                    queue = GetGraphDataQueue(chartDisplayItem.LibreSensorId);
                }
                else if (chartDisplayItem.SensorType == Enums.SensorType.Plugin)
                {
                    queue = GetGraphPluginDataQueue(chartDisplayItem.PluginSensorId);
                }
                else
                {
                    queue = GetGraphDataQueue(chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId);
                }

                if (queue.Count == 0)
                {
                    return;
                }

                double[] tempValues;

                lock (queue)
                {
                    tempValues = [.. queue];
                }

                double minValue = chartDisplayItem.MinValue;
                double maxValue = chartDisplayItem.MaxValue;

                switch (chartDisplayItem)
                {
                    case GraphDisplayItem graphDisplayItem:

                        if (chartDisplayItem.Background)
                        {
                            g.FillRectangle(chartDisplayItem.BackgroundColor, frameRect.X, frameRect.Y, frameRect.Width, frameRect.Height);
                        }

                        switch (graphDisplayItem.Type)
                        {
                            case GraphDisplayItem.GraphType.LINE:
                                {
                                    var size = frameRect.Width / graphDisplayItem.Step;

                                    if (size * graphDisplayItem.Step != frameRect.Width)
                                    {
                                        size += 2;
                                    }
                                    else
                                    {
                                        size += 1;
                                    }

                                    size = Math.Min(size, tempValues.Length);

                                    if (size == 0)
                                    {
                                        break;
                                    }

                                    var values = tempValues[(tempValues.Length - size)..];

                                    if (chartDisplayItem.AutoValue)
                                    {
                                        if (values.Length > 1 && values.Min() != values.Max())
                                        {
                                            minValue = values.Min();
                                            maxValue = values.Max();
                                        }
                                    }

                                    MyPoint[] points = new MyPoint[size + 2];
                                    points[0] = new MyPoint(frameRect.X + graphDisplayItem.Width + graphDisplayItem.Thickness, frameRect.Y + graphDisplayItem.Height + graphDisplayItem.Thickness);

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

                                        value = value * (frameRect.Height - graphDisplayItem.Thickness);
                                        value = Math.Round(value, 0, MidpointRounding.AwayFromZero);

                                        var newPoint = new MyPoint(frameRect.X + frameRect.Width - (i * graphDisplayItem.Step), frameRect.Y + (int)(frameRect.Height - (value + (graphDisplayItem.Thickness / 2.0))));

                                        points[i + 1] = newPoint;
                                    }

                                    points[^1] = new MyPoint(points[^2].X - graphDisplayItem.Thickness, frameRect.Y + graphDisplayItem.Height + graphDisplayItem.Thickness);

                                    if (graphDisplayItem.Fill)
                                    {
                                        g.FillPath(points, graphDisplayItem.FillColor);
                                    }

                                    //for (int i = 0; i < frameRect.Height / 5; i++)
                                    //{
                                    //    g.DrawLine(0, i * 5, frameRect.Width, i * 5, graphDisplayItem.FrameColor, 0.5f);
                                    //}

                                    //for (int i = 0; i < frameRect.Width / 5; i++)
                                    //{
                                    //    g.DrawLine(i * 5, 0, i * 5, frameRect.Height, graphDisplayItem.FrameColor, 0.5f);
                                    //}

                                    g.DrawPath(points, graphDisplayItem.Color, graphDisplayItem.Thickness);

                                    break;
                                }
                            case GraphDisplayItem.GraphType.HISTOGRAM:
                                {
                                    var penSize = 1;
                                    var size = frameRect.Width / (graphDisplayItem.Thickness + graphDisplayItem.Step + penSize * 2);

                                    if (size * graphDisplayItem.Step != frameRect.Width)
                                    {
                                        size += 1;
                                    }

                                    size = Math.Min(size, tempValues.Length);

                                    if (size == 0)
                                    {
                                        break;
                                    }

                                    var values = tempValues[(tempValues.Length - size)..];

                                    if (chartDisplayItem.AutoValue)
                                    {
                                        if (values.Length > 1 && values.Min() != values.Max())
                                        {
                                            minValue = values.Min();
                                            maxValue = values.Max();
                                        }
                                    }

                                    // Initialize refRect to start at the right edge of frameRect
                                    var refRect = new Rectangle(
                                        frameRect.Right - graphDisplayItem.Thickness - penSize * 2,
                                        frameRect.Bottom - penSize * 2,
                                        graphDisplayItem.Thickness,
                                        0);

                                    var maxHeight = frameRect.Height - 3; // Precalculate the drawable height range
                                    var offset = graphDisplayItem.Thickness + graphDisplayItem.Step + penSize * 2; // Precalculate horizontal offset

                                    for (int i = 0; i < size; i++)
                                    {
                                        // Normalize and scale the value
                                        var value = Math.Clamp(values[i] - minValue, 0, maxValue) / (maxValue - minValue) * maxHeight;
                                        var normalizedHeight = (int)Math.Round(value, 0, MidpointRounding.AwayFromZero);

                                        // Update refRect properties for the current rectangle
                                        refRect.Y = frameRect.Bottom - normalizedHeight - penSize * 2;
                                        refRect.Height = normalizedHeight + penSize;

                                        // Draw the rectangle (filled and outlined)
                                        if (graphDisplayItem.Fill)
                                        {
                                            g.FillRectangle(graphDisplayItem.FillColor, refRect.X, refRect.Y, refRect.Width, refRect.Height);
                                        }

                                        g.DrawRectangle(graphDisplayItem.Color, penSize, refRect.X, refRect.Y, refRect.Width, refRect.Height);

                                        // Move refRect horizontally for the next rectangle
                                        refRect.X -= offset;
                                    }

                                    break;
                                }
                        }
                        break;

                    case BarDisplayItem barDisplayItem:
                        {
                            if (chartDisplayItem.AutoValue)
                            {
                                if (tempValues.Length > 1 && tempValues.Min() != tempValues.Max())
                                {
                                    minValue = tempValues.Min();
                                    maxValue = tempValues.Max();
                                }
                            }

                            var value = 0.0;
                            var sensorReading = barDisplayItem.GetValue();

                            if (sensorReading.HasValue)
                            {
                                value = sensorReading.Value.ValueNow;
                            }

                            value = (value - minValue) / (maxValue - minValue);
                            value = Math.Clamp(value, 0, 1);
                            value = value * Math.Max(frameRect.Width, frameRect.Height);
                            value = Math.Round(value, 0, MidpointRounding.AwayFromZero);

                            GraphDataSmoothCache.TryGetValue(chartDisplayItem.Guid, out double lastValue);
                            value = InterpolateWithCycles(lastValue, value, (g is AcceleratedGraphics) ? 180 : ConfigModel.Instance.Settings.TargetFrameRate * 3);
                            GraphDataSmoothCache.Set(chartDisplayItem.Guid, value, TimeSpan.FromSeconds(5));

                            // Create SKPath for usage rectangle
                            using SKPath usagePath = new();
                            if (frameRect.Height > frameRect.Width)
                            {
                                // Vertical bar - bottom draw
                                usagePath.AddRoundRect(new SKRoundRect(new SKRect(
                                    frameRect.X,
                                    frameRect.Y + frameRect.Height - (float)value,
                                    frameRect.X + frameRect.Width,
                                    frameRect.Y + frameRect.Height
                                ), barDisplayItem.CornerRadius));
                            }
                            else
                            {
                                // Horizontal bar
                                usagePath.AddRoundRect(new SKRoundRect(new SKRect(
                                    frameRect.X,
                                    frameRect.Y,
                                    frameRect.X + (float)value,
                                    frameRect.Y + frameRect.Height
                                ), barDisplayItem.CornerRadius));
                            }

                            // Draw background if enabled
                            if (chartDisplayItem.Background && SKColor.TryParse(barDisplayItem.BackgroundColor, out var backgroundColor))
                            {
                                using var bgPath = new SKPath();
                                bgPath.AddRoundRect(new SKRoundRect(new SKRect(frameRect.X, frameRect.Y, frameRect.X + frameRect.Width, frameRect.Y + frameRect.Height), barDisplayItem.CornerRadius));
                                g.FillPath(bgPath, backgroundColor);
                            }

                            // Draw the bar if it has size
                            if (value > 0 && SKColor.TryParse(barDisplayItem.Color, out var color))
                            {
                                if (barDisplayItem.Gradient && SKColor.TryParse(barDisplayItem.GradientColor, out var gradientColor))
                                {
                                    g.FillPath(usagePath, color, color, gradientColor);
                                }
                                else
                                {
                                    g.FillPath(usagePath, color);
                                }
                            }

                            if (barDisplayItem.Frame && SKColor.TryParse(barDisplayItem.FrameColor, out var frameColor))
                            {
                                using var framePath = new SKPath();
                                framePath.AddRoundRect(new SKRoundRect(new SKRect(frameRect.X, frameRect.Y, frameRect.X + frameRect.Width, frameRect.Y + frameRect.Height), barDisplayItem.CornerRadius));

                                g.DrawPath(framePath, frameColor, 1);
                            }

                            break;
                        }
                    case DonutDisplayItem donutDisplayItem:
                        {
                            if (donutDisplayItem.AutoValue)
                            {
                                if (tempValues.Length > 1 && tempValues.Min() != tempValues.Max())
                                {
                                    minValue = tempValues.Min();
                                    maxValue = tempValues.Max();
                                }
                            }

                            var value = tempValues.LastOrDefault(0.0);
                            value = (value - minValue) / (maxValue - minValue);
                            value = Math.Clamp(value, 0, 1);
                            value = value * 100;

                            GraphDataSmoothCache.TryGetValue(chartDisplayItem.Guid, out double lastValue);
                            value = InterpolateWithCycles(lastValue, value, (g is AcceleratedGraphics) ? 180 : ConfigModel.Instance.Settings.TargetFrameRate * 3);
                            GraphDataSmoothCache.Set(chartDisplayItem.Guid, value, TimeSpan.FromSeconds(5));

                            var offset = 1;
                            g.FillDonut(frameRect.X + offset, frameRect.Y + offset, (frameRect.Width / 2) - offset, donutDisplayItem.Thickness,
                                 donutDisplayItem.Rotation, (int)value, donutDisplayItem.Span, donutDisplayItem.Color,
                                donutDisplayItem.Background ? donutDisplayItem.BackgroundColor : "#00000000",
                                donutDisplayItem.Frame ? 1 : 0, donutDisplayItem.FrameColor);

                            break;
                        }
                }

                if (chartDisplayItem is not DonutDisplayItem && chartDisplayItem is not BarDisplayItem && chartDisplayItem.Frame)
                {
                    g.DrawRectangle(chartDisplayItem.FrameColor, 1, 0, 0, chartDisplayItem.Width, chartDisplayItem.Height);
                }
            }
        }

        public static double Interpolate(double A, double B, double t)
        {
            // Ensure t is clamped between 0 and 1
            t = Math.Clamp(t, 0.0, 1.0);

            return A + (B - A) * t;
        }

        public static double InterpolateWithCycles(double A, double B, int cycles)
        {
            double tolerance = 0.001;
            double initialDifference = Math.Abs(B - A);
            double decayFactor = Math.Pow(tolerance / initialDifference, 1.0 / cycles);
            double t = 1 - decayFactor;

            t = Math.Clamp(t, 0.0, 1.0);

            return A + (B - A) * t;
        }
    }
}
