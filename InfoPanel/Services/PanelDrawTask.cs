using InfoPanel.Drawing;
using InfoPanel.Models;
using InfoPanel.Utils;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace InfoPanel
{
    public sealed class PanelDrawTask
    {
        public static SKBitmap RenderSK(Profile profile, bool drawSelected = true, double scale = 1, bool cache = true, SKColorType colorType = SKColorType.Bgra8888, SKAlphaType alphaType = SKAlphaType.Premul)
        {
            var bitmap = new SKBitmap(profile.Width, profile.Height, colorType, alphaType);
            
            using var g = SkiaGraphics.FromBitmap(bitmap) as MyGraphics;
            PanelDraw.Run(profile, g, drawSelected, scale, cache, $"DISPLAY-{profile.Guid}");

            return bitmap;
        }

        public static SKBitmap RenderSplashSK(int width, int height, SKColorType colorType = SKColorType.Bgra8888, SKAlphaType alphaType = SKAlphaType.Premul, RotateFlipType rotateFlipType = RotateFlipType.RotateNoneFlipNone)
        {
            var bitmap = new SKBitmap(width, height, colorType, alphaType);
            using var g = SkiaGraphics.FromBitmap(bitmap) as MyGraphics;
            //g.Clear(SKColors.Black);
            //using var logo = LoadBitmapFromResource("logo.png");
            //logo.RotateFlip(rotateFlipType);
            var size = Math.Min(width, height) / 3;
            //g.DrawBitmap(logo, width / 2 - size / 2, height / 2 - size / 2, size, size);
            return bitmap;
        }

        public static Bitmap LoadBitmapFromResource(string resourceName)
        {
            // Construct the URI for the image resource
            var uri = new Uri($"pack://application:,,,/Resources/Images/{resourceName}");

            // Load the image as a BitmapImage
            var bitmapImage = new BitmapImage(uri);

            // Convert the BitmapImage to a Bitmap
            using var memoryStream = new MemoryStream();
            // Create a new encoder to save the bitmap image to a memory stream
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapImage));
            encoder.Save(memoryStream);

            // Create a Bitmap from the memory stream
            using var tempBitmap = new Bitmap(memoryStream);
            // Clone the bitmap to ensure the stream is closed
            return new Bitmap(tempBitmap);
        }
    }
}
