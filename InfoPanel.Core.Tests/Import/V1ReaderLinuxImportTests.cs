using InfoPanel.Core.Tests.Sensors.Linux;
using InfoPanel.Editing;
using InfoPanel.Import;
using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Linux;
using InfoPanel.Spec;
using Xunit;
using static InfoPanel.Core.Tests.Import.ImportKit;

namespace InfoPanel.Core.Tests.Import;

/// <summary>
/// The 1.x import of a Linux build's <c>Hwmon</c> items on Linux (the platform seam set): an item whose id the Linux
/// source lists binds to it (<c>import.sensor.translated</c>); else one whose chip and channel (and stored label) only
/// one listed sensor has binds to that one (<c>import.sensor.matched</c>); else it imports unbound, as off Linux, with
/// the tolerant import's diagnostic saying why. The files are made here; the Linux source reads a sysfs tree made by
/// <see cref="SysfsKit"/>.
/// </summary>
public sealed class V1ReaderLinuxImportTests : IDisposable
{
    private const string Fan1 = "hwmon/v1/it8696/platform+it87.2624/fan1";

    private readonly ImportKit _kit = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
    private readonly List<LinuxSensorSource> _sources = [];

    public void Dispose()
    {
        foreach (var source in _sources)
            source.Dispose();
        _kit.Dispose();
    }

    /// <summary>The Linux source over <paramref name="sysfs"/> (the VFD machine when null), discovered and polled once.</summary>
    private LinuxSensorSource Linux(SysfsKit? sysfs = null)
    {
        var source = LinuxSensorSource.Create(sysfs ?? SysfsKit.VfdMachine(), _time);
        _sources.Add(source);
        source.Open();
        return source;
    }

    /// <summary>A source with the Linux source's id that lists <paramref name="ids"/> (ones it doesn't read yet).</summary>
    private sealed class ListingSource(params string[] ids) : ISensorSource
    {
        public string Id => LinuxSensorSource.SourceId;

        public bool Handles(SensorKey key) => key.Id.StartsWith(LinuxSensorSource.KeyPrefix, StringComparison.Ordinal);

        public IReadOnlyList<SensorInfo> GetSensors() =>
            ids.Select(id => new SensorInfo(LinuxSensorSource.KeyOf(id), Id, "System", id, "%")).ToList();

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            reading = default;
            return false;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// A Linux source that lists <paramref name="ids"/> only once <see cref="Finish"/> ends its first discovery. It counts
    /// how often its state was read while starting (<see cref="StartingReads"/>) and signals the first
    /// (<see cref="Checked"/>): the importer asking whether the discovery has ended.
    /// </summary>
    private sealed class DelayedSource(params string[] ids) : ISensorSource, ISensorSourceState
    {
        private readonly TaskCompletionSource _checked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _done;
        private int _startingReads;

        public string Id => LinuxSensorSource.SourceId;

        public Task Checked => _checked.Task;

        public int StartingReads => Volatile.Read(ref _startingReads);

        public SourceState State
        {
            get
            {
                if (_done)
                    return SourceState.Running;
                Interlocked.Increment(ref _startingReads);
                _checked.TrySetResult();
                return SourceState.Starting;
            }
        }

        public void Finish() => _done = true;

        public bool Handles(SensorKey key) => key.Id.StartsWith(LinuxSensorSource.KeyPrefix, StringComparison.Ordinal);

        public IReadOnlyList<SensorInfo> GetSensors() => !_done ? [] :
            ids.Select(id => new SensorInfo(LinuxSensorSource.KeyOf(id), Id, "System", id, "%")).ToList();

        public bool TryRead(SensorKey key, out SensorReading reading)
        {
            reading = default;
            return false;
        }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private V1Profile Read(SensorHub sensors, bool onLinux, params string[] items) =>
        ReadWith(sensors, onLinux, null, default, items);

    private V1Profile ReadWith(SensorHub sensors, bool onLinux, TimeSpan? discoveryWait, CancellationToken cancellation, params string[] items) =>
        V1Reader.ReadArchive(new MemoryStream(V1Archive(Items(items))),
            new V1ReadContext(_kit.Workspace, sensors, null, Fonts.Contains)
            {
                OnLinux = onLinux,
                DiscoveryWait = discoveryWait ?? TimeSpan.FromSeconds(10),
                Cancellation = cancellation,
            });

