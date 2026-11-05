using SkiaSharp;

namespace InfoPanel.Shell.Windows;

/// <summary>
/// The notification-area icon's image: the app's coloured logo (<c>Images/logo.png</c>, embedded), as Windows apps show
/// theirs there, the same on a light taskbar and a dark one. macOS keeps its template glyph (<see cref="ShellGlyph"/>).
/// </summary>
internal static class WinTrayLogo
{
    /// <summary>The embedded logo's resource name (<c>InfoPanel.csproj</c>).</summary>
    public const string Resource = "InfoPanel.Shell.logo.png";

    /// <summary>The logo's PNG as embedded: a 1000-pixel square.</summary>
    public static byte[] Source()
    {
        using var stream = typeof(WinTrayLogo).Assembly.GetManifestResourceStream(Resource)
                           ?? throw new InvalidOperationException($"The engine has no {Resource} resource.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>The logo as a PNG <paramref name="pixels"/> square, scaled down through mipmaps so it stays smooth at 16.</summary>
    public static byte[] Png(int pixels)
    {
        using var logo = SKImage.FromEncodedData(Source());
        using var surface = SKSurface.Create(new SKImageInfo(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(logo, SKRect.Create(pixels, pixels), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
