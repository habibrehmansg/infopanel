using InfoPanel.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;

namespace InfoPanel.Drawing
{
    internal class PanelDraw
    {
        private static Stopwatch stopwatch = new Stopwatch();

        static PanelDraw ()
        {
            stopwatch.Start();
        }

        public static void Run(Profile profile, MyGraphics g, bool drawSelected = true)
        {
            g.Clear(ColorTranslator.FromHtml(profile.BackgroundColor));

            DisplayItem? selectedItem = SharedModel.Instance.SelectedItem;
            List<Rectangle> selectedRectangles = [];

            foreach (var displayItem in SharedModel.Instance.GetProfileDisplayItemsCopy(profile))
            {
                if (displayItem.Hidden) continue;

                switch (displayItem)
                {
                    case TextDisplayItem textDisplayItem:
                        {
                            (var text, var color) = textDisplayItem.EvaluateTextAndColor();

                            g.DrawString(text, textDisplayItem.Font, textDisplayItem.FontSize, color, textDisplayItem.X, textDisplayItem.Y, textDisplayItem.RightAlign,
                                textDisplayItem.Bold, textDisplayItem.Italic, textDisplayItem.Underline, textDisplayItem.Strikeout);

                            if (displayItem.Selected)
                            {
                                var (textWidth, textHeight) = g.MeasureString(text, textDisplayItem.Font, textDisplayItem.FontSize);

                                if (textDisplayItem.RightAlign)
                                {
                                    selectedRectangles.Add(new Rectangle((int)(textDisplayItem.X - textWidth), textDisplayItem.Y - 2, (int)textWidth, (int)(textHeight - 4)));
                                }
                                else
                                {
                                    selectedRectangles.Add(new Rectangle(textDisplayItem.X, textDisplayItem.Y, (int)textWidth, (int)(textHeight)));
                                }
                            }

                            break;
                        }
                    case ImageDisplayItem imageDisplayItem:
                        if (imageDisplayItem.CalculatedPath != null)
                        {
                            var cachedImage = Cache.GetLocalImage(imageDisplayItem.CalculatedPath);

                            if (cachedImage != null)
                            {
                                var scaledWidth = (int)(cachedImage.Width * imageDisplayItem.Scale / 100.0f);
                                var scaledHeight = (int)(cachedImage.Height * imageDisplayItem.Scale / 100.0f);

                                g.DrawImage(cachedImage, imageDisplayItem.X, imageDisplayItem.Y, scaledWidth, scaledHeight);

                                if (imageDisplayItem.Layer)
                                {
                                    g.FillRectangle(imageDisplayItem.LayerColor, imageDisplayItem.X, imageDisplayItem.Y, scaledWidth, scaledHeight);
                                }

                                if (displayItem.Selected)
                                {
                                    selectedRectangles.Add(new Rectangle(imageDisplayItem.X + 2, imageDisplayItem.Y + 2, scaledWidth - 4, scaledHeight - 4));
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
                                    var scaledWidth = (int)(cachedImage.Width * imageDisplayItem.Scale / 100.0f);
                                    var scaledHeight = (int)(cachedImage.Height * imageDisplayItem.Scale / 100.0f);

                                    g.DrawImage(cachedImage, gaugeDisplayItem.X, gaugeDisplayItem.Y, scaledWidth, scaledHeight);

                                    if (displayItem.Selected)
                                    {
                                        selectedRectangles.Add(new Rectangle(gaugeDisplayItem.X + 2, gaugeDisplayItem.Y + 2, scaledWidth - 4, scaledHeight - 4));
                                    }
                                }
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
                        g.DrawRectangle(Color.FromArgb(255, 0, 255, 0), 2, rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
                    }
                }
            }
        }
    }
}
