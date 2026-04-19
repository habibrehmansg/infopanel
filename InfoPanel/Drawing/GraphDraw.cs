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
        private static readonly IMemoryCache AutoRangeCache = new MemoryCache(new MemoryCacheOptions()
        {
            ExpirationScanFrequency = TimeSpan.FromSeconds(5)
        });
        private static readonly ConcurrentDictionary<(int, UInt32, UInt32, UInt32), Queue<double>> GraphDataCache = [];
        private static readonly ConcurrentDictionary<string, Queue<double>> GraphDataCache2 = [];
        private static readonly ConcurrentDictionary<string, Queue<double>> GraphDataCache3 = [];
        private static readonly Stopwatch Stopwatch = new();

        // Expand the auto-scale range immediately on new extremes; contract it gradually so
        // the scale doesn't yo-yo when a transient spike falls out of the sample window.
        // 0.05 per frame ≈ 14 frames to halfway convergence at 40fps — responsive but stable.
        private const double AutoRangeContract = 0.05;
        private const double AutoRangeMinSpan = 1e-6;
        private static readonly TimeSpan AutoRangeTtl = TimeSpan.FromSeconds(30);

        private static (double min, double max) ResolveAutoRange(
            Guid id, double[] samples, double userMin, double userMax, bool autoValue)
        {
            if (!autoValue || samples.Length == 0)
                return (userMin, userMax);

            var sMin = samples[0];
            var sMax = samples[0];
            for (int i = 1; i < samples.Length; i++)
            {
                var v = samples[i];
                if (v < sMin) sMin = v;
                if (v > sMax) sMax = v;
            }

            if (AutoRangeCache.TryGetValue(id, out (double min, double max) prev))
            {
                var newMin = sMin < prev.min ? sMin : prev.min + (sMin - prev.min) * AutoRangeContract;
                var newMax = sMax > prev.max ? sMax : prev.max + (sMax - prev.max) * AutoRangeContract;

                if (newMax - newMin < AutoRangeMinSpan)
                {
                    var mid = 0.5 * (newMin + newMax);
                    newMin = mid - 0.5;
                    newMax = mid + 0.5;
                }

                AutoRangeCache.Set(id, (newMin, newMax), AutoRangeTtl);
                return (newMin, newMax);
            }

            // First frame: seed from samples, or fall back to user range if the window is flat.
            if (sMax - sMin < AutoRangeMinSpan)
            {
                AutoRangeCache.Set(id, (userMin, userMax), AutoRangeTtl);
                return (userMin, userMax);
            }

            AutoRangeCache.Set(id, (sMin, sMax), AutoRangeTtl);
            return (sMin, sMax);
        }

        static GraphDraw()
        {
            Stopwatch.Start();
        }

        private static Queue<double> GetGraphDataQueue(int remoteIndex, UInt32 id, UInt32 instance, UInt32 entryId)
        {
            var key = (remoteIndex, id, instance, entryId);

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

        public static void Run(ChartDisplayItem chartDisplayItem, SkiaGraphics g, bool preview = false)
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
                        if(PluginMonitor.SENSORHASH.TryGetValue(key, out PluginMonitor.PluginReading value) && value.Data is IPluginSensor sensor)
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
                    queue = GetGraphDataQueue(chartDisplayItem.HwInfoRemoteIndex, chartDisplayItem.Id, chartDisplayItem.Instance, chartDisplayItem.EntryId);
                }

                if (queue.Count == 0 && chartDisplayItem is not BarDisplayItem)
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
                        {
                            if (chartDisplayItem.Background)
                            {
                                g.FillRectangle(chartDisplayItem.BackgroundColor, (int)frameRect.Left, (int)frameRect.Top, (int)frameRect.Width, (int)frameRect.Height);
                            }

                            switch (graphDisplayItem.Type)
                            {
                                case GraphDisplayItem.GraphType.LINE:
                                    {
                                        var size = (int)frameRect.Width / Math.Max(graphDisplayItem.Step, 1);

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

                                        (minValue, maxValue) = ResolveAutoRange(
                                            chartDisplayItem.Guid, values, chartDisplayItem.MinValue, chartDisplayItem.MaxValue, chartDisplayItem.AutoValue);

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
                                        var size = (int)frameRect.Width / (graphDisplayItem.Thickness + Math.Max(graphDisplayItem.Step, 1) + penSize * 2);

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

                                        (minValue, maxValue) = ResolveAutoRange(
                                            chartDisplayItem.Guid, values, chartDisplayItem.MinValue, chartDisplayItem.MaxValue, chartDisplayItem.AutoValue);

                                        // Initialize refRect to start at the right edge of frameRect
                                        var refRect = new SKRect(
                                            frameRect.Right - graphDisplayItem.Thickness - penSize * 2,
                                            frameRect.Bottom - penSize * 2,
                                            frameRect.Right - penSize * 2,
                                            frameRect.Bottom - penSize * 2);

                                        var maxHeight = Math.Max(frameRect.Height - 3, 1); // Precalculate the drawable height range
                                        var offset = graphDisplayItem.Thickness + Math.Max(graphDisplayItem.Step, 1) + penSize * 2; // Precalculate horizontal offset

                                        for (int i = 0; i < size; i++)
                                        {
                                            // Normalize and scale the value
                                            var scale = maxValue - minValue;
                                            var value = scale <= 0 ? 0 : Math.Clamp(values[i] - minValue, 0, maxValue) / scale * maxHeight;
                                            var normalizedHeight = (int)Math.Round(value, 0, MidpointRounding.AwayFromZero);

                                            // Update refRect properties for the current rectangle
                                            refRect = new SKRect(
                                                refRect.Left,
                                                frameRect.Bottom - normalizedHeight - penSize,
                                                refRect.Right,
                                                frameRect.Bottom - penSize);

                                            // Draw the rectangle (filled and outlined)
                                            if (graphDisplayItem.Fill)
                                            {
                                                g.FillRectangle(graphDisplayItem.FillColor, (int)refRect.Left, (int)refRect.Top, (int)refRect.Width, (int)refRect.Height);
                                            }

                                            if (SKColor.TryParse(graphDisplayItem.Color, out var color))
                                            {
                                                g.DrawRectangle(color, penSize, (int)refRect.Left, (int)refRect.Top, (int)refRect.Width, (int)refRect.Height);
                                            }

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
                        }
                    case BarDisplayItem barDisplayItem:
                        {
                            (minValue, maxValue) = ResolveAutoRange(
                                chartDisplayItem.Guid, tempValues, chartDisplayItem.MinValue, chartDisplayItem.MaxValue, chartDisplayItem.AutoValue);

                            var value = 0.0;
                            var sensorReading = barDisplayItem.GetValue();

                            if (sensorReading.HasValue)
                            {
                                value = sensorReading.Value.ValueNow;
                            }

                            var scale = maxValue - minValue;
                            value = scale <= 0 ? 0 : (value - minValue) / scale;
                            value = Math.Clamp(value, 0, 1);
                            value = value * Math.Max(frameRect.Width, frameRect.Height);
                            value = Math.Round(value, 0, MidpointRounding.AwayFromZero);

                            GraphDataSmoothCache.TryGetValue(chartDisplayItem.Guid, out double lastValue);
                            value = preview ? value : InterpolateWithCycles(lastValue, value, ConfigModel.Instance.Settings.TargetFrameRate * 3);
                            GraphDataSmoothCache.Set(chartDisplayItem.Guid, value, TimeSpan.FromSeconds(5));

                            // Inset fill/background when the frame is drawn so the 1px AA stroke
                            // doesn't blend with the fill underneath (issue #81).
                            var innerLeft = frameRect.Left;
                            var innerTop = frameRect.Top;
                            var innerRight = frameRect.Left + frameRect.Width;
                            var innerBottom = frameRect.Top + frameRect.Height;
                            var innerRadius = (float)barDisplayItem.CornerRadius;
                            if (barDisplayItem.Frame)
                            {
                                innerLeft = Math.Min(innerLeft + 1, innerRight);
                                innerTop = Math.Min(innerTop + 1, innerBottom);
                                innerRight = Math.Max(innerRight - 1, innerLeft);
                                innerBottom = Math.Max(innerBottom - 1, innerTop);
                                innerRadius = Math.Max(0, innerRadius - 1);
                            }
                            var innerWidth = innerRight - innerLeft;
                            var innerHeight = innerBottom - innerTop;

                            // Create SKPath for usage rectangle
                            using SKPath usagePath = new();
                            if (frameRect.Height > frameRect.Width)
                            {
                                // Vertical bar - bottom draw
                                var fillHeight = Math.Min((float)value, innerHeight);
                                usagePath.AddRoundRect(new SKRoundRect(new SKRect(
                                    innerLeft,
                                    innerBottom - fillHeight,
                                    innerRight,
                                    innerBottom
                                ), innerRadius));
                            }
                            else
                            {
                                // Horizontal bar
                                var fillWidth = Math.Min((float)value, innerWidth);
                                usagePath.AddRoundRect(new SKRoundRect(new SKRect(
                                    innerLeft,
                                    innerTop,
                                    innerLeft + fillWidth,
                                    innerBottom
                                ), innerRadius));
                            }

                            // Draw background if enabled
                            if (chartDisplayItem.Background && SKColor.TryParse(barDisplayItem.BackgroundColor, out var backgroundColor))
                            {
                                using var bgPath = new SKPath();
                                bgPath.AddRoundRect(new SKRoundRect(new SKRect(innerLeft, innerTop, innerRight, innerBottom), innerRadius));
                                g.FillPath(bgPath, backgroundColor);
                            }

                            // Draw the bar if it has size
                            if (value > 0 && SKColor.TryParse(barDisplayItem.Color, out var barColor))
                            {
                                if (barDisplayItem.Gradient && SKColor.TryParse(barDisplayItem.GradientColor, out var gradientColor))
                                {
                                    g.FillPath(usagePath, barColor, barColor, gradientColor);
                                }
                                else
                                {
                                    g.FillPath(usagePath, barColor);
                                }
                            }

                            if (barDisplayItem.Frame && SKColor.TryParse(barDisplayItem.FrameColor, out var color))
                            {
                                // Offset by 0.5px so the 1px stroke lands on whole pixels instead of
                                // splitting across two columns at 50% opacity each.
                                using var framePath = new SKPath();
                                framePath.AddRoundRect(new SKRoundRect(new SKRect(
                                    frameRect.Left + 0.5f,
                                    frameRect.Top + 0.5f,
                                    frameRect.Left + frameRect.Width - 0.5f,
                                    frameRect.Top + frameRect.Height - 0.5f
                                ), Math.Max(0, barDisplayItem.CornerRadius - 0.5f)));

                                g.DrawPath(framePath, color, 1);
                            }

                            break;
                        }
                    case DonutDisplayItem donutDisplayItem:
                        {
                            (minValue, maxValue) = ResolveAutoRange(
                                chartDisplayItem.Guid, tempValues, chartDisplayItem.MinValue, chartDisplayItem.MaxValue, chartDisplayItem.AutoValue);

                            var value = tempValues.LastOrDefault(0.0);
                            var scale = maxValue - minValue;
                            value = scale <= 0 ? 0 : (value - minValue) / scale;
                            value = Math.Clamp(value, 0, 1);
                            value = value * 100;

                            GraphDataSmoothCache.TryGetValue(chartDisplayItem.Guid, out double lastValue);
                            value = preview ? value : InterpolateWithCycles(lastValue, value, ConfigModel.Instance.Settings.TargetFrameRate * 3);
                            GraphDataSmoothCache.Set(chartDisplayItem.Guid, value, TimeSpan.FromSeconds(5));

                            var offset = 1;
                            g.FillDonut((int)frameRect.Left + offset, (int)frameRect.Top + offset, ((int)frameRect.Width / 2) - offset, donutDisplayItem.Thickness,
                                 0, (int)value, donutDisplayItem.Span, donutDisplayItem.Color,
                                donutDisplayItem.Background ? donutDisplayItem.BackgroundColor : "#00000000",
                                donutDisplayItem.Frame ? 1 : 0, donutDisplayItem.FrameColor);

                            break;
                        }
                }

                if (chartDisplayItem is not DonutDisplayItem && chartDisplayItem is not BarDisplayItem && chartDisplayItem.Frame && SKColor.TryParse(chartDisplayItem.FrameColor, out var frameColor))
                {
                    // Offset by 0.5px so the 1px stroke lands on whole pixels instead of
                    // splitting across two columns at 50% opacity each.
                    using var framePath = new SKPath();
                    framePath.AddRect(new SKRect(0.5f, 0.5f, chartDisplayItem.Width - 0.5f, chartDisplayItem.Height - 0.5f));
                    g.DrawPath(framePath, frameColor, 1);
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
            if (cycles <= 0) return B;
            
            double tolerance = 0.001;
            double initialDifference = Math.Abs(B - A);
            
            if (initialDifference <= tolerance) return B;
            
            double decayFactor = Math.Pow(tolerance / initialDifference, 1.0 / cycles);
            double t = 1 - decayFactor;

            t = Math.Clamp(t, 0.0, 1.0);

            return A + (B - A) * t;
        }
    }
}
