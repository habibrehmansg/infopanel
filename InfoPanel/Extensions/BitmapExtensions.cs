using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;

namespace InfoPanel.Extensions
{
    public static partial class BitmapExtensions
    {
        public static Bitmap EnsureBitmapSize(Bitmap sourceBitmap, int desiredWidth, int desiredHeight)
        {
            // Check if the bitmap exceeds the desired size
            if (sourceBitmap.Width > desiredWidth || sourceBitmap.Height > desiredHeight)
            {
                // Calculate scale factors
                double scaleX = (double)desiredWidth / sourceBitmap.Width;
                double scaleY = (double)desiredHeight / sourceBitmap.Height;

                // Use the smallest scale factor to ensure the image does not exceed desired size
                double scale = Math.Min(scaleX, scaleY);

                // Calculate scaled width and height
                int scaledWidth = (int)(sourceBitmap.Width * scale);
                int scaledHeight = (int)(sourceBitmap.Height * scale);

                // Create a new bitmap of the scaled size
                Bitmap resizedBitmap = new(scaledWidth, scaledHeight, sourceBitmap.PixelFormat);

                using (Graphics graphics = Graphics.FromImage(resizedBitmap))
                {
                    graphics.Clear(Color.Transparent); // Optional background
                    graphics.DrawImage(sourceBitmap, 0, 0, scaledWidth, scaledHeight);
                }

                // Create a new bitmap with the desired dimensions and center the resized image
                Bitmap finalBitmap = new(desiredWidth, desiredHeight, sourceBitmap.PixelFormat);

                using (Graphics graphics = Graphics.FromImage(finalBitmap))
                {
                    graphics.Clear(Color.Transparent); // Optional background

                    int offsetX = (desiredWidth - scaledWidth) / 2;
                    int offsetY = (desiredHeight - scaledHeight) / 2;

                    graphics.DrawImage(resizedBitmap, offsetX, offsetY, scaledWidth, scaledHeight);
                }

                return finalBitmap;
            }
            else
            {
                // Create a new bitmap with the desired dimensions
                Bitmap expandedBitmap = new(desiredWidth, desiredHeight, sourceBitmap.PixelFormat);

                using (Graphics graphics = Graphics.FromImage(expandedBitmap))
                {
                    graphics.Clear(Color.Transparent); // Optional background

                    // Center the original image on the new canvas
                    int offsetX = (desiredWidth - sourceBitmap.Width) / 2;
                    int offsetY = (desiredHeight - sourceBitmap.Height) / 2;

                    graphics.DrawImage(sourceBitmap, offsetX, offsetY, sourceBitmap.Width, sourceBitmap.Height);
                }

                return expandedBitmap;
            }
        }


        public static List<Rectangle> GetChangedSectors(Bitmap bitmap1, Bitmap bitmap2, int sectorWidth, int sectorHeight, int maxSectorWidth = 32, int maxSectorHeight = 32)
        {
            List<Rectangle> changedSectors = new List<Rectangle>();

            // Ensure bitmaps are the same size
            if (bitmap1.Width != bitmap2.Width || bitmap1.Height != bitmap2.Height)
            {
                throw new ArgumentException("Bitmaps are not the same size.");
            }

            int width = bitmap1.Width;
            int height = bitmap1.Height;

            BitmapData data1 = bitmap1.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap1.PixelFormat);
            BitmapData data2 = bitmap2.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, bitmap2.PixelFormat);

            try
            {
                int sectorCountX = (width + sectorWidth - 1) / sectorWidth;
                int sectorCountY = (height + sectorHeight - 1) / sectorHeight;

                // Store sector changes in a concurrent collection for thread safety
                var localChanges = new List<Rectangle>[sectorCountY];
                Parallel.For(0, sectorCountY, sectorY =>
                {
                    localChanges[sectorY] = new List<Rectangle>();
                    for (int sectorX = 0; sectorX < sectorCountX; sectorX++)
                    {
                        int startX = sectorX * sectorWidth;
                        int startY = sectorY * sectorHeight;
                        int currentSectorWidth = Math.Min(sectorWidth, width - startX);
                        int currentSectorHeight = Math.Min(sectorHeight, height - startY);

                        if (!AreSectorsEqual(data1, data2, startX, startY, currentSectorWidth, currentSectorHeight, width))
                        {
                            localChanges[sectorY].Add(new Rectangle(startX, startY, currentSectorWidth, currentSectorHeight));
                        }
                    }
                });

                // Combine results from all threads
                foreach (var sectorList in localChanges)
                {
                    changedSectors.AddRange(sectorList);
                }
            }
            finally
            {
                bitmap1.UnlockBits(data1);
                bitmap2.UnlockBits(data2);
            }

