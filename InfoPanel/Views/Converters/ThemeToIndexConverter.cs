using System;
using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Appearance;

namespace InfoPanel.Views.Converters
{
    internal class ThemeToIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ApplicationTheme theme)
            {
                return theme switch
                {
                    ApplicationTheme.Dark => 1,
                    ApplicationTheme.HighContrast => 2,
                    _ => 0
                };
            }

            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index)
            {
                return index switch
                {
                    1 => ApplicationTheme.Dark,
                    2 => ApplicationTheme.HighContrast,
                    _ => ApplicationTheme.Light
                };
            }

            return ApplicationTheme.Light;
        }
    }
}
