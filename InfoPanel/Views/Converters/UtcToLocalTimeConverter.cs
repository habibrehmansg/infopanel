using System;
using System.Globalization;
using System.Windows.Data;

namespace InfoPanel;

[ValueConversion(typeof(string), typeof(string))]
public class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string dateString && DateTimeOffset.TryParse(dateString, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            var local = dto.ToLocalTime();
            var now = DateTimeOffset.Now;
            var prefix = parameter is string p ? p : string.Empty;

            if (local.Date == now.Date)
                return $"{prefix}Today at {local:h:mm tt}";
            if (local.Date == now.Date.AddDays(-1))
                return $"{prefix}Yesterday at {local:h:mm tt}";
            if (local.Year == now.Year)
                return $"{prefix}{local:MMM d} at {local:h:mm tt}";

            return $"{prefix}{local:MMM d, yyyy} at {local:h:mm tt}";
        }

        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
