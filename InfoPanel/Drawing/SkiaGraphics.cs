using InfoPanel.Models;
using SkiaSharp;
using System;
using System.Drawing;
using System.IO;
using unvell.D2DLib;

namespace InfoPanel.Drawing
{
    internal partial class SkiaGraphics(SKCanvas canvas, float fontScale = 1) : MyGraphics
    {
        private readonly SKCanvas Canvas = canvas;
        private readonly float FontScale = fontScale;

        public override void Clear(Color color)
        {
            this.Canvas.Clear(new SKColor(color.R, color.G, color.B, color.A));
        }

        public override void Dispose()
        {
            this.Canvas.Dispose();
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y)
        {
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
        }

        public override void DrawBitmap(D2DBitmap bitmap, int x, int y)
        {
        }

        public override void DrawBitmap(D2DBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
        }

        public override void DrawBitmap(D2DBitmapGraphics bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
        }

        public override void DrawBitmap(SKBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            if (bitmap == null || Canvas == null)
                return;

            Canvas.Save();

            if (rotation != 0)
            {
                // Move the origin to the rotation center
                Canvas.Translate(rotationCenterX, rotationCenterY);

                // Rotate the canvas
                Canvas.RotateDegrees(rotation);

                // Move the origin back
                Canvas.Translate(-rotationCenterX, -rotationCenterY);
            }

            // Draw the bitmap at the specified position
            var destRect = new SKRect(x, y, x + width, y + height);
            Canvas.DrawBitmap(bitmap, destRect);

            Canvas.Restore();
        }

        public override void DrawImage(LockedImage lockedImage, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool cache = true)
        {
            lockedImage.AccessSK(bitmap =>
            {
                if(bitmap != null)
                {
                    DrawBitmap(bitmap, x, y, width, height, rotation, rotationCenterX, rotationCenterY);
                }
            });
        }

