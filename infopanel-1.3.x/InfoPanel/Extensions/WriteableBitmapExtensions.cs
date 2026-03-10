using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace InfoPanel.Extensions
{
    public static class WriteableBitmapExtensions
    {
        public static BitmapImage ToBitmapImage(this WriteableBitmap wbm)
        {
            BitmapImage bmImage = new();
            using (MemoryStream stream = new())
            {
                PngBitmapEncoder encoder = new();
                encoder.Frames.Add(BitmapFrame.Create(wbm));
                encoder.Save(stream);
                bmImage.BeginInit();
                bmImage.CacheOption = BitmapCacheOption.OnLoad;
                bmImage.StreamSource = stream;
                bmImage.EndInit();
                bmImage.Freeze();
            }
            return bmImage;
        }

        public static void UpdateFrom(this WriteableBitmap target, WriteableBitmap source)
        {
            if (target.PixelWidth != source.PixelWidth || target.PixelHeight != source.PixelHeight)
                throw new ArgumentException("Bitmaps must have the same dimensions");

            // Calculate stride and buffer size
            int stride = source.PixelWidth * 4; // Assuming 32bpp
            byte[] pixels = new byte[stride * source.PixelHeight];

            // Copy from source
            source.CopyPixels(pixels, stride, 0);

            // Write to target
            target.WritePixels(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight),
                               pixels, stride, 0);
        }
    }
}
