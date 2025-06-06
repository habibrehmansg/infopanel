//using InfoPanel.Models;
//using SkiaSharp;
//using System;
//using System.Drawing;
//using System.Drawing.Drawing2D;
//using System.Drawing.Text;
//using unvell.D2DLib;

//namespace InfoPanel.Drawing
//{
//    internal partial class CompatGraphics(Graphics graphics) : MyGraphics
//    {
//        private readonly Graphics Graphics = graphics;

//        public static CompatGraphics FromBitmap(Bitmap bitmap)
//        {
//            var graphics = Graphics.FromImage(bitmap);
//            graphics.SmoothingMode = SmoothingMode.AntiAlias;
//            graphics.InterpolationMode = InterpolationMode.Bilinear;
//            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;

//            return new CompatGraphics(graphics);
//        }
//        public override void Clear(Color color)
//        {
//            this.Graphics.Clear(color);
//        }

//        public override (float width, float height) MeasureString(string text, string fontName, string fontStyle, int fontSize, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
//        {
//            var style =
//                                     (bold ? FontStyle.Bold : FontStyle.Regular) |
//                                     (italic ? FontStyle.Italic : FontStyle.Regular) |
//                                     (underline ? FontStyle.Underline : FontStyle.Regular) |
//                                     (strikeout ? FontStyle.Strikeout : FontStyle.Regular);

//            using var font = new Font(fontName, fontSize, style);

//            var size = this.Graphics.MeasureString(text, font, 0, StringFormat.GenericTypographic);
//            return (size.Width, size.Height);
//        }

//        public override void DrawString(string text, string fontName, string fontStyle, int fontSize, string color, int x, int y, bool rightAlign = false, bool centerAlign = false,
//            bool bold = false, bool italic = false, bool underline = false, bool strikeout = false, int width = 0, int height = 0)
//        {
//            var style =
//                                       (bold ? FontStyle.Bold : FontStyle.Regular) |
//                                       (italic ? FontStyle.Italic : FontStyle.Regular) |
//                                       (underline ? FontStyle.Underline : FontStyle.Regular) |
//                                       (strikeout ? FontStyle.Strikeout : FontStyle.Regular);

//            using var font = new Font(fontName, fontSize, style);
//            using var brush = new SolidBrush(ColorTranslator.FromHtml(color));

//            using StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone();

//            format.Alignment = StringAlignment.Near;

//            if (rightAlign)
//            {
//                format.Alignment = StringAlignment.Far;
//            }

//            if (centerAlign && width > 0)
//            {
//                format.Alignment = StringAlignment.Center;
//            }

//            format.FormatFlags = StringFormatFlags.NoWrap;
//            format.Trimming = StringTrimming.EllipsisCharacter;

//            if (width == 0 && height == 0)
//            {
//                this.Graphics.DrawString(text, font, brush, new PointF(x, y), format);
//            }
//            else
//            {
//                this.Graphics.DrawString(text, font, brush, new RectangleF(x, y, width, height), format);
//            }
//        }

//        public override void DrawImage(LockedImage lockedImage, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool cache = true)
//        {
//            lockedImage.Access(bitmap =>
//            {
//                if (bitmap != null)
//                    this.DrawBitmap(bitmap, x, y, width, height, rotation, rotationCenterX, rotationCenterY);
//            }, cache);
//        }

//        public override void DrawBitmap(Bitmap bitmap, int x, int y)
//        {
//            this.DrawBitmap(bitmap, x, y, bitmap.Width, bitmap.Height);
//        }

//        public override void DrawBitmap(Bitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
//        {
//            if (rotation != 0)
//            {
//                var state = Graphics.Save();
//                // Move the origin to the center of the image
//                Graphics.TranslateTransform(rotationCenterX, rotationCenterY);

//                // Rotate the graphics context
//                Graphics.RotateTransform(rotation);

//                // Move the origin back
//                Graphics.TranslateTransform(-rotationCenterX, -rotationCenterY);
//                this.Graphics.DrawImage(bitmap, x, y, width, height);

//                // Restore the graphics context to the state before rotation
//                Graphics.Restore(state);
//            }
//            else
//            {
//                this.Graphics.DrawImage(bitmap, x, y, width, height);
//            }
//        }

//        public override void DrawBitmap(D2DBitmapGraphics bitmapGraphics, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
//        {
//            throw new NotSupportedException();
//        }

//        public override void DrawBitmap(D2DBitmap bitmap, int x, int y)
//        {
//            throw new NotSupportedException();
//        }

//        public override void DrawBitmap(D2DBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
//        {
//            throw new NotSupportedException();
//        }
//        public override void DrawBitmap(SKBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool flipX = false, bool flipY = false)
//        {
//            throw new NotSupportedException();
//        }

//        public override void DrawLine(float x1, float y1, float x2, float y2, string color, float strokeWidth)
//        {
//            using var pen = new Pen(ColorTranslator.FromHtml(color), strokeWidth);
//            this.Graphics.DrawLine(pen, new PointF(x1, y1), new PointF(x2, y2));
//        }

//        public override void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
//        {
//            using var pen = new Pen(color, strokeWidth);
//            this.Graphics.DrawRectangle(pen, x, y, width, height);
//        }

