using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Controls;

namespace InfoPanel.Models
{
    public class LockedBitmap : IDisposable
    {
        private readonly Bitmap Bitmap;
        public readonly int Width;
        public readonly int Height;
        public readonly bool OverrideDpi;
        public readonly float HorizontalResolution;
        public readonly float VerticalResolution;

        private readonly object bitmapLock = new object();
        private bool isDisposed = false;

        public LockedBitmap(Bitmap bitmap, bool overrideDpi = false)
        {
            this.Bitmap = bitmap;
            this.Width = bitmap.Width;
            this.Height = bitmap.Height;
            this.OverrideDpi = overrideDpi;
            this.HorizontalResolution = bitmap.HorizontalResolution;
            this.VerticalResolution = bitmap.VerticalResolution;
        }

        public void Access(Action<Bitmap> access)
        {
            if (isDisposed)
                throw new ObjectDisposedException("LockedImage");

            lock (bitmapLock)
            {
                access(Bitmap);
            }
        }

        public void Dispose()
        {
            if (isDisposed)
                return;

            lock (bitmapLock)
            {
                if (!isDisposed)
                {
                    Bitmap.Dispose();
                    isDisposed = true;
                }
            }
        }
    }
}
