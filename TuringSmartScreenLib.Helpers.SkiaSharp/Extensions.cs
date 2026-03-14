namespace TuringSmartScreenLib.Helpers.SkiaSharp;

using global::SkiaSharp;

public static class Extensions
{
    public static IScreenBuffer CreateBufferFrom(this IScreen screen, SKBitmap bitmap)
    {
        var buffer = screen.CreateBuffer(bitmap.Width, bitmap.Height);
        buffer.ReadFrom(bitmap);
        return buffer;
    }

    public static IScreenBuffer CreateBufferFrom(this IScreen screen, SKBitmap bitmap, int sx, int sy, int sw, int sh)
    {
        var buffer = screen.CreateBuffer(sw, sh);
        buffer.ReadFrom(bitmap, sx, sy, sw, sh);
        return buffer;
    }

    public static void ReadFrom(this IScreenBuffer buffer, SKBitmap bitmap) =>
        buffer.ReadFrom(bitmap, 0, 0, bitmap.Width, bitmap.Height);

    public static void ReadFrom(this IScreenBuffer buffer, SKBitmap bitmap, int sx, int sy, int sw, int sh)
    {
        for (var y = 0; y < sh; y++)
        {
            for (var x = 0; x < sw; x++)
            {
                var color = bitmap.GetPixel(x + sx, y + sy);
                buffer.SetPixel(x, y, color.Red, color.Green, color.Blue);
            }
        }
    }
}
