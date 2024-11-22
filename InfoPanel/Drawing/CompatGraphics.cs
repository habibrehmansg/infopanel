using InfoPanel.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using unvell.D2DLib;

namespace InfoPanel.Drawing
{
    internal partial class CompatGraphics(Graphics graphics) : MyGraphics
    {
        private readonly Graphics Graphics = graphics;

        public static CompatGraphics FromBitmap(Bitmap bitmap)
        {
           var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.TextRenderingHint = TextRenderingHint.AntiAlias;

            return new CompatGraphics(Graphics.FromImage(bitmap));
        }
        public override void Clear(Color color)
        {
            this.Graphics.Clear(color);
        }

        public override (float width, float height) MeasureString(string text, string fontName, int fontSize, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            var fontStyle =
                                     (bold ? FontStyle.Bold : FontStyle.Regular) |
                                     (italic ? FontStyle.Italic : FontStyle.Regular) |
                                     (underline ? FontStyle.Underline : FontStyle.Regular) |
                                     (strikeout ? FontStyle.Strikeout : FontStyle.Regular);

            using var font = new Font(fontName, fontSize, fontStyle);
            var size = this.Graphics.MeasureString(text, font);
            return (size.Width, size.Height);
        }

        public override void DrawString(string text, string fontName, int fontSize, string color, int x, int y, bool rightAlign = false,
            bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            var fontStyle =
                                       (bold ? FontStyle.Bold : FontStyle.Regular) |
                                       (italic ? FontStyle.Italic : FontStyle.Regular) |
                                       (underline ? FontStyle.Underline : FontStyle.Regular) |
                                       (strikeout ? FontStyle.Strikeout : FontStyle.Regular);

            using var font = new Font(fontName, fontSize, fontStyle);
            using var brush = new SolidBrush(ColorTranslator.FromHtml(color));

            using var format = new StringFormat();
            if (rightAlign)
            {
                format.Alignment = StringAlignment.Far;
            }
            else
            {
                format.Alignment = StringAlignment.Near;
            }

            this.Graphics.DrawString(text, font, brush, new PointF(x, y), format);
        }

        public override void DrawImage(LockedImage lockedImage, int x, int y, int width, int height)
        {
            lockedImage.Access(bitmap =>
            {
                if (bitmap != null)
                    this.Graphics.DrawImage(bitmap, x, y, width, height);
            });
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y)
        {
            this.DrawBitmap(bitmap, x, y, bitmap.Width, bitmap.Height);
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y, int width, int height)
        {
            this.Graphics.DrawImage(bitmap, x, y, width, height);
        }

        public override void DrawBitmap(D2DBitmapGraphics bitmapGraphics, int x, int y, int width, int height)
        {
            throw new System.NotSupportedException();
        }

        public override void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height)
        {
            using var pen = new Pen(color, strokeWidth);
            this.Graphics.DrawRectangle(pen, x, y, width, height);
        }

        public override void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height)
        {
            using var pen = new Pen(ColorTranslator.FromHtml(color), strokeWidth);
            this.Graphics.DrawRectangle(pen, x, y, width, height);
        }

        public override void FillRectangle(string color, int x, int y, int width, int height, string? gradientColor = null)
        {
            if (gradientColor != null)
            {
                using var brush = new LinearGradientBrush(new Rectangle(x, y, width, height), ColorTranslator.FromHtml(color), ColorTranslator.FromHtml(gradientColor), LinearGradientMode.Vertical);
                this.Graphics.FillRectangle(brush, x, y, width, height);
            }
            else
            {
                using var brush = new SolidBrush(ColorTranslator.FromHtml(color));
                this.Graphics.FillRectangle(brush, x, y, width, height);
            }
        }

        private GraphicsPath CreateGraphicsPath(MyPoint[] points)
        {
            var path = new GraphicsPath();

            for (int i = 0; i < points.Length; i++)
            {
                if (i == 0)
                {
                    path.StartFigure();
                }
                else
                {
                    path.AddLine(new Point(points[i - 1].X, points[i - 1].Y), new Point(points[i].X, points[i].Y));
                }
            }

            path.CloseFigure();

            return path;
        }

        public override void DrawPath(MyPoint[] points, string color, int strokeWidth)
        {
            using var path = CreateGraphicsPath(points);
            using var pen = new Pen(ColorTranslator.FromHtml(color), strokeWidth);
            this.Graphics.DrawPath(pen, path);
        }

        public override void FillPath(MyPoint[] points, string color)
        {
            using var path = CreateGraphicsPath(points);
            using var brush = new SolidBrush(ColorTranslator.FromHtml(color));
            this.Graphics.FillPath(brush, path);
        }

        public override void Dispose()
        {
            this.Graphics.Dispose();
        }

    }
}
