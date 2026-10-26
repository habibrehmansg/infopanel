using SkiaSharp;
using System;

namespace InfoPanel.Extensions
{
    public static partial class SKBitmapExtensions
    {
        public static SKBitmap Resize(this SKBitmap bitmap, int targetWidth, int targetHeight, SKColor backgroundColor = default, bool expand = true)
        {
            // Set default background to transparent if not specified
            if (backgroundColor == default)
                backgroundColor = SKColors.Transparent;

            // Calculate the scaling factor to fit the image within bounds
            float scaleX = (float)targetWidth / bitmap.Width;
            float scaleY = (float)targetHeight / bitmap.Height;
            float scale = Math.Min(scaleX, scaleY);

            // Calculate the scaled dimensions
            int scaledWidth = (int)(bitmap.Width * scale);
            int scaledHeight = (int)(bitmap.Height * scale);

            if(!expand && scale > 1.0f)
            {
                return bitmap.Copy();
            }

            // Calculate position to center the image
            int x = (targetWidth - scaledWidth) / 2;
            int y = (targetHeight - scaledHeight) / 2;

            // Create new bitmap with exact target dimensions
            var result = new SKBitmap(targetWidth, targetHeight);
            using (var canvas = new SKCanvas(result))
            {
                // Fill background
                canvas.Clear(backgroundColor);

                // Draw the resized image centered
                using var paint = new SKPaint();
                paint.IsAntialias = true;

                var destRect = new SKRect(x, y, x + scaledWidth, y + scaledHeight);
                var srcRect = new SKRect(0, 0, bitmap.Width, bitmap.Height);

                canvas.DrawBitmap(bitmap, srcRect, destRect, paint);
            }

            return result;
        }

        public static SKBitmap CreateMaterialNoImageBitmap(int width, int height)
        {
            var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);

            // Material background color
            canvas.Clear(new SKColor(250, 250, 250));

            using var borderPaint = new SKPaint()
            {
                Color = new SKColor(158, 158, 158),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
            };

            canvas.DrawRect(SKRect.Create(width - 1, height - 1), borderPaint);

            var centerX = width / 2f;
            var centerY = height / 2f;
            var minDimension = Math.Min(width, height);

            // Draw material icon background circle
            var circleRadius = minDimension * 0.15f;
            using (var circlePaint = new SKPaint())
            {
                circlePaint.IsAntialias = true;
                circlePaint.Color = new SKColor(224, 224, 224);
                canvas.DrawCircle(centerX, centerY, circleRadius, circlePaint);
            }

            // Draw image icon
            using (var iconPaint = new SKPaint())
            {
                iconPaint.IsAntialias = true;
                iconPaint.Color = new SKColor(158, 158, 158);
                iconPaint.StrokeWidth = 2;
                iconPaint.Style = SKPaintStyle.Stroke;

                var iconSize = circleRadius * 0.6f;
                var iconLeft = centerX - iconSize / 2;
                var iconTop = centerY - iconSize / 2;

                // Draw image frame
                canvas.DrawRect(iconLeft, iconTop, iconSize, iconSize, iconPaint);

                // Draw mountain peaks
                var path = new SKPath();
                path.MoveTo(iconLeft + iconSize * 0.2f, iconTop + iconSize * 0.8f);
                path.LineTo(iconLeft + iconSize * 0.4f, iconTop + iconSize * 0.5f);
                path.LineTo(iconLeft + iconSize * 0.6f, iconTop + iconSize * 0.7f);
                path.LineTo(iconLeft + iconSize * 0.8f, iconTop + iconSize * 0.4f);

                iconPaint.Style = SKPaintStyle.Stroke;
                canvas.DrawPath(path, iconPaint);

                // Draw sun
                iconPaint.Style = SKPaintStyle.Fill;
                canvas.DrawCircle(iconLeft + iconSize * 0.75f, iconTop + iconSize * 0.25f, iconSize * 0.08f, iconPaint);
            }

            return bitmap;
        }
    }
}
