using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

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

        public static List<Point> GetChangedSectors(Bitmap bitmap1, Bitmap bitmap2, int sectorWidth, int sectorHeight)
        {
            List<Point> changedSectors = [];

            // Ensure bitmaps are the same size
            if (bitmap1.Width != bitmap2.Width || bitmap1.Height != bitmap2.Height)
            {
                throw new ArgumentException("Bitmaps are not the same size.");
            }

            int width = bitmap1.Width;
            int height = bitmap1.Height;

            // Lock bitmap data for faster access
            BitmapData data1 = bitmap1.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap1.PixelFormat);
            BitmapData data2 = bitmap2.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap2.PixelFormat);

            try
            {
                // Parallelize the outer loop for performance on large images
                Parallel.For(0, height / sectorHeight + 1, (sectorY) =>
                {
                    for (int sectorX = 0; sectorX < width / sectorWidth + 1; sectorX++)
                    {
                        int startX = sectorX * sectorWidth;
                        int startY = sectorY * sectorHeight;
                        int currentSectorWidth = Math.Min(sectorWidth, width - startX);
                        int currentSectorHeight = Math.Min(sectorHeight, height - startY);

                        if (!AreSectorsEqual(data1, data2, startX, startY, currentSectorWidth, currentSectorHeight, width))
                        {
                            lock (changedSectors) // Ensure thread safety when adding to the list
                            {
                                changedSectors.Add(new Point(startX, startY));
                            }
                        }
                    }
                });
            }
            finally
            {
                bitmap1.UnlockBits(data1);
                bitmap2.UnlockBits(data2);
            }

            return changedSectors;
        }

        public static unsafe bool AreSectorsEqual(BitmapData data1, BitmapData data2, int startX, int startY, int sectorWidth, int sectorHeight, int bitmapWidth)
        {
            int bytesPerPixel = Image.GetPixelFormatSize(data1.PixelFormat) / 8;
            byte* ptr1 = (byte*)data1.Scan0;
            byte* ptr2 = (byte*)data2.Scan0;

            for (int y = startY; y < startY + sectorHeight; y++)
            {
                for (int x = startX; x < startX + sectorWidth; x++)
                {
                    byte* pixel1 = ptr1 + (y * data1.Stride) + (x * bytesPerPixel);
                    byte* pixel2 = ptr2 + (y * data2.Stride) + (x * bytesPerPixel);

                    for (int i = 0; i < bytesPerPixel; i++)
                    {
                        if (pixel1[i] != pixel2[i])
                        {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        public static Bitmap GetSectorBitmap(Bitmap sourceBitmap, Point sectorTopLeft, int sectorWidth, int sectorHeight)
        {
            // Define the rectangle for the sector
            Rectangle sector = new(sectorTopLeft.X, sectorTopLeft.Y, sectorWidth, sectorHeight);

            // Clone the sector into a new Bitmap
            Bitmap sectorBitmap = sourceBitmap.Clone(sector, sourceBitmap.PixelFormat);

            return sectorBitmap;
        }
    }
}
