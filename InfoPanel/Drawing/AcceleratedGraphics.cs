using InfoPanel.Models;
using SkiaSharp;
using System;
using System.Drawing;
using System.Numerics;
using unvell.D2DLib;
using unvell.D2DLib.WinForm;

namespace InfoPanel.Drawing
{
    internal partial class AcceleratedGraphics(D2DGraphics d2dGraphics, IntPtr handle, float fontScale = 1, int textXOffset = 0, int textYOffSet = 0) : MyGraphics
    {
        public readonly IntPtr Handle = handle;
        public readonly D2DGraphics D2DGraphics = d2dGraphics;
        public readonly D2DDevice D2DDevice = d2dGraphics.Device;
        public readonly int TextXOffset = textXOffset;
        public readonly int TextYOffset = textYOffSet;
        public readonly float FontScale = fontScale;

        public static AcceleratedGraphics FromD2DGraphics(D2DGraphics d2dGraphics, AcceleratedGraphics acceleratedGraphics)
        {
            return new AcceleratedGraphics(d2dGraphics, acceleratedGraphics.Handle, acceleratedGraphics.FontScale, acceleratedGraphics.TextXOffset, acceleratedGraphics.TextYOffset);
        }

        public static AcceleratedGraphics FromD2DGraphics(D2DGraphics d2DGraphics, IntPtr handle, float fontScale = 1, int textXOffset = 0, int textYOffSet = 0)
        {
            return new AcceleratedGraphics(d2DGraphics, handle, fontScale, textXOffset, textYOffSet);
        }

        public override void Clear(Color color)
        {
            this.D2DGraphics.Clear(D2DColor.FromGDIColor(color));
        }

        private D2DTextFormat CreateTextFormat(string fontName, string fontStyle, float fontSize, bool rightAlign = false, bool centerAlign = false, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            DWriteTextAlignment alignment = DWriteTextAlignment.Leading;

            if (rightAlign)
            {
                alignment = DWriteTextAlignment.Trailing;
            }

            if (centerAlign)
            {
                alignment = DWriteTextAlignment.Center;
            }

            var skiaTypeFace = SkiaGraphics.CreateTypeface(fontName, fontStyle, bold, italic);

            D2DFontWeight weight;
            if (Enum.IsDefined(typeof(D2DFontWeight), skiaTypeFace.FontWeight))
            {
                weight = (D2DFontWeight)skiaTypeFace.FontWeight;
            }
            else
            {
                weight = D2DFontWeight.Normal;
            }

            return this.D2DDevice.CreateTextFormat(skiaTypeFace.FamilyName, fontSize,
                weight, skiaTypeFace.IsItalic ? D2DFontStyle.Italic : D2DFontStyle.Normal, ConvertToD2DFontStretch(skiaTypeFace),
                alignment);
        }

        private static D2DFontStretch ConvertToD2DFontStretch(SKTypeface typeface)
        {
            // Standard font stretch values (1-9)
            return typeface.FontWidth switch
            {
                1 => D2DFontStretch.UltraCondensed,
                2 => D2DFontStretch.ExtraCondensed,
                3 => D2DFontStretch.Condensed,
                4 => D2DFontStretch.SemiCondensed,
                5 => D2DFontStretch.Normal,
                6 => D2DFontStretch.SemiExpanded,
                7 => D2DFontStretch.Expanded,
                8 => D2DFontStretch.ExtraExpanded,
                9 => D2DFontStretch.UltraExpanded,
                _ => D2DFontStretch.Normal
            };
        }

        public override (float width, float height) MeasureString(string text, string fontName, string fontStyle, int fontSize, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            using var textFormat = CreateTextFormat(fontName, fontStyle, fontSize, false, false, bold, italic, underline, strikeout);
            var textSize = new D2DSize(float.MaxValue, 0);
            this.D2DGraphics.MeasureText(text, textFormat, ref textSize);
            return (textSize.width, textSize.height);
        }

        public override void DrawString(string text, string fontName, string fontStyle, int fontSize, string color, int x, int y,
            bool rightAlign = false, bool centerAlign = false, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false,
            int width = 0, int height = 0)
        {
            using var textFormat = CreateTextFormat(fontName, fontStyle, fontSize * FontScale, rightAlign, width > 0 && centerAlign, bold, italic, underline, strikeout);
            using var textColor = this.D2DDevice.CreateSolidColorBrush(D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)));

