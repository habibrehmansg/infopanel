using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace InfoPanel
{
    class SensorSourceConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 1 || values[0] is not Enums.SensorType sensorType)
            {
                return "Sensor";
            }

            int remoteIndex = values.Length >= 2 && values[1] is int ri ? ri : -1;

            return sensorType switch
            {
                Enums.SensorType.HwInfo when remoteIndex >= 0 => $"HWiNFO Remote Sensor {remoteIndex}",
                Enums.SensorType.HwInfo => "HWiNFO Sensor",
                Enums.SensorType.Libre => "Libre Sensor",
                Enums.SensorType.Plugin => "Plugin Sensor",
                _ => "Sensor",
            };
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
