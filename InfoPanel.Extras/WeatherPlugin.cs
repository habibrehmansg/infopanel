using InfoPanel.Plugins;
using IniParser;
using IniParser.Model;
using OpenWeatherMap.Standard;
using System.Reflection;

namespace InfoPanel.Extras
{
    public class WeatherPlugin : BasePlugin
    {
        private Current? _current;
        private string? _city;

        private readonly PluginText _name = new("name", "Name", "-");
        private readonly PluginText _weather = new("weather", "Weather", "-");
        private readonly PluginText _weatherDesc = new("weather_desc", "Weather Description", "-");
        private readonly PluginText _weatherIcon = new("weather_icon", "Weather Icon", "-");
        private readonly PluginText _weatherIconUrl = new("weather_icon_url", "Weather Icon URL", "-");

        private readonly PluginSensor _temp = new("temp", "Temperature", 0, "°C");
        private readonly PluginSensor _maxTemp = new("max_temp", "Maximum Temperature", 0, "°C");
        private readonly PluginSensor _minTemp = new("min_temp", "Minimum Temperature", 0, "°C");
        private readonly PluginSensor _pressure = new("pressure", "Pressure", 0, "hPa");
        private readonly PluginSensor _seaLevel = new("sea_level", "Sea Level", 0, "hPa");
        private readonly PluginSensor _groundLevel = new("ground_level", "Ground Level", 0, "hPa");
        private readonly PluginSensor _feelsLike = new("feels_like", "Feels Like", 0, "°C");
        private readonly PluginSensor _humidity = new("humidity", "Humidity", 0, "%");

        private readonly PluginSensor _windSpeed = new("wind_speed", "Wind Speed", 0, "m/s");
        private readonly PluginSensor _windDeg = new("wind_deg", "Wind Degree", 0, "°");
        private readonly PluginSensor _windGust = new("wind_gust", "Wind Gust", 0, "m/s");

        private readonly PluginSensor _clouds = new("clouds", "Clouds", 0, "%");

        private readonly PluginSensor _rain = new("rain", "Rain", 0, "mm/h");
        private readonly PluginSensor _snow = new("snow", "Snow", 0, "mm/h");

        public WeatherPlugin() : base("weather-plugin","Weather Info - OpenWeatherMap", "Retrieves weather information periodically from openweathermap.org. API key required.")
        {
        }

        private string? _configFilePath = null;
        public override string? ConfigFilePath => _configFilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(1);

        public override void Initialize()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            _configFilePath = $"{assembly.ManifestModule.FullyQualifiedName}.ini";

            var parser = new FileIniDataParser();
            IniData config;
            if (!File.Exists(_configFilePath))
            {
                config = new IniData();
                config["Weather Plugin"]["APIKey"] = "<your-open-weather-api-key>";
                config["Weather Plugin"]["City"] = "Singapore";
                parser.WriteFile(_configFilePath, config);
            }
            else
            {
                config = parser.ReadFile(_configFilePath);

                var apiKey = config["Weather Plugin"]["APIKey"];
                _city = config["Weather Plugin"]["City"];

                if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(_city))
                {
                    _current = new(apiKey, OpenWeatherMap.Standard.Enums.WeatherUnits.Metric);
                }
            }
        }

        public override void Close()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {
            if (_city != null)
            {
                var container = new PluginContainer(_city);
                container.Entries.AddRange([_name, _weather, _weatherDesc, _weatherIcon, _weatherIconUrl]);
                container.Entries.AddRange([_temp, _maxTemp, _minTemp, _pressure, _seaLevel, _groundLevel, _feelsLike, _humidity, _windSpeed, _windDeg, _windGust, _clouds, _rain, _snow]);
                containers.Add(container);
            }
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            await GetWeather();
        }

        private async Task GetWeather()
        {
            if (_current == null || _city == null)
            {
                return;
            }

            var result = await _current.GetWeatherDataByCityNameAsync(_city);

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