            var rect = new D2DRect(x + TextXOffset, y + TextYOffset, width == 0 ? float.MaxValue : width, height);

            if (rightAlign && width == 0 && height == 0)
            {
                rect.X = 0;
                rect.Width = x - TextXOffset;
            }
            else if (width > 0)
            {
                //add truncate support manually
                var textSize = new D2DSize(float.MaxValue, 0);
                this.D2DGraphics.MeasureText(text, textFormat, ref textSize);

                if (textSize.width > width)
                {
                    string ellipsis = "...";
                    string truncatedText = text;

                    while (truncatedText.Length > 0)
                    {
                        var tempText = truncatedText + ellipsis;

                        textSize = new D2DSize(float.MaxValue, 0);
                        this.D2DGraphics.MeasureText(tempText, textFormat, ref textSize);

                        if (textSize.width <= width)
                        {
                            text = tempText;
                            break;
                        }

                        // Remove the last character
                        truncatedText = truncatedText[..^1];
                    }
                }
            }

            this.D2DGraphics.DrawText(text, textColor,
                   textFormat,
                   rect);
        }

        public override void DrawImage(LockedImage lockedImage, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool cache = true, string cacheHint = "default")
        {
            lockedImage.AccessD2D(this.D2DDevice, this.Handle, width, height, d2dBitmap =>
            {
                this.DrawBitmap(d2dBitmap, x, y, width, height, rotation, rotationCenterX, rotationCenterY);
            }, cache, cacheHint);
        }

        public override void DrawBitmap(D2DBitmap bitmap, int x, int y)
        {
            this.DrawBitmap(bitmap, x, y, (int)bitmap.Width, (int)bitmap.Height);
        }

        public override void DrawBitmap(D2DBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            if (rotation != 0)
            {
                // Save the current transform state
                var originalTransform = this.D2DGraphics.GetTransform();

                // Default to rectangle center if no rotation center specified
                int centerX = rotationCenterX == 0 ? x + width / 2 : rotationCenterX;
                int centerY = rotationCenterY == 0 ? y + height / 2 : rotationCenterY;

                // Create a rotation matrix
                var radians = (float)(rotation * (Math.PI / 180.0));
                var rotationMatrix = Matrix3x2.CreateRotation(radians, new Vector2(centerX, centerY));

                // Apply the rotation transformation
                this.D2DGraphics.SetTransform(rotationMatrix);
                this.D2DGraphics.DrawBitmap(bitmap, new D2DRect(x, y, width, height));
                // Undo the transformation
                this.D2DGraphics.SetTransform(originalTransform);
            }
            else
            {
                this.D2DGraphics.DrawBitmap(bitmap, new D2DRect(x, y, width, height));
            }
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y)
        {
            this.DrawBitmap(bitmap, x, y, bitmap.Width, bitmap.Height);
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            this.D2DGraphics.DrawBitmap(bitmap, new D2DRect(x, y, width, height), alpha: true);
        }

        public override void DrawBitmap(D2DBitmapGraphics bitmapGraphics, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            this.D2DGraphics.DrawBitmap(bitmapGraphics, new D2DRect(x, y, width, height));
        }

        public override void DrawBitmap(SKBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool flipX = false, bool flipY = false)
        {
            throw new NotSupportedException();
        }

        public override void DrawLine(float x1, float y1, float x2, float y2, string color, float strokeWidth)
        {
            this.D2DGraphics.DrawLine(x1, y1, x2, y2, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)), strokeWidth);
        }

        public override void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            // Save the current transform state
            var originalTransform = this.D2DGraphics.GetTransform();

            if (rotation != 0)
            {
                // Default to rectangle center if no rotation center specified
                int centerX = rotationCenterX == 0 ? x + width / 2 : rotationCenterX;
                int centerY = rotationCenterY == 0 ? y + height / 2 : rotationCenterY;

                // Create a rotation matrix
                var radians = (float)(rotation * (Math.PI / 180.0));
                var rotationMatrix = Matrix3x2.CreateRotation(radians, new Vector2(centerX, centerY));

                // Apply the rotation transformation
                this.D2DGraphics.SetTransform(rotationMatrix);
            }

            this.D2DGraphics.DrawRectangle(new D2DRect(x, y, width, height), D2DColor.FromGDIColor(color), strokeWidth);

            // Undo the transformation
            this.D2DGraphics.SetTransform(originalTransform);
        }

        public override void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            this.DrawRectangle(ColorTranslator.FromHtml(color), strokeWidth, x, y, width, height, rotation, rotationCenterX, rotationCenterY);
        }

        public override void FillRectangle(string color, int x, int y, int width, int height, string? gradientColor = null, bool gradientHorizontal = true, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
        {
            // Save the current transform state
            var originalTransform = this.D2DGraphics.GetTransform();

            if (rotation != 0)
            {
                // Default to rectangle center if no rotation center specified
                int centerX = rotationCenterX == 0 ? x + width / 2 : rotationCenterX;
                int centerY = rotationCenterY == 0 ? y + height / 2 : rotationCenterY;

                // Create a rotation matrix
                var radians = (float)(rotation * (Math.PI / 180.0));
                var rotationMatrix = Matrix3x2.CreateRotation(radians, new Vector2(centerX, centerY));

                // Apply the rotation transformation
                this.D2DGraphics.SetTransform(rotationMatrix);
            }

            if (gradientColor != null)
            {
                if (gradientHorizontal)
                {
                    using var brush = this.D2DDevice.CreateLinearGradientBrush(
                     new Vector2(x, y),
                     new Vector2(x + width, y),
                     [
                        new(0, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color))),
                        new(1, D2DColor.FromGDIColor(ColorTranslator.FromHtml(gradientColor)))
                     ]);
                    this.D2DGraphics.FillRectangle(new D2DRect(x, y, width, height), brush);
                }
                else
                {
                    using var brush = this.D2DDevice.CreateLinearGradientBrush(
                     new Vector2(x, y),
                     new Vector2(x, y + height),
                     [
                        new(0, D2DColor.FromGDIColor(ColorTranslator.FromHtml(gradientColor))),
                        new(1, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)))
                     ]);
                    this.D2DGraphics.FillRectangle(new D2DRect(x, y, width, height), brush);
                }

            }
            else
            {
                this.D2DGraphics.FillRectangle(x, y, width, height, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)));
            }

            // Undo the transformation
            this.D2DGraphics.SetTransform(originalTransform);
        }

        public override void FillDonut(int x, int y, int radius, int thickness, int rotation, int percentage, int span, string color, string backgroundColor, int strokeWidth, string strokeColor)
        {
            thickness = Math.Clamp(thickness, 0, radius);
            rotation = Math.Clamp(rotation, 0, 360);
            percentage = Math.Clamp(percentage, 0, 100);
            span = Math.Clamp(span, 0, 360);

            var innerRadius = radius - thickness;

            // Create geometries for the outer and inner ellipses
            using var outerGeometry = this.D2DDevice.CreateEllipseGeometry(
                new Vector2(x + radius, y + radius),
                new D2DSize(radius, radius)
            );

            using var innerGeometry = this.D2DDevice.CreateEllipseGeometry(
                new Vector2(x + radius, y + radius),
                new D2DSize(innerRadius, innerRadius)
            );

            // Combine the outer and inner geometries to create a ring
            using var ringGeometry = this.D2DDevice.CreateCombinedGeometry(
                outerGeometry,
                innerGeometry,
                D2D1CombineMode.Exclude
            );

            //constrain drawing to this layer
            using var layer = this.D2DGraphics.PushLayer(ringGeometry);

            //debug
            //this.D2DGraphics.DrawEllipse(new Vector2(x + radius, y + radius), radius, radius, D2DColor.Red);
            //this.D2DGraphics.DrawEllipse(new Vector2(x + radius, y + radius), innerRadius, innerRadius, D2DColor.Red);
            //this.D2DGraphics.FillPath(ringGeometry, D2DColor.Yellow);

            // Fill background
            if (span == 360)
            {
                this.D2DGraphics.FillPath(ringGeometry, D2DColor.FromGDIColor(ColorTranslator.FromHtml(backgroundColor)));
            }
            else
            {
                using var backgroundPath = this.D2DDevice.CreatePieGeometry(
                    new Vector2(x + radius, y + radius),
                    new D2DSize(radius * 2, radius * 2),
                    rotation, rotation + span
                );
                this.D2DGraphics.FillPath(backgroundPath, D2DColor.FromGDIColor(ColorTranslator.FromHtml(backgroundColor)));
            }

            // Adjusted angleSpan considering the span
            var angleSpan = percentage * span / 100f;

            //Fill usage
            using var path = this.D2DDevice.CreatePieGeometry(
                new Vector2(x + radius, y + radius),
                new D2DSize(radius * 2, radius * 2),
                rotation, rotation + angleSpan
            );
            this.D2DGraphics.FillPath(path, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)));

            // Draw outline
            if (strokeWidth > 0)
            {
                if (span == 360)
                {
                    this.D2DGraphics.DrawPath(ringGeometry, D2DColor.FromGDIColor(ColorTranslator.FromHtml(strokeColor)), strokeWidth);
                }
                else
                {
                    using var strokePath = this.D2DDevice.CreatePieGeometry(
                    new Vector2(x + radius, y + radius),
                    new D2DSize(radius * 2, radius * 2),
                    rotation, rotation + span
                );
                    this.D2DGraphics.DrawPath(strokePath, D2DColor.FromGDIColor(ColorTranslator.FromHtml(strokeColor)), strokeWidth);

                    using var strokePath2 = this.D2DDevice.CreatePieGeometry(
                     new Vector2(x + radius, y + radius),
                     new D2DSize(innerRadius * 2, innerRadius * 2),
                     rotation, rotation + span
                 );
                    this.D2DGraphics.DrawPath(strokePath2, D2DColor.FromGDIColor(ColorTranslator.FromHtml(strokeColor)), strokeWidth);
                }
            }

            this.D2DGraphics.PopLayer();
        }


        private D2DPathGeometry CreateGraphicsPath(MyPoint[] points)
        {
            var vectors = new Vector2[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                vectors[i] = new Vector2(points[i].X, points[i].Y);
            }

            var d2dPath = this.D2DDevice.CreatePathGeometry();
            d2dPath.SetStartPoint(vectors[0]);
            d2dPath.AddLines(vectors);
            d2dPath.ClosePath();

            return d2dPath;
        }

        private D2DPathGeometry CreateGraphicsPath(SKPath path)
        {
            var points = path.Points;
            var vectors = new Vector2[points.Length];
            for (int i = 0; i < points.Length; i++)
            {
                vectors[i] = new Vector2(points[i].X, points[i].Y);
            }

            var d2dPath = this.D2DDevice.CreatePathGeometry();
            d2dPath.SetStartPoint(vectors[0]);
            d2dPath.AddLines(vectors);
            d2dPath.ClosePath();

            return d2dPath;
        }

        public override void DrawPath(SKPath path, SKColor color, int strokeWidth, SKColor? gradientColor = null, SKColor? gradientColor2 = null, float gradientAngle = 90f, GradientType gradientType = GradientType.Linear)
        {
            using var d2dPath = CreateGraphicsPath(path);
            this.D2DGraphics.DrawPath(d2dPath, new D2DColor(color.Alpha, color.Red, color.Green, color.Blue), strokeWidth);

        }

        public override void DrawPath(MyPoint[] points, string color, int strokeWidth)
        {
            using var d2dPath = CreateGraphicsPath(points);
            this.D2DGraphics.DrawPath(d2dPath, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)), strokeWidth);
        }

        public override void FillPath(MyPoint[] points, string color)
        {
            using var d2dPath = CreateGraphicsPath(points);
            this.D2DGraphics.FillPath(d2dPath, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)));
        }

        public override void FillPath(SKPath path, SKColor color, SKColor? gradientColor = null, SKColor? gradientColor2 = null, float gradientAngle = 90f, GradientType gradientType = GradientType.Linear)
        {
            //gradient not supported
            using var d2dPath = CreateGraphicsPath(path);
            this.D2DGraphics.FillPath(d2dPath, new D2DColor(color.Alpha, color.Red, color.Green, color.Blue));
        }

        public override void Dispose()
        {
            //do nothing
        }
    }
}
