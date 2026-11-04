using System.Text;
using InfoPanel.Core.Tests.Images;
using InfoPanel.Core.Tests.Persistence;
using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Plugins;
using InfoPanel.Scenes;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using InfoPanel.Sensors.Weather;
using InfoPanel.Service;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x profile bound to the 1.x Weather plugin, whose place the engine's weather source takes now: reading it, from a
/// <c>.infopanel</c> file or the 1.x folder, binds every item bound to one of the plugin's 15 entries the source has an
/// equivalent for to the source's key, once, in the model, so its props and <c>meta.sensor</c> carry the new binding, and
/// reports each (<c>import.sensor.translated</c>). The 4 entries with no equivalent (snow, ground level, the icon and its
/// address) stay as stored, unresolved (<c>import.sensor</c>).
/// </summary>
public sealed class V1ReaderWeatherTests : IAsyncLifetime
{
    /// <summary>The plugin's 19 entries (its container "Weather", id <c>weather</c>), and what each is bound to; null: none.</summary>
    private static readonly (string Old, string? New)[] Table =
    [
        ("name", "place"), ("weather", "condition"), ("weather_desc", "condition"), ("weather_icon", null), ("weather_icon_url", null),
        ("temp", "temperature"), ("max_temp", "high"), ("min_temp", "low"), ("pressure", "pressure"), ("sea_level", "pressure"),
        ("ground_level", null), ("feels_like", "feels_like"), ("humidity", "humidity"), ("wind_speed", "wind_speed"),
        ("wind_deg", "wind_direction"), ("wind_gust", "wind_gust"), ("clouds", "cloud_cover"), ("rain", "precipitation"), ("snow", null),
    ];

    private const string Answer = """
        {"format":"infopanel.weather/1","place":{"name":"Testville","country":"TV","from":"network"},"time":"2026-11-01T13:00:00Z",
         "updated":"2026-11-01T12:40:00Z","expires":"2026-11-01T13:35:00Z","stale":false,
         "current":{"temperature":6.1,"feelsLike":3.2,"humidity":81,"dewPoint":3,"pressure":1012.4,"windSpeed":3.4,"windGust":7.9,
          "windDirection":210,"cloudCover":88,"uvIndex":0.4,"precipitation":0.3,"symbol":"lightrain","condition":"Light rain"},
         "next24h":{"high":8.2,"low":2.9},"sun":{"sunrise":"2026-11-01T06:58:00Z","sunset":"2026-11-01T15:31:00Z"},
         "attribution":{"text":"Weather data from the test service","url":"https://service.example/","licence":"Test terms","licenceUrl":"https://service.example/terms","changes":"None"}}
        """;

