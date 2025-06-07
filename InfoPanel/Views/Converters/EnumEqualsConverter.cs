using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace InfoPanel
{
    public class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return Visibility.Collapsed;

            string? parameterString = parameter.ToString();
            string? valueString = value.ToString();

            return string.Equals(valueString, parameterString, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
