using InfoPanel.Extensions;
using InfoPanel.Models;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace InfoPanel
{
    public class CacheImageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                value = "NO_IMAGE";
            }

            if (value is string imagePath)
            {
                LockedImage? lockedImage = Cache.GetLocalImage(imagePath);

                if (lockedImage != null)
                {
                    BitmapImage? bitmapImage = null;

                    if (lockedImage.IsSvg)
                    {
                        lockedImage.AccessSVG(picture =>
                        {
                            var bounds = picture.CullRect;
                            var writeableBitmap = picture.ToWriteableBitmap(new SKSizeI((int)bounds.Width, (int)bounds.Height));
                            writeableBitmap.Freeze();
                            bitmapImage = writeableBitmap.ToBitmapImage();
                        });
                    }
                    else
                    {
                        lockedImage.AccessSK(lockedImage.Width, lockedImage.Height, bitmap =>
                        {
                            if (bitmap != null)
                            {
                                var writeableBitmap = bitmap.ToWriteableBitmap();
                                writeableBitmap.Freeze();
                                bitmapImage = writeableBitmap.ToBitmapImage();
                            }
                        }, true, "ImageConverter");
                    }

                    return bitmapImage;
                }
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