//        public override void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
//        {
//            this.DrawRectangle(ColorTranslator.FromHtml(color), strokeWidth, x, y, width, height);
//        }

//        public override void FillRectangle(string color, int x, int y, int width, int height, string? gradientColor = null, bool gradientHorizontal = true, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0)
//        {
//            if (gradientColor != null)
//            {
//                if (gradientHorizontal)
//                {
//                    using var brush = new LinearGradientBrush(new Rectangle(x, y, width + 1, height), ColorTranslator.FromHtml(color), ColorTranslator.FromHtml(gradientColor), LinearGradientMode.Horizontal);
//                    this.Graphics.FillRectangle(brush, x, y, width, height);
//                }
//                else
//                {
//                    using var brush = new LinearGradientBrush(new Rectangle(x, y, width, height + 1), ColorTranslator.FromHtml(gradientColor), ColorTranslator.FromHtml(color), LinearGradientMode.Vertical);
//                    this.Graphics.FillRectangle(brush, x, y, width, height);
//                }
//            }
//            else
//            {
//                using var brush = new SolidBrush(ColorTranslator.FromHtml(color));
//                this.Graphics.FillRectangle(brush, x, y, width, height);
//            }
//        }

//        private GraphicsPath CreateGraphicsPath(MyPoint[] points)
//        {
//            var path = new GraphicsPath();

//            for (int i = 0; i < points.Length; i++)
//            {
//                if (i == 0)
//                {
//                    path.StartFigure();
//                }
//                else
//                {
//                    path.AddLine(new Point(points[i - 1].X, points[i - 1].Y), new Point(points[i].X, points[i].Y));
//                }
//            }

//            path.CloseFigure();

//            return path;
//        }

//        public override void DrawPath(MyPoint[] points, string color, int strokeWidth)
//        {
//            using var path = CreateGraphicsPath(points);
//            using var pen = new Pen(ColorTranslator.FromHtml(color), strokeWidth);
//            this.Graphics.DrawPath(pen, path);
//        }

//        public override void FillPath(MyPoint[] points, string color)
//        {
//            using var path = CreateGraphicsPath(points);
//            using var brush = new SolidBrush(ColorTranslator.FromHtml(color));
//            this.Graphics.FillPath(brush, path);
//        }

//        public override void FillDonut(int x, int y, int radius, int thickness, int rotation, int percentage, int span, string color, string backgroundColor, int strokeWidth, string strokeColor)
//        {
//            thickness = Math.Clamp(thickness, 0, radius);
//            rotation = Math.Clamp(rotation, 0, 360);
//            percentage = Math.Clamp(percentage, 0, 100);

//            var innerRadius = radius - thickness;

//            // Create ring path (outer ellipse minus inner ellipse)
//            using var ringPath = new GraphicsPath();
//            ringPath.AddEllipse(x, y, 2 * (radius), 2 * (radius));
//            ringPath.AddEllipse(x + (radius - innerRadius), y + (radius - innerRadius), 2 * (innerRadius), 2 * (innerRadius));

//            //debug
//            //Graphics.FillPath(Brushes.Yellow, ringPath);

//            // Clip to the ring region
//            using var ringRegion = new Region(ringPath);
//            Graphics.SetClip(ringRegion, CombineMode.Replace);

//            //fill background
//            using var backgroundBrush = new SolidBrush(ColorTranslator.FromHtml(backgroundColor));
//            Graphics.FillPie(backgroundBrush, x + strokeWidth, y + strokeWidth, 2 * (radius - strokeWidth), 2 * (radius - strokeWidth), rotation, span);

//            // Draw the pie slice
//            float angleSpan = percentage * span / 100f;
//            using var colorBrush = new SolidBrush(ColorTranslator.FromHtml(color));
//            Graphics.FillPie(colorBrush, x + strokeWidth, y + strokeWidth, 2 * (radius - strokeWidth), 2 * (radius - strokeWidth), rotation, angleSpan);

//            //Graphics.ResetClip();

//            //draw outline
//            if (strokeWidth > 0)
//            {
//                using var pen = new Pen(ColorTranslator.FromHtml(strokeColor), strokeWidth);

//                if (span == 360)
//                {
//                    Graphics.DrawEllipse(pen, x + strokeWidth, y + strokeWidth, 2 * (radius - strokeWidth), 2 * (radius - strokeWidth));
//                    Graphics.DrawEllipse(pen, x + (radius - innerRadius) - strokeWidth, y + (radius - innerRadius) - strokeWidth, 2 * (innerRadius + strokeWidth), 2 * (innerRadius + strokeWidth));
//                }
//                else
//                {
//                    Graphics.DrawPie(pen, x + strokeWidth, y + strokeWidth, 2 * (radius - strokeWidth), 2 * (radius - strokeWidth), rotation, span);
//                    Graphics.DrawPie(pen, x + (radius - innerRadius) - strokeWidth, y + (radius - innerRadius) - strokeWidth, 2 * (innerRadius + strokeWidth), 2 * (innerRadius + strokeWidth), rotation, span);
//                }
//            }

//            Graphics.ResetClip();
//        }

//        public override void Dispose()
//        {
//            this.Graphics.Dispose();
//        }

//    }
//}
