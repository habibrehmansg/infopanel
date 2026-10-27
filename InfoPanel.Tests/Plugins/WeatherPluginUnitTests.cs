using System.Reflection;
using System.Text.Json;
using InfoPanel.Extras;
using InfoPanel.Plugins;
using OpenWeatherMap.Cache.Models;
using Xunit;

namespace InfoPanel.Tests.Plugins;

// The Weather plugin reports in the measurement system chosen in its configuration: the sensors' units and the
// values written into them have to change together.
public class WeatherPluginUnitTests
{
    [Fact]
    public void Metric_is_the_default_and_reports_celsius_hpa_metres_per_second_and_millimetres()
    {
        var weather = Loaded(out var sensors);

        weather.ApplyReadings(Reading());

        AssertSensor(sensors, "temp", 21.5f, "°C");
        AssertSensor(sensors, "max_temp", 25f, "°C");
        AssertSensor(sensors, "min_temp", 18f, "°C");
        AssertSensor(sensors, "feels_like", 20f, "°C");
        AssertSensor(sensors, "pressure", 1013f, "hPa");
        AssertSensor(sensors, "sea_level", 1020f, "hPa");
        AssertSensor(sensors, "ground_level", 1000f, "hPa");
        AssertSensor(sensors, "wind_speed", 10f, "m/s");
        AssertSensor(sensors, "wind_gust", 15f, "m/s");
        AssertSensor(sensors, "rain", 25.4f, "mm/h");
        AssertSensor(sensors, "snow", 2.54f, "mm/h");
        AssertSensor(sensors, "humidity", 60f, "%");
    }

    [Fact]
    public void Imperial_reports_fahrenheit_inhg_miles_per_hour_and_inches()
    {
        var weather = Loaded(out var sensors);

        weather.ApplyConfig("MeasurementSystem", "Imperial");
        weather.ApplyReadings(Reading());

        AssertSensor(sensors, "temp", 70.7f, "°F");
        AssertSensor(sensors, "max_temp", 77f, "°F");
        AssertSensor(sensors, "min_temp", 64.4f, "°F");
        AssertSensor(sensors, "feels_like", 68f, "°F");
        AssertSensor(sensors, "pressure", 29.91f, "inHg");
        AssertSensor(sensors, "sea_level", 30.12f, "inHg");
        AssertSensor(sensors, "ground_level", 29.53f, "inHg");
        AssertSensor(sensors, "wind_speed", 22.37f, "mph");
        AssertSensor(sensors, "wind_gust", 33.55f, "mph");
        AssertSensor(sensors, "rain", 1f, "in/h");
        AssertSensor(sensors, "snow", 0.1f, "in/h");
        AssertSensor(sensors, "humidity", 60f, "%");
    }

    [Fact]
    public void Imperial_units_show_before_the_first_reading_arrives()
    {
        var weather = new WeatherPlugin();
        weather.ApplyConfig("MeasurementSystem", "Imperial");
        var sensors = Load(weather);

        Assert.Equal("°F", sensors["temp"].Unit);
        Assert.Equal("inHg", sensors["pressure"].Unit);
        Assert.Equal("mph", sensors["wind_speed"].Unit);
        Assert.Equal("in/h", sensors["rain"].Unit);
    }

    [Fact]
    public void Switching_back_to_metric_restores_the_units_and_values()
    {
        var weather = Loaded(out var sensors);

        weather.ApplyConfig("MeasurementSystem", "Imperial");
        weather.ApplyConfig("MeasurementSystem", "Metric");
        weather.ApplyReadings(Reading());

        AssertSensor(sensors, "temp", 21.5f, "°C");
        AssertSensor(sensors, "pressure", 1013f, "hPa");
        AssertSensor(sensors, "wind_speed", 10f, "m/s");
        AssertSensor(sensors, "rain", 25.4f, "mm/h");
    }

    // The host compares and sends a sensor's value and unit together, so a switch must never leave the old system's
    // values under the new units: the reading already held is converted at once, without waiting for the next fetch.
    [Fact]
    public void A_switch_converts_the_reading_already_held()
    {
        var weather = Loaded(out var sensors);
        weather.ApplyReadings(Reading());

        weather.ApplyConfig("MeasurementSystem", "Imperial");

        AssertSensor(sensors, "temp", 70.7f, "°F");
        AssertSensor(sensors, "pressure", 29.91f, "inHg");
        AssertSensor(sensors, "wind_speed", 22.37f, "mph");
        AssertSensor(sensors, "rain", 1f, "in/h");

        weather.ApplyConfig("MeasurementSystem", "Metric");

        AssertSensor(sensors, "temp", 21.5f, "°C");
        AssertSensor(sensors, "pressure", 1013f, "hPa");
        AssertSensor(sensors, "wind_speed", 10f, "m/s");
        AssertSensor(sensors, "rain", 25.4f, "mm/h");
    }