            return CombineRectangles(changedSectors, maxSectorWidth, maxSectorHeight);
        }

        public static unsafe bool AreSectorsEqual(BitmapData data1, BitmapData data2, int startX, int startY, int sectorWidth, int sectorHeight, int bitmapWidth)
        {
            int bytesPerPixel = Image.GetPixelFormatSize(data1.PixelFormat) / 8;
            int startXBytes = startX * bytesPerPixel;
            int sectorWidthBytes = sectorWidth * bytesPerPixel;

            byte* ptr1 = (byte*)data1.Scan0;
            byte* ptr2 = (byte*)data2.Scan0;

            for (int y = startY; y < startY + sectorHeight; y++)
            {
                byte* row1 = ptr1 + y * data1.Stride + startXBytes;
                byte* row2 = ptr2 + y * data2.Stride + startXBytes;

                if (!MemoryCompare(row1, row2, sectorWidthBytes))
                {
                    return false;
                }
            }
            return true;
        }

        private static unsafe bool MemoryCompare(byte* ptr1, byte* ptr2, int length)
        {
            const int blockSize = sizeof(long);
            int blockCount = length / blockSize;
            int remainingBytes = length % blockSize;

            long* longPtr1 = (long*)ptr1;
            long* longPtr2 = (long*)ptr2;

            for (int i = 0; i < blockCount; i++)
            {
                if (longPtr1[i] != longPtr2[i])
                {
                    return false;
                }
            }

            byte* bytePtr1 = (byte*)(longPtr1 + blockCount);
            byte* bytePtr2 = (byte*)(longPtr2 + blockCount);

            for (int i = 0; i < remainingBytes; i++)
            {
                if (bytePtr1[i] != bytePtr2[i])
                {
                    return false;
                }
            }

            return true;
        }


        public static List<Rectangle> CombineRectangles(List<Rectangle> rectangles, int maxWidth, int maxHeight)
        {
            if (rectangles == null || rectangles.Count == 0)
                return new List<Rectangle>();

            // Sort rectangles to make processing predictable
            rectangles = rectangles.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();

            List<Rectangle> result = new List<Rectangle>();

            // Loop through rectangles and merge them
            while (rectangles.Count > 0)
            {
                var current = rectangles[0];
                rectangles.RemoveAt(0);

                bool merged;
                do
                {
                    merged = false;

                    for (int i = 0; i < rectangles.Count; i++)
                    {
                        var candidate = rectangles[i];

                        if (AreAdjacent(current, candidate) && CanMerge(current, candidate, maxWidth, maxHeight))
                        {
                            current = Merge(current, candidate);
                            rectangles.RemoveAt(i);
                            merged = true;
                            break;
                        }
                    }
                } while (merged);

                result.Add(current);
            }

            return result;
        }

        private static bool AreAdjacent(Rectangle a, Rectangle b)
        {
            // Check if rectangles share an edge or corner
            return a.IntersectsWith(b) ||
                   a.Right == b.Left && (a.Bottom >= b.Top && a.Top <= b.Bottom) || // Adjacent on the left-right
                   a.Bottom == b.Top && (a.Right >= b.Left && a.Left <= b.Right);  // Adjacent on the top-bottom
        }

        private static bool CanMerge(Rectangle a, Rectangle b, int maxWidth, int maxHeight)
        {
            var merged = Merge(a, b);
            return merged.Width <= maxWidth && merged.Height <= maxHeight;
        }

        private static Rectangle Merge(Rectangle a, Rectangle b)
        {
            // Create a rectangle that encompasses both a and b
            int x = Math.Min(a.X, b.X);
            int y = Math.Min(a.Y, b.Y);
            int right = Math.Max(a.Right, b.Right);
            int bottom = Math.Max(a.Bottom, b.Bottom);

            return new Rectangle(x, y, right - x, bottom - y);
        }
    }
}
