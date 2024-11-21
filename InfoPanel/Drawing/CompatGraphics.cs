using InfoPanel.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace InfoPanel.Drawing
{
    internal partial class CompatGraphics(Graphics graphics) : MyGraphics
    {
        private readonly Graphics Graphics = graphics;

        public static CompatGraphics FromImage(Bitmap bitmap)
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

        public override void DrawImage(Bitmap image, int x, int y)
        {
            this.Graphics.DrawImage(image, x, y);
        }

        public override void DrawImage(Bitmap image, int x, int y, int width, int height)
        {
            this.Graphics.DrawImage(image, x, y, width, height);
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

        public override void FillRectangle(string color, int x, int y, int width, int height)
        {
            using var brush = new SolidBrush(ColorTranslator.FromHtml(color));
            this.Graphics.FillRectangle(brush, x, y, width, height);
        }

        public override void Dispose()
        {
            this.Graphics.Dispose();
        }

    }
}
