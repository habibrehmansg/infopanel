using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using InfoPanel.Sensors.Volume;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x profile bound to a plugin whose sensors the engine reads itself now (the Clock plugin; the Volume Info plugin,
/// its default output device and any device by the SDK's id of its endpoint id): reading it, from a
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

        // The Clock plugin's part of the fixed table (the Weather plugin's is V1ReaderWeatherTests').
        var table = RetiredPlugins.All.Where(t => t.Key.Id.StartsWith("/clock-plugin/", StringComparison.Ordinal)).ToList();

        Assert.Equal(expected.Order(StringComparer.Ordinal), table.Select(t => t.Key.ToString()).Order(StringComparer.Ordinal));
        var catalogue = new ClockSensorSource(_time).GetSensors().ToDictionary(s => s.Key);
        Assert.All(table, t =>
        {
            Assert.True(catalogue.TryGetValue(t.Value.Key, out var sensor), $"{t.Value.Key} isn't listed by the clock source");
            Assert.Equal((sensor!.Name, ClockSensorSource.Name), (t.Value.Name, t.Value.Source));
        });
        Assert.Equal("Clock", RetiredPlugins.SourceOf("clock-plugin"));
        Assert.Equal("Volume", RetiredPlugins.SourceOf("volume-plugin"));
    }

    // ---- The 1.x Volume Info plugin (volume-plugin), read by the volume source (infopanel.volume) ----

    private static readonly string[] VolumeIds = ["device_friendly_name", "friendly_name", "short_name", "volume", "mute"];

    // A Windows endpoint id, as the plugin kept it as its container's id, and the plugin SDK's id of it.
    private const string Endpoint = "{0.0.0.00000000}.{a1b2c3d4-0000-4000-8000-0123456789ab}";
    private const string EndpointId = "00000000000a1b2c3d40000400080000123456789ab";

    // Another PC's endpoint, and its SDK id.
    private const string OtherEndpoint = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";
    private const string OtherEndpointId = "0000000000011111111222233334444555555555555";

    private sealed class Speakers : IAudioDevice
    {
        public string Id => Endpoint;
        public string Name => "Speakers (High Definition Audio Device)";
        public string DeviceName => "High Definition Audio Device";
        public float? Volume => 0.67f;
        public bool? Mute => false;
    }

    private sealed class ThisPcsSpeakers : IAudioLister
    {
        private readonly Speakers _speakers = new();

        public IReadOnlyList<IAudioDevice> List() => [_speakers];

        public IAudioDevice? Default() => _speakers;
    }

    /// <summary>This engine's sensors: the clock and the volume source over this PC's speakers (the default output device), read once.</summary>
    private V1ReadContext VolumeContext(PolledSensorSource volume) => new(_kit.Workspace, new SensorHub([new ClockSensorSource(_time), volume]), null, Fonts.Contains);

    private PolledSensorSource OpenVolume()
    {
        var volume = VolumeSource.Create(new ThisPcsSpeakers(), _time);
        volume.Open();
        return volume;
    }

    private static string Vol(string container, string id) => $"/volume-plugin/{container}/{id}";

    [Fact]
    public void Volume_EveryEntryOfTheDefaultDevice_AndOfADevice_IsBoundToTheVolumeSource_InPropsAndMeta_Live_AndListedOnce()
    {
        using var volume = OpenVolume();
        var envelope = V1Reader.ReadArchive(new MemoryStream(V1Archive(Items(
        [
            .. VolumeIds.Select(id => Bound("SensorDisplayItem", $"Default {id}", Vol("default", id))),
            .. VolumeIds.Select(id => Bound("SensorDisplayItem", $"Speakers {id}", Vol(Endpoint, id))),
        ]))), VolumeContext(volume));

        string[] expected = [.. VolumeIds.Select(id => $"default/{id}"), .. VolumeIds.Select(id => $"{EndpointId}/{id}")];
        Assert.Equal(expected.Select(k => $"/infopanel.volume/{k}"), envelope.Items.Select(PropsKey));
        Assert.Equal(expected.Select(k => $"plugin:/infopanel.volume/{k}"), envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Live, n.Meta.Sensor!.Status));
        Assert.All(envelope.Items, n => Assert.Equal("Plugin", (string)n.Props["sensorType"]!));

        var translated = With(envelope, "import.sensor.translated");
        Assert.Equal(expected.Select((_, i) => $"/items/{i}"), translated.Select(d => d.Path));
        Assert.All(translated, d => Assert.Equal(("info", "DisplayItems.xml"), (d.Severity, d.File)));
        Assert.Equal(
            "'Default volume' was bound to plugin:/volume-plugin/default/volume, the 1.x Volume Info plugin's; bound to plugin:/infopanel.volume/default/volume, 'Volume of Default' in 'Volume', which the engine reads itself.",
            translated[3].Message);
        Assert.Equal(
            $"'Speakers mute' was bound to plugin:/volume-plugin/{Endpoint}/mute, the 1.x Volume Info plugin's; bound to plugin:/infopanel.volume/{EndpointId}/mute, 'Mute of {Endpoint}' in 'Volume', which the engine reads itself.",
            translated[9].Message);
        Assert.Contains("'Device Name of Default' in 'Volume'", translated[0].Message);
        Assert.Contains($"'Short Name of {Endpoint}' in 'Volume'", translated[7].Message);
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Volume_AnotherPcsDevice_IsBound_AndReadsUnavailable_EveryKind_AtAnyDepth()
    {
        using var volume = OpenVolume();
        var envelope = V1Reader.ReadArchive(new MemoryStream(V1Archive(Items(
            Bound("GaugeDisplayItem", "Gauge", Vol("default", "volume")),
            Bound("BarDisplayItem", "Bar", Vol(Endpoint, "volume")),
            Bound("SensorDisplayItem", "Other", Vol(OtherEndpoint, "volume")),
            Group("Box", Bound("SensorDisplayItem", "Muted", Vol("default", "mute")))))), VolumeContext(volume));

        var bound = envelope.Items.Take(3).Append(envelope.Items[3].Children![0]).ToList();
        Assert.Equal(["gauge", "bar", "sensor", "sensor"], bound.Select(n => n.Kind));
        Assert.Equal(
            ["plugin:/infopanel.volume/default/volume", $"plugin:/infopanel.volume/{EndpointId}/volume", $"plugin:/infopanel.volume/{OtherEndpointId}/volume", "plugin:/infopanel.volume/default/mute"],
            bound.Select(n => n.Meta.Sensor!.Key));
        Assert.Equal([SensorStatus.Live, SensorStatus.Live, SensorStatus.Unavailable, SensorStatus.Live], bound.Select(n => n.Meta.Sensor!.Status));
        Assert.Equal(["/items/0", "/items/1", "/items/2", "/items/3/children/0"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        // Another PC's device is bound all the same, and reads unavailable here.
        Assert.Equal(["/items/2"], With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Volume_OnTheFolderRoute_Too()
    {
        using var volume = OpenVolume();
        var root = _kit.Temp.Combine("v1");
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder"), root);
        const string desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
        File.WriteAllText(Path.Combine(root, "profiles", desk + ".xml"), Items(
            Bound("GaugeDisplayItem", "Volume", Vol("default", "volume")),
            Bound("SensorDisplayItem", "Mute", Vol(Endpoint, "mute"))));

        var envelope = new V1Folder(root).ReadProfile(Guid.Parse(desk), VolumeContext(volume))!;

        Assert.Equal("folder", envelope.Source);
        Assert.Equal(["plugin:/infopanel.volume/default/volume", $"plugin:/infopanel.volume/{EndpointId}/mute"], envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Live, n.Meta.Sensor!.Status));
        Assert.Equal(["/items/0", "/items/1"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Volume_AnEntryTheSourceDoesntHave_OrADeviceWhoseIdMakesNoneOrDefault_StaysBound_AndUnresolved()
    {
        using var volume = OpenVolume();
        string[] keys =
        [
            Vol("default", "peak"),
            Vol("default", "Volume"),
            Vol("Default", "volume"),
            Vol("{}", "volume"),
            Vol("{De-fault}", "volume"),
            "/volume-plugin/default",
            "/volume-plugin/volume",
        ];
        var envelope = V1Reader.ReadArchive(new MemoryStream(V1Archive(Items([.. keys.Select((k, i) => Bound("SensorDisplayItem", $"Item {i}", k))]))), VolumeContext(volume));

        Assert.Equal(keys, envelope.Items.Select(PropsKey));
        Assert.Equal(keys.Select(k => $"plugin:{k}"), envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Unavailable, n.Meta.Sensor!.Status));
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.Equal(keys.Select((_, i) => $"/items/{i}"), With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Volume_TheOldKeysAre1xs_AndEveryNewKeyIsInTheSourcesCatalogue()
    {
        // 1.x's ids: the Default container by the plugin SDK's rule over its name, the entries the plugin's own ids,
        // a device's container its endpoint id as the plugin kept it.
        Assert.Equal("default", new PluginContainer("Default").Id);
        using var volume = OpenVolume();
        var catalogue = volume.GetSensors().ToDictionary(s => s.Key);
        foreach (var container in new[] { "default", Endpoint })
        {
            foreach (var id in VolumeIds)
            {
                var translation = RetiredPlugins.Translate(SensorKey.Plugin(Vol(container, id)));
                Assert.NotNull(translation);
                Assert.True(catalogue.TryGetValue(translation.Key, out var sensor), $"{translation.Key} isn't listed by the volume source");
                Assert.Equal(("Volume Info", VolumeSource.Name), (translation.Plugin, translation.Source));
                Assert.StartsWith(sensor!.Name + " of ", translation.Name, StringComparison.Ordinal);
            }
        }
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