    private readonly ImportKit _kit = new();
    private readonly TempDirectory _temp = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 13, 5, 7, TimeSpan.Zero));
    private readonly LocalHttpServer _service = new((_, stream, _) => LocalHttpServer.Answer(stream, 200, Encoding.UTF8.GetBytes(Answer), "application/json"));
    private ServiceClient? _client;
    private WeatherSource? _weather;

    public async ValueTask InitializeAsync()
    {
        // This engine's weather source holds an answer from its service, so the readings bound are live.
        var address = $"http://127.0.0.1:{_service.Port}";
        var credentials = new ServiceCredentials(FileServiceCredentialStore.ForPrivateFile(_temp.Path), () => address);
        credentials.Put("import-test-credential");
        _client = new ServiceClient(credentials, loopbackOnly: true, _time);
        _weather = new WeatherSource(_client, credentials, _time, new WeatherSourceOptions { Format = RegionalFormat.Invariant });
        await _weather.Fetcher.FetchOnceAsync(TestContext.Current.CancellationToken);
        _weather.Polled.Open();
    }

    public async ValueTask DisposeAsync()
    {
        _weather?.Dispose();
        _client?.Dispose();
        await _service.DisposeAsync();
        _temp.Dispose();
        _kit.Dispose();
    }

    /// <summary>This engine's sensors: the clock and the weather, unknown keys unavailable (not mock mode).</summary>
    private SensorHub Hub() => new([new ClockSensorSource(_time), _weather!]);

    private V1ReadContext Context() => new(_kit.Workspace, Hub(), null, Fonts.Contains);

    private V1Profile Read(params string[] items) => V1Reader.ReadArchive(new MemoryStream(V1Archive(Items(items))), Context());

    private static string Bound(string type, string name, string key, string sensorType = "Plugin") =>
        $"""<DisplayItem xsi:type="{type}"><Name>{name}</Name><SensorName>{name}</SensorName><SensorType>{sensorType}</SensorType><PluginSensorId>{key}</PluginSensorId><Id>0</Id><Instance>0</Instance><EntryId>0</EntryId></DisplayItem>""";

    private static string Weather(string id) => $"/weather-plugin/weather/{id}";

    private static string PropsKey(ItemNode node) => (string)node.Props["pluginSensorId"]!;

    /// <summary>A theme bound to all 19 entries, a sensor item each, in the table's order.</summary>
    private static string[] AllNineteen() => [.. Table.Select(t => Bound("SensorDisplayItem", $"Weather {t.Old}", Weather(t.Old)))];

    private static void AssertAllNineteen(V1Profile envelope)
    {
        Assert.Equal(19, envelope.Items.Count);
        var mapped = new List<string>();
        var unmapped = new List<string>();
        for (var i = 0; i < Table.Length; i++)
        {
            var (old, to) = Table[i];
            var item = envelope.Items[i];
            Assert.Equal("Plugin", (string)item.Props["sensorType"]!);
            if (to is null)
            {
                Assert.Equal(Weather(old), PropsKey(item));
                Assert.Equal($"plugin:{Weather(old)}", item.Meta.Sensor!.Key);
                Assert.Equal(SensorStatus.Unavailable, item.Meta.Sensor.Status);
                unmapped.Add($"/items/{i}");
            }
            else
            {
                Assert.Equal($"/infopanel.weather/{to}", PropsKey(item));
                Assert.Equal($"plugin:/infopanel.weather/{to}", item.Meta.Sensor!.Key);
                Assert.Equal(SensorStatus.Live, item.Meta.Sensor.Status);
                mapped.Add($"/items/{i}");
            }
        }

        // The 15 mapped, each listed once; the 4 others unresolved, each listed once.
        Assert.Equal(15, mapped.Count);
        var translated = With(envelope, "import.sensor.translated");
        Assert.Equal(mapped, translated.Select(d => d.Path));
        Assert.All(translated, d => Assert.Equal("info", d.Severity));
        Assert.Equal(unmapped, With(envelope, "import.sensor").Select(d => d.Path));
        Assert.Equal(["/items/3", "/items/4", "/items/10", "/items/18"], unmapped);
    }

    [Fact]
    public void Translated_The15MappedEntries_InPropsAndMeta_Live_ListedOnce_AndThe4OthersUnresolved()
    {
        var envelope = Read(AllNineteen());

        AssertAllNineteen(envelope);
        var translated = With(envelope, "import.sensor.translated");
        Assert.All(translated, d => Assert.Equal("DisplayItems.xml", d.File));
        Assert.Equal(
            "'Weather temp' was bound to plugin:/weather-plugin/weather/temp, the 1.x Weather plugin's; bound to plugin:/infopanel.weather/temperature, 'Temperature' in 'Weather', which the engine reads itself.",
            translated.Single(d => d.Path == "/items/5").Message);
        Assert.Contains("'Pressure (sea level)' in 'Weather'", translated.Single(d => d.Path == "/items/9").Message);
        Assert.Contains("'High (next 24 h)' in 'Weather'", translated.Single(d => d.Path == "/items/6").Message);
        // Importing asks the service nothing more.
        Assert.Equal(1, _weather!.Fetcher.Requests);
    }

    [Fact]
    public void Translated_OnTheFolderRoute_Too()
    {
        var root = _kit.Temp.Combine("v1");
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder"), root);
        const string desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
        File.WriteAllText(Path.Combine(root, "profiles", desk + ".xml"), Items(AllNineteen()));

        var envelope = new V1Folder(root).ReadProfile(Guid.Parse(desk), Context())!;

        Assert.Equal("folder", envelope.Source);
        AssertAllNineteen(envelope);
    }

    [Fact]
    public void Translated_EveryKindBoundToThePlugin_AtAnyDepth()
    {
        var envelope = Read(
            Bound("SensorDisplayItem", "Temp", Weather("temp")),
            Bound("GraphDisplayItem", "Graph", Weather("temp")),
            Bound("GaugeDisplayItem", "Gauge", Weather("wind_deg")),
            Group("Box", Group("Inner", Bound("SensorDisplayItem", "Description", Weather("weather_desc")), Bound("HttpImageDisplayItem", "Icon", Weather("weather_icon_url")))));

        var inner = envelope.Items[3].Children![0].Children!;
        Assert.Equal(["plugin:/infopanel.weather/temperature", "plugin:/infopanel.weather/temperature", "plugin:/infopanel.weather/wind_direction",
            "plugin:/infopanel.weather/condition", "plugin:/weather-plugin/weather/weather_icon_url"],
            envelope.Items.Take(3).Concat(inner).Select(n => n.Meta.Sensor!.Key));
        Assert.Equal(["/items/0", "/items/1", "/items/2", "/items/3/children/0/children/0"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Equal(["/items/3/children/0/children/1"], With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Untouched_AnEntryOrContainerThePluginDidntHave_AndAnItemWhoseBindingIsntThePlugin()
    {
        var envelope = Read(
            Bound("SensorDisplayItem", "Other container", "/weather-plugin/city/temp"),
            Bound("SensorDisplayItem", "Upper", Weather("Temp")),
            Bound("SensorDisplayItem", "Unknown", Weather("visibility")),
            Bound("SensorDisplayItem", "Libre", Weather("temp"), sensorType: "Libre"));

        Assert.Equal(["/weather-plugin/city/temp", Weather("Temp"), Weather("visibility"), Weather("temp")], envelope.Items.Select(PropsKey));
        Assert.Empty(With(envelope, "import.sensor.translated"));
    }

    [Fact]
    public void TheTable_TheOldKeysAre1xs_AndEveryNewKeyIsInTheSourcesCatalogue()
    {
        var catalogue = _weather!.GetSensors().ToDictionary(s => s.Key);
        // 1.x's ids: the plugin SDK's rule over the container's name; the entries' ids are the plugin's own.
        var container = new PluginContainer("Weather").Id;
        Assert.Equal("weather", container);

        foreach (var (old, to) in Table)
        {
            var translation = RetiredPlugins.Translate(SensorKey.Plugin($"/weather-plugin/{container}/{old}"));
            if (to is null)
            {
                Assert.Null(translation);
                continue;
            }

            Assert.NotNull(translation);
            Assert.Equal(WeatherSource.KeyOf(to), translation.Key);
            Assert.True(catalogue.TryGetValue(translation.Key, out var sensor), $"{translation.Key} isn't listed by the weather source");
            Assert.Equal((sensor!.Name, WeatherSource.Name, "Weather"), (translation.Name, translation.Source, translation.Plugin));
        }

        Assert.Equal(15, RetiredPlugins.All.Count(t => t.Key.Id.StartsWith("/weather-plugin/", StringComparison.Ordinal)));
        Assert.Equal("Weather", RetiredPlugins.SourceOf("weather-plugin"));
        Assert.Equal("Weather", RetiredPlugins.SourceOf("Weather-Plugin"));
        Assert.Null(RetiredPlugins.Translate(SensorKey.Libre(Weather("temp"))));
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}
