using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace InfoPanel
{
    [ValueConversion(typeof(object), typeof(Visibility))]
    public class NullToVisibilityConverter: IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
            {
                return Visibility.Collapsed;
            }

            if (value is IEnumerable enumerable)
            {
                // Check if the collection is empty
                var enumerator = enumerable.GetEnumerator();
                if (!enumerator.MoveNext())
                {
                    return Visibility.Collapsed;
                }
            }

            return Visibility.Visible;
        }


        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
