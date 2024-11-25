using InfoPanel.Models;
using InfoPanel.Views.Components;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Numerics;
using unvell.D2DLib;
using unvell.D2DLib.WinForm;
using WinRT;

namespace InfoPanel.Drawing
{
    internal partial class AcceleratedGraphics(D2DGraphics d2dGraphics, IntPtr handle, float fontScale = 1, int textXOffset = 0, int textYOffSet = 0) : MyGraphics
    {
        public readonly IntPtr Handle = handle;
        public readonly D2DGraphics D2DGraphics = d2dGraphics;
        public readonly D2DDevice D2DDevice = d2dGraphics.Device!;
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

        private D2DTextFormat CreateTextFormat(string fontName, float fontSize, bool rightAlign = false, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            return this.D2DDevice.CreateTextFormat(fontName, fontSize,
                bold ? D2DFontWeight.Bold : D2DFontWeight.Normal, italic ? D2DFontStyle.Italic : D2DFontStyle.Normal, D2DFontStretch.Normal,
                rightAlign ? DWriteTextAlignment.Trailing : DWriteTextAlignment.Leading);
        }

        public override (float width, float height) MeasureString(string text, string fontName, int fontSize, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            using var textFormat = CreateTextFormat(fontName, fontSize * FontScale, false, bold, italic, underline, strikeout);
            var textSize = new D2DSize(float.MaxValue, 0);
            this.D2DGraphics.MeasureText(text, textFormat, ref textSize);
            return (textSize.width + 10, textSize.height);
        }

        public override void DrawString(string text, string fontName, int fontSize, string color, int x, int y, bool rightAlign = false, bool bold = false, bool italic = false, bool underline = false, bool strikeout = false)
        {
            using var textFormat = CreateTextFormat(fontName, fontSize * FontScale, rightAlign, bold, italic, underline, strikeout);
            using var textColor = this.D2DDevice.CreateSolidColorBrush(D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)));

            var rect = new D2DRect(x + TextXOffset, y + TextYOffset, float.MaxValue, 0);

            if(rightAlign)
            {
                rect.X = 0;
                rect.Width = x - TextXOffset;
            }

            this.D2DGraphics.DrawText(text, textColor,
                   textFormat,
                   rect);
        }

        public override void DrawImage(LockedImage lockedImage, int x, int y, int width, int height, bool cache = true)
        {
            lockedImage.AccessD2D(this.D2DDevice, this.Handle, d2dBitmap =>
            {
                if (d2dBitmap != null)
                    this.D2DGraphics.DrawBitmap(d2dBitmap, new D2DRect(x, y, width, height));
            });
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y)
        {
            this.DrawBitmap(bitmap, x, y, bitmap.Width, bitmap.Height);
        }

        public override void DrawBitmap(Bitmap bitmap, int x, int y, int width, int height)
        {
            this.D2DGraphics.DrawBitmap(bitmap, new D2DRect(x, y, width, height), alpha: true);
        }

        public override void DrawBitmap(D2DBitmapGraphics bitmapGraphics, int x, int y, int width, int height)
        {
            this.D2DGraphics.DrawBitmap(bitmapGraphics, new D2DRect(x, y, width, height));
        }

        public override void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height)
        {
            this.D2DGraphics.DrawRectangle(new D2DRect(x, y, width, height), D2DColor.FromGDIColor(color), strokeWidth);
        }

        public override void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height)
        {
            this.D2DGraphics.DrawRectangle(x, y, width, height, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)), strokeWidth);
        }

        public override void FillRectangle(string color, int x, int y, int width, int height, string? gradientColor = null)
        {
            if (gradientColor != null)
            {
                using var brush = this.D2DDevice.CreateLinearGradientBrush(
                    new Vector2(0, 0), new Vector2(0, height),
                   [
                            new(0, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color))),
                            new(1, D2DColor.FromGDIColor(ColorTranslator.FromHtml(gradientColor)))
                   ]);
                this.D2DGraphics.FillRectangle(new D2DRect(x, y, width, height), brush);
            }
            else
            {
                this.D2DGraphics.FillRectangle(x, y, width, height, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)));
            }
        }

        //public void test()
        //{
        //    this.D2DGraphics.FillEllipse(0, 0, 100, 100, D2DColor.Yellow);
        //    this.D2DGraphics.DrawEllipse(0, 0, 100, 100, D2DColor.Black);

        //    var figureOrigin = new Vector2(50, 50);
        //    var figureSize = new D2DSize(100, 100);

        //    float currentAngle = 90;
        //    var angleSpan = 0.9f * 360;
        //    var path = D2DDevice.CreatePieGeometry(figureOrigin, figureSize, currentAngle, currentAngle + angleSpan);
        //    D2DGraphics.FillPath(path, D2DColor.Green);


        //    this.D2DGraphics.FillEllipse(10, 10, 80, 80, D2DColor.Yellow);

        //}

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

        public override void Dispose()
        {
            //do nothing
        }
    }
}
