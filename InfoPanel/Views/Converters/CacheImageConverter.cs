using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

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
                    lockedImage.Access(bitmap =>
                    {
                        if(bitmap != null)
                        {
                            bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.StreamSource = bitmap.ToStream(ImageFormat.Png);
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.EndInit();
                            bitmapImage.Freeze();
                        }
                    });

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
