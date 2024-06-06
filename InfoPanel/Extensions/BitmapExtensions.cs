using System;
using System.Drawing;

namespace InfoPanel.Extensions
{
    public static class BitmapExtensions
    {
        public static Bitmap EnsureBitmapSize(Bitmap sourceBitmap, int desiredWidth, int desiredHeight)
        {
            // Check if the bitmap is already the desired size
            if (sourceBitmap.Width == desiredWidth && sourceBitmap.Height == desiredHeight)
            {
                return new Bitmap(sourceBitmap);
            }
            else
            {
                // Create a new bitmap of the desired size
                Bitmap resizedBitmap = new Bitmap(desiredWidth, desiredHeight);

                // Calculate scale factors
                double scaleX = (double)desiredWidth / sourceBitmap.Width;
                double scaleY = (double)desiredHeight / sourceBitmap.Height;

                // Use the smallest scale factor to preserve aspect ratio
                double scale = Math.Min(scaleX, scaleY);

                // Calculate scaled width and height
                int scaledWidth = (int)(sourceBitmap.Width * scale);
                int scaledHeight = (int)(sourceBitmap.Height * scale);

                // Draw the source bitmap onto the new bitmap
                using (Graphics graphics = Graphics.FromImage(resizedBitmap))
                {
                    // Clear with background color (optional)
                    graphics.Clear(Color.Transparent);

                    // Calculate x and y positions to center the image
                    int offsetX = (desiredWidth - scaledWidth) / 2;
                    int offsetY = (desiredHeight - scaledHeight) / 2;

                    // Draw image with offset
                    graphics.DrawImage(sourceBitmap, offsetX, offsetY, scaledWidth, scaledHeight);
                }

                return resizedBitmap;
            }
        }
    }
}
