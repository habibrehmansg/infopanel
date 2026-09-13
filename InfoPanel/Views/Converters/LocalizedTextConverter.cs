using InfoPanel.Services;
using System;
using System.Globalization;
using System.Windows.Data;

namespace InfoPanel.Views.Converters
{
    public class LocalizedTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string text && !string.IsNullOrEmpty(text))
            {
                var translated = LocalizationService.GetString(text);
                if (translated != text)
                {
                    return translated;
                }

                var mappedKey = text switch
                {
                    "Clock" => "Extras_ClockTitle",
                    "Returns raw clock information as sensors for custom styling with Gauges etc." => "Extras_ClockDesc",
                    "Drive Info" => "Extras_DriveInfoTitle",
                    "Retrieves local disk space information." => "Extras_DriveInfoDesc",
                    "HWiNFO Registry" => "Extras_HWiNFORegistryTitle",
                    "Alternate access to HWiNFO via registry." => "Extras_HWiNFORegistryDesc",
                    "Network Info" => "Extras_NetworkInfoTitle",
                    "Retrieves network devices info. Public IP lookup powered by ipify.org API." => "Extras_NetworkInfoDesc",
                    "System Info" => "Extras_SystemInfoTitle",
                    "Misc system information and statistics." => "Extras_SystemInfoDesc",
                    "Process Blacklist" => "Extras_ProcessBlacklist",
                    "Volume Info" => "Extras_VolumeTitle",
                    "Retrieves audio output devices and relevant details. Powered by NAudio." => "Extras_VolumeDesc",
                    "Weather Info - OpenWeatherMap" => "Extras_WeatherTitle",
                    "Retrieves weather information periodically from openweathermap.org. API key required." => "Extras_WeatherDesc",
                    "Get API Key" => "Extras_GetApiKey",
                    "Provides extra functionality to supercharge your panels." => "Extras_SuperpackDesc",
                    "Reload" => "Common_Reload",
                    "Save" => "Common_Save",
                    _ => null
                };

                if (mappedKey != null)
                {
                    return LocalizationService.GetString(mappedKey);
                }
            }
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
