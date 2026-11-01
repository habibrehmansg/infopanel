using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x profile bound to a plugin whose sensors the engine reads itself now (the Clock plugin): reading it, from a
/// <c>.infopanel</c> file or the 1.x folder, binds every item bound to one of the plugin's entries to the source's key
/// for it, in the model, so its props and <c>meta.sensor</c> carry the new binding, and reports each
/// (<c>import.sensor.translated</c>). A key no source has an entry for stays as stored, unresolved (<c>import.sensor</c>).
/// </summary>
public sealed class V1ReaderRetiredPluginTests : IDisposable
{
    private static readonly string[] Ids = ["day", "month", "year", "12-hour", "24-hour", "minute", "second", "pm"];

    private readonly ImportKit _kit = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 13, 5, 7, TimeSpan.Zero));

    public void Dispose() => _kit.Dispose();

    /// <summary>This engine's sensors: the clock source alone, unknown keys unavailable (not mock mode).</summary>
    private SensorHub Hub() => new([new ClockSensorSource(_time)]);

    private V1ReadContext Context() => new(_kit.Workspace, Hub(), null, Fonts.Contains);

    private V1Profile Read(params string[] items) => V1Reader.ReadArchive(new MemoryStream(V1Archive(Items(items))), Context());

    /// <summary>A 1.x item of <paramref name="type"/> bound to the plugin key <paramref name="key"/>.</summary>
    private static string Bound(string type, string name, string key, string sensorType = "Plugin") =>
        $"""<DisplayItem xsi:type="{type}"><Name>{name}</Name><SensorName>{name}</SensorName><SensorType>{sensorType}</SensorType><PluginSensorId>{key}</PluginSensorId><Id>0</Id><Instance>0</Instance><EntryId>0</EntryId></DisplayItem>""";

    private static string Clock(string id) => $"/clock-plugin/clock/{id}";

    private static string PropsKey(ItemNode node) => (string)node.Props["pluginSensorId"]!;

    [Fact]
    public void Translated_EveryClockEntry_IsBoundToTheClockSource_InPropsAndMeta_Live_AndListedOnce()
    {
        var envelope = Read([.. Ids.Select(id => Bound("SensorDisplayItem", $"Clock {id}", Clock(id)))]);

        Assert.Equal(Ids.Length, envelope.Items.Count);
        for (var i = 0; i < Ids.Length; i++)
        {
            var item = envelope.Items[i];
            Assert.Equal($"/infopanel.clock/{Ids[i]}", PropsKey(item));
            Assert.Equal("Plugin", (string)item.Props["sensorType"]!);
            Assert.Equal($"plugin:/infopanel.clock/{Ids[i]}", item.Meta.Sensor!.Key);
            Assert.Equal(SensorStatus.Live, item.Meta.Sensor.Status);
        }

        var translated = With(envelope, "import.sensor.translated");
        Assert.Equal(Ids.Select((_, i) => $"/items/{i}"), translated.Select(d => d.Path));
        Assert.All(translated, d => Assert.Equal(("info", "DisplayItems.xml"), (d.Severity, d.File)));
        Assert.Equal(
            "'Clock second' was bound to plugin:/clock-plugin/clock/second, the 1.x Clock plugin's; bound to plugin:/infopanel.clock/second, 'Second' in 'Clock', which the engine reads itself.",
            translated[6].Message);
        // Translated, they read: no import.sensor line.
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Translated_EveryKindBoundToThePlugin_AtAnyDepth()
    {
        var envelope = Read(
            Bound("SensorDisplayItem", "Sensor", Clock("second")),
            Bound("GraphDisplayItem", "Graph", Clock("second")),
            Bound("BarDisplayItem", "Bar", Clock("second")),
            Bound("DonutDisplayItem", "Donut", Clock("second")),
            Bound("GaugeDisplayItem", "Gauge", Clock("second")),
            Bound("SensorImageDisplayItem", "Sensor image", Clock("second")),
            Bound("HttpImageDisplayItem", "Web image", Clock("second")),
            Group("Box", Group("Inner", Bound("GaugeDisplayItem", "Hand", Clock("minute")))));

        var bound = envelope.Items.Take(7).Append(envelope.Items[7].Children![0].Children![0]).ToList();
        Assert.Equal(["sensor", "graph", "bar", "donut", "gauge", "sensorImage", "httpImage", "gauge"], bound.Select(n => n.Kind));
        Assert.Equal([.. Enumerable.Repeat("plugin:/infopanel.clock/second", 7), "plugin:/infopanel.clock/minute"], bound.Select(n => n.Meta.Sensor!.Key));
        Assert.Equal([.. Enumerable.Repeat("/infopanel.clock/second", 7), "/infopanel.clock/minute"], bound.Select(PropsKey));
        Assert.Equal(
            ["/items/0", "/items/1", "/items/2", "/items/3", "/items/4", "/items/5", "/items/6", "/items/7/children/0/children/0"],
            With(envelope, "import.sensor.translated").Select(d => d.Path));
    }

    [Fact]
    public void Translated_OnTheFolderRoute_Too()
    {
        var root = _kit.Temp.Combine("v1");
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder"), root);
        const string desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
        File.WriteAllText(Path.Combine(root, "profiles", desk + ".xml"), Items(
            Bound("GaugeDisplayItem", "Hours", Clock("12-hour")),
            Bound("GaugeDisplayItem", "Minutes", Clock("minute")),
            Bound("GaugeDisplayItem", "Seconds", Clock("second"))));

        var envelope = new V1Folder(root).ReadProfile(Guid.Parse(desk), Context())!;

        Assert.Equal("folder", envelope.Source);
        Assert.Equal(["plugin:/infopanel.clock/12-hour", "plugin:/infopanel.clock/minute", "plugin:/infopanel.clock/second"],
            envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Live, n.Meta.Sensor!.Status));
        Assert.Equal(["/items/0", "/items/1", "/items/2"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Untouched_AnEntryOrContainerTheSourceDoesntHave_StaysBound_AndUnresolved()
    {
        var envelope = Read(
            Bound("SensorDisplayItem", "Weekday", Clock("weekday")),
            Bound("SensorDisplayItem", "Other", "/clock-plugin/other/second"),
            Bound("SensorDisplayItem", "Upper", "/clock-plugin/clock/Second"));

        Assert.Equal(["/clock-plugin/clock/weekday", "/clock-plugin/other/second", "/clock-plugin/clock/Second"], envelope.Items.Select(PropsKey));
        Assert.Equal(["plugin:/clock-plugin/clock/weekday", "plugin:/clock-plugin/other/second", "plugin:/clock-plugin/clock/Second"],
            envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Unavailable, n.Meta.Sensor!.Status));
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.Equal(["/items/0", "/items/1", "/items/2"], With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Untouched_AnItemWhoseBindingIsntThePlugin_ThoughItKeepsAClockId()
    {
        // 1.x keeps every binding's fields; only the one SensorType names is the item's.
        var envelope = Read(Bound("SensorDisplayItem", "Libre", Clock("second"), sensorType: "Libre"));

        Assert.Equal(Clock("second"), PropsKey(envelope.Items[0]));
        Assert.Empty(With(envelope, "import.sensor.translated"));
    }

    [Fact]
    public void TheTable_TheOldKeysAre1xs_AndEveryNewKeyIsInTheSourcesCatalogue()
    {
        // 1.x's ids: the plugin SDK's rule over the plugin's container and entry names.
        string[] names = ["Day", "Month", "Year", "12 Hour", "24 Hour", "Minute", "Second", "PM"];
        var container = new PluginContainer("Clock").Id;
        var expected = names.Select(n => SensorKey.Plugin($"/clock-plugin/{container}/{new PluginSensor(n, 0).Id}").ToString()).ToList();

        var table = RetiredPlugins.All.ToList();

        Assert.Equal(expected.Order(StringComparer.Ordinal), table.Select(t => t.Key.ToString()).Order(StringComparer.Ordinal));
        var catalogue = new ClockSensorSource(_time).GetSensors().ToDictionary(s => s.Key);
        Assert.All(table, t =>
        {
            Assert.True(catalogue.TryGetValue(t.Value.Key, out var sensor), $"{t.Value.Key} isn't listed by the clock source");
            Assert.Equal((sensor!.Name, ClockSensorSource.Name), (t.Value.Name, t.Value.Source));
        });
        Assert.Equal("Clock", RetiredPlugins.SourceOf("clock-plugin"));
        Assert.Null(RetiredPlugins.SourceOf("weather-plugin"));
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
