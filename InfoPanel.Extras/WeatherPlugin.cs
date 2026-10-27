using InfoPanel.Plugins;
using OpenWeatherMap.Cache;
using OpenWeatherMap.Cache.Models;
using System.Diagnostics;

namespace InfoPanel.Extras
{
    public class WeatherPlugin : BasePlugin, IPluginConfigurable
    {
        private const string MetricMeasurementSystem = "Metric";
        private const string ImperialMeasurementSystem = "Imperial";

        private OpenWeatherMapCache? _current;
        private City _cityObj;
        private bool _isConfigured;
        private string _apiKey = "";
        private string _cityName = "";
        private string _measurementSystem = MetricMeasurementSystem;

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

        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(1);

        public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
        [
            new PluginConfigProperty
            {
                Key = "APIKey",
                DisplayName = "API Key",
                Description = "OpenWeatherMap API key. Use the 'Get API Key' action to obtain one.",
                Type = PluginConfigType.String,
                Value = _apiKey
            },
            new PluginConfigProperty
            {
                Key = "City",
                DisplayName = "City",
                Description = "City name for weather data (e.g. Singapore, London).",
                Type = PluginConfigType.String,
                Value = _cityName
            },
            new PluginConfigProperty
            {
                Key = "MeasurementSystem",
                DisplayName = "Measurement System",
                Description = "Units used for weather values.",
                Type = PluginConfigType.Choice,
                Value = _measurementSystem,
                Options = [MetricMeasurementSystem, ImperialMeasurementSystem]
            }
        ];

        public void ApplyConfig(string key, object? value)
        {
            var strValue = value?.ToString() ?? "";

            switch (key)
            {
                case "APIKey":
                    _apiKey = strValue;
                    RebuildClient();
                    break;
                case "City":
                    _cityName = strValue;
                    RebuildClient();
                    break;
                case "MeasurementSystem":
                    _measurementSystem = string.Equals(strValue, ImperialMeasurementSystem, StringComparison.OrdinalIgnoreCase)
                        ? ImperialMeasurementSystem
                        : MetricMeasurementSystem;
                    ApplyMeasurementUnits();
                    break;
                default:
                    return;
            }
        }

        private void RebuildClient()
        {
            _current?.Dispose();
            _current = null;
            _isConfigured = false;

            if (!string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_cityName))
            {
                _current = new OpenWeatherMapCache(_apiKey, 10_000);
                _cityObj = new City(_cityName);
                _isConfigured = true;
            }
        }

        private void ApplyMeasurementUnits()
        {
            if (UseImperial)
            {
                _temp.Unit = "°F";
                _maxTemp.Unit = "°F";
                _minTemp.Unit = "°F";
                _feelsLike.Unit = "°F";
                _pressure.Unit = "inHg";
                _seaLevel.Unit = "inHg";
                _groundLevel.Unit = "inHg";
                _windSpeed.Unit = "mph";
                _windGust.Unit = "mph";
                _rain.Unit = "in/h";
                _snow.Unit = "in/h";
            }
            else
            {
                _temp.Unit = "°C";
                _maxTemp.Unit = "°C";
                _minTemp.Unit = "°C";
                _feelsLike.Unit = "°C";
                _pressure.Unit = "hPa";
                _seaLevel.Unit = "hPa";
                _groundLevel.Unit = "hPa";
                _windSpeed.Unit = "m/s";
                _windGust.Unit = "m/s";
                _rain.Unit = "mm/h";
                _snow.Unit = "mm/h";
            }
        }

        private bool UseImperial => _measurementSystem == ImperialMeasurementSystem;

        public override void Initialize()
        {
        }

        public override void Close()
        {
            _current?.Dispose();
            _current = null;
        }

        public override void Load(List<IPluginContainer> containers)
        {
            ApplyMeasurementUnits();

            var container = new PluginContainer("Weather");
            container.Entries.AddRange([_name, _weather, _weatherDesc, _weatherIcon, _weatherIconUrl]);
            container.Entries.AddRange([_temp, _maxTemp, _minTemp, _pressure, _seaLevel, _groundLevel, _feelsLike, _humidity, _windSpeed, _windDeg, _windGust, _clouds, _rain, _snow]);
            containers.Add(container);
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
            if (_current == null || !_isConfigured)
            {
                return;
            }

            try
            {
                var result = await _current.GetReadingsAsync(_cityObj);

                if (result != null)
                {
                    _name.Value = result.CityName;

                    if (result.Weather is { Length: > 0 })
                    {
                        _weather.Value = result.Weather[0].Main;
                        _weatherDesc.Value = result.Weather[0].Description;
                        _weatherIcon.Value = result.Weather[0].IconId;
                        _weatherIconUrl.Value = $"https://openweathermap.org/img/wn/{result.Weather[0].IconId}@2x.png";
                    }

                    if (UseImperial)
                    {
                        _temp.Value = Convert.ToSingle(result.Temperature.DegreesFahrenheit);
                        _maxTemp.Value = Convert.ToSingle(result.MaximumTemperature.DegreesFahrenheit);
                        _minTemp.Value = Convert.ToSingle(result.MinimumTemperature.DegreesFahrenheit);
                        _pressure.Value = Convert.ToSingle(result.Pressure.InchesOfMercury);
                        _seaLevel.Value = Convert.ToSingle(result.SeaLevelPressure?.InchesOfMercury ?? 0);
                        _groundLevel.Value = Convert.ToSingle(result.GroundLevelPressure?.InchesOfMercury ?? 0);
                        _feelsLike.Value = Convert.ToSingle(result.FeelsLike.DegreesFahrenheit);
                        _windSpeed.Value = Convert.ToSingle(result.WindSpeed.MilesPerHour);
                        _windGust.Value = Convert.ToSingle(result.WindGust?.MilesPerHour ?? 0);
                        _rain.Value = Convert.ToSingle(result.RainfallLastHour?.Inches ?? 0);
                        _snow.Value = Convert.ToSingle(result.SnowfallLastHour?.Inches ?? 0);
                    }
                    else
                    {
                        _temp.Value = Convert.ToSingle(result.Temperature.DegreesCelsius);
                        _maxTemp.Value = Convert.ToSingle(result.MaximumTemperature.DegreesCelsius);
                        _minTemp.Value = Convert.ToSingle(result.MinimumTemperature.DegreesCelsius);
                        _pressure.Value = Convert.ToSingle(result.Pressure.Hectopascals);
                        _seaLevel.Value = Convert.ToSingle(result.SeaLevelPressure?.Hectopascals ?? 0);
                        _groundLevel.Value = Convert.ToSingle(result.GroundLevelPressure?.Hectopascals ?? 0);
                        _feelsLike.Value = Convert.ToSingle(result.FeelsLike.DegreesCelsius);
                        _windSpeed.Value = Convert.ToSingle(result.WindSpeed.MetersPerSecond);
                        _windGust.Value = Convert.ToSingle(result.WindGust?.MetersPerSecond ?? 0);
                        _rain.Value = Convert.ToSingle(result.RainfallLastHour?.Millimeters ?? 0);
                        _snow.Value = Convert.ToSingle(result.SnowfallLastHour?.Millimeters ?? 0);
                    }

                    _humidity.Value = Convert.ToSingle(result.Humidity.Percent);
                    _windDeg.Value = Convert.ToSingle(result.WindDirection.Value);
                    _clouds.Value = Convert.ToSingle(result.Cloudiness.Percent);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