    [Theory]
    [InlineData("imperial", "°F")]
    [InlineData("IMPERIAL", "°F")]
    [InlineData("Metric", "°C")]
    [InlineData("", "°C")]
    [InlineData("Furlongs", "°C")]
    public void The_choice_is_case_insensitive_and_anything_else_is_metric(string choice, string expectedTemperatureUnit)
    {
        var weather = Loaded(out var sensors);

        weather.ApplyConfig("MeasurementSystem", choice);

        Assert.Equal(expectedTemperatureUnit, sensors["temp"].Unit);
    }

    // A choice other than Imperial is Metric whatever was chosen before, so each is applied after Imperial.
    [Theory]
    [InlineData("Furlongs")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_empty_choice_after_imperial_reports_metric_units_and_readings(string? choice)
    {
        var weather = Loaded(out var sensors);
        weather.ApplyConfig("MeasurementSystem", "Imperial");

        weather.ApplyConfig("MeasurementSystem", choice);
        weather.ApplyReadings(Reading());

        AssertSensor(sensors, "temp", 21.5f, "°C");
        AssertSensor(sensors, "pressure", 1013f, "hPa");
        AssertSensor(sensors, "wind_speed", 10f, "m/s");
        AssertSensor(sensors, "rain", 25.4f, "mm/h");
    }

    // Configured with a key and a city, the plugin holds a service client (built without fetching). Switching units
    // only changes how readings are reported, so it keeps that client and its cache; a new key or city needs a new one.
    [Fact]
    public void A_measurement_system_change_keeps_the_service_client()
    {
        var weather = Configured();
        var client = weather.Client;
        Assert.NotNull(client);

        weather.ApplyConfig("MeasurementSystem", "Imperial");
        Assert.Same(client, weather.Client);

        weather.ApplyConfig("MeasurementSystem", "Metric");
        Assert.Same(client, weather.Client);
    }

    [Theory]
    [InlineData("APIKey", "another-key")]
    [InlineData("City", "Othertown")]
    public void An_api_key_or_city_change_replaces_the_service_client(string key, string value)
    {
        var weather = Configured();
        var client = weather.Client;
        Assert.NotNull(client);

        weather.ApplyConfig(key, value);

        Assert.NotNull(weather.Client);
        Assert.NotSame(client, weather.Client);
    }

    private static WeatherPlugin Configured()
    {
        var weather = new WeatherPlugin();
        weather.ApplyConfig("APIKey", "test-key");
        weather.ApplyConfig("City", "Testville");
        return weather;
    }

    [Fact]
    public void The_measurement_system_is_offered_as_a_choice_of_metric_and_imperial()
    {
        var property = Assert.Single(new WeatherPlugin().ConfigProperties, p => p.Key == "MeasurementSystem");

        Assert.Equal(PluginConfigType.Choice, property.Type);
        Assert.Equal("Metric", property.Value);
        Assert.Equal(["Metric", "Imperial"], property.Options!);
    }

    private static WeatherPlugin Loaded(out Dictionary<string, IPluginSensor> sensors)
    {
        var weather = new WeatherPlugin();
        sensors = Load(weather);
        return weather;
    }

    private static Dictionary<string, IPluginSensor> Load(WeatherPlugin weather)
    {
        var containers = new List<IPluginContainer>();
        weather.Load(containers);
        return containers.SelectMany(c => c.Entries).OfType<IPluginSensor>().ToDictionary(s => s.Id);
    }

    private static void AssertSensor(Dictionary<string, IPluginSensor> sensors, string id, float expected, string unit)
    {
        Assert.Equal(unit, sensors[id].Unit);
        Assert.Equal(expected, sensors[id].Value, 0.01f);
    }

    // A reading as the OpenWeatherMap service returns it: Kelvin, hPa, m/s and mm. Readings can't be constructed
    // directly (its constructor is internal), so it is built from the service's JSON, the way the library does.
    private const string ServiceJson = """
        {
          "weather": [{ "id": 800, "main": "Clear", "description": "clear sky", "icon": "01d" }],
          "main": { "temp": 294.65, "feels_like": 293.15, "temp_min": 291.15, "temp_max": 298.15,
                    "pressure": 1013, "humidity": 60, "sea_level": 1020, "grnd_level": 1000 },
          "wind": { "speed": 10, "deg": 90, "gust": 15 },
          "clouds": { "all": 40 },
          "rain": { "1h": 25.4 },
          "snow": { "1h": 2.54 },
          "dt": 1700000000,
          "sys": { "country": "XX", "sunrise": 1699990000, "sunset": 1700030000 },
          "timezone": 0, "id": 1, "name": "Testville", "cod": 200
        }
        """;

    private static Readings Reading()
    {
        var constructor = typeof(Readings).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(c => c.GetParameters() is [{ ParameterType.Name: "ApiWeatherResult" }]);
        var apiResult = JsonSerializer.Deserialize(ServiceJson, constructor.GetParameters()[0].ParameterType)!;
        return (Readings)constructor.Invoke([apiResult]);
    }
}
