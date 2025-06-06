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

        public static void Run(Profile profile, MyGraphics g, bool drawSelected = true, double scale = 1, bool cache = true, string cacheHint = "default", FpsCounter? fpsCounter = null, GRContext? grContext = null)
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
                var renderingEngine = "CPU";

                if(g is SkiaGraphics skiaGraphics && skiaGraphics.OpenGL)
                {
                    renderingEngine = "OpenGL";
                }

                var text = $"{renderingEngine} | FPS {fpsCounter.FramesPerSecond} | {fpsCounter.FrameTime}ms";
                var font = "Consolas";
                var fontStyle = "Regular";
                var fontSize = 10;

                var rect = new SKRect(0, 0, profile.Width, 15);

                g.FillRectangle("#84000000", (int)rect.Left, (int)rect.Top, (int)rect.Width, (int)rect.Height);
                g.DrawString(profile.Name, font, fontStyle, fontSize, "#FF00FF00", (int)rect.Left + 1, (int)rect.Top, width: (int)rect.Width, height: (int)rect.Height);
                g.DrawString(text, font, fontStyle, fontSize, "#FF00FF00",(int)rect.Left, (int)rect.Top, width: (int)rect.Width - 1, height: (int)rect.Height, rightAlign: true);
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
                case ShapeDisplayItem shapeDisplayItem:
                    {
                        var width = scale == 1 ? (int)shapeDisplayItem.Width : (int)Math.Ceiling(shapeDisplayItem.Width * scale);
                        var height = scale == 1 ? (int)shapeDisplayItem.Height : (int)Math.Ceiling(shapeDisplayItem.Height * scale);

                        var centerX = x + width / 2;
                        var centerY = y + height / 2;

                        using var path = new SKPath();

                        switch (shapeDisplayItem.Type)
                        {
                            case ShapeDisplayItem.ShapeType.Rectangle:
                                {
                                    path.AddRect(SKRect.Create(x, y, width, height));
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Capsule:
                                {
                                    path.AddRoundRect(SKRect.Create(x, y, width, height), shapeDisplayItem.CornerRadius, shapeDisplayItem.CornerRadius);
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Ellipse:
                                {
                                    path.AddOval(SKRect.Create(x, y, width, height));
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Triangle:
                                {
                                    // Top point
                                    var topX = centerX;
                                    var topY = y;

                                    // Bottom left point
                                    var bottomLeftX = x;
                                    var bottomLeftY = y + height;

                                    // Bottom right point
                                    var bottomRightX = x + width;
                                    var bottomRightY = y + height;

                                    // Create the triangle path
                                    path.MoveTo(topX, topY);
                                    path.LineTo(bottomLeftX, bottomLeftY);
                                    path.LineTo(bottomRightX, bottomRightY);
                                    path.Close();
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Star:
                                {
                                    // Star that stretches to fit the bounding box
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    // Calculate the 5 outer points with scaling
                                    var points = new SKPoint[10];
                                    for (int i = 0; i < 10; i++)
                                    {
                                        var angle = (i * 36 - 90) * Math.PI / 180;
                                        var r = (i % 2 == 0) ? 1f : 0.382f; // 1 for outer, 0.382 for inner

                                        // Apply different scaling for X and Y
                                        points[i] = new SKPoint(
                                            centerX + (float)(r * scaleX * Math.Cos(angle)),
                                            centerY + (float)(r * scaleY * Math.Sin(angle))
                                        );
                                    }

                                    // Create the path
                                    path.MoveTo(points[0]);
                                    for (int i = 1; i < 10; i++)
                                    {
                                        path.LineTo(points[i]);
                                    }
                                    path.Close();
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Pentagon:
                                {
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    for (int i = 0; i < 5; i++)
                                    {
                                        var angle = (i * 72 - 90) * Math.PI / 180;
                                        var pointX = centerX + scaleX * Math.Cos(angle);
                                        var pointY = centerY + scaleY * Math.Sin(angle);

                                        if (i == 0)
                                            path.MoveTo((float)pointX, (float)pointY);
                                        else
                                            path.LineTo((float)pointX, (float)pointY);
                                    }
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Hexagon:
                                {
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    for (int i = 0; i < 6; i++)
                                    {
                                        var angle = (i * 60 - 90) * Math.PI / 180;
                                        var pointX = centerX + scaleX * Math.Cos(angle);
                                        var pointY = centerY + scaleY * Math.Sin(angle);

                                        if (i == 0)
                                            path.MoveTo((float)pointX, (float)pointY);
                                        else
                                            path.LineTo((float)pointX, (float)pointY);
                                    }
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Plus:
                                {
                                    var thickness = Math.Min(width, height) / 3;
                                    var horizontalY = centerY - thickness / 2;
                                    var verticalX = centerX - thickness / 2;

                                    // Create plus shape as single path
                                    path.MoveTo(verticalX, y);
                                    path.LineTo(verticalX + thickness, y);
                                    path.LineTo(verticalX + thickness, horizontalY);
                                    path.LineTo(x + width, horizontalY);
                                    path.LineTo(x + width, horizontalY + thickness);
                                    path.LineTo(verticalX + thickness, horizontalY + thickness);
                                    path.LineTo(verticalX + thickness, y + height);
                                    path.LineTo(verticalX, y + height);
                                    path.LineTo(verticalX, horizontalY + thickness);
                                    path.LineTo(x, horizontalY + thickness);
                                    path.LineTo(x, horizontalY);
                                    path.LineTo(verticalX, horizontalY);
                                    path.Close();
                                    break;
                                }
                            case ShapeDisplayItem.ShapeType.Arrow:
                                {
                                    var headHeight = height * 0.4f;
                                    var shaftWidth = width * 0.5f;

                                    // Arrow pointing up
                                    path.MoveTo(centerX, y);                                    // Tip
                                    path.LineTo(x + width, y + headHeight);                     // Right head
                                    path.LineTo(x + width * 0.75f, y + headHeight);
                                    path.LineTo(x + width * 0.75f, y + height);                // Right shaft bottom
                                    path.LineTo(x + width * 0.25f, y + height);                // Left shaft bottom
                                    path.LineTo(x + width * 0.25f, y + headHeight);
                                    path.LineTo(x, y + headHeight);                            // Left head
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Octagon:
                                {
                                    var scaleX = width / 2;
                                    var scaleY = height / 2;

                                    for (int i = 0; i < 8; i++)
                                    {
                                        var angle = (i * 45 - 90) * Math.PI / 180;
                                        var pointX = centerX + scaleX * Math.Cos(angle);
                                        var pointY = centerY + scaleY * Math.Sin(angle);

                                        if (i == 0)
                                            path.MoveTo((float)pointX, (float)pointY);
                                        else
                                            path.LineTo((float)pointX, (float)pointY);
                                    }
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Trapezoid:
                                {
                                    var topInset = width * 0.2f;

                                    path.MoveTo(x + topInset, y);                    // Top left
                                    path.LineTo(x + width - topInset, y);            // Top right
                                    path.LineTo(x + width, y + height);              // Bottom right
                                    path.LineTo(x, y + height);                      // Bottom left
                                    path.Close();
                                    break;
                                }

                            case ShapeDisplayItem.ShapeType.Parallelogram:
                                {
                                    var skew = width * 0.25f;

                                    path.MoveTo(x + skew, y);                        // Top left
                                    path.LineTo(x + width, y);                       // Top right
                                    path.LineTo(x + width - skew, y + height);       // Bottom right
                                    path.LineTo(x, y + height);                      // Bottom left
                                    path.Close();
                                    break;
                                }
                        }


                        if (shapeDisplayItem.Rotation != 0)
                        {
                            var matrix = SKMatrix.CreateRotationDegrees(shapeDisplayItem.Rotation, centerX, centerY);
                            path.Transform(matrix);
                        }

                        if (shapeDisplayItem.ShowFill)
                        {
                            if (SKColor.TryParse(shapeDisplayItem.FillColor, out var color))
                            {
                                if (shapeDisplayItem.ShowGradient && SKColor.TryParse(shapeDisplayItem.GradientColor, out var gradientColor) && SKColor.TryParse(shapeDisplayItem.GradientColor2, out var gradientColor2))
                                {
                                    g.FillPath(path, color, gradientColor, gradientColor2, shapeDisplayItem.GetGradientAnimationOffset(), shapeDisplayItem.GradientType);
                                }
                                else
                                {
                                    g.FillPath(path, color);
                                }
                            }
                        }

                        if (shapeDisplayItem.ShowFrame)
                        {
                            if (SKColor.TryParse(shapeDisplayItem.FrameColor, out var color))
                            {
                                if (shapeDisplayItem.ShowGradient && SKColor.TryParse(shapeDisplayItem.GradientColor, out var gradientColor) && SKColor.TryParse(shapeDisplayItem.GradientColor2, out var gradientColor2))
                                {
                                    g.DrawPath(path, color, shapeDisplayItem.FrameThickness, gradientColor, gradientColor2, shapeDisplayItem.GetGradientAnimationOffset(), shapeDisplayItem.GradientType);
                                }
                                else
                                {
                                    g.DrawPath(path, color, shapeDisplayItem.FrameThickness);
                                }
                            }
                        }

                        break;
                    }
            }

            if (displayItem.Selected && displayItem is not GroupDisplayItem)
            {
                selectedRectangles.Add(new SelectedRectangle(displayItem.EvaluateBounds(), displayItem.Rotation));
            }
        }


    }
}
