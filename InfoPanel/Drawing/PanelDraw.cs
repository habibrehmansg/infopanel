using InfoPanel.Models;
using InfoPanel.Plugins;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Numerics;
using System.Windows.Media.Media3D;

namespace InfoPanel.Drawing
{
    struct SelectedRectangle(int x, int y, int width, int height, int rotation = 0)
    {
        public readonly int X = x;
        public readonly int Y = y;
        public readonly int Width = width;
        public readonly int Height = height;
        public readonly int Rotation = rotation;
    }

    internal class PanelDraw
    {
        private static readonly Stopwatch stopwatch = new();

        static PanelDraw()
        {
            stopwatch.Start();
        }

        public static void Run(Profile profile, MyGraphics g, bool drawSelected = true, double scale = 1, bool cache = true, bool videoBackgroundFallback = false)
        {
            //Compat graphics has background handled by WPF
            if (g is AcceleratedGraphics || videoBackgroundFallback)
            {
                g.Clear(ColorTranslator.FromHtml(profile.BackgroundColor));

                if (profile.VideoBackgroundFilePath is string videoBackgroundFilePath)
                {
                    var videoBackgroundWebPFilePath = $"{FileUtil.GetRelativeAssetPath(profile, videoBackgroundFilePath)}.webp";
                    var cachedImage = Cache.GetLocalImage(videoBackgroundWebPFilePath);

                    if (cachedImage != null)
                    {
                        var scaledWidth = (int)Math.Ceiling(cachedImage.Width * scale);
                        var scaledHeight = (int)Math.Ceiling(cachedImage.Height * scale);

                        (int rotation, int centerX, int centerY) = profile.VideoBackgroundRotation switch
                        {
                            Enums.Rotation.Rotate90FlipNone => (90, scaledHeight / 2, scaledHeight / 2),
                            Enums.Rotation.Rotate180FlipNone => (180, scaledWidth / 2, scaledHeight / 2),
                            Enums.Rotation.Rotate270FlipNone => (270, scaledWidth / 2, scaledWidth / 2),
                            _ => (0, 0, 0)
                        };

                        g.DrawImage(cachedImage, 0, 0, scaledWidth, scaledHeight, rotation, centerX, centerY, false);
                    }
                }
            }

            List<SelectedRectangle> selectedRectangles = [];

            foreach (var displayItem in SharedModel.Instance.GetProfileDisplayItemsCopy(profile))
            {
                if (displayItem.Hidden) continue;
                Draw(g, scale, cache, displayItem, selectedRectangles);
            }

            if (drawSelected && SharedModel.Instance.SelectedProfile == profile && selectedRectangles.Any())
            {
                var elapsedMilliseconds = stopwatch.ElapsedMilliseconds;

                // Define "on" and "off" durations
                int onDuration = 600;  // Time the rectangle is visible
                int offDuration = 400; // Time the rectangle is invisible
                int cycleDuration = onDuration + offDuration; // Total cycle time

                // Determine if we are in the "on" phase
                if (elapsedMilliseconds % cycleDuration < onDuration) // "on" phase
                {
                    foreach (var rectangle in selectedRectangles)
                    {
                        // Pen width
                        int penWidth = 2;

                        // Calculate the center of the rectangle
                        var centerX = rectangle.X + rectangle.Width / 2;
                        var centerY = rectangle.Y + rectangle.Height / 2;

                        // Create a matrix for transformation
                        var matrix = new Matrix();

                        // Translate to the center, rotate, then translate back
                        matrix.Translate(centerX, centerY);
                        matrix.Rotate(rectangle.Rotation);
                        matrix.Translate(-centerX, -centerY);

                        // Define the rectangle points
                        PointF[] points =
                        [
                            new PointF(rectangle.X, rectangle.Y),
                            new PointF(rectangle.X + rectangle.Width, rectangle.Y),
                            new PointF(rectangle.X + rectangle.Width, rectangle.Y + rectangle.Height),
                            new PointF(rectangle.X, rectangle.Y + rectangle.Height)
                        ];

                        // Apply the transformation
                        matrix.TransformPoints(points);

                        // Find the bounding box of the transformed points
                        float minX = points.Min(p => p.X);
                        float minY = points.Min(p => p.Y);
                        float maxX = points.Max(p => p.X);
                        float maxY = points.Max(p => p.Y);

                        // Clamp the bounding box
                        minX = Math.Clamp(minX, penWidth / 2, profile.Width - penWidth / 2);
                        minY = Math.Clamp(minY, penWidth / 2, profile.Height - penWidth / 2);
                        maxX = Math.Clamp(maxX, penWidth / 2, profile.Width - penWidth / 2);
                        maxY = Math.Clamp(maxY, penWidth / 2, profile.Height - penWidth / 2);

                        // Draw the rectangle using the bounding box
                        g.DrawRectangle(Color.FromArgb(255, 0, 255, 0), penWidth, (int)minX, (int)minY, (int)(maxX - minX), (int)(maxY - minY)); // Rotation already applied
                    }
                }
            }
        }

