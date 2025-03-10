using InfoPanel.Models;
using System;
using System.Globalization;
using System.Windows.Data;

namespace InfoPanel
{
    internal class IsSensorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return SharedModel.Instance.SelectedItem is SensorDisplayItem 
                || SharedModel.Instance.SelectedItem is TableSensorDisplayItem
                || SharedModel.Instance.SelectedItem is ChartDisplayItem 
                || SharedModel.Instance.SelectedItem is GaugeDisplayItem 
                || SharedModel.Instance.SelectedItem is SensorImageDisplayItem
                || SharedModel.Instance.SelectedItem is HttpImageDisplayItem;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
