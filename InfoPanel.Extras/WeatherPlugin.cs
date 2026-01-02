using InfoPanel.Plugins;
using OpenWeatherMap.Cache;
using OpenWeatherMap.Cache.Models;
using System.Diagnostics;

namespace InfoPanel.Extras
{
    public class WeatherPlugin : BasePlugin
    {
        private OpenWeatherMapCache? _current;
        private City? _city;

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

        public override string? ConfigFilePath => Config.FilePath;
        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(1);

        public override void Initialize()
        {
            Config.Instance.Load();
            Config.Instance.TryGetValue(Config.SECTION_WEATHER, "APIKey", out string apiKey);
            Config.Instance.TryGetValue(Config.SECTION_WEATHER, "City", out string city);

            if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(city))
            {
                _current = new(apiKey, 10_000);
                _city = new(city);
            }
        }

        public override void Close()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {
            if (_city != null)
            {
                var container = new PluginContainer(_city.CityName);
                container.Entries.AddRange([_name, _weather, _weatherDesc, _weatherIcon, _weatherIconUrl]);
                container.Entries.AddRange([_temp, _maxTemp, _minTemp, _pressure, _seaLevel, _groundLevel, _feelsLike, _humidity, _windSpeed, _windDeg, _windGust, _clouds, _rain, _snow]);
                containers.Add(container);
            }
        }

        [PluginAction("Get API Key")]
        public void LaunchApiUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://openweathermap.org/api",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                // Handle exceptions here
                Console.WriteLine($"An error occurred: {ex.Message}");
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

            try
            {
                var result = await _current.GetReadingsAsync(_city);

                if (result != null)
                {
                    _name.Value = result.CityName;
                    _weather.Value = result.Weather[0].Main;
                    _weatherDesc.Value = result.Weather[0].Description;
                    _weatherIcon.Value = result.Weather[0].IconId;
                    _weatherIconUrl.Value = $"https://openweathermap.org/img/wn/{result.Weather[0].IconId}@2x.png";

                    _temp.Value = Convert.ToSingle(result.Temperature.DegreesCelsius);
                    _maxTemp.Value = Convert.ToSingle(result.MaximumTemperature.DegreesCelsius);
                    _minTemp.Value = Convert.ToSingle(result.MinimumTemperature.DegreesCelsius);
                    _pressure.Value = Convert.ToSingle(result.Pressure.Hectopascals);
                    _seaLevel.Value = Convert.ToSingle(result.SeaLevelPressure?.Hectopascals ?? 0);
                    _groundLevel.Value = Convert.ToSingle(result.GroundLevelPressure?.Hectopascals ?? 0);
                    _feelsLike.Value = Convert.ToSingle(result.FeelsLike.DegreesCelsius);
                    _humidity.Value = Convert.ToSingle(result.Humidity.Percent);

                    _windSpeed.Value = Convert.ToSingle(result.WindSpeed.MetersPerSecond);
                    _windDeg.Value = Convert.ToSingle(result.WindDirection.Value);
                    _windGust.Value = Convert.ToSingle(result.WindGust?.MetersPerSecond ?? 0);

                    _clouds.Value = Convert.ToSingle(result.Cloudiness.Percent);

                    _rain.Value = Convert.ToSingle(result.RainfallLastHour?.Millimeters ?? 0);
                    _snow.Value = Convert.ToSingle(result.SnowfallLastHour?.Millimeters ?? 0);
                }
            }catch(Exception e) { }
        }
    }
}
