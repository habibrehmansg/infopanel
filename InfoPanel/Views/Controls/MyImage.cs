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

        private readonly Timer Timer = new(TimeSpan.FromMilliseconds(41));
        private volatile bool _isProcessing;

        public MyImage()
        {
            Loaded += OnLoaded;
            Unloaded += MyImage_Unloaded;
        }

        private static void OnImageDisplayItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MyImage myImage)
            {
                if (e.NewValue is ImageDisplayItem imageDisplayItem)
                {
                    myImage.Timer.Start();
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
            if (_isProcessing) return;
            _isProcessing = true;

            Dispatcher.InvokeAsync(() =>
            {
                var imageDisplayItem = ImageDisplayItem;
                var width = (int)Width;
                var height = (int)Height;

                if (imageDisplayItem == null)
                {
                    _isProcessing = false;
                    return;
                }

                // Process on background thread to avoid blocking UI
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
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
                                    targetWidth = width;
                                    targetHeight = (int)Math.Ceiling(width / imageAspectRatio);
                                }
                                else
                                {
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

                            if (image.Frames <= 1 && image.Type != LockedImage.ImageType.PLUGIN && sender is Timer timer)
                            {
                                timer.Stop();
                            }
                        }

                        Dispatcher.InvokeAsync(() =>
                        {
                            Source = writeableBitmap;
                            _isProcessing = false;
                        });
                    }
                    catch
                    {
                        _isProcessing = false;
                    }
                });
            });
        }


    }
}
