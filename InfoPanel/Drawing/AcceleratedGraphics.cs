using InfoPanel.Models;
using InfoPanel.Views.Components;
using System;
using System.Drawing;
using unvell.D2DLib;
using unvell.D2DLib.WinForm;

namespace InfoPanel.Drawing
{
    internal partial class AcceleratedGraphics(D2DGraphics d2dGraphics, IntPtr handle, float fontScale = 1, int textXOffset = 0, int textYOffSet = 0) : MyGraphics
    {
        private readonly IntPtr Handle = handle;
        private readonly D2DGraphics D2DGraphics = d2dGraphics;
        private readonly D2DDevice D2DDevice = d2dGraphics.Device!;
        private readonly float TextXOffset = textXOffset;
        private readonly float TextYOffset = textYOffSet;
        private readonly float FontScale = fontScale;

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

            var textSize = new D2DSize(float.MaxValue, float.MaxValue);
            this.D2DGraphics.MeasureText(text, textFormat, ref textSize);

            textSize.width += 10;

            var rect = new D2DRect(rightAlign? x - TextXOffset - textSize.width : x + TextXOffset, y + TextYOffset, textSize.width, textSize.height);
           
            this.D2DGraphics.DrawText(text, textColor,
                   textFormat,
                   rect);
        }

        public override void DrawImage(LockedImage lockedImage, int x, int y, int width, int height)
        {
            lockedImage.AccessD2D(this.D2DDevice, handle, d2dBitmap =>
            {
                if (d2dBitmap != null)
                    this.D2DGraphics.DrawBitmap(d2dBitmap, new D2DRect(x, y, width, height));
            });
        }

        public override void DrawImage(Bitmap image, int x, int y)
        {
            this.D2DGraphics.DrawBitmap(image, x, y, alpha: true);
        }

        public override void DrawImage(Bitmap image, int x, int y, int width, int height)
        {
            this.D2DGraphics.DrawBitmap(image, new D2DRect(x, y, width, height), alpha: true);
        }
        public override void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height)
        {
            this.D2DGraphics.DrawRectangle(new D2DRect(x, y, width, height), D2DColor.FromGDIColor(color), strokeWidth);
        }

        public override void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height)
        {
            this.D2DGraphics.DrawRectangle(x, y, width, height, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)), strokeWidth);
        }

        public override void FillRectangle(string color, int x, int y, int width, int height)
        {
            this.D2DGraphics.FillRectangle(x, y, width, height, D2DColor.FromGDIColor(ColorTranslator.FromHtml(color)));
        }

        public override void Dispose()
        {
            //do nothing
        }
    }
}
