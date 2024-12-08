using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Windows.Graphics.Imaging;

namespace InfoPanel.Drawing
{
    internal class PanelDraw
    {
        private static Stopwatch stopwatch = new Stopwatch();

        static PanelDraw ()
        {
            stopwatch.Start();
        }

        public static void Run(Profile profile, MyGraphics g, bool drawSelected = true, double scale = 1, bool cache = true)
        {
            g.Clear(ColorTranslator.FromHtml(profile.BackgroundColor));

            DisplayItem? selectedItem = SharedModel.Instance.SelectedItem;
            List<Rectangle> selectedRectangles = [];

            foreach (var displayItem in SharedModel.Instance.GetProfileDisplayItemsCopy(profile))
            {
                if (displayItem.Hidden) continue;
                
                var x = (int) Math.Ceiling(displayItem.X * scale);
                var y = (int) Math.Ceiling(displayItem.Y * scale);

                switch (displayItem)
                {
                    case TextDisplayItem textDisplayItem:
                        {
                            (var text, var color) = textDisplayItem.EvaluateTextAndColor();
                            var fontSize = (int) Math.Ceiling(textDisplayItem.FontSize * scale);

                            g.DrawString(text, textDisplayItem.Font, fontSize, color, x, y, textDisplayItem.RightAlign,
                                textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline, textDisplayItem.Strikeout);

                            if (displayItem.Selected)
                            {
                                var (textWidth, textHeight) = g.MeasureString(text, textDisplayItem.Font, textDisplayItem.FontSize);

                                if (textDisplayItem.RightAlign)
                                {
                                    selectedRectangles.Add(new Rectangle((int)(x - textWidth), y - 2, (int)textWidth, (int)(textHeight - 4)));
                                }
                                else
                                {
                                    selectedRectangles.Add(new Rectangle(x, y, (int)textWidth, (int)(textHeight)));
                                }
                            }

                            break;
                        }
                    case ImageDisplayItem imageDisplayItem:
                        if(imageDisplayItem is SensorImageDisplayItem sensorImageDisplayItem)
                        {
                            if (!sensorImageDisplayItem.ShouldShow())
                            {
                                break;
                            }
                        }

                        if (imageDisplayItem.CalculatedPath != null)
                        {
                            var cachedImage = Cache.GetLocalImage(imageDisplayItem.CalculatedPath);

                            if (cachedImage != null)
                            {
                                var scaledWidth = (int) Math.Ceiling(cachedImage.Width * imageDisplayItem.Scale / 100.0f * scale);
                                var scaledHeight = (int) Math.Ceiling(cachedImage.Height * imageDisplayItem.Scale / 100.0f * scale);

                                g.DrawImage(cachedImage, x, y, scaledWidth, scaledHeight, imageDisplayItem.Cache && cache);

                                if (imageDisplayItem.Layer)
                                {
                                    g.FillRectangle(imageDisplayItem.LayerColor, x, y, scaledWidth, scaledHeight);
                                }

                                if (displayItem.Selected)
                                {
                                    selectedRectangles.Add(new Rectangle(x - 2, y - 2, scaledWidth + 4, scaledHeight + 4));
                                }
                            }
                        }
                        break;
                    case GaugeDisplayItem gaugeDisplayItem:
                        {
                            //var imageDisplayItem = gaugeDisplayItem.EvaluateImage(1.0 / frameRateLimit);
                            var imageDisplayItem = gaugeDisplayItem.EvaluateImage();

                            if (imageDisplayItem?.CalculatedPath != null)
                            {
                                var cachedImage = Cache.GetLocalImage(imageDisplayItem.CalculatedPath);

                                if (cachedImage != null)
                                {
                                    var scaledWidth = (int) Math.Ceiling(cachedImage.Width * imageDisplayItem.Scale / 100.0f * scale);
                                    var scaledHeight = (int) Math.Ceiling(cachedImage.Height * imageDisplayItem.Scale / 100.0f * scale);

                                    g.DrawImage(cachedImage, x, y, scaledWidth, scaledHeight, cache);

                                    if (displayItem.Selected)
                                    {
                                        selectedRectangles.Add(new Rectangle(x - 2, y - 2, scaledWidth + 4, scaledHeight + 4));
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
                                d2dGraphics.SetDPI(96, 96);
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

                        if (displayItem.Selected)
                        {
                            selectedRectangles.Add(new Rectangle(chartDisplayItem.X - 2, chartDisplayItem.Y - 2, chartDisplayItem.Width + 4, chartDisplayItem.Height + 4));
                        }

                        break;
                }
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
                        // pen width
                        int penWidth = 2;

                        // Clamp the top-left corner of the rectangle, considering the pen thickness
                        var x = Math.Clamp(rectangle.X, penWidth / 2, profile.Width - penWidth / 2);
                        var y = Math.Clamp(rectangle.Y, penWidth / 2, profile.Height - penWidth / 2);

                        // Adjust width and height to ensure the rectangle stays within bounds
                        var width = rectangle.Width - (x - rectangle.X);
                        var height = rectangle.Height - (y - rectangle.Y);

                        // Ensure the width does not extend beyond the right boundary, considering the pen thickness
                        if (x + width + penWidth / 2 > profile.Width)
                        {
                            width = profile.Width - x - penWidth / 2;
                        }

                        // Ensure the height does not extend beyond the bottom boundary, considering the pen thickness
                        if (y + height + penWidth / 2 > profile.Height)
                        {
                            height = profile.Height - y - penWidth / 2;
                        }

                        // draw the rectangle
                        g.DrawRectangle(Color.FromArgb(255, 0, 255, 0), penWidth,
                            x, y,
                            width, height);
                    }
                }
            }
        }
    }
}
