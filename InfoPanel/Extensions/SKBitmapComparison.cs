using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public static class SKBitmapComparison
{
    public static List<SKRectI> GetChangedSectors(SKBitmap bitmap1, SKBitmap bitmap2, int sectorWidth, int sectorHeight, int maxSectorWidth = 32, int maxSectorHeight = 32)
    {
        List<SKRectI> changedSectors = new List<SKRectI>();

        // Ensure bitmaps are the same size
        if (bitmap1.Width != bitmap2.Width || bitmap1.Height != bitmap2.Height)
        {
            throw new ArgumentException("Bitmaps are not the same size.");
        }

        // Ensure bitmaps have the same color type
        if (bitmap1.ColorType != bitmap2.ColorType)
        {
            throw new ArgumentException("Bitmaps have different color types.");
        }

        int width = bitmap1.Width;
        int height = bitmap1.Height;

        // Get direct access to pixel data
        IntPtr pixels1 = bitmap1.GetPixels();
        IntPtr pixels2 = bitmap2.GetPixels();

        if (pixels1 == IntPtr.Zero || pixels2 == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot access bitmap pixels.");
        }

        // Calculate bytes per pixel based on color type
        int bytesPerPixel = GetBytesPerPixel(bitmap1.ColorType);
        int rowBytes = bitmap1.RowBytes;

        int sectorCountX = (width + sectorWidth - 1) / sectorWidth;
        int sectorCountY = (height + sectorHeight - 1) / sectorHeight;

        // Use concurrent collection for better thread safety
        var localChanges = new System.Collections.Concurrent.ConcurrentBag<SKRectI>();

        Parallel.For(0, sectorCountY, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, sectorY =>
        {
            for (int sectorX = 0; sectorX < sectorCountX; sectorX++)
            {
                int startX = sectorX * sectorWidth;
                int startY = sectorY * sectorHeight;
                int currentSectorWidth = Math.Min(sectorWidth, width - startX);
                int currentSectorHeight = Math.Min(sectorHeight, height - startY);

                if (!AreSectorsEqual(pixels1, pixels2, startX, startY, currentSectorWidth, currentSectorHeight, rowBytes, bytesPerPixel))
                {
                    localChanges.Add(new SKRectI(startX, startY, startX + currentSectorWidth, startY + currentSectorHeight));
                }
            }
        });

        // Convert concurrent bag to list
        changedSectors.AddRange(localChanges);

        if(sectorWidth >= maxSectorWidth && sectorHeight >= maxSectorHeight)
        {
            return changedSectors;
        }

        return CombineRectangles(changedSectors, maxSectorWidth, maxSectorHeight);
    }

    private static int GetBytesPerPixel(SKColorType colorType)
    {
        return colorType switch
        {
            SKColorType.Unknown => throw new NotSupportedException("Unknown color type"),
            SKColorType.Alpha8 => 1,
            SKColorType.Rgb565 => 2,
            SKColorType.Argb4444 => 2,
            SKColorType.Rgba8888 => 4,
            SKColorType.Bgra8888 => 4,
            SKColorType.Rgb888x => 4,
            SKColorType.Rgba1010102 => 4,
            SKColorType.Rgb101010x => 4,
            SKColorType.Gray8 => 1,
            SKColorType.RgbaF16 => 8,
            SKColorType.RgbaF16Clamped => 8,
            SKColorType.RgbaF32 => 16,
            SKColorType.Rg88 => 2,
            SKColorType.AlphaF16 => 2,
            SKColorType.RgF16 => 4,
            SKColorType.Alpha16 => 2,
            SKColorType.Rg1616 => 4,
            SKColorType.Rgba16161616 => 8,
            _ => throw new NotSupportedException($"Unsupported color type: {colorType}")
        };
    }

    public static unsafe bool AreSectorsEqual(IntPtr pixels1, IntPtr pixels2, int startX, int startY, int sectorWidth, int sectorHeight, int rowBytes, int bytesPerPixel)
    {
        byte* ptr1 = (byte*)pixels1.ToPointer();
        byte* ptr2 = (byte*)pixels2.ToPointer();

        int startXBytes = startX * bytesPerPixel;
        int sectorWidthBytes = sectorWidth * bytesPerPixel;

        // Use vectorized comparison for better performance
        for (int y = 0; y < sectorHeight; y++)
        {
            int rowOffset = (startY + y) * rowBytes + startXBytes;
            if (!MemoryCompare(ptr1 + rowOffset, ptr2 + rowOffset, sectorWidthBytes))
            {
                return false;
            }
        }
        return true;
    }

    private static unsafe bool MemoryCompare(byte* ptr1, byte* ptr2, int length)
    {
        // Use 64-bit comparison for better performance on 64-bit systems
        const int blockSize = sizeof(long);
        int blockCount = length / blockSize;
        int remainingBytes = length % blockSize;

        long* longPtr1 = (long*)ptr1;
        long* longPtr2 = (long*)ptr2;

        // Unroll loop for better performance
        int i = 0;
        for (; i < blockCount - 3; i += 4)
        {
            if (longPtr1[i] != longPtr2[i] ||
                longPtr1[i + 1] != longPtr2[i + 1] ||
                longPtr1[i + 2] != longPtr2[i + 2] ||
                longPtr1[i + 3] != longPtr2[i + 3])
            {
                return false;
            }
        }

        // Handle remaining blocks
        for (; i < blockCount; i++)
        {
            if (longPtr1[i] != longPtr2[i])
            {
                return false;
            }
        }

        // Handle remaining bytes
        byte* bytePtr1 = (byte*)(longPtr1 + blockCount);
        byte* bytePtr2 = (byte*)(longPtr2 + blockCount);

        for (i = 0; i < remainingBytes; i++)
        {
            if (bytePtr1[i] != bytePtr2[i])
            {
                return false;
            }
        }

        return true;
    }

    public static List<SKRectI> CombineRectangles(List<SKRectI> rectangles, int maxWidth, int maxHeight)
    {
        if (rectangles == null || rectangles.Count == 0)
            return [];

        // Sort rectangles to make processing predictable
        rectangles = [.. rectangles.OrderBy(r => r.Top).ThenBy(r => r.Left)];

        List<SKRectI> result = [];
        HashSet<int> processed = [];

        for (int i = 0; i < rectangles.Count; i++)
        {
            if (processed.Contains(i))
                continue;

            var current = rectangles[i];
            processed.Add(i);

            bool merged;
            do
            {
                merged = false;

                for (int j = i + 1; j < rectangles.Count; j++)
                {
                    if (processed.Contains(j))
                        continue;

                    var candidate = rectangles[j];

                    if (AreAdjacent(current, candidate) && CanMerge(current, candidate, maxWidth, maxHeight))
                    {
                        current = Merge(current, candidate);
                        processed.Add(j);
                        merged = true;
                    }
                }
            } while (merged);

            result.Add(current);
        }

        return result;
    }

    private static bool AreAdjacent(SKRectI a, SKRectI b)
    {
        // Check if rectangles share an edge
        return (a.Right == b.Left && a.Bottom >= b.Top && a.Top <= b.Bottom) ||  // Adjacent on the left-right
               (a.Left == b.Right && a.Bottom >= b.Top && a.Top <= b.Bottom) ||  // Adjacent on the right-left
               (a.Bottom == b.Top && a.Right >= b.Left && a.Left <= b.Right) || // Adjacent on the top-bottom
               (a.Top == b.Bottom && a.Right >= b.Left && a.Left <= b.Right) || // Adjacent on the bottom-top
               a.IntersectsWith(b);                                              // Overlapping
    }

    private static bool CanMerge(SKRectI a, SKRectI b, int maxWidth, int maxHeight)
    {
        var merged = Merge(a, b);
        return merged.Width <= maxWidth && merged.Height <= maxHeight;
    }

    private static SKRectI Merge(SKRectI a, SKRectI b)
    {
        // Create a rectangle that encompasses both a and b
        int left = Math.Min(a.Left, b.Left);
        int top = Math.Min(a.Top, b.Top);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);

        return new SKRectI(left, top, right, bottom);
    }
}