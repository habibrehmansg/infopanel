using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.Extensions
{
    public static class CanvasExtensions
    {
        public static void DrawPicture(this SKCanvas canvas, SKPicture picture, float x, float y, float width, float height, float rotation = 0, float? rotationCenterX = null, float? rotationCenterY = null)
        {
            var bounds = picture.CullRect;

            canvas.Save();

            // Apply transformations
            int centerX = (int)(rotationCenterX ?? x + width / 2);
            int centerY = (int)(rotationCenterY ?? y + width / 2);

            canvas.Translate(centerX, centerY);
            canvas.RotateDegrees(rotation);
            canvas.Translate(-centerX, -centerY);

            canvas.Translate(x, y);
            canvas.Scale(width / bounds.Width, height / bounds.Height);

            canvas.DrawPicture(picture);

            canvas.Restore();
        }
    }
}