        public static SKBitmap ConvertToSKBitmap(System.Drawing.Bitmap bitmap)
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png); // or BMP/JPEG
            ms.Seek(0, SeekOrigin.Begin);
            return SKBitmap.Decode(ms);
        }

        public override void DrawLine(float x1, float y1, float x2, float y2, string color, float strokeWidth)
        {
            using var paint = new SKPaint
            {
                Color = SKColor.Parse(color),
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            Canvas.DrawLine(x1, y1, x2, y2, paint);
        }

        public override void DrawPath(MyPoint[] points, string color, int strokeWidth)
        {
            if (points == null || points.Length < 2)
                return;

            using var path = new SKPath();
            path.MoveTo(points[0].X, points[0].Y);

            for (int i = 1; i < points.Length; i++)
            {
                path.LineTo(points[i].X, points[i].Y);
            }

            using var paint = new SKPaint
            {
                Color = SKColor.Parse(color),
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            Canvas.DrawPath(path, paint); // assuming 'canvas' is your SKCanvas instance
        }

        public override void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height)
        {
            using var paint = new SKPaint
            {
                Color = SKColor.Parse(color),
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            Canvas.DrawRect(x, y, width, height, paint);
        }

        public override void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, color.A),
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke // or Fill
            };

            Canvas.DrawRect(x, y, width, height, paint);
        }

        public override void DrawString(string text, string fontName, int fontSize, string color, int x, int y, bool rightAlign = false, bool centerAlign = false, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false, int width = 0, int height = 0)
        {
            if (string.IsNullOrEmpty(text))
                return;

            using var paint = new SKPaint
            {
                Color = SKColor.Parse(color),
                IsAntialias = true
                
            };

            // Handle font style (bold, italic)
            SKFontStyleWeight weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            SKFontStyleWidth widthStyle = SKFontStyleWidth.Normal;
            SKFontStyleSlant slant = italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

            var fontStyle = new SKFontStyle(weight, widthStyle, slant);
            using var typeface = SKTypeface.FromFamilyName(fontName, fontStyle);
            using var font = new SKFont(typeface, size: (float)(fontSize * FontScale));

            // Calculate position
            var metrics = font.Metrics;
            float newY = y - metrics.Ascent;
            float newX = x;
            // Get text alignment
            var align = rightAlign ? SKTextAlign.Right : SKTextAlign.Left;

            float textWidth = font.MeasureText(text);

            // Apply clipping if width and height are provided
            if (width > 0)
            {
                if (textWidth > width)
                {
                    string ellipsis = "...";
                    string truncatedText = text.TrimEnd();

                    while (truncatedText.Length > 0)
                    {
                        var tempText = truncatedText + ellipsis;
                        textWidth = font.MeasureText(tempText);

                        if (textWidth <= width)
                        {
                            text = tempText;
                            break;
                        }

                        // Remove the last character
                        truncatedText = truncatedText[..^1];
                    }
                }

                if (rightAlign)
                {
                    newX = x + width;
                    align = SKTextAlign.Right;
                }

                if(centerAlign)
                {
                    newX = x + width / 2 - textWidth / 2;
                    align = SKTextAlign.Left;
                }
            }

            // Draw text using the overload that takes alignment parameter
            Canvas.DrawText(text, newX, newY, align, font, paint);

            // Calculate text width for decoration lines
            float startX = x;
            if (rightAlign)
                startX -= textWidth;
            else if (centerAlign)
                startX -= textWidth / 2;

            // Draw strikeout if needed
            if (strikeout)
            {
                float strikeY = y - metrics.Ascent / 2;
                using var strikePaint = new SKPaint
                {
                    Color = SKColor.Parse(color),
                    StrokeWidth = (float)(metrics.StrikeoutThickness > 0 ? metrics.StrikeoutThickness : 1),
                    IsAntialias = true
                };

                Canvas.DrawLine(startX, strikeY, startX + textWidth, strikeY, strikePaint);
            }

            // Draw underline if needed
            if (underline)
            {
                float underlineY = y - metrics.Ascent + metrics.Descent / 2;
                using var underlinePaint = new SKPaint
                {
                    Color = SKColor.Parse(color),
                    StrokeWidth = (float)(metrics.UnderlineThickness > 0 ? metrics.UnderlineThickness : 1),
                    IsAntialias = true
                };

                Canvas.DrawLine(startX, underlineY, startX + textWidth, underlineY, underlinePaint);
            }
        }

        public override void FillDonut(int x, int y, int radius, int thickness, int rotation, int percentage, int span, string color, string backgroundColor, int strokeWidth, string strokeColor)
        {
        }

        public override void FillPath(MyPoint[] points, string color)
        {
            if (points == null || points.Length < 3)
                return; // Need at least 3 points to fill a shape

            using var path = new SKPath();
            path.MoveTo(points[0].X, points[0].Y);

            for (int i = 1; i < points.Length; i++)
            {
                path.LineTo(points[i].X, points[i].Y);
            }

            path.Close(); // Close the path to form a filled shape

            using var paint = new SKPaint
            {
                Color = SKColor.Parse(color),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            Canvas.DrawPath(path, paint);
        }

        public override void FillRectangle(string color, int x, int y, int width, int height, string? gradientColor = null, bool gradientHorizontal = true)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            if (gradientColor != null)
            {
                var startColor = SKColor.Parse(color);
                var endColor = SKColor.Parse(gradientColor);

                var startPoint = new SKPoint(x, y);
                var endPoint = gradientHorizontal
                    ? new SKPoint(x + width, y)
                    : new SKPoint(x, y + height);

                paint.Shader = SKShader.CreateLinearGradient(
                    startPoint,
                    endPoint,
                    new[] { startColor, endColor },
                    null,
                    SKShaderTileMode.Clamp);
            }
            else
            {
                paint.Color = SKColor.Parse(color);
            }

            Canvas.DrawRect(x, y, width, height, paint);
        }

        public override (float width, float height) MeasureString(string text, string fontName, int fontSize, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            throw new NotImplementedException();
        }
    }
}