    private V1Profile ReadOnLinux(params string[] items) => Read(new SensorHub([Linux()]), onLinux: true, items);

    /// <summary>
    /// A sensor item as a Linux build of 1.x writes it: the <c>Hwmon</c> sensor type, HWiNFO ids beside it, the id in
    /// <c>LibreSensorId</c>, and the chip and channel label when given.
    /// </summary>
    private static string Hwmon(string name, string id, string? chip = null, string? label = null, string type = "SensorDisplayItem")
    {
        var identity = chip is null && label is null ? ""
            : "<HardwareSensorIdentity>" + (chip is null ? "" : $"<ChipName>{chip}</ChipName>") + (label is null ? "" : $"<ChannelLabel>{label}</ChannelLabel>") + "</HardwareSensorIdentity>";
        return $"""<DisplayItem xsi:type="{type}"><Name>{name}</Name><SensorName>{label ?? name}</SensorName><SensorType>Hwmon</SensorType><HwInfoRemoteIndex>-1</HwInfoRemoteIndex><Id>4026532608</Id><Instance>0</Instance><EntryId>117440537</EntryId>{identity}<LibreSensorId>{id}</LibreSensorId><PluginSensorId /></DisplayItem>""";
    }

    private static void AssertBound(ItemNode node, string id)
    {
        Assert.Equal(("Plugin", LinuxSensorSource.KeyPrefix + id), ((string)node.Props["sensorType"]!, (string)node.Props["pluginSensorId"]!));
        Assert.Equal(LinuxSensorSource.KeyOf(id).ToString(), node.Meta.Sensor!.Key);
    }

    private static void AssertUnbound(ItemNode node)
    {
        Assert.Equal(("HwInfo", "0", "0", "0"), ((string)node.Props["sensorType"]!, node.Props["id"]!.ToJsonString(), node.Props["instance"]!.ToJsonString(), node.Props["entryId"]!.ToJsonString()));
        Assert.Equal(SensorStatus.Missing, node.Meta.Sensor!.Status);
    }

    /// <summary>The tolerant import's diagnostic on the sensor type of the item at <paramref name="path"/>.</summary>
    private static ImportDiagnostic UnknownType(V1Profile envelope, string path) =>
        Assert.Single(With(envelope, "import.element"), d => d.Path == path && d.Message.Contains("'Hwmon'"));

    [Fact]
    public void An_id_the_Linux_source_lists_binds_to_it_exactly_in_a_group_and_on_every_kind()
    {
        var envelope = ReadOnLinux(
            Hwmon("Fan", Fan1, chip: "it8696", label: "fan1"),
            Hwmon("Current", "hwmon/v1/wireview/platform+wireview_hwmon/curr1"),
            Group("Box", Hwmon("Gauge", Fan1, type: "GaugeDisplayItem")),
            Hwmon("Graph", Fan1, type: "GraphDisplayItem"));

        AssertBound(envelope.Items[0], Fan1);
        AssertBound(envelope.Items[1], "hwmon/v1/wireview/platform+wireview_hwmon/curr1");
        AssertBound(envelope.Items[2].Children![0], Fan1);
        AssertBound(envelope.Items[3], Fan1);
        var translated = With(envelope, "import.sensor.translated");
        Assert.Equal(["/items/0", "/items/1", "/items/2/children/0", "/items/3"], translated.Select(d => d.Path));
        Assert.All(translated, d => Assert.Equal(("info", "DisplayItems.xml"), (d.Severity, d.File)));
        Assert.Contains("'Fan'", translated[0].Message);
        Assert.Contains(Fan1, translated[0].Message);
        Assert.Contains(LinuxSensorSource.KeyOf(Fan1).ToString(), translated[0].Message);
        // Bound: neither the unbound diagnostic nor a matched one, and no "no reading now" line for a sensor not yet in use.
        Assert.DoesNotContain(With(envelope, "import.element"), d => d.Message.Contains("'Hwmon'"));
        Assert.Empty(With(envelope, "import.sensor.matched"));
        Assert.Empty(With(envelope, "import.sensor"));
    }

