using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace InfoPanel
{
    [ValueConversion(typeof(object), typeof(bool))]
    public class IntToBooleanConverter: IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if(value is int intValue)
            {
                return intValue != 0;
            }

            return false;
        }


        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
