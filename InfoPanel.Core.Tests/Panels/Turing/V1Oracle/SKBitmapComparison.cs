using SkiaSharp;

namespace InfoPanel.Core.Tests.Panels.Turing.V1Oracle;

/// <summary>
/// InfoPanel 1.4.x's <c>InfoPanel/Extensions/SKBitmapComparison.cs</c> (the 1.4.x tip, <c>c71f0f00</c>), copied verbatim
/// but for the comparison of a sector's rows: 1.4.x compared them through pointers (<c>AreSectorsEqual</c>,
/// <c>MemoryCompare</c>, <c>:101-166</c>), here as spans over the same bytes (this project compiles no unsafe code), the
/// same answer. Test-only.
/// </summary>
internal static class V1SKBitmapComparison
{
    // :9-72
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

        var pixels1 = bitmap1.GetPixelSpan().ToArray();
        var pixels2 = bitmap2.GetPixelSpan().ToArray();

        int bytesPerPixel = bitmap1.BytesPerPixel;
        int rowBytes = bitmap1.RowBytes;

        int sectorCountX = (width + sectorWidth - 1) / sectorWidth;
        int sectorCountY = (height + sectorHeight - 1) / sectorHeight;

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

        changedSectors.AddRange(localChanges);

        if (sectorWidth >= maxSectorWidth && sectorHeight >= maxSectorHeight)
        {
            return changedSectors;
        }

        return CombineRectangles(changedSectors, maxSectorWidth, maxSectorHeight);
    }

    // :101-119 and :121-166, as spans
    private static bool AreSectorsEqual(byte[] pixels1, byte[] pixels2, int startX, int startY, int sectorWidth, int sectorHeight, int rowBytes, int bytesPerPixel)
    {
        int startXBytes = startX * bytesPerPixel;
        int sectorWidthBytes = sectorWidth * bytesPerPixel;

        for (int y = 0; y < sectorHeight; y++)
        {
            int rowOffset = (startY + y) * rowBytes + startXBytes;
            if (!pixels1.AsSpan(rowOffset, sectorWidthBytes).SequenceEqual(pixels2.AsSpan(rowOffset, sectorWidthBytes)))
            {
                return false;
            }
        }
        return true;
    }

    // :168-212
    public static List<SKRectI> CombineRectangles(List<SKRectI> rectangles, int maxWidth, int maxHeight)
    {
        if (rectangles == null || rectangles.Count == 0)
            return [];

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

    // :214-222
    private static bool AreAdjacent(SKRectI a, SKRectI b)
    {
        return (a.Right == b.Left && a.Bottom >= b.Top && a.Top <= b.Bottom) ||
               (a.Left == b.Right && a.Bottom >= b.Top && a.Top <= b.Bottom) ||
               (a.Bottom == b.Top && a.Right >= b.Left && a.Left <= b.Right) ||
               (a.Top == b.Bottom && a.Right >= b.Left && a.Left <= b.Right) ||
               a.IntersectsWith(b);
    }

    // :224-228
    private static bool CanMerge(SKRectI a, SKRectI b, int maxWidth, int maxHeight)
    {
        var merged = Merge(a, b);
        return merged.Width <= maxWidth && merged.Height <= maxHeight;
    }

    // :230-239
    private static SKRectI Merge(SKRectI a, SKRectI b)
    {
        int left = Math.Min(a.Left, b.Left);
        int top = Math.Min(a.Top, b.Top);
        int right = Math.Max(a.Right, b.Right);
        int bottom = Math.Max(a.Bottom, b.Bottom);

        return new SKRectI(left, top, right, bottom);
    }
}
