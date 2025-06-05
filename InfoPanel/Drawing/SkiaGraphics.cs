using InfoPanel.Extensions;
using InfoPanel.Models;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Shapes;
using unvell.D2DLib;

namespace InfoPanel.Drawing
{
    internal partial class SkiaGraphics(SKCanvas canvas, float fontScale = 1.33f) : MyGraphics
    {
        private readonly SKCanvas Canvas = canvas;
        private readonly GRContext? GRContext = canvas.Context as GRContext;
        private readonly float FontScale = fontScale;

        public bool OpenGL => GRContext != null;

        public static SkiaGraphics FromBitmap(SKBitmap bitmap)
        {
            var canvas = new SKCanvas(bitmap);
            return new SkiaGraphics(canvas);
        }

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

        public override void DrawBitmap(SKBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool flipX = false, bool flipY = false)
        {
            using var image = SKImage.FromBitmap(bitmap);
            DrawImage(image, x, y, width, height, rotation, rotationCenterX, rotationCenterY, flipX, flipY);
        }

        public void DrawImage(SKImage image, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool flipX = false, bool flipY = false)
        {
            using var paint = new SKPaint
            {
                IsAntialias = true
            };

            var destRect = new SKRect(x, y, x + width, y + height);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Nearest);

            Canvas.Save();

            if (flipX || flipY)
            {
                float scaleX = flipX ? -1 : 1;
                float scaleY = flipY ? -1 : 1;
                int flipCenterX = x + width / 2;
                int flipCenterY = y + height / 2;

                Canvas.Scale(scaleX, scaleY, flipCenterX, flipCenterY);
            }

            if (rotation != 0)
            {
                // Default to rectangle center if no rotation center specified
                int centerX = rotationCenterX == 0 ? x + width / 2 : rotationCenterX;
                int centerY = rotationCenterY == 0 ? y + height / 2 : rotationCenterY;
                Canvas.RotateDegrees(rotation, centerX, centerY);
            }
            Canvas.DrawImage(image, destRect, sampling, paint);

            Canvas.Restore();
        }

        public override void DrawImage(LockedImage lockedImage, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool cache = true, string cacheHint = "default")
        {
            if (lockedImage.IsSvg)
            {
                lockedImage.AccessSVG(picture =>
                {
                    Canvas.DrawPicture(picture, x, y, width, height, rotation);
                });
            }
            else
            {
                lockedImage.AccessSK(width, height, bitmap =>
                {
                    if (bitmap != null) { 
                        DrawImage(bitmap, x, y, width, height, rotation, rotationCenterX, rotationCenterY);
                    }
                }, cache, cacheHint, GRContext);
            }
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

        public override void DrawPath(SKPath path, SKColor color, int strokeWidth, SKColor? gradientColor = null, SKColor? gradientColor2 = null, float gradientAngle = 90f, GradientType gradientType = GradientType.Linear)
        {
            using var paint = new SKPaint
            {
                Color = color,
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            if(gradientColor.HasValue)
            {
                using var shader = CreateGradient(path, color, gradientColor.Value, gradientColor2, gradientAngle, gradientType);
                if (shader != null)
                {
                    paint.Shader = shader;
                }
            }

            Canvas.DrawPath(path, paint);
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

            Canvas.DrawPath(path, paint);
        }

        public override void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            using var paint = new SKPaint
            {
                Color = SKColor.Parse(color),
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            Canvas.Save();

            if (rotation != 0)
            {
                // Default to rectangle center if no rotation center specified
                int centerX = rotationCenterX == 0 ? x + width / 2 : rotationCenterX;
                int centerY = rotationCenterY == 0 ? y + height / 2 : rotationCenterY;
                Canvas.RotateDegrees(rotation, centerX, centerY);
            }

            Canvas.DrawRect(x, y, width, height, paint);

            Canvas.Restore();
        }

        public override void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, color.A),
                StrokeWidth = strokeWidth,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke // or Fill
            };

            Canvas.Save();

            if (rotation != 0)
            {
                // Default to rectangle center if no rotation center specified
                int centerX = rotationCenterX == 0 ? x + width / 2 : rotationCenterX;
                int centerY = rotationCenterY == 0 ? y + height / 2 : rotationCenterY;
                Canvas.RotateDegrees(rotation, centerX, centerY);
            }

            Canvas.DrawRect(x, y, width, height, paint);

            Canvas.Restore();
        }

        public override void DrawString(string text, string fontName, string fontStyle, int fontSize, string color, int x, int y, bool rightAlign = false, bool centerAlign = false, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false, int width = 0, int height = 0)
        {
            if (string.IsNullOrEmpty(text))
                return;

            using var paint = new SKPaint
            {
                Color = SKColor.Parse(color),
                IsAntialias = true
            };

            SKTypeface typeface = CreateTypeface(fontName, fontStyle, bold, italic);
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

                if (centerAlign)
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

        private static readonly ConcurrentDictionary<string, SKTypeface> _typefaceCache = [];

        public static SKTypeface CreateTypeface(string fontName, string fontStyle, bool bold, bool italic)
        {
            string cacheKey = $"{fontName}-{fontStyle}-{bold}-{italic}";

            if (_typefaceCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            Trace.WriteLine("Cache miss: " + cacheKey);

            SKTypeface? result = null;

            if (string.IsNullOrEmpty(fontStyle))
            {
                result = LoadTypeface(fontName, bold, italic);
            }
            else
            {
                using var typeface = SKTypeface.FromFamilyName(fontName);
                using var fontStyles = SKFontManager.Default.GetFontStyles(fontName);

                for (int i = 0; i < fontStyles.Count; i++)
                {
                    if (fontStyles.GetStyleName(i).Equals(fontStyle))
                    {
                        result = SKTypeface.FromFamilyName(fontName, fontStyles[i]);
                        break;
                    }
                }
            }

            result ??= SKTypeface.CreateDefault();

            _typefaceCache.TryAdd(cacheKey, result);
            return result;
        }

        private static SKTypeface LoadTypeface(string fontName, bool bold, bool italic)
        {
            SKFontStyleWeight weight = bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
            SKFontStyleWidth widthStyle = SKFontStyleWidth.Normal;
            SKFontStyleSlant slant = italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;

            // Check if font name contains width indicators
            if (fontName.Contains("Ultra Compressed", StringComparison.OrdinalIgnoreCase) ||
                fontName.Contains("Ultra Condensed", StringComparison.OrdinalIgnoreCase))
            {
                widthStyle = SKFontStyleWidth.UltraCondensed;
            }
            else if (fontName.Contains("Compressed", StringComparison.OrdinalIgnoreCase) ||
                     fontName.Contains("Condensed", StringComparison.OrdinalIgnoreCase))
            {
                widthStyle = SKFontStyleWidth.Condensed;
            }

            using var fontStyle = new SKFontStyle(weight, widthStyle, slant);

            var typeface = TryLoadTypeface(fontName, fontStyle);
            if (typeface != null) return typeface;

            var baseFamilyName = ExtractBaseFamilyName(fontName);
            if (baseFamilyName != fontName)
            {
                typeface = TryLoadTypeface(baseFamilyName, fontStyle);
                if (typeface != null) return typeface;
            }

            typeface = FindSimilarFont(fontName, fontStyle);
            if (typeface != null) return typeface;

            // Fallback to default
            Console.WriteLine($"Warning: Font '{fontName}' not found, using fallback");
            return SKTypeface.FromFamilyName("Arial", fontStyle);
        }

        private static SKTypeface? TryLoadTypeface(string familyName, SKFontStyle fontStyle)
        {
            var typeface = SKTypeface.FromFamilyName(familyName, fontStyle);

            // Check if we actually got the requested font or a fallback
            if (typeface != null &&
                !typeface.FamilyName.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase) &&
                (familyName.Contains(typeface.FamilyName, StringComparison.OrdinalIgnoreCase) ||
                 typeface.FamilyName.Contains(familyName, StringComparison.OrdinalIgnoreCase)))
            {
                return typeface;
            }

            typeface?.Dispose();
            return null;
        }

        public static string ExtractBaseFamilyName(string fontName)
        {
            // Remove common style descriptors
            var descriptors = new[]
            {
            "Ultra Compressed", "Ultra Condensed", "Compressed", "Condensed",
            "Extended", "Narrow", "Wide", "Black", "Bold", "Semibold", "Light", "Thin",
            "Heavy", "Medium", "Regular", "Italic", "Oblique", "BT"
        };

            var result = fontName;
            foreach (var descriptor in descriptors)
            {
                result = Regex.Replace(result, $@"\s*{Regex.Escape(descriptor)}\s*", " ",
                    RegexOptions.IgnoreCase);
            }

            return result.Trim();
        }

        public static SKTypeface? FindSimilarFont(string requestedFont, SKFontStyle fontStyle)
        {
            var fontManager = SKFontManager.Default;
            var searchTerms = requestedFont.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Simple list of fonts to skip
            var symbolFonts = new[] { "Webdings", "Wingdings", "Symbol", "Marlett" };

            foreach (var family in fontManager.GetFontFamilies())
            {
                // Skip symbol fonts
                if (symbolFonts.Any(sf => family.Contains(sf, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Check if family contains any of our search terms
                if (searchTerms.Any(term => family.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    var typeface = SKTypeface.FromFamilyName(family, fontStyle);
                    if (typeface != null && !typeface.FamilyName.Equals("Segoe UI", StringComparison.OrdinalIgnoreCase))
                    {
                        Trace.WriteLine($"Using similar font: '{family}' for requested '{requestedFont}'");
                        return typeface;
                    }
                    typeface?.Dispose();
                }
            }

            return null;
        }

        public override void FillDonut(int x, int y, int radius, int thickness, int rotation, int percentage, int span, string color, string backgroundColor, int strokeWidth, string strokeColor)
        {
            thickness = Math.Clamp(thickness, 0, radius);
            rotation = Math.Clamp(rotation, 0, 360);
            percentage = Math.Clamp(percentage, 0, 100);

            float innerRadius = radius - thickness;
            float centerX = x + radius;
            float centerY = y + radius;

            // --- Fill background ---
            if (span == 360)
            {
                using var ringPath = new SKPath();
                ringPath.AddCircle(centerX, centerY, radius, SKPathDirection.Clockwise);
                ringPath.AddCircle(centerX, centerY, innerRadius, SKPathDirection.CounterClockwise);

                using var bgPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = SKColor.Parse(backgroundColor),
                    IsAntialias = true
                };
                Canvas.DrawPath(ringPath, bgPaint);
            }
            else
            {
                FillPie(centerX, centerY, radius, innerRadius, rotation, span, backgroundColor);
            }

            // --- Fill foreground (percentage of span) ---
            if (percentage > 0)
            {
                float angleSpan = percentage * span / 100f;
                if (span == 360 && percentage == 100)
                {
                    using var ringPath = new SKPath();
                    ringPath.AddCircle(centerX, centerY, radius, SKPathDirection.Clockwise);
                    ringPath.AddCircle(centerX, centerY, innerRadius, SKPathDirection.CounterClockwise);

                    using var fillPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = SKColor.Parse(color),
                        IsAntialias = true
                    };
                    Canvas.DrawPath(ringPath, fillPaint);
                }
                else
                {
                    FillPie(centerX, centerY, radius, innerRadius, rotation, angleSpan, color);
                }
            }

            // --- Draw outline (stroke) ---
            if (strokeWidth > 0)
            {
                using var strokePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = SKColor.Parse(strokeColor),
                    StrokeWidth = strokeWidth,
                    IsAntialias = true
                };

                if (span == 360)
                {
                    Canvas.DrawCircle(centerX, centerY, radius - strokeWidth / 2f, strokePaint);
                    Canvas.DrawCircle(centerX, centerY, innerRadius + strokeWidth / 2f, strokePaint);
                }
                else
                {
                    DrawPie(centerX, centerY, radius - strokeWidth / 2f, innerRadius + strokeWidth / 2f, rotation, span, strokeColor, strokeWidth);
                }
            }
        }

        private void FillPie(float centerX, float centerY, float outerRadius, float innerRadius, float rotation, float sweep, string color)
        {
            using var path = new SKPath();

            // Outer arc (clockwise)
            path.ArcTo(
                new SKRect(centerX - outerRadius, centerY - outerRadius, centerX + outerRadius, centerY + outerRadius),
                rotation,
                sweep,
                false
            );

            // Inner arc (counterclockwise)
            path.ArcTo(
                new SKRect(centerX - innerRadius, centerY - innerRadius, centerX + innerRadius, centerY + innerRadius),
                rotation + sweep,
                -sweep,
                false
            );

            path.Close();

            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = SKColor.Parse(color),
                IsAntialias = true
            };

            Canvas.DrawPath(path, paint);
        }

        private void DrawPie(float centerX, float centerY, float outerRadius, float innerRadius, float rotation, float sweep, string color, float strokeWidth)
        {
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = SKColor.Parse(color),
                StrokeWidth = strokeWidth,
                IsAntialias = true
            };

            // Outer arc
            using (var outerArc = new SKPath())
            {
                outerArc.AddArc(
                    new SKRect(
                        centerX - outerRadius, centerY - outerRadius,
                        centerX + outerRadius, centerY + outerRadius),
                    rotation, sweep);
                Canvas.DrawPath(outerArc, paint);
            }

            // Inner arc
            using (var innerArc = new SKPath())
            {
                innerArc.AddArc(
                    new SKRect(
                        centerX - innerRadius, centerY - innerRadius,
                        centerX + innerRadius, centerY + innerRadius),
                    rotation, sweep);
                Canvas.DrawPath(innerArc, paint);
            }

            // Start and end radial lines
            float startRad = rotation * (float)Math.PI / 180f;
            float endRad = (rotation + sweep) * (float)Math.PI / 180f;

            float startOuterX = centerX + outerRadius * (float)Math.Cos(startRad);
            float startOuterY = centerY + outerRadius * (float)Math.Sin(startRad);
            float startInnerX = centerX + innerRadius * (float)Math.Cos(startRad);
            float startInnerY = centerY + innerRadius * (float)Math.Sin(startRad);
            Canvas.DrawLine(startInnerX, startInnerY, startOuterX, startOuterY, paint);

            float endOuterX = centerX + outerRadius * (float)Math.Cos(endRad);
            float endOuterY = centerY + outerRadius * (float)Math.Sin(endRad);
            float endInnerX = centerX + innerRadius * (float)Math.Cos(endRad);
            float endInnerY = centerY + innerRadius * (float)Math.Sin(endRad);
            Canvas.DrawLine(endInnerX, endInnerY, endOuterX, endOuterY, paint);
        }

        private static SKShader? CreateGradient(SKPath path, SKColor color, SKColor gradientColor, SKColor? gradientColor2, float gradientAngle, GradientType gradientType)
        {
            SKShader? shader = null;

            // Get path bounds for gradient
            var bounds = path.Bounds;
            var centerX = bounds.MidX;
            var centerY = bounds.MidY;

            // Use the third color if provided, otherwise use the first color for symmetry
            var thirdColor = gradientColor2 ?? color;

            switch (gradientType)
            {
                case GradientType.Linear:
                    {
                        // Calculate the diagonal length for gradient coverage
                        var diagonal = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
                        var halfDiagonal = diagonal / 2;

                        // Convert angle to radians (subtract from 90 to match standard gradient direction)
                        var angleRad = (90f - gradientAngle) * (float)(Math.PI / 180);

                        // Calculate start and end points based on angle
                        var dx = (float)Math.Cos(angleRad) * halfDiagonal;
                        var dy = (float)Math.Sin(angleRad) * halfDiagonal;

                        var startPoint = new SKPoint(centerX - dx, centerY - dy);
                        var endPoint = new SKPoint(centerX + dx, centerY + dy);

                        shader = SKShader.CreateLinearGradient(
                            startPoint,
                            endPoint,
                            [color, gradientColor, thirdColor],
                            [0f, 0.5f, 1f],
                            SKShaderTileMode.Clamp
                        );
                        break;
                    }

                case GradientType.Sweep:
                    {
                        // Create a sweep gradient (angular/conic gradient)
                        var startAngle = gradientAngle - 90f; // Adjust so 0° starts at top

                        // Create rotation matrix to rotate the gradient
                        var matrix = SKMatrix.CreateRotationDegrees(startAngle, centerX, centerY);

                        // Create sweep gradient with three colors
                        shader = SKShader.CreateSweepGradient(
                            new SKPoint(centerX, centerY),
                            [color, gradientColor, thirdColor, color], // Loop back to first color
                            [0f, 0.33f, 0.67f, 1f]
                        );

                        // Apply rotation
                        shader = shader.WithLocalMatrix(matrix);
                        break;
                    }

                case GradientType.Radial:
                    {
                        // Radial gradient that pulses and overextends
                        var baseRadius = Math.Max(bounds.Width, bounds.Height);

                        var angleRad = gradientAngle * (float)(Math.PI / 180);
                        var pulseFactor = (float)(Math.Sin(angleRad) + 1) / 2;

                        // Add overextension effect - goes from 0.8x to 1.3x the base radius
                        var overextendFactor = 0.8f + (0.5f * pulseFactor);
                        var animatedRadius = baseRadius * overextendFactor;

                        // Animate color positions with slight overshoot
                        var pos1 = Math.Min(0.3f * pulseFactor * 1.2f, 1f);  // Overshoot by 20%
                        var pos2 = Math.Min(0.6f * pulseFactor * 1.1f, 1f);  // Overshoot by 10%

                        shader = SKShader.CreateRadialGradient(
                            new SKPoint(centerX, centerY),
                            animatedRadius,
                            [color, gradientColor, thirdColor, thirdColor],
                            [0f, pos1, pos2, 1f],
                            SKShaderTileMode.Clamp
                        );
                        break;
                    }

                case GradientType.Diamond:
                    {
                        // Create a diamond/square gradient that rotates with the angle
                        var diagonal = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
                        var halfDiagonal = diagonal / 2;

                        var angleRad = gradientAngle * (float)(Math.PI / 180);
                        var perpAngleRad = angleRad + (float)(Math.PI / 2);

                        var dx1 = (float)Math.Cos(angleRad) * halfDiagonal;
                        var dy1 = (float)Math.Sin(angleRad) * halfDiagonal;
                        var dx2 = (float)Math.Cos(perpAngleRad) * halfDiagonal;
                        var dy2 = (float)Math.Sin(perpAngleRad) * halfDiagonal;

                        // Create first gradient with three colors
                        var shader1 = SKShader.CreateLinearGradient(
                            new SKPoint(centerX - dx1, centerY - dy1),
                            new SKPoint(centerX + dx1, centerY + dy1),
                            [thirdColor, gradientColor, color, gradientColor, thirdColor],
                            [0f, 0.25f, 0.5f, 0.75f, 1f],
                            SKShaderTileMode.Clamp
                        );

                        // Create perpendicular gradient
                        var shader2 = SKShader.CreateLinearGradient(
                            new SKPoint(centerX - dx2, centerY - dy2),
                            new SKPoint(centerX + dx2, centerY + dy2),
                            [thirdColor, gradientColor, color, gradientColor, thirdColor],
                            [0f, 0.25f, 0.5f, 0.75f, 1f],
                            SKShaderTileMode.Clamp
                        );

                        shader = SKShader.CreateCompose(shader1, shader2, SKBlendMode.Multiply);
                        shader1.Dispose();
                        shader2.Dispose();
                        break;
                    }

                case GradientType.Reflected:
                    {
                        var diagonal = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
                        var halfDiagonal = diagonal / 2;

                        var reflectAngleRad = gradientAngle * (float)(Math.PI / 180);
                        var reflectDx = (float)Math.Cos(reflectAngleRad) * halfDiagonal;
                        var reflectDy = (float)Math.Sin(reflectAngleRad) * halfDiagonal;

                        shader = SKShader.CreateLinearGradient(
                            new SKPoint(centerX - reflectDx, centerY - reflectDy),
                            new SKPoint(centerX + reflectDx, centerY + reflectDy),
                            [color, gradientColor, thirdColor, gradientColor, color],
                            [0f, 0.25f, 0.5f, 0.75f, 1f],
                            SKShaderTileMode.Clamp
                        );
                        break;
                    }

                case GradientType.Spiral:
                    {
                        // Spiral with three colors for more variety
                        var spiralColors = new List<SKColor>();
                        var spiralPositions = new List<float>();
                        var segments = 9; // Divisible by 3 for three colors

                        for (int i = 0; i <= segments; i++)
                        {
                            var colorIndex = i % 3;
                            spiralColors.Add(colorIndex == 0 ? color : (colorIndex == 1 ? gradientColor : thirdColor));
                            spiralPositions.Add(i / (float)segments);
                        }

                        shader = SKShader.CreateSweepGradient(
                            new SKPoint(centerX, centerY),
                            [.. spiralColors],
                            [.. spiralPositions]
                        );

                        var matrix = SKMatrix.CreateRotationDegrees(gradientAngle, centerX, centerY);
                        shader = shader.WithLocalMatrix(matrix);
                        break;
                    }
            }

            return shader;
        }

        public override void FillPath(SKPath path, SKColor color, SKColor? gradientColor = null, SKColor? gradientColor2 = null,
            float gradientAngle = 90f, GradientType gradientType = GradientType.Linear)
        {
            if (path == null || path.IsEmpty)
                return; // Nothing to fill

            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = color
            };

            if (gradientColor.HasValue)
            {
                using var shader = CreateGradient(path, color, gradientColor.Value, gradientColor2, gradientAngle, gradientType);
                if (shader != null)
                {
                    paint.Shader = shader;
                }
            }

            Canvas.DrawPath(path, paint);
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

        public override void FillRectangle(string color, int x, int y, int width, int height, string? gradientColor = null, bool gradientHorizontal = true, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
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

            Canvas.Save();

            if (rotation != 0)
            {
                // Default to rectangle center if no rotation center specified
                int centerX = rotationCenterX == 0 ? x + width / 2 : rotationCenterX;
                int centerY = rotationCenterY == 0 ? y + height / 2 : rotationCenterY;
                Canvas.RotateDegrees(rotation, centerX, centerY);
            }

            Canvas.DrawRect(x, y, width, height, paint);

            Canvas.Restore();
        }

        public override (float width, float height) MeasureString(string text, string fontName, string fontStyle, int fontSize, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            var typeface = CreateTypeface(fontName, fontStyle, bold, italic);
            using var font = new SKFont(typeface, size: fontSize * FontScale);

            font.MeasureText(text, out var bounds);

            var metrics = font.Metrics;

            float width = bounds.Width;
            float height = metrics.Descent - metrics.Ascent;

            return (width, height);
        }
    }
}