    [Fact]
    public void A_system_id_the_Linux_source_lists_binds_to_it_exactly()
    {
        var envelope = Read(new SensorHub([new ListingSource("system/cpu/total")]), onLinux: true,
            Hwmon("CPU", "system/cpu/total"), Hwmon("Memory", "system/memory/used_percent"));

        AssertBound(envelope.Items[0], "system/cpu/total");
        Assert.Equal(["/items/0"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        // An id the source doesn't list stays unbound, and a system id has no chip to match by.
        AssertUnbound(envelope.Items[1]);
        Assert.Contains("system/memory/used_percent", UnknownType(envelope, "/items/1").Message);
        Assert.Empty(With(envelope, "import.sensor.matched"));
    }

    [Fact]
    public void An_id_from_another_machine_binds_to_the_one_sensor_with_its_chip_and_channel()
    {
        // Another board's address for the same chip: the one it8696 fan1 here is the sensor it meant.
        var envelope = ReadOnLinux(
            Hwmon("Fan", "hwmon/v1/it8696/platform+it87.656/fan1", chip: "it8696", label: "fan1"),
            Hwmon("No label", "hwmon/v1/it8696/platform+it87.656/fan2"));

        AssertBound(envelope.Items[0], Fan1);
        AssertBound(envelope.Items[1], "hwmon/v1/it8696/platform+it87.2624/fan2");
        var matched = With(envelope, "import.sensor.matched");
        Assert.Equal(["/items/0", "/items/1"], matched.Select(d => d.Path));
        Assert.All(matched, d => Assert.Equal("info", d.Severity));
        Assert.Contains("hwmon/v1/it8696/platform+it87.656/fan1", matched[0].Message);
        Assert.Contains(LinuxSensorSource.KeyOf(Fan1).ToString(), matched[0].Message);
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.DoesNotContain(With(envelope, "import.element"), d => d.Message.Contains("'Hwmon'"));
    }

    [Fact]
    public void A_stored_label_must_match_and_two_sensors_with_the_chip_and_channel_match_neither()
    {
        var sysfs = SysfsKit.VfdMachine();
        sysfs.Hwmon(9, sysfs.BusDevice("devices/platform/it87.552", "platform"), "it8696", ("temp1_input", "40000"));
        var envelope = Read(new SensorHub([Linux(sysfs)]), onLinux: true,
            Hwmon("Labelled", "hwmon/v1/it8696/platform+it87.656/fan1", chip: "it8696", label: "CPU fan"),
            Hwmon("Two", "hwmon/v1/it8696/platform+it87.656/temp1", chip: "it8696", label: "temp1"));

        AssertUnbound(envelope.Items[0]);
        AssertUnbound(envelope.Items[1]);
        Assert.Contains("'CPU fan'", UnknownType(envelope, "/items/0").Message);
        Assert.Contains("more than one", UnknownType(envelope, "/items/1").Message);
        Assert.Empty(With(envelope, "import.sensor.matched"));
        Assert.Empty(With(envelope, "import.sensor.translated"));
    }

    [Fact]
    public void An_id_no_listed_sensor_has_nor_its_chip_and_channel_stays_unbound_and_says_why()
    {
        var envelope = ReadOnLinux(
            Hwmon("Fan 4", "hwmon/v1/it8696/platform+it87.2624/fan4", chip: "it8696", label: "fan4"),
            Hwmon("Other", "chip0/temp1"));

        AssertUnbound(envelope.Items[0]);
        AssertUnbound(envelope.Items[1]);
        var none = UnknownType(envelope, "/items/0");
        Assert.Equal("warning", none.Severity);
        Assert.Contains("unbound", none.Message);
        Assert.Contains("hwmon/v1/it8696/platform+it87.2624/fan4", none.Message);
        Assert.Contains("'it8696'", none.Message);
        Assert.Contains("'fan4'", none.Message);
        // An id that isn't a Linux sensor id is named as such.
        Assert.Contains("isn't a Linux sensor id", UnknownType(envelope, "/items/1").Message);
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.Empty(With(envelope, "import.sensor.matched"));
    }

    [Fact]
    public void Off_Linux_a_listed_id_stays_unbound_as_the_tolerant_import_leaves_it()
    {
        var envelope = Read(new SensorHub([Linux()]), onLinux: false, Hwmon("Fan", Fan1, chip: "it8696", label: "fan1"));

        AssertUnbound(Assert.Single(envelope.Items));
        var unknown = UnknownType(envelope, "/items/0");
        Assert.EndsWith("is imported unbound, to be bound to a sensor on this computer.", unknown.Message);
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.Empty(With(envelope, "import.sensor.matched"));
    }

    [Fact]
    public void A_plugin_binding_beside_them_stays_as_stored()
    {
        var plugin = """<DisplayItem xsi:type="SensorDisplayItem"><Name>FPS</Name><SensorName>fps</SensorName><SensorType>Plugin</SensorType><HwInfoRemoteIndex>-1</HwInfoRemoteIndex><PluginSensorId>/fps-plugin/game/fps</PluginSensorId></DisplayItem>""";
        var envelope = ReadOnLinux(plugin, Hwmon("Fan", Fan1));

        var fps = envelope.Items[0];
        Assert.Equal(("Plugin", "/fps-plugin/game/fps", "plugin:/fps-plugin/game/fps"),
            ((string)fps.Props["sensorType"]!, (string)fps.Props["pluginSensorId"]!, fps.Meta.Sensor!.Key));
        // No sensor reads it here: reported as any binding the engine lacks, and left alone by the Linux rule.
        Assert.Equal(["/items/0"], With(envelope, "import.sensor").Select(d => d.Path));
        Assert.Equal(["/items/1"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        AssertBound(envelope.Items[1], Fan1);
    }

    [Fact]
    public void An_import_while_discovery_still_runs_waits_for_it_and_binds()
    {
        var source = new DelayedSource("system/cpu/total");
        Assert.Empty(source.GetSensors());
        // Discovery ends only once the importer has asked whether it has: the import must wait for it to bind.
        var finish = Task.Run(async () =>
        {
            await source.Checked;
            source.Finish();
        });

        var envelope = ReadWith(new SensorHub([source]), onLinux: true, null, default, Hwmon("CPU", "system/cpu/total"));

        Assert.True(finish.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(source.StartingReads >= 1);
        AssertBound(Assert.Single(envelope.Items), "system/cpu/total");
        Assert.Equal(["/items/0"], With(envelope, "import.sensor.translated").Select(d => d.Path));
        Assert.DoesNotContain(With(envelope, "import.element"), d => d.Message.Contains("'Hwmon'"));
    }

    [Fact]
    public void An_import_whose_discovery_outlasts_the_wait_leaves_the_items_unbound_and_says_the_sensors_are_still_being_read()
    {
        var source = new DelayedSource("system/cpu/total");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var envelope = ReadWith(new SensorHub([source]), onLinux: true, TimeSpan.FromMilliseconds(150), default,
            Hwmon("CPU", "system/cpu/total"), Hwmon("Other", "chip0/temp1"));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");
        AssertUnbound(envelope.Items[0]);
        var still = UnknownType(envelope, "/items/0");
        Assert.Equal("warning", still.Severity);
        Assert.Contains("still being read", still.Message);
        Assert.Contains("import again in a moment", still.Message);
        Assert.DoesNotContain("lists no sensor", still.Message);
        // An id that can never be a Linux sensor's is named as such, not blamed on the wait.
        Assert.Contains("isn't a Linux sensor id", UnknownType(envelope, "/items/1").Message);
        Assert.Empty(With(envelope, "import.sensor.translated"));
        Assert.Empty(With(envelope, "import.sensor.matched"));
    }

    [Fact]
    public void Cancelling_the_import_ends_the_wait_for_discovery()
    {
        var source = new DelayedSource("system/cpu/total");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ReadWith(new SensorHub([source]), onLinux: true, TimeSpan.FromSeconds(30), cancel.Token, Hwmon("CPU", "system/cpu/total")));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"waited {watch.Elapsed}");
    }

    [Fact]
    public void A_profile_with_no_Hwmon_item_to_bind_does_not_wait_for_discovery()
    {
        var source = new DelayedSource("system/cpu/total");
        var plugin = """<DisplayItem xsi:type="SensorDisplayItem"><Name>FPS</Name><SensorName>fps</SensorName><SensorType>Plugin</SensorType><HwInfoRemoteIndex>-1</HwInfoRemoteIndex><PluginSensorId>/fps-plugin/game/fps</PluginSensorId></DisplayItem>""";

        // Neither a plugin item nor a Hwmon item whose id is no Linux sensor's needs the Linux listing.
        var envelope = ReadWith(new SensorHub([source]), onLinux: true, TimeSpan.FromSeconds(1), default, plugin, Hwmon("Other", "chip0/temp1"));

        Assert.Equal(0, source.StartingReads);
        Assert.Contains("isn't a Linux sensor id", UnknownType(envelope, "/items/1").Message);
    }
}
