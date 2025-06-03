using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;

namespace InfoPanel.Drawing
{
    readonly struct SelectedRectangle(SKRect rect, int rotation = 0)
    {
        public readonly SKRect Rect = rect;
        public readonly int Rotation = rotation;
    }

    internal class PanelDraw
    {
        private static readonly Stopwatch _selectionStopwatch = new();

        static PanelDraw()
        {
            _selectionStopwatch.Start();
        }

        public static void Run(Profile profile, MyGraphics g, bool drawSelected = true, double scale = 1, bool cache = true, string cacheHint = "default", FpsCounter? fpsCounter = null)
        {
            var stopwatch = Stopwatch.StartNew();

            List<SelectedRectangle> selectedRectangles = [];

            g.Clear(ColorTranslator.FromHtml(profile.BackgroundColor));

            foreach (var displayItem in SharedModel.Instance.GetProfileDisplayItemsCopy(profile))
            {
                if (displayItem.Hidden) continue;
                Draw(g, scale, cache, cacheHint, displayItem, selectedRectangles);
            }

            if (drawSelected && SharedModel.Instance.SelectedProfile == profile && selectedRectangles.Count != 0)
            {
                if (ConfigModel.Instance.Settings.ShowGridLines)
                {
                    var gridSpace = ConfigModel.Instance.Settings.GridLinesSpacing;
                    var gridColor = ConfigModel.Instance.Settings.GridLinesColor;

                    var verticalLines = profile.Width / gridSpace;
                    for (int i = 1; i < verticalLines; i++)
                    {
                        //draw vertical lines
                        g.DrawLine(i * gridSpace, 0, i * gridSpace, profile.Height, gridColor, 1);
                    }

                    var horizontalLines = profile.Height / gridSpace;
                    for (int j = 1; j < horizontalLines; j++)
                    {
                        //draw horizontal lines
                        g.DrawLine(0, j * gridSpace, profile.Width, j * gridSpace, gridColor, 1);
                    }
                }

                if (SKColor.TryParse(ConfigModel.Instance.Settings.SelectedItemColor, out var color))
                {
                    var elapsedMilliseconds = _selectionStopwatch.ElapsedMilliseconds;

                    // Define "on" and "off" durations
                    int onDuration = 600;  // Time the rectangle is visible
                    int offDuration = 400; // Time the rectangle is invisible
                    int cycleDuration = onDuration + offDuration; // Total cycle time

                    // Determine if we are in the "on" phase
                    if (elapsedMilliseconds % cycleDuration < onDuration) // "on" phase
                    {
                        foreach (var rectangle in selectedRectangles)
                        {
                            using var path = RectToPath(rectangle.Rect, rectangle.Rotation, profile.Width, profile.Height, 2);

                            if (path != null)
                            {
                                g.DrawPath(path, color, 2);
                            }
                        }
                    }
                }else
                {
                    Trace.WriteLine("Fail to parse color");
                }
            }

            fpsCounter?.Update(stopwatch.ElapsedMilliseconds);

            if (profile.ShowFps && fpsCounter != null)
            {
                var text = $"FPS {fpsCounter.FramesPerSecond} @ {fpsCounter.FrameTime}ms  ";
                var font = "Arial";
                var fontStyle = "Normal";
                var fontSize = 12;

                var rect = new SKRect(0, 0, profile.Width, 20);

                g.FillRectangle("#84000000", (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);
                g.DrawString(profile.Name, font, fontStyle, fontSize, "#FF00FF00", (int)rect.Left, (int)rect.Top, width: (int)rect.Width, height: (int)rect.Height);
                g.DrawString(text, font, fontStyle, fontSize, "#FF00FF00",(int)rect.Left, (int)rect.Top, width: (int)rect.Width, height: (int)rect.Height, rightAlign: true);
            }
        }


        public static SKPath? RectToPath(SKRect rect, int rotation, float maxWidth, float maxHeight, float penWidth = 2)
        {
            float halfPenWidth = penWidth / 2f;
            var canvasBounds = new SKRect(halfPenWidth, halfPenWidth,
                                          maxWidth - halfPenWidth, maxHeight - halfPenWidth);

            // Early check: if the rectangle is entirely outside canvas bounds before rotation
            if (!rect.IntersectsWith(canvasBounds) && rotation == 0)
            {
                return null;
            }

            var path = new SKPath();
            path.AddRect(rect);

            // Apply rotation if needed
            if (rotation != 0)
            {
                float centerX = rect.MidX;
                float centerY = rect.MidY;
                var rotationMatrix = SKMatrix.CreateRotationDegrees(rotation, centerX, centerY);
                path.Transform(rotationMatrix);
            }

            // Get the bounds of the transformed path
            var pathBounds = path.Bounds;

            // Check if the path is entirely outside the canvas bounds
            if (pathBounds.Right < halfPenWidth || pathBounds.Left > maxWidth - halfPenWidth ||
                pathBounds.Bottom < halfPenWidth || pathBounds.Top > maxHeight - halfPenWidth)
            {
                path.Dispose();
                return null;
            }

            // Check if the path is entirely within the canvas bounds
            if (pathBounds.Left >= halfPenWidth && pathBounds.Top >= halfPenWidth &&
                pathBounds.Right <= maxWidth - halfPenWidth && pathBounds.Bottom <= maxHeight - halfPenWidth)
            {
                return path;
            }

            // Path partially overlaps with canvas, perform intersection
            using var canvasPath = new SKPath();
            canvasPath.AddRect(canvasBounds);
            var clippedPath = new SKPath();

            if (path.Op(canvasPath, SKPathOp.Intersect, clippedPath))
            {
                path.Dispose();
                return clippedPath;
            }

            // Intersection failed (shouldn't happen if bounds checks are correct)
            path.Dispose();
            clippedPath.Dispose();
            return null;
        }

        private static void Draw(MyGraphics g, double scale, bool cache, string cacheHint, DisplayItem displayItem, List<SelectedRectangle> selectedRectangles)
        {
            var x = (int)Math.Ceiling(displayItem.X * scale);
            var y = (int)Math.Ceiling(displayItem.Y * scale);

            switch (displayItem)
            {
                case GroupDisplayItem groupDisplayItem:
                    {
                        foreach (var item in groupDisplayItem.DisplayItems)
                        {
                            if (item.Hidden) continue;
                            Draw(g, scale, cache, cacheHint, item, selectedRectangles);
                        }
                        break;
                    }
                case TextDisplayItem textDisplayItem:
                    {

                        var fontSize = (int)Math.Ceiling(textDisplayItem.FontSize * scale);
                        (var text, var color) = textDisplayItem.EvaluateTextAndColor();

                        if (textDisplayItem is TableSensorDisplayItem tableSensorDisplayItem
                            && tableSensorDisplayItem.GetValue() is SensorReading sensorReading
                            && sensorReading.ValueTable is DataTable table)
                        {
                            var format = tableSensorDisplayItem.TableFormat;

                            var maxRows = tableSensorDisplayItem.MaxRows;

                            var formatParts = format.Split('|');

                            if (formatParts.Length > 0)
                            {
                                (float fWidth, float fHeight) = g.MeasureString("A", textDisplayItem.Font, textDisplayItem.FontStyle, fontSize, textDisplayItem.Bold,
                                              textDisplayItem.Italic, textDisplayItem.Underline, textDisplayItem.Strikeout);

                                var tWidth = 0;
                                for (int i = 0; i < formatParts.Length; i++)
                                {
                                    var split = formatParts[i].Split(':');
                                    if (split.Length == 2)
                                    {
                                        if (int.TryParse(split[0], out var column) && column < table.Columns.Count && int.TryParse(split[1], out var length))
                                        {
                                            if (tableSensorDisplayItem.ShowHeader)
                                            {
                                                g.DrawString(table.Columns[column].ColumnName, textDisplayItem.Font, textDisplayItem.FontStyle, fontSize, color,
                                                    x + tWidth, y,
                                          i != 0 && textDisplayItem.RightAlign, textDisplayItem.CenterAlign, textDisplayItem.Bold,
                                          textDisplayItem.Italic, textDisplayItem.Underline,
                                          textDisplayItem.Strikeout, length, 0);
                                            }

                                            var rows = Math.Min(table.Rows.Count, maxRows);

                                            for (int j = 0; j < rows; j++)
                                            {
                                                var col = table.Rows[j][column];

                                                if (table.Rows[j][column] is IPluginData pluginData)
                                                {

                                                    g.DrawString(pluginData.ToString(), textDisplayItem.Font, textDisplayItem.FontStyle, fontSize, color,
                                                        x + tWidth, (int)(y + (fHeight * (j + (tableSensorDisplayItem.ShowHeader ? 1 : 0)))),
                                       i != 0 && textDisplayItem.RightAlign, textDisplayItem.CenterAlign, textDisplayItem.Bold,
                                       textDisplayItem.Italic, textDisplayItem.Underline,
                                       textDisplayItem.Strikeout, length, 0);
                                                }
                                            }

                                            tWidth += length + 10;
                                        }
                                    }
                                }
                            }

                            break;
                        }

                        g.DrawString(text, textDisplayItem.Font, textDisplayItem.FontStyle, fontSize, color, x, y, textDisplayItem.RightAlign, textDisplayItem.CenterAlign,
                            textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline, textDisplayItem.Strikeout,
                            textDisplayItem.Width);

                        break;
                    }
                case ImageDisplayItem imageDisplayItem:
                    {
                        if (imageDisplayItem is SensorImageDisplayItem sensorImageDisplayItem)
                        {
                            if (!sensorImageDisplayItem.ShouldShow())
                            {
                                break;
                            }
                        }

                        LockedImage? cachedImage = Cache.GetLocalImage(imageDisplayItem);

                        var size = imageDisplayItem.EvaluateSize();
                        var scaledWidth = (int)size.Width;
                        var scaledHeight = (int)size.Height;

                        scaledWidth = (int)Math.Ceiling(scaledWidth * scale);
                        scaledHeight = (int)Math.Ceiling(scaledHeight * scale);

                        if (cachedImage != null)
                        {
                            g.DrawImage(cachedImage, x, y, scaledWidth, scaledHeight, imageDisplayItem.Rotation, cache: imageDisplayItem.Cache && cache, cacheHint: cacheHint);

                            if (imageDisplayItem.Layer)
                            {
                                g.FillRectangle(imageDisplayItem.LayerColor, x, y, scaledWidth, scaledHeight, rotation: imageDisplayItem.Rotation);
                            }
                        }
                        break;
                    }
                case GaugeDisplayItem gaugeDisplayItem:
                    {
                        var imageDisplayItem = gaugeDisplayItem.EvaluateImage();

                        if (imageDisplayItem != null)
                        {
                            var cachedImage = Cache.GetLocalImage(imageDisplayItem);

                            if (cachedImage != null)
                            {
                                var scaledWidth = gaugeDisplayItem.Width;
                                var scaledHeight = gaugeDisplayItem.Height;

                                if (scaledWidth == 0)
                                {
                                    scaledWidth = cachedImage.Width;
                                }

                                if (scaledHeight == 0)
                                {
                                    scaledHeight = cachedImage.Height;
                                }

                                scaledWidth = (int)Math.Ceiling(scaledWidth * gaugeDisplayItem.Scale / 100.0f * scale);
                                scaledHeight = (int)Math.Ceiling(scaledHeight * gaugeDisplayItem.Scale / 100.0f * scale);

                                g.DrawImage(cachedImage, x, y, scaledWidth, scaledHeight, 0, 0, 0, cache, cacheHint);
                            }
                        }
                        break;
                    }
                case ChartDisplayItem chartDisplayItem:
                    //if (g is CompatGraphics)
                    //{
                    //    var width = scale == 1 ? (int)chartDisplayItem.Width : (int)Math.Ceiling(chartDisplayItem.Width * scale);
                    //    var height = scale == 1 ? (int)chartDisplayItem.Height : (int)Math.Ceiling(chartDisplayItem.Height * scale);
                    //    using var graphBitmap = new Bitmap(chartDisplayItem.Width, chartDisplayItem.Height);
                    //    using var g1 = CompatGraphics.FromBitmap(graphBitmap);
                    //    GraphDraw.Run(chartDisplayItem, g1);

                    //    if (chartDisplayItem.FlipX)
                    //    {
                    //        graphBitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    //    }

                    //    g.DrawBitmap(graphBitmap, x, y, width, height);
                    //}
                    //else
                    if (g is AcceleratedGraphics acceleratedGraphics)
                    {
                        using var d2dGraphics = acceleratedGraphics.D2DDevice
                            .CreateBitmapGraphics(chartDisplayItem.Width, chartDisplayItem.Height);

                        if (d2dGraphics != null)
                        {
                            //d2dGraphics.SetDPI(96, 96);
                            d2dGraphics.Antialias = true;

                            if (chartDisplayItem.FlipX)
                            {
                                var flipMatrix = Matrix3x2.CreateScale(-1.0f, 1.0f) *
                                     Matrix3x2.CreateTranslation(chartDisplayItem.Width, 0);
                                d2dGraphics.SetTransform(flipMatrix);
                            }

                            using var g1 = AcceleratedGraphics.FromD2DGraphics(d2dGraphics, acceleratedGraphics);
                            d2dGraphics.BeginRender();
                            GraphDraw.Run(chartDisplayItem, g1);
                            d2dGraphics.EndRender();

                            g.DrawBitmap(d2dGraphics, chartDisplayItem.X, chartDisplayItem.Y, chartDisplayItem.Width, chartDisplayItem.Height);
                        }
                    }
                    else if (g is SkiaGraphics)
                    {
                        var width = scale == 1 ? (int)chartDisplayItem.Width : (int)Math.Ceiling(chartDisplayItem.Width * scale);
                        var height = scale == 1 ? (int)chartDisplayItem.Height : (int)Math.Ceiling(chartDisplayItem.Height * scale);

                        using var graphBitmap = new SKBitmap(width, height);
                        using var canvas = new SKCanvas(graphBitmap);

                        using var g1 = new SkiaGraphics(canvas);
                        GraphDraw.Run(chartDisplayItem, g1);

                        if (chartDisplayItem.FlipX)
                        {
                            g.DrawBitmap(graphBitmap, x, y, width, height, flipX: true);
                        }
                        else
                        {
                            g.DrawBitmap(graphBitmap, x, y, width, height);
                        }
                    }

                    break;
            }

            if (displayItem.Selected && displayItem is not GroupDisplayItem)
            {
                selectedRectangles.Add(new SelectedRectangle(displayItem.EvaluateBounds(), displayItem.Rotation));
            }
        }


    }
}