        private static void Draw(MyGraphics g, double scale, bool cache, DisplayItem displayItem, List<SelectedRectangle> selectedRectangles)
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
                            Draw(g, scale, cache, item, selectedRectangles);
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
                                (float fWidth, float fHeight) = g.MeasureString("A", textDisplayItem.Font, fontSize, textDisplayItem.Bold,
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
                                                g.DrawString(table.Columns[column].ColumnName, textDisplayItem.Font, fontSize, color,
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

                                                    g.DrawString(pluginData.ToString(), textDisplayItem.Font, fontSize, color,
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

                                if (displayItem.Selected)
                                {
                                    var size = tableSensorDisplayItem.EvaluateSize();
                                    selectedRectangles.Add(new SelectedRectangle(x, y, (int)size.Width, (int)size.Height));
                                }
                            }

                            break;
                        }

                        g.DrawString(text, textDisplayItem.Font, fontSize, color, x, y, textDisplayItem.RightAlign, textDisplayItem.CenterAlign,
                            textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline, textDisplayItem.Strikeout,
                            textDisplayItem.Width);


                        if (displayItem.Selected)
                        {
                            var size = textDisplayItem.EvaluateSize();
                            int rectX = textDisplayItem.Width == 0 && textDisplayItem.RightAlign ? (int)(x - size.Width) : x;

                            selectedRectangles.Add(new SelectedRectangle(rectX, y, (int)size.Width, (int)size.Height));
                        }

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

                        LockedImage? cachedImage = null;

                        if (imageDisplayItem is HttpImageDisplayItem httpImageDisplayItem)
                        {
                            var sensorReading = httpImageDisplayItem.GetValue();

                            if (sensorReading.HasValue && sensorReading.Value.ValueText != null)
                            {
                                cachedImage = Cache.GetLocalImage(sensorReading.Value.ValueText);
                            }
                        }
                        else
                        {
                            if (imageDisplayItem.CalculatedPath != null)
                            {
                                cachedImage = Cache.GetLocalImage(imageDisplayItem.CalculatedPath);
                            }
                        }

                        var size = imageDisplayItem.EvaluateSize();
                        var scaledWidth = (int)size.Width;
                        var scaledHeight = (int)size.Height;

                        scaledWidth = (int)Math.Ceiling(scaledWidth * scale);
                        scaledHeight = (int)Math.Ceiling(scaledHeight * scale);

                        if (cachedImage != null)
                        {
                            g.DrawImage(cachedImage, x, y, scaledWidth, scaledHeight, imageDisplayItem.Rotation, (int)(x + scaledWidth / 2.0f), (int)(y + scaledHeight / 2.0f), imageDisplayItem.Cache && cache);

                            if (imageDisplayItem.Layer)
                            {
                                g.FillRectangle(imageDisplayItem.LayerColor, x, y, scaledWidth, scaledHeight);
                            }
                        }

                        if (displayItem.Selected)
                        {
                            selectedRectangles.Add(new SelectedRectangle(x - 2, y - 2, scaledWidth + 4, scaledHeight + 4, imageDisplayItem.Rotation));
                        }
                        break;
                    }
                case GaugeDisplayItem gaugeDisplayItem:
                    {
                        var imageDisplayItem = gaugeDisplayItem.EvaluateImage();

                        if (imageDisplayItem?.CalculatedPath != null)
                        {
                            var cachedImage = Cache.GetLocalImage(imageDisplayItem.CalculatedPath);

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

                                g.DrawImage(cachedImage, x, y, scaledWidth, scaledHeight, 0, 0, 0, cache);

                                if (displayItem.Selected)
                                {
                                    selectedRectangles.Add(new SelectedRectangle(x - 2, y - 2, scaledWidth + 4, scaledHeight + 4));
                                }
                            }
                        }
                        break;
                    }
                case ChartDisplayItem chartDisplayItem:
                    if (g is CompatGraphics)
                    {
                        var width = scale == 1 ? (int)chartDisplayItem.Width : (int)Math.Ceiling(chartDisplayItem.Width * scale);
                        var height = scale == 1 ? (int)chartDisplayItem.Height : (int)Math.Ceiling(chartDisplayItem.Height * scale);
                        using var graphBitmap = new Bitmap(chartDisplayItem.Width, chartDisplayItem.Height);
                        using var g1 = CompatGraphics.FromBitmap(graphBitmap);
                        GraphDraw.Run(chartDisplayItem, g1);

                        if (chartDisplayItem.FlipX)
                        {
                            graphBitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);
                        }

                        g.DrawBitmap(graphBitmap, x, y, width, height);
                    }
                    else if (g is AcceleratedGraphics acceleratedGraphics)
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
                    } else if(g is SkiaGraphics)
                    {
                        var width = scale == 1 ? (int)chartDisplayItem.Width : (int)Math.Ceiling(chartDisplayItem.Width * scale);
                        var height = scale == 1 ? (int)chartDisplayItem.Height : (int)Math.Ceiling(chartDisplayItem.Height * scale);

                        using var graphBitmap = new SKBitmap(width, height);
                        using var canvas = new SKCanvas(graphBitmap);

                        using var g1 = new SkiaGraphics(canvas);
                        GraphDraw.Run(chartDisplayItem, g1);

                        if (chartDisplayItem.FlipX)
                        {
                            //todo
                        }

                        g.DrawBitmap(graphBitmap, x, y, width, height);
                    }

                    if (displayItem.Selected)
                    {
                        selectedRectangles.Add(new SelectedRectangle(chartDisplayItem.X - 2, chartDisplayItem.Y - 2, chartDisplayItem.Width + 4, chartDisplayItem.Height + 4));
                    }

                    break;
            }
        }


    }
}
