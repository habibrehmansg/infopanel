using System;
using System.Globalization;
using System.Windows.Data;

namespace InfoPanel.Views.Converters
{
    [ValueConversion(typeof(double), typeof(string))]
    public class DownloadCountConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double count)
            {
                return count switch
                {
                    >= 1_000_000 => $"{count / 1_000_000:0.#}M",
                    >= 1_000 => $"{count / 1_000:0.#}K",
                    _ => ((int)count).ToString()
                };
            }

            return "0";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
