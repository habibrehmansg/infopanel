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

        public static void Run(ChartDisplayItem chartDisplayItem, MyGraphics g, bool preview = false)
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
                g.Clear(SKColors.Transparent);

                var frameRect = new SKRect(0, 0, chartDisplayItem.Width, chartDisplayItem.Height);

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
                            g.FillRectangle(chartDisplayItem.BackgroundColor, (int)frameRect.Left, (int)frameRect.Top, (int)frameRect.Width, (int)frameRect.Height);
                        }

                        switch (graphDisplayItem.Type)
                        {
                            case GraphDisplayItem.GraphType.LINE:
                                {
                                    var size = (int)frameRect.Width / graphDisplayItem.Step;

                                    if (size * graphDisplayItem.Step != (int)frameRect.Width)
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

                                    using var path = new SKPath();
                                    
                                    // Start point for fill area
                                    path.MoveTo((int)frameRect.Left + graphDisplayItem.Width + graphDisplayItem.Thickness, (int)frameRect.Top + graphDisplayItem.Height + graphDisplayItem.Thickness);

                                    float lastX = 0;
                                    float lastY = 0;

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

                                        lastX = (int)frameRect.Left + (int)frameRect.Width - (i * graphDisplayItem.Step);
                                        lastY = (int)frameRect.Top + (int)(frameRect.Height - (value + (graphDisplayItem.Thickness / 2.0)));

                                        path.LineTo(lastX, lastY);
                                    }

                                    // End point for fill area
                                    path.LineTo(lastX - graphDisplayItem.Thickness, (int)frameRect.Top + graphDisplayItem.Height + graphDisplayItem.Thickness);

                                    if (graphDisplayItem.Fill)
                                    {
                                        g.FillPath(path, SKColor.Parse(graphDisplayItem.FillColor));
                                    }

                                    g.DrawPath(path, SKColor.Parse(graphDisplayItem.Color), graphDisplayItem.Thickness);

                                    break;
                                }
                            case GraphDisplayItem.GraphType.HISTOGRAM:
                                {
                                    var penSize = 1;
                                    var size = (int)frameRect.Width / (graphDisplayItem.Thickness + graphDisplayItem.Step + penSize * 2);

                                    if (size * graphDisplayItem.Step != (int)frameRect.Width)
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
                                    var refRect = new SKRect(
                                        frameRect.Right - graphDisplayItem.Thickness - penSize * 2,
                                        frameRect.Bottom - penSize * 2,
                                        frameRect.Right - penSize * 2,
                                        frameRect.Bottom - penSize * 2);

                                    var maxHeight = frameRect.Height - 3; // Precalculate the drawable height range
                                    var offset = graphDisplayItem.Thickness + graphDisplayItem.Step + penSize * 2; // Precalculate horizontal offset

                                    for (int i = 0; i < size; i++)
                                    {
                                        // Normalize and scale the value
                                        var value = Math.Clamp(values[i] - minValue, 0, maxValue) / (maxValue - minValue) * maxHeight;
                                        var normalizedHeight = (int)Math.Round(value, 0, MidpointRounding.AwayFromZero);

                                        // Update refRect properties for the current rectangle
                                        refRect = new SKRect(
                                            refRect.Left,
                                            frameRect.Bottom - normalizedHeight - penSize * 2,
                                            refRect.Right,
                                            frameRect.Bottom - penSize * 2 + normalizedHeight + penSize);

                                        // Draw the rectangle (filled and outlined)
                                        if (graphDisplayItem.Fill)
                                        {
                                            g.FillRectangle(graphDisplayItem.FillColor, (int)refRect.Left, (int)refRect.Top, (int)refRect.Width, (int)refRect.Height);
                                        }

                                        g.DrawRectangle(graphDisplayItem.Color, penSize, (int)refRect.Left, (int)refRect.Top, (int)refRect.Width, (int)refRect.Height);

                                        // Move refRect horizontally for the next rectangle
                                        refRect = new SKRect(
                                            refRect.Left - offset,
                                            refRect.Top,
                                            refRect.Right - offset,
                                            refRect.Bottom);
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
                            value = preview ? value : InterpolateWithCycles(lastValue, value, ConfigModel.Instance.Settings.TargetFrameRate * 3);
                            GraphDataSmoothCache.Set(chartDisplayItem.Guid, value, TimeSpan.FromSeconds(5));

                            // Create SKPath for usage rectangle
                            using SKPath usagePath = new();
                            if (frameRect.Height > frameRect.Width)
                            {
                                // Vertical bar - bottom draw
                                usagePath.AddRoundRect(new SKRoundRect(new SKRect(
                                    frameRect.Left,
                                    frameRect.Top + frameRect.Height - (float)value,
                                    frameRect.Left + frameRect.Width,
                                    frameRect.Top + frameRect.Height
                                ), barDisplayItem.CornerRadius));
                            }
                            else
                            {
                                // Horizontal bar
                                usagePath.AddRoundRect(new SKRoundRect(new SKRect(
                                    frameRect.Left,
                                    frameRect.Top,
                                    frameRect.Left + (float)value,
                                    frameRect.Top + frameRect.Height
                                ), barDisplayItem.CornerRadius));
                            }

                            // Draw background if enabled
                            if (chartDisplayItem.Background && SKColor.TryParse(barDisplayItem.BackgroundColor, out var backgroundColor))
                            {
                                using var bgPath = new SKPath();
                                bgPath.AddRoundRect(new SKRoundRect(new SKRect(frameRect.Left, frameRect.Top, frameRect.Left + frameRect.Width, frameRect.Top + frameRect.Height), barDisplayItem.CornerRadius));
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
                                framePath.AddRoundRect(new SKRoundRect(new SKRect(frameRect.Left, frameRect.Top, frameRect.Left + frameRect.Width, frameRect.Top + frameRect.Height), barDisplayItem.CornerRadius));

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
                            value = preview ? value : InterpolateWithCycles(lastValue, value, ConfigModel.Instance.Settings.TargetFrameRate * 3);
                            GraphDataSmoothCache.Set(chartDisplayItem.Guid, value, TimeSpan.FromSeconds(5));

                            var offset = 1;
                            g.FillDonut((int)frameRect.Left + offset, (int)frameRect.Top + offset, ((int)frameRect.Width / 2) - offset, donutDisplayItem.Thickness,
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
