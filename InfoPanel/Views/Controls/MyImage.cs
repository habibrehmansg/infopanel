using InfoPanel.Models;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace InfoPanel.Views.Controls
{
    public class MyImage : Image
    {
        public static readonly DependencyProperty ImagePathProperty =
            DependencyProperty.Register(
                "ImagePath",
                typeof(string),
                typeof(MyImage),
                new PropertyMetadata(string.Empty, OnImagePathChanged));

        public string ImagePath
        {
            get => (string)GetValue(ImagePathProperty);
            set => SetValue(ImagePathProperty, value);
        }

        private readonly Timer Timer = new(TimeSpan.FromMilliseconds(33));

        public MyImage()
        {
            Loaded += OnLoaded;
            Unloaded += MyImage_Unloaded;
        }

        private static void OnImagePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if(d is MyImage myImage)
            {
                string newImagePath = (string)e.NewValue;

                if (!string.IsNullOrEmpty(newImagePath))
                {
                    if (Cache.GetLocalImage(newImagePath) is LockedImage lockedImage)
                    {
                        if (lockedImage.Frames > 1)
                        {
                            myImage.Timer.Start();
                        }
                        else
                        {
                            myImage.Timer.Stop();
                            myImage.Timer_Tick(null, null);
                        }
                    }
                }
                else
                {
                    myImage.Source = null;
                    myImage.Timer.Stop();
                }
            }
        }

        private void MyImage_Unloaded(object sender, RoutedEventArgs e)
        {
            Timer.Stop();
            Timer.Elapsed -= Timer_Tick;
            Timer.Dispose();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Timer.Elapsed += Timer_Tick;
        }

        private void Timer_Tick(object? sender, EventArgs? e)
        {
            (string currentImagePath, int width, int height, ImageSource source) = Dispatcher.Invoke(() =>
            {
                return (ImagePath, (int)Width, (int)Height, Source);
            });

            if (!string.IsNullOrEmpty(currentImagePath))
            {
                var image = Cache.GetLocalImage(currentImagePath);

                WriteableBitmap? writeableBitmap = null;

                if (image != null)
                {
                    if (image.IsSvg)
                    {
                        image.AccessSVG(picture =>
                        {
                            var bounds = picture.CullRect;
                            writeableBitmap = picture.ToWriteableBitmap(new SKSizeI((int)bounds.Width, (int)bounds.Height));
                            writeableBitmap.Freeze();
                        });
                    }
                    else
                    {
                        double imageAspectRatio = (double)image.Width / image.Height;
                        double containerAspectRatio = (double)width / height;

                        int targetWidth, targetHeight;

                        if (imageAspectRatio > containerAspectRatio)
                        {
                            // Image is wider relative to its height than the container
                            // Fit to width, adjust height
                            targetWidth = width;
                            targetHeight = (int)Math.Ceiling(width / imageAspectRatio);
                        }
                        else
                        {
                            // Image is taller relative to its width than the container
                            // Fit to height, adjust width
                            targetWidth = (int)Math.Ceiling(height * imageAspectRatio);
                            targetHeight = height;
                        }

                        image.AccessSK(targetWidth, targetHeight, bitmap =>
                        {
                            if (bitmap != null)
                            {
                                writeableBitmap = bitmap.ToWriteableBitmap();
                                writeableBitmap.Freeze();
                            }
                        }, true, "MyImage");
                    }
                }

                this.Dispatcher.Invoke(() =>
                {
                    this.Source = writeableBitmap;
                });
            }
        }


    }
}
