namespace InfoPanel.Panels.Turing;

/// <summary>A rectangle of a frame, its right and bottom edges excluded.</summary>
internal readonly record struct Sector(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    /// <summary>Whether the two overlap (SkiaSharp's <c>SKRectI.IntersectsWith</c>).</summary>
    public bool IntersectsWith(Sector other) => Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
}

/// <summary>
/// The parts of a frame that changed since the last one sent, as 1.4's <c>SKBitmapComparison.GetChangedSectors</c> found
/// them for its serial Turing panels: the frame cut into sectors (the last row and column of them cut short at the
/// frame's edge), each compared row by row, the changed ones merged with their neighbours while the union stays within
/// the merge limit (<c>CombineRectangles</c>). 1.4 compared the rendered bitmaps; this compares the frames as sent.
/// </summary>
internal static class SectorDiff
{
    public static List<Sector> Changed(ReadOnlySpan<byte> sent, ReadOnlySpan<byte> frame, int width, int height, int bytesPerPixel,
        int sectorWidth, int sectorHeight, int maxSectorWidth, int maxSectorHeight)
    {
        var rowBytes = width * bytesPerPixel;
        if (sent.Length < rowBytes * height || frame.Length < rowBytes * height)
            throw new ArgumentException("A frame is smaller than its size.");
        var countX = (width + sectorWidth - 1) / sectorWidth;
        var countY = (height + sectorHeight - 1) / sectorHeight;
        var changed = new List<Sector>();
        for (var sy = 0; sy < countY; sy++)
        {
            for (var sx = 0; sx < countX; sx++)
            {
                var x = sx * sectorWidth;
                var y = sy * sectorHeight;
                var w = Math.Min(sectorWidth, width - x);
                var h = Math.Min(sectorHeight, height - y);
                if (!Equal(sent, frame, x, y, w, h, rowBytes, bytesPerPixel))
                    changed.Add(new Sector(x, y, x + w, y + h));
            }
        }
        if (sectorWidth >= maxSectorWidth && sectorHeight >= maxSectorHeight)
            return changed;
        return Combine(changed, maxSectorWidth, maxSectorHeight);
    }

    private static bool Equal(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int x, int y, int w, int h, int rowBytes, int bytesPerPixel)
    {
        for (var row = 0; row < h; row++)
        {
            var offset = ((y + row) * rowBytes) + (x * bytesPerPixel);
            if (!a.Slice(offset, w * bytesPerPixel).SequenceEqual(b.Slice(offset, w * bytesPerPixel)))
                return false;
        }
        return true;
    }

    /// <summary>1.4's <c>CombineRectangles</c>: sorted by top, then left; each grows by every later one it touches or overlaps while the union fits.</summary>
    private static List<Sector> Combine(List<Sector> sectors, int maxWidth, int maxHeight)
    {
        if (sectors.Count == 0)
            return [];
        var sorted = sectors.OrderBy(r => r.Top).ThenBy(r => r.Left).ToList();
        var result = new List<Sector>();
        var processed = new bool[sorted.Count];
        for (var i = 0; i < sorted.Count; i++)
        {
            if (processed[i])
                continue;
            var current = sorted[i];
            processed[i] = true;
            bool merged;
            do
            {
                merged = false;
                for (var j = i + 1; j < sorted.Count; j++)
                {
                    if (processed[j])
                        continue;
                    var candidate = sorted[j];
                    if (Adjacent(current, candidate) && Fits(Merge(current, candidate), maxWidth, maxHeight))
                    {
                        current = Merge(current, candidate);
                        processed[j] = true;
                        merged = true;
                    }
                }
            } while (merged);
            result.Add(current);
        }
        return result;
    }

    private static bool Adjacent(Sector a, Sector b) =>
        (a.Right == b.Left && a.Bottom >= b.Top && a.Top <= b.Bottom) ||
        (a.Left == b.Right && a.Bottom >= b.Top && a.Top <= b.Bottom) ||
        (a.Bottom == b.Top && a.Right >= b.Left && a.Left <= b.Right) ||
        (a.Top == b.Bottom && a.Right >= b.Left && a.Left <= b.Right) ||
        a.IntersectsWith(b);

    private static bool Fits(Sector s, int maxWidth, int maxHeight) => s.Width <= maxWidth && s.Height <= maxHeight;

    private static Sector Merge(Sector a, Sector b) =>
        new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
