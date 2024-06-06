using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;

namespace InfoPanel
{
    internal class IsSensorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return SharedModel.Instance.SelectedItem is SensorDisplayItem || SharedModel.Instance.SelectedItem is ChartDisplayItem || SharedModel.Instance.SelectedItem is GaugeDisplayItem;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
