using InfoPanel.Models;
using InfoPanel.ViewModels.Components;
using System;
using System.Globalization;
using System.Windows.Data;

namespace InfoPanel
{
    internal class IsSensorConverter : IValueConverter, IMultiValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return IsSensorDisplayItem(SharedModel.Instance.SelectedItem);
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Length >= 2
                && ((IsSensorDisplayItem(values[0])
                        && values[1] is HwInfoSensorItem or LibreSensorItem or PluginSensorItem)
                    || (values[0] is TextDisplayItem
                        && values[0] is not SensorDisplayItem
                        && values[1] is HwInfoHardwareTreeItem or LibreHardwareTreeItem));
        }

        private static bool IsSensorDisplayItem(object? value)
        {
            return value is SensorDisplayItem
                || value is TableSensorDisplayItem
                || value is ChartDisplayItem
                || value is GaugeDisplayItem
                || value is SensorImageDisplayItem
                || value is HttpImageDisplayItem;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
