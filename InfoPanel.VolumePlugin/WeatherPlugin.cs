using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;
using OpenWeatherMap.Standard;
using System.Diagnostics;
using System.Reflection;

namespace InfoPanel.Extras
{
    public class WeatherPlugin : IPlugin
    {
        private readonly Stopwatch _stopwatch = new();

        private Current? _current;
        private string? _city;

        private readonly WeatherSensor _name = new("name", "Name", IPluginSensorValueType.String, "-");
        private readonly WeatherSensor _weather = new("weather", "Weather", IPluginSensorValueType.String, "-");
        private readonly WeatherSensor _weatherDesc = new("weather_desc", "Weather Description", IPluginSensorValueType.String, "-");
        private readonly WeatherSensor _weatherIcon = new("weather_icon", "Weather Icon", IPluginSensorValueType.String, "-");
        private readonly WeatherSensor _weatherIconUrl = new("weather_icon_url", "Weather Icon URL", IPluginSensorValueType.String, "-");

        private readonly WeatherSensor _temp = new("temp", "Temperature", IPluginSensorValueType.Double, 0, "°C");
        private readonly WeatherSensor _maxTemp = new("max_temp", "Maximum Temperature", IPluginSensorValueType.Double, 0, "°C");
        private readonly WeatherSensor _minTemp = new("min_temp", "Minimum Temperature", IPluginSensorValueType.Double, 0, "°C");
        private readonly WeatherSensor _pressure = new("pressure", "Pressure", IPluginSensorValueType.Double, 0, "hPa");
        private readonly WeatherSensor _seaLevel = new("sea_level", "Sea Level", IPluginSensorValueType.Double, 0, "hPa");
        private readonly WeatherSensor _groundLevel = new("ground_level", "Ground Level", IPluginSensorValueType.Double, 0, "hPa");
        private readonly WeatherSensor _feelsLike = new("feels_like", "Feels Like", IPluginSensorValueType.Double, 0, "°C");
        private readonly WeatherSensor _humidity = new("humidity", "Humidity", IPluginSensorValueType.Double, 0, "%");

        private readonly WeatherSensor _windSpeed = new("wind_speed", "Wind Speed", IPluginSensorValueType.Double, 0, "m/s");
        private readonly WeatherSensor _windDeg = new("wind_deg", "Wind Degree", IPluginSensorValueType.Double, 0, "°");
        private readonly WeatherSensor _windGust = new("wind_gust", "Wind Gust", IPluginSensorValueType.Double, 0, "m/s");

        private readonly WeatherSensor _clouds = new("clouds", "Clouds", IPluginSensorValueType.Double, 0, "%");

        private readonly WeatherSensor _rain = new("rain", "Rain", IPluginSensorValueType.Double, 0, "mm/h");
        private readonly WeatherSensor _snow = new("snow", "Snow", IPluginSensorValueType.Double, 0, "mm/h");

        string IPlugin.Name => "Weather Plugin";

        void IPlugin.Initialize()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            var configPath = $"{assembly.ManifestModule.FullyQualifiedName}.ini";
            
            var parser = new FileIniDataParser();
            IniData config;
            if (!File.Exists(configPath))
            {
                config = new IniData();
                config["Weather Plugin"]["APIKey"] = "<your-open-weather-api-key>";
                config["Weather Plugin"]["City"] = "Singapore";
                parser.WriteFile(configPath, config);
            }else
            {
                config = parser.ReadFile(configPath);

                var apiKey = config["Weather Plugin"]["APIKey"];
                _city = config["Weather Plugin"]["City"];

                if(!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(_city))
                {
                    _current = new(apiKey, OpenWeatherMap.Standard.Enums.WeatherUnits.Metric);
                }
            }
        }

        void IPlugin.Close()
        {
        }

        List<IPluginSensor> IPlugin.GetData()
        {
           return [
                _name,
                _weather,
                _weatherDesc,
                _weatherIcon,
                _weatherIconUrl,
                _temp,
                _maxTemp,
                _minTemp,
                _pressure,
                _seaLevel,
                _groundLevel,
                _feelsLike,
                _humidity,
                _windSpeed,
                _windDeg,
                _windGust,
                _clouds,
                _rain,
                _snow
            ];
        }

        async Task IPlugin.UpdateAsync()
        {
            // Update weather data every minute
            if (!_stopwatch.IsRunning || _stopwatch.ElapsedMilliseconds > 60000)
            {
                Trace.WriteLine("WeatherPlugin: Getting weather data");
                await GetWeather();
                _stopwatch.Restart();
            }
        }

        private async Task GetWeather()
        {
            if(_current == null)
            {
                return;
            }

            var result = await _current.GetWeatherDataByCityNameAsync("Singapore");

            if (result != null)
            {
                _name.Value = result.Name;
                _weather.Value = result.Weathers[0].Main;
                _weatherDesc.Value = result.Weathers[0].Description;
                _weatherIcon.Value = result.Weathers[0].Icon;
                _weatherIconUrl.Value = $"https://openweathermap.org/img/wn/{result.Weathers[0].Icon}@2x.png";

                _temp.Value = result.WeatherDayInfo.Temperature;
                _maxTemp.Value = result.WeatherDayInfo.MaximumTemperature;
                _minTemp.Value = result.WeatherDayInfo.MinimumTemperature;
                _pressure.Value = result.WeatherDayInfo.Pressure;
                _seaLevel.Value = result.WeatherDayInfo.SeaLevel;
                _groundLevel.Value = result.WeatherDayInfo.GroundLevel;
                _feelsLike.Value = result.WeatherDayInfo.FeelsLike;
                _humidity.Value = result.WeatherDayInfo.Humidity;
                
                _windSpeed.Value = result.Wind.Speed;
                _windDeg.Value = result.Wind.Degree;
                _windGust.Value = result.Wind.Gust;

                _clouds.Value = result.Clouds.All;

                _rain.Value = result.Rain.LastHour;
                _snow.Value = result.Snow.LastHour;
            }
        }
    }
}
