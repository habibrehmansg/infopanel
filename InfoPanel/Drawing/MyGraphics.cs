using InfoPanel.Models;
using System;
using System.Drawing;
using unvell.D2DLib;

namespace InfoPanel.Drawing
{
    internal abstract class MyGraphics: IDisposable
    {
        public abstract void Clear(Color color);
        public abstract (float width, float height) MeasureString(string text, string fontName, int fontSize, 
            bool bold = false, bool italic = false, bool underline = false, bool strikeout = false);
        public abstract void DrawString(string text, string fontName, int fontSize, string color, int x, int y, bool rightAlign = false,
            bool bold = false, bool italic = false, bool underline = false, bool strikeout = false, int width = 0, int height = 0);
        public abstract void DrawImage(LockedImage lockedImage, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0, bool cache = true);
        public abstract void DrawBitmap(Bitmap bitmap, int x, int y);
        public abstract void DrawBitmap(Bitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0);
        public abstract void DrawBitmap(D2DBitmap bitmap, int x, int y);
        public abstract void DrawBitmap(D2DBitmap bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0);
        public abstract void DrawBitmap(D2DBitmapGraphics bitmap, int x, int y, int width, int height, int rotation = 0, int rotationCenterX = 0, int rotationCenterY = 0);
        public abstract void DrawLine(float x1, float y1, float x2, float y2, string color, float strokeWidth);
        public abstract void DrawRectangle(string color, int strokeWidth, int x, int y, int width, int height);
        public abstract void DrawRectangle(Color color, int strokeWidth, int x, int y, int width, int height);
        public abstract void FillRectangle(string color, int x, int y, int width, int height, string? gradientColor = null, bool gradientHorizontal = true);
        public abstract void DrawPath(MyPoint[] points, string color, int strokeWidth);
        public abstract void FillPath(MyPoint[] points, string color);
        public abstract void FillDonut(int x, int y, int radius, int thickness, int rotation, int percentage, int span, string color, string backgroundColor, int strokeWidth, string strokeColor);
        public abstract void Dispose();
    }

    public struct MyPoint()
    {
        public int X { get; set; }
        public int Y { get; set; }

        public MyPoint(int x, int y): this()
        {
            this.X = x;
            this.Y = y;
        }
    }
}
