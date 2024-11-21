using InfoPanel.Models;
using System;
using System.Drawing;
using unvell.D2DLib;
using unvell.D2DLib.WinForm;

namespace InfoPanel.Drawing
{
    internal abstract class MyGraphics: IDisposable
    {
        public abstract void Clear(Color color);
        public abstract (float width, float height) MeasureString(string text, string fontName, int fontSize, 
            bool bold = false, bool italic = false, bool underline = false, bool strikeout = false);
        public abstract void DrawString(string text, string fontName, int fontSize, string color, int x, int y, bool rightAlign = false,
            bool bold = false, bool italic = false, bool underline = false, bool strikeout = false);
        public abstract void DrawImage(LockedImage lockedImage, int x, int y, int width, int height);
        public abstract void DrawImage(Bitmap image, int x, int y);
        public abstract void DrawImage(Bitmap image, int x, int y, int width, int height);
        public abstract void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height);
        public abstract void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height);
        public abstract void FillRectangle(string color, int x, int y, int width, int height);

        public abstract void Dispose();
    }
}
