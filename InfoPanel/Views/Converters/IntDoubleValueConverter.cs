using System;
using System.Globalization;
using System.Windows.Data;

namespace InfoPanel
{
    class IntDoubleValueConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (double.TryParse(value.ToString(), out double doubleValue))
            {
                return doubleValue;
            }

            return Binding.DoNothing;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double doubleValue)
            {
                if (int.TryParse(doubleValue.ToString(), out int intValue))
                {
                    return intValue;
                }
            }

            return Binding.DoNothing;
        }
    }
}
