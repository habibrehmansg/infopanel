using InfoPanel.Drawing;
using InfoPanel.Models;
using SkiaSharp;
using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace InfoPanel
{
    public sealed class PanelDrawTask
    {
        public static SKBitmap RenderSK(Profile profile, bool preview = false, float scale = 1, bool cache = true, SKColorType colorType = SKColorType.Bgra8888, SKAlphaType alphaType = SKAlphaType.Premul)
        {
            var bitmap = new SKBitmap(profile.Width, profile.Height, colorType, alphaType);
            
            using var g = SkiaGraphics.FromBitmap(bitmap) as MyGraphics;
            PanelDraw.Run(profile, g, preview, scale, cache, $"DISPLAY-{profile.Guid}");

            return bitmap;
        }
    }
}
