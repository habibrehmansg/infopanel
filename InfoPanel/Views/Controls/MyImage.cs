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
        public static readonly DependencyProperty ImageDisplayItemProperty =
    DependencyProperty.Register(
        nameof(ImageDisplayItem),  // Property name
        typeof(ImageDisplayItem),   // Change to ImageDisplayItem type
        typeof(MyImage),
        new PropertyMetadata(null, OnImageDisplayItemChanged));

        public ImageDisplayItem ImageDisplayItem
        {
            get => (ImageDisplayItem)GetValue(ImageDisplayItemProperty);
            set => SetValue(ImageDisplayItemProperty, value);  // Add setter
        }

        private readonly Timer Timer = new(TimeSpan.FromMilliseconds(33));

        public MyImage()
        {
            Loaded += OnLoaded;
            Unloaded += MyImage_Unloaded;
        }

        private static void OnImageDisplayItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MyImage myImage)
            {
                var imageDisplayItem = e.NewValue as ImageDisplayItem;

                if (imageDisplayItem != null)
                {
                    if (Cache.GetLocalImage(imageDisplayItem) is LockedImage lockedImage)
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
            (ImageDisplayItem imageDisplayItem, int width, int height, ImageSource source) = Dispatcher.Invoke(() =>
            {
                return (ImageDisplayItem, (int)Width, (int)Height, Source);
            });

            if(imageDisplayItem == null)
            {
                return;
            }

            var image = Cache.GetLocalImage(imageDisplayItem);

            WriteableBitmap? writeableBitmap = null;

            if (image != null)
            {
                if (image.Type == LockedImage.ImageType.SVG)
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
