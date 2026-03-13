using System;
using System.Globalization;
using System.Windows.Data;

namespace InfoPanel
{
    /// <summary>Converts int to/from string with optional clamping. ConverterParameter format: "Min,Max" (e.g. "2,5").</summary>
    internal class ClampingIntStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int i)
                return i.ToString(culture);
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string s || !int.TryParse(s, NumberStyles.Integer, culture, out int result))
                return Binding.DoNothing;
            if (parameter is string param && param.Contains(','))
            {
                var parts = param.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int min) && int.TryParse(parts[1].Trim(), out int max))
                    result = Math.Clamp(result, min, max);
            }
            return result;
        }
    }
}
