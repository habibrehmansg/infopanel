using InfoPanel.Extensions;
using System;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Timers;
using System.Diagnostics;

namespace InfoPanel.Views.Controls
{
    public class MyImage : Image
    {
        public static readonly DependencyProperty ImagePathProperty =
            DependencyProperty.Register(
                "ImagePath",
                typeof(string),
                typeof(MyImage));

        public string ImagePath
        {
            get => (string)GetValue(ImagePathProperty);
            set => SetValue(ImagePathProperty, value);
        }

        private WriteableBitmap? _writeableBitmap;
        private readonly Timer Timer = new(TimeSpan.FromMilliseconds(16));

        public MyImage()
        {
            Loaded += OnLoaded;
            Unloaded += MyImage_Unloaded;
        }

        private void MyImage_Unloaded(object sender, RoutedEventArgs e)
        {
            Timer.Stop();
            Timer.Elapsed -= Timer_Tick;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeWriteableBitmap();
        }

        private void InitializeWriteableBitmap()
        {
            int width = (int)Width;
            int height = (int)Height;

            if (width > 0 && height > 0)
            {
                _writeableBitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
                this.Source = _writeableBitmap;

                Timer.Elapsed += Timer_Tick;
                Timer.Start();
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            (string currentImagePath, int width, int height) = Dispatcher.Invoke(() =>
            {
                return (ImagePath, (int)Width, (int)Height);
            });

            if (!string.IsNullOrEmpty(currentImagePath) && _writeableBitmap != null)
            {
                using var bitmap = Cache.GetLocalImage(currentImagePath)?.GetBitmapCopy(width, height);

                // Update the WriteableBitmap with the new frame data
                if (bitmap != null)
                {
                    try
                    {
                        if (_writeableBitmap != null)
                        {
                            IntPtr backBuffer = IntPtr.Zero;

                            _writeableBitmap.Dispatcher.Invoke(() =>
                            {
                                if (_writeableBitmap.Width == bitmap.Width && _writeableBitmap.Height == bitmap.Height)
                                {
                                    _writeableBitmap.Lock();
                                    backBuffer = _writeableBitmap.BackBuffer;
                                }
                            });

                            if (backBuffer == IntPtr.Zero)
                            {
                                return;
                            }

                            // copy the pixel data from the bitmap to the back buffer
                            BitmapData bitmapData = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                            int stride = bitmapData.Stride;
                            byte[] pixels = new byte[stride * bitmap.Height];
                            Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
                            Marshal.Copy(pixels, 0, backBuffer, pixels.Length);
                            bitmap.UnlockBits(bitmapData);

                            _writeableBitmap.Dispatcher.Invoke(() =>
                            {
                                _writeableBitmap.AddDirtyRect(new Int32Rect(0, 0, _writeableBitmap.PixelWidth, _writeableBitmap.PixelHeight));
                                _writeableBitmap.Unlock();
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine(ex.Message);
                    }
                    }
            }
        }


    }
}
