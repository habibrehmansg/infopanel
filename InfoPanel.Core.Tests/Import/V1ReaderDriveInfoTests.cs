using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Plugins;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using InfoPanel.Sensors.Drives;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x profile bound to the 1.x Drive Info plugin, whose sensors the engine's drives source reads now: reading it,
/// from a <c>.infopanel</c> file or the 1.x folder, binds every item bound to one of a drive's entries to the source's
/// key for that drive and entry, in the model, so its props and <c>meta.sensor</c> carry the new binding, and reports
/// each (<c>import.sensor.translated</c>). An entry the source doesn't have stays as stored, unresolved
/// (<c>import.sensor</c>).
/// </summary>
public sealed class V1ReaderDriveInfoTests : IDisposable
{
    private static readonly string[] Ids = ["name", "type", "volume_label", "format", "total_size", "free_space", "available_space", "used_space"];

    private sealed class Volume(string name) : IVolume
    {
        public string Name => name;
        public DriveType DriveType => DriveType.Fixed;
        public bool IsReady => true;
        public string VolumeLabel => "Windows";
        public string DriveFormat => "NTFS";
        public long TotalSize => 500_107_862_016;
        public long TotalFreeSpace => 123_456_789_012;
        public long AvailableFreeSpace => 100_000_000_000;
    }

    private sealed class Lister(params string[] names) : IVolumeLister
    {
        public IReadOnlyList<IVolume> List() => names.Select(n => (IVolume)new Volume(n)).ToArray();
    }

