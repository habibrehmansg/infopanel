using InfoPanel.Core.Tests.Sensors;
using InfoPanel.Core.Tests.Sensors.HwInfo;
using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Sensors;
using InfoPanel.Sensors.HwInfo;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// A 1.x file's smart import: read from a <c>.infopanel</c> file on a PC whose HWiNFO (a fake one, over images a test
/// builds) lacks an item's key, the item is bound to the first reading on its remote with the item's sensor name, a
/// reading in a sensor with the item's sensor id first, and the rebinding is reported (<c>import.sensor.matched</c>).
/// Libre, plugin and table bindings, present keys and the 1.x folder's profiles are read as stored.
/// </summary>
public sealed class V1ReaderSmartImportTests : IDisposable
{
    private const uint Cpu = 0xF0000300, Board = 0xF0000301, Gpu = 0xE0002000;
    private const uint Package = 0x1000000, Usage = 0x7000019, Temp = 0x1000001;

    private readonly ImportKit _kit = new();
    private readonly FakeHwInfoMemory _memory = new();
    private readonly TimerTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero));

    private readonly List<PolledSensorSource> _sources = [];

    public void Dispose()
    {
        foreach (var source in _sources)
            source.Dispose();
        _kit.Dispose();
    }

    /// <summary>
    /// This PC's HWiNFO: a board sensor, then the CPU (its second instance: the file's PC had one sensor fewer), then the
    /// GPU; "Temperature" in each, in that order, and "Total CPU Usage" in the board sensor only.
    /// </summary>
    private static HwInfoImageBuilder ThisPc(string? host = null)
    {
        var prefix = host is null ? "" : $"[{host}] ";
        var b = new HwInfoImageBuilder();
        b.AddSensor(Board, 0, prefix + "Motherboard: Example Board");
        b.AddSensor(Cpu, 1, prefix + "CPU [#0]: Example CPU");
        b.AddSensor(Gpu, 0, prefix + "GPU [#0]: Example GPU");
        b.AddReading(0, Temp, "Temperature", "°C", 41);
        b.AddReading(1, Package, "CPU Package", "°C", 55);
        b.AddReading(1, Temp, "Temperature", "°C", 52);
        b.AddReading(0, Usage, "Total CPU Usage", "%", 7.5);
        b.AddReading(2, Temp, "Temperature", "°C", 48);
        return b;
    }

    /// <summary>A sensor hub with the HWiNFO source over <see cref="_memory"/>, opened (discovered and polled once).</summary>
    private SensorHub Hub()
    {
        var source = HwInfoSource.Create(_memory, _time);
        _sources.Add(source);
        source.Open();
        return new SensorHub([source]);
    }

    private V1Profile Read(byte[] archive, SensorHub sensors) =>
        V1Reader.ReadArchive(new MemoryStream(archive), new V1ReadContext(_kit.Workspace, sensors, null, Fonts.Contains));

    private V1Profile ReadHere(params string[] items)
    {
        _memory.Set(HwInfoSampler.LocalName, ThisPc().Build());
        return Read(V1Archive(Items(items)), Hub());
    }

    /// <summary>A 1.x item of <paramref name="type"/> bound to HWiNFO's sensor <paramref name="id"/>.</summary>
    private static string Bound(string type, string name, string sensorName, uint id, uint instance, uint entry, int remote = -1, string inner = "") =>
        $"""<DisplayItem xsi:type="{type}"><Name>{name}</Name>{inner}<SensorName>{sensorName}</SensorName><SensorType>HwInfo</SensorType><HwInfoRemoteIndex>{remote}</HwInfoRemoteIndex><Id>{id}</Id><Instance>{instance}</Instance><EntryId>{entry}</EntryId></DisplayItem>""";

    private static string Sensor(string name, string sensorName, uint id, uint instance, uint entry, int remote = -1) =>
        Bound("SensorDisplayItem", name, sensorName, id, instance, entry, remote);

    private static (uint Id, uint Instance, uint Entry, int Remote) Binding(ItemNode node) =>
        (uint.Parse(node.Props["id"]!.ToJsonString()), uint.Parse(node.Props["instance"]!.ToJsonString()),
            uint.Parse(node.Props["entryId"]!.ToJsonString()), int.Parse(node.Props["hwInfoRemoteIndex"]!.ToJsonString()));

    private static string Key(int remote, uint id, uint instance, uint entry) => SensorKey.HwInfo(remote, id, instance, entry).ToString();

    [Fact]
    public void Matched_AMissingKey_IsReboundByName_InPropsAndMeta_AndReported()
    {
        var envelope = ReadHere(Sensor("CPU Temp", "CPU Package", Cpu, 0, Package));

        var item = Assert.Single(envelope.Items);
        Assert.Equal((Cpu, 1u, Package, -1), Binding(item));
        Assert.Equal("CPU Package", (string)item.Props["sensorName"]!);
        Assert.Equal("HwInfo", (string)item.Props["sensorType"]!);
        Assert.Equal(Key(-1, Cpu, 1, Package), item.Meta.Sensor!.Key);
        Assert.Equal(SensorStatus.Live, item.Meta.Sensor.Status);

        var matched = Single(envelope, "import.sensor.matched");
        Assert.Equal("info", matched.Severity);
        Assert.Equal("/items/0", matched.Path);
        Assert.Equal("DisplayItems.xml", matched.File);
        Assert.Equal(
            $"'CPU Temp' was bound to {Key(-1, Cpu, 0, Package)}, which this PC doesn't have; bound by name to {Key(-1, Cpu, 1, Package)}, 'CPU Package' in 'CPU [#0]: Example CPU'.",
            matched.Message);
        // Rebound, it reads: no import.sensor line.
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void NotMatched_AnItemWhoseSensorTypeIsUnknown_StaysUnbound()
    {
        // Another build's sensor type: the item is imported unbound, so the user picks its sensor; its sensor name,
        // though HWiNFO lists a reading of that name, doesn't bind it. A HWiNFO binding beside it is still matched.
        var envelope = ReadHere(
            $"""<DisplayItem xsi:type="SensorDisplayItem"><Name>Other build</Name><SensorName>CPU Package</SensorName><SensorType>Hwmon</SensorType><HwInfoRemoteIndex>-1</HwInfoRemoteIndex><Id>{Cpu}</Id><Instance>0</Instance><EntryId>{Package}</EntryId></DisplayItem>""",
            Sensor("CPU Temp", "CPU Package", Cpu, 0, Package));

        Assert.Equal((0u, 0u, 0u, -1), Binding(envelope.Items[0]));
        Assert.Equal(SensorStatus.Missing, envelope.Items[0].Meta.Sensor!.Status);
        Assert.Equal(["/items/1"], With(envelope, "import.sensor.matched").Select(d => d.Path));
        Assert.Equal(["/items/0"], With(envelope, "import.element").Select(d => d.Path));
    }

    [Fact]
    public void Matched_AnItemWithAnElementOfTheMarkersName_IsStillMatched()
    {
        // An input element that happens to have the name an unknown sensor type's element is given is just an unknown
        // element: the item is matched by name as any other is, and nothing says its sensor type is unknown.
        var envelope = ReadHere(Bound("SensorDisplayItem", "Literal", "CPU Package", 0, 0, 0,
            inner: "<SensorType.Unknown>extension data</SensorType.Unknown>"));

        Assert.Equal((Cpu, 1u, Package, -1), Binding(Assert.Single(envelope.Items)));
        Assert.Equal(["/items/0"], With(envelope, "import.sensor.matched").Select(d => d.Path));
        var unknown = Single(envelope, "import.element");
        Assert.Contains("'SensorType.Unknown'", unknown.Message);
        Assert.DoesNotContain("sensor type", unknown.Message);
    }

    [Fact]
    public void Matched_EveryKindBoundToHwinfo_AtAnyDepth_IsRebound()
    {
        var envelope = ReadHere(
            Bound("SensorDisplayItem", "Sensor", "CPU Package", Cpu, 0, Package),
            Bound("GraphDisplayItem", "Graph", "CPU Package", Cpu, 0, Package),
            Bound("BarDisplayItem", "Bar", "CPU Package", Cpu, 0, Package),
            Bound("DonutDisplayItem", "Donut", "CPU Package", Cpu, 0, Package),
            Bound("GaugeDisplayItem", "Gauge", "CPU Package", Cpu, 0, Package),
            Bound("SensorImageDisplayItem", "Sensor image", "CPU Package", Cpu, 0, Package),
            Bound("HttpImageDisplayItem", "Web image", "CPU Package", Cpu, 0, Package),
            Group("Box", Group("Inner", Sensor("Nested", "CPU Package", Cpu, 0, Package))));

        var expected = Key(-1, Cpu, 1, Package);
        var bound = envelope.Items.Take(7).Append(envelope.Items[7].Children![0].Children![0]).ToList();
        Assert.Equal(["sensor", "graph", "bar", "donut", "gauge", "sensorImage", "httpImage", "sensor"], bound.Select(n => n.Kind));
        Assert.All(bound, node => Assert.Equal(expected, node.Meta.Sensor!.Key));
        Assert.All(bound, node => Assert.Equal((Cpu, 1u, Package, -1), Binding(node)));
        Assert.Equal(
            ["/items/0", "/items/1", "/items/2", "/items/3", "/items/4", "/items/5", "/items/6", "/items/7/children/0/children/0"],
            With(envelope, "import.sensor.matched").Select(d => d.Path));
    }

    [Fact]
    public void Matched_AmongEqualNames_ASensorWithTheItemsSensorIdComesFirst_ThenHwinfosOrder()
    {
        var envelope = ReadHere(
            // The CPU's "Temperature" though the board's comes first in HWiNFO's order.
            Sensor("CPU", "Temperature", Cpu, 0, Temp),
            // The GPU's, likewise, past two others.
            Sensor("GPU", "Temperature", Gpu, 3, 0x1000009),
            // No sensor with its id: HWiNFO's first.
            Sensor("Other", "Temperature", 0xF0000999, 0, Temp));

        Assert.Equal((Cpu, 1u, Temp, -1), Binding(envelope.Items[0]));
        Assert.Equal((Gpu, 0u, Temp, -1), Binding(envelope.Items[1]));
        Assert.Equal((Board, 0u, Temp, -1), Binding(envelope.Items[2]));
        Assert.Equal(3, With(envelope, "import.sensor.matched").Count);
    }

    [Fact]
    public void Matched_OnTheItemsOwnRemote_Only()
    {
        _memory.Set(HwInfoSampler.LocalName, ThisPc().Build());
        _memory.Set(HwInfoSampler.RemotePrefix + "1", ThisPc("PC1").Build());
        var hub = Hub();

        var envelope = Read(V1Archive(Items(
            Sensor("Remote CPU", "CPU Package", Cpu, 0, Package, remote: 1),
            // Remote 2 isn't connected: nothing there to match, though this PC has the name.
            Sensor("Gone", "CPU Package", Cpu, 0, Package, remote: 2))), hub);

        Assert.Equal((Cpu, 1u, Package, 1), Binding(envelope.Items[0]));
        Assert.Equal(Key(1, Cpu, 1, Package), envelope.Items[0].Meta.Sensor!.Key);
        Assert.Equal((Cpu, 0u, Package, 2), Binding(envelope.Items[1]));
        Assert.Equal("/items/0", Single(envelope, "import.sensor.matched").Path);
        Assert.Equal("/items/1", Single(envelope, "import.sensor").Path);
    }

    [Fact]
    public void Untouched_ANameThisPcDoesntHave_StaysBound_AsImportSensor()
    {
        var envelope = ReadHere(Sensor("Fan", "CPU Fan", Board, 0, 0x3000000), Sensor("Unnamed", "", Cpu, 0, Package));

        Assert.Equal((Board, 0u, 0x3000000u, -1), Binding(envelope.Items[0]));
        Assert.Equal((Cpu, 0u, Package, -1), Binding(envelope.Items[1]));
        Assert.Empty(With(envelope, "import.sensor.matched"));
        Assert.Equal(["/items/0", "/items/1"], With(envelope, "import.sensor").Select(d => d.Path));
    }

    [Fact]
    public void Untouched_APresentKey_EvenUnderAnotherName()
    {
        // The key is this PC's; its name differs from the reading's (renamed in 1.x, or HWiNFO's names changed).
        var envelope = ReadHere(Sensor("Usage", "CPU Package", Board, 0, Usage));

        Assert.Equal((Board, 0u, Usage, -1), Binding(envelope.Items[0]));
        Assert.Equal(Key(-1, Board, 0, Usage), envelope.Items[0].Meta.Sensor!.Key);
        Assert.Empty(With(envelope, "import.sensor.matched"));
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void Untouched_LibrePluginAndTableBindings_ThoughHwinfoHasTheirNames()
    {
        string[] items =
        [
            """<DisplayItem xsi:type="SensorDisplayItem"><Name>Libre</Name><SensorName>CPU Package</SensorName><SensorType>Libre</SensorType><LibreSensorId>/amdcpu/0/temperature/0</LibreSensorId><Id>4026532608</Id><Instance>0</Instance><EntryId>16777216</EntryId></DisplayItem>""",
            """<DisplayItem xsi:type="DonutDisplayItem"><Name>Plugin</Name><SensorName>CPU Package</SensorName><SensorType>Plugin</SensorType><PluginSensorId>/example/cpu/package</PluginSensorId><Id>4026532608</Id><Instance>0</Instance><EntryId>16777216</EntryId></DisplayItem>""",
            """<DisplayItem xsi:type="TableSensorDisplayItem"><Name>Table</Name><SensorName>CPU Package</SensorName><SensorType>HwInfo</SensorType><PluginSensorId>/example/processes</PluginSensorId></DisplayItem>""",
        ];

        var envelope = ReadHere(items);

        // As stored: the same file read with no sensor sources.
        var stored = Read(V1Archive(Items(items)), new SensorHub([]));
        Assert.Equal(WithoutIds(stored.Items).ToJsonString(), WithoutIds(envelope.Items).ToJsonString());
        Assert.Equal("libre:/amdcpu/0/temperature/0", envelope.Items[0].Meta.Sensor!.Key);
        Assert.Equal("plugin:/example/cpu/package", envelope.Items[1].Meta.Sensor!.Key);
        Assert.Equal("plugin:/example/processes", envelope.Items[2].Meta.Sensor!.Key);
        Assert.Empty(With(envelope, "import.sensor.matched"));
        Assert.Equal(3, With(envelope, "import.sensor").Count);
    }

    [Fact]
    public void Untouched_TheFoldersProfiles_AreThisPcsOwn()
    {
        var root = _kit.Temp.Combine("v1");
        CopyFolder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder"), root);
        const string desk = "6f1c2a3b-4d5e-4f60-8a71-92b3c4d5e6f7";
        File.WriteAllText(Path.Combine(root, "profiles", desk + ".xml"), Items(Sensor("CPU Temp", "CPU Package", Cpu, 0, Package)));
        _memory.Set(HwInfoSampler.LocalName, ThisPc().Build());

        var envelope = new V1Folder(root).ReadProfile(Guid.Parse(desk), new V1ReadContext(_kit.Workspace, Hub(), null, Fonts.Contains))!;

        Assert.Equal("folder", envelope.Source);
        Assert.Equal((Cpu, 0u, Package, -1), Binding(envelope.Items[0]));
        Assert.Equal(Key(-1, Cpu, 0, Package), envelope.Items[0].Meta.Sensor!.Key);
        Assert.Empty(With(envelope, "import.sensor.matched"));
        Assert.Equal("/items/0", Single(envelope, "import.sensor").Path);
    }

    [Fact]
    public void Untouched_WithoutHwinfo_OnlyImportSensorLines()
    {
        var items = Items(Sensor("CPU Temp", "CPU Package", Cpu, 0, Package), Sensor("GPU", "Temperature", Gpu, 0, Temp));

        // No HWiNFO source (a Mac), and one whose HWiNFO isn't running (no mapping).
        foreach (var hub in new[] { new SensorHub([]), Hub() })
        {
            var envelope = Read(V1Archive(items), hub);

            Assert.Equal((Cpu, 0u, Package, -1), Binding(envelope.Items[0]));
            Assert.Equal((Gpu, 0u, Temp, -1), Binding(envelope.Items[1]));
            Assert.Empty(With(envelope, "import.sensor.matched"));
            Assert.Equal(["/items/0", "/items/1"], With(envelope, "import.sensor").Select(d => d.Path));
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
