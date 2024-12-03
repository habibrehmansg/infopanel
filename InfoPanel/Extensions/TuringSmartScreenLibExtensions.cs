using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using TuringSmartScreenLib;

namespace InfoPanel.Extensions
{
    public static class TuringSmartScreenLibExtensions
    {
        public static IScreenBuffer CreateBufferFrom(this IScreen screen, Bitmap bitmap)
        {
            IScreenBuffer screenBuffer = screen.CreateBuffer(bitmap.Width, bitmap.Height);
            screenBuffer.ReadFrom(bitmap);
            return screenBuffer;
        }

        public static IScreenBuffer CreateBufferFrom(this IScreen screen, Bitmap bitmap, int sx, int sy, int sw, int sh)
        {
            IScreenBuffer screenBuffer = screen.CreateBuffer(sw, sh);
            screenBuffer.ReadFrom(bitmap, sx, sy, sw, sh);
            return screenBuffer;
        }

        public static void ReadFrom(this IScreenBuffer buffer, Bitmap bitmap)
        {
            buffer.ReadFrom(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        }


        public static void ReadFrom(this IScreenBuffer buffer, Bitmap bitmap, int sx, int sy, int sw, int sh)
        {
            var rect = new Rectangle(sx, sy, sw, sh);
            var bitmapData = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);

            try
            {
                int bytesPerPixel = Image.GetPixelFormatSize(bitmap.PixelFormat) / 8;
                int stride = bitmapData.Stride;
                IntPtr scan0 = bitmapData.Scan0;

                unsafe
                {
                    byte* pixelPtr = (byte*)scan0;

                    for (int y = 0; y < sh; y++)
                    {
                        for (int x = 0; x < sw; x++)
                        {
                            byte* pixel = pixelPtr + y * stride + x * bytesPerPixel;

                            byte b = pixel[0]; // Blue
                            byte g = pixel[1]; // Green
                            byte r = pixel[2]; // Red
                                               // Alpha is available if needed (bytesPerPixel == 4)

                            buffer.SetPixel(x, y, r, g, b);
                        }
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }




    }
}
