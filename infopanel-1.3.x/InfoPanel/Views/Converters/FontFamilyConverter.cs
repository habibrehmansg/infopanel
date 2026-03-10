using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace InfoPanel
{
    public class FontFamilyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string fontFamilyName)
            {
                return new FontFamily(fontFamilyName);
            }
            return DependencyProperty.UnsetValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is FontFamily fontFamily)
            {
                return fontFamily.Source;
            }
            return DependencyProperty.UnsetValue;
        }
    }
}