    private readonly ImportKit _kit = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 13, 5, 7, TimeSpan.Zero));
    private readonly PolledSensorSource _drives;

    public V1ReaderDriveInfoTests()
    {
        // This PC has C: and D:.
        _drives = DriveSource.Create(new Lister(@"C:\", @"D:\"), VolumePlatform.Windows, _time);
        _drives.Open();
    }

    public void Dispose()
    {
        _drives.Dispose();
        _kit.Dispose();
    }

    /// <summary>This engine's sensors: the clock and the drives, unknown keys unavailable (not mock mode).</summary>
    private SensorHub Hub() => new([new ClockSensorSource(_time), _drives]);

    private V1ReadContext Context() => new(_kit.Workspace, Hub(), null, Fonts.Contains);

    private V1Profile Read(params string[] items) => V1Reader.ReadArchive(new MemoryStream(V1Archive(Items(items))), Context());

    /// <summary>A 1.x item of <paramref name="type"/> bound to the plugin key <paramref name="key"/>.</summary>
    private static string Bound(string type, string name, string key, string sensorType = "Plugin") =>
        $"""<DisplayItem xsi:type="{type}"><Name>{name}</Name><SensorName>{name}</SensorName><SensorType>{sensorType}</SensorType><PluginSensorId>{key}</PluginSensorId><Id>0</Id><Instance>0</Instance><EntryId>0</EntryId></DisplayItem>""";

    private static string Drive(string drive, string id) => $"/drive-info-plugin/{drive}/{id}";

    private static string PropsKey(ItemNode node) => (string)node.Props["pluginSensorId"]!;

    [Fact]
    public void Translated_EveryEntryOfADrive_IsBoundToTheDrivesSource_InPropsAndMeta_Live_AndListedOnce()
    {
        var envelope = Read([.. Ids.Select(id => Bound("SensorDisplayItem", $"C {id}", Drive("c", id)))]);

        Assert.Equal(Ids.Length, envelope.Items.Count);
        for (var i = 0; i < Ids.Length; i++)
        {
            var item = envelope.Items[i];
            Assert.Equal($"/infopanel.drives/c/{Ids[i]}", PropsKey(item));
            Assert.Equal("Plugin", (string)item.Props["sensorType"]!);
            Assert.Equal($"infopanel:/drives/c/{Ids[i]}", item.Meta.Sensor!.Key);
            Assert.Equal(SensorStatus.Live, item.Meta.Sensor.Status);
        }

        var translated = With(envelope, "import.sensor.translated");
        Assert.Equal(Ids.Select((_, i) => $"/items/{i}"), translated.Select(d => d.Path));
        Assert.All(translated, d => Assert.Equal(("info", "DisplayItems.xml"), (d.Severity, d.File)));
        Assert.Equal(
            "'C free_space' was bound to plugin:/drive-info-plugin/c/free_space, the 1.x Drive Info plugin's; bound to infopanel:/drives/c/free_space, 'Free Space of C:' in 'Drives', which the engine reads itself.",
            translated[5].Message);
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Translated_AnyDrive_ThoughThisPcLacksIt_WhichReadsUnavailable()
    {
        // A theme from another PC: its E: is bound to the drives source's E:, which this PC doesn't have.
        var envelope = Read(
            Bound("BarDisplayItem", "D bar", Drive("d", "used_space")),
            Bound("BarDisplayItem", "E bar", Drive("e", "used_space")),
            Bound("SensorDisplayItem", "Share", Drive("servershare", "free_space")));

        Assert.Equal(["infopanel:/drives/d/used_space", "infopanel:/drives/e/used_space", "infopanel:/drives/servershare/free_space"],
            envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.Equal([SensorStatus.Live, SensorStatus.Unavailable, SensorStatus.Unavailable], envelope.Items.Select(n => n.Meta.Sensor!.Status));
        Assert.Equal(["/items/0", "/items/1", "/items/2"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Contains("'Used Space of E:' in 'Drives'", With(envelope, "import.sensor.translated")[1].Message);
        Assert.Contains("'Free Space of servershare' in 'Drives'", With(envelope, "import.sensor.translated")[2].Message);
        // Translated, they aren't the plugin's any more; the ones this PC lacks read unavailable, as any missing sensor.
        Assert.Equal(["/items/1", "/items/2"], With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Translated_EveryKindBoundToThePlugin_AtAnyDepth_AndBesideTheClock()
    {
        var envelope = Read(
            Bound("SensorDisplayItem", "Sensor", Drive("c", "free_space")),
            Bound("GraphDisplayItem", "Graph", Drive("c", "free_space")),
            Bound("BarDisplayItem", "Bar", Drive("c", "used_space")),
            Bound("DonutDisplayItem", "Donut", Drive("c", "used_space")),
            Bound("GaugeDisplayItem", "Gauge", Drive("c", "used_space")),
            Bound("SensorImageDisplayItem", "Sensor image", Drive("c", "free_space")),
            Bound("HttpImageDisplayItem", "Web image", Drive("c", "free_space")),
            Group("Box", Group("Inner", Bound("SensorDisplayItem", "Label", Drive("d", "volume_label")), Bound("GaugeDisplayItem", "Hand", "/clock-plugin/clock/second"))));

        var inner = envelope.Items[7].Children![0].Children!;
        var bound = envelope.Items.Take(7).Concat(inner).ToList();
        Assert.Equal(["sensor", "graph", "bar", "donut", "gauge", "sensorImage", "httpImage", "sensor", "gauge"], bound.Select(n => n.Kind));
        Assert.Equal(
            [.. new[] { "free_space", "free_space", "used_space", "used_space", "used_space", "free_space", "free_space" }.Select(id => $"infopanel:/drives/c/{id}"),
             "infopanel:/drives/d/volume_label", "infopanel:/clock/second"],
            bound.Select(n => n.Meta.Sensor!.Key));
        Assert.Equal(
            ["/items/0", "/items/1", "/items/2", "/items/3", "/items/4", "/items/5", "/items/6", "/items/7/children/0/children/0", "/items/7/children/0/children/1"],
            With(envelope, "import.sensor.translated").Select(d => d.Path));
    }

    [Fact]
    public void Translated_OnTheFolderRoute_Too()
    {
        var root = _kit.Temp.Combine("v1");
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder"), root);
        const string desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
        File.WriteAllText(Path.Combine(root, "profiles", desk + ".xml"), Items(
            Bound("BarDisplayItem", "C used", Drive("c", "used_space")),
            Bound("SensorDisplayItem", "C free", Drive("c", "free_space")),
            Bound("SensorDisplayItem", "D name", Drive("d", "name"))));

        var envelope = new V1Folder(root).ReadProfile(Guid.Parse(desk), Context())!;

        Assert.Equal("folder", envelope.Source);
        Assert.Equal(["infopanel:/drives/c/used_space", "infopanel:/drives/c/free_space", "infopanel:/drives/d/name"],
            envelope.Items.Select(n => n.Meta.Sensor!.Key));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Live, n.Meta.Sensor!.Status));
        Assert.Equal(["/items/0", "/items/1", "/items/2"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Untouched_AnEntryTheSourceDoesntHave_OrADriveIdThePluginNeverMade_StaysBound_AndUnresolved()
    {
        var envelope = Read(
            Bound("SensorDisplayItem", "Percent", Drive("c", "used_percent")),
            Bound("SensorDisplayItem", "Upper", Drive("C", "free_space")),
            Bound("SensorDisplayItem", "Upper entry", Drive("c", "Free_Space")),
            Bound("SensorDisplayItem", "Deeper", "/drive-info-plugin/c/x/free_space"),
            Bound("SensorDisplayItem", "No entry", "/drive-info-plugin/c"));

        Assert.Equal(["/drive-info-plugin/c/used_percent", "/drive-info-plugin/C/free_space", "/drive-info-plugin/c/Free_Space", "/drive-info-plugin/c/x/free_space", "/drive-info-plugin/c"],
            envelope.Items.Select(PropsKey));
        Assert.All(envelope.Items, n => Assert.Equal(SensorStatus.Unavailable, n.Meta.Sensor!.Status));
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.Equal(["/items/0", "/items/1", "/items/2", "/items/3", "/items/4"], With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Untouched_AnItemWhoseBindingIsntThePlugin_ThoughItKeepsADriveId()
    {
        var envelope = Read(Bound("SensorDisplayItem", "Libre", Drive("c", "free_space"), sensorType: "Libre"));

        Assert.Equal(Drive("c", "free_space"), PropsKey(envelope.Items[0]));
        Assert.Empty(With(envelope, "import.sensor.translated"));
    }

    [Fact]
    public void TheTable_TheOldKeysAre1xs_AndEveryNewKeyIsInTheSourcesCatalogue()
    {
        // 1.x's ids: the plugin SDK's rule over the drive's name ("C:", "D:"); the entries' ids are the plugin's own.
        foreach (var name in new[] { "C:", "D:" })
        {
            var container = new PluginContainer(name).Id;
            var catalogue = _drives.GetSensors().ToDictionary(s => s.Key);
            foreach (var id in Ids)
            {
                var old = SensorKey.Plugin($"/drive-info-plugin/{container}/{id}");
                var to = RetiredPlugins.Translate(old);
                Assert.NotNull(to);
                Assert.True(catalogue.TryGetValue(to.Key, out var sensor), $"{to.Key} isn't listed by the drives source");
                Assert.Equal(($"{sensor!.Name} of {name}", DriveSource.Name, "Drive Info"), (to.Name, to.Source, to.Plugin));
            }
        }

        Assert.Equal("Drives", RetiredPlugins.SourceOf("drive-info-plugin"));
        Assert.Equal("Drives", RetiredPlugins.SourceOf("Drive-Info-Plugin"));
        Assert.Null(RetiredPlugins.Translate(SensorKey.Libre("/drive-info-plugin/c/free_space")));
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
