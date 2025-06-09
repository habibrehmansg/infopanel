using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using InfoPanel.Models;

namespace InfoPanel
{
    [ValueConversion(typeof(BeadaPanelDeviceStatus), typeof(Visibility))]
    public class DeviceStatusRunningConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is BeadaPanelDeviceStatus status)
            {
                return status.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            }
            
            // If DeviceStatus is null, not running
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
    
    [ValueConversion(typeof(BeadaPanelDeviceStatus), typeof(Visibility))]
    public class DeviceStatusNotRunningConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is BeadaPanelDeviceStatus status)
            {
                return status.IsRunning ? Visibility.Collapsed : Visibility.Visible;
            }
            
            // If DeviceStatus is null, show "not running"
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}