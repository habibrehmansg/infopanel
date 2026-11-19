using InfoPanel.Core.Tests.SensorHelper;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Libre;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Libre;

/// <summary>
/// Each top-level hardware's name as a text sensor, <c>libre:{hardware id}/name</c>: listed after the helper's sensors
/// in its hardware order, in the hardware's group, read live each answered round, and gone, withdrawn or stale with its
/// hardware's sensors. Sub-hardware has none; the numbers keep their own keys and values.
/// </summary>
public sealed class LibreNameTests
{
    private static LibreRig TreeRig(FakeTree tree)
    {
        var rig = new LibreRig(time => new TreeLauncher(tree, time));
        return rig;
    }

    private static string NameKey(string hardware) => "libre:" + hardware + "/name";

    private static string? Text(LibreRig rig, string hardware) => rig.Peek(hardware + "/name").Reading.ValueText;

    [Fact]
    public void EachTopLevelHardware_HasOneTextSensor_NamedName_InItsGroup_AfterTheHelpersSensors()
    {
        using var rig = TreeRig(NamesTree.Pc());
        rig.Open();

        // The helper's sensors first, in its order, unchanged; then one name per top-level hardware, in its order.
        Assert.Equal([.. NamesTree.SensorKeys, .. NamesTree.TopLevel.Select(h => NameKey(h.Id))], rig.Keys);

        var sensors = rig.Source.GetSensors().ToDictionary(s => s.Key.ToString());
        foreach (var (id, name) in NamesTree.TopLevel)
        {
            var sensor = sensors[NameKey(id)];
            Assert.Equal((name, "Name", "", "libre"), (sensor.Group, sensor.Name, sensor.Unit, sensor.SourceId));
            Assert.Equal(SensorKind.Text, rig.Peek(id + "/name").Type);
        }

        // The board's Super I/O chip is part of the board: no name of its own.
        Assert.DoesNotContain(NameKey("/lpc/nct6798d/0"), rig.Keys);
        // The memory has no sensors, and still its name.
        Assert.Contains(NameKey("/ram"), rig.Keys);

        // In the engine's catalogue, the names say so by their type, for a mapping by sensor type to skip them.
        var gpu = rig.Source.Catalogue.Single(s => s.Identifier == "/gpu-nvidia/1/name");
        Assert.Equal(("Name", "Name", -1, "", "/gpu-nvidia/1", "NVIDIA GeForce RTX 4070", "GpuNvidia", "NVIDIA GeForce RTX 4070"),
            (gpu.Name, gpu.SensorType, gpu.Index, gpu.Unit, gpu.Hardware, gpu.HardwareName, gpu.HardwareType, gpu.Group));
    }

    [Fact]
    public void EachName_ReadsItsHardwaresName_Live_AndEveryNumber_ReadsItsOwnValue()
    {
        var tree = NamesTree.Pc();
        using var rig = TreeRig(tree);
        rig.Open();
        rig.Poll(LibreSource.Interval);

        foreach (var (id, name) in NamesTree.TopLevel)
        {
            Assert.Equal(SensorStatus.Live, rig.Status(id + "/name"));
            Assert.Equal(name, Text(rig, id));
        }

        // The numbers keep their places: a 1.x key reads its own number with the names listed after it.
        Assert.Equal(9.75, rig.Now("/amdcpu/0/load/0"));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/load/0"));
        foreach (var sensor in tree.Sensors.Where(s => s.Value is { } v && float.IsFinite(v)))
        {
            Assert.Equal(SensorStatus.Live, rig.Status(sensor.Identifier));
            Assert.Equal((double)sensor.Value!.Value, rig.Now(sensor.Identifier));
        }
    }

    [Fact]
    public void TwoComponentsOfOneModel_GiveTwoKeys_WithTheSameGroupAndText()
    {
        using var rig = TreeRig(NamesTree.Pc());
        rig.Open();

        var sensors = rig.Source.GetSensors().ToDictionary(s => s.Key.ToString());
        var first = sensors[NameKey("/gpu-nvidia/0")];
        var second = sensors[NameKey("/gpu-nvidia/1")];
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(first.Group, second.Group);
        Assert.Equal("NVIDIA GeForce RTX 4070", Text(rig, "/gpu-nvidia/0"));
        Assert.Equal(Text(rig, "/gpu-nvidia/0"), Text(rig, "/gpu-nvidia/1"));
    }

    [Fact]
    public void GoneHardware_TakesItsName_StaleThenUnavailableThenUnlisted_AndNewHardwareBringsItsOwn()
    {
        var tree = NamesTree.Pc();
        using var rig = TreeRig(tree);
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("/gpu-nvidia/1/name"));

        tree.Hardware.RemoveAll(h => h.Identifier == "/gpu-nvidia/1");
        tree.Sensors.RemoveAll(s => s.Hardware == "/gpu-nvidia/1");
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        // As its sensor: stale while still listed, the rest live.
        Assert.Equal(SensorStatus.Stale, rig.Status("/gpu-nvidia/1/temperature/0"));
        Assert.Equal(SensorStatus.Stale, rig.Status("/gpu-nvidia/1/name"));
        Assert.Contains(NameKey("/gpu-nvidia/1"), rig.Keys);
        Assert.Equal(SensorStatus.Live, rig.Status("/gpu-nvidia/0/name"));

        rig.Poll(TimeSpan.FromSeconds(31));
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/gpu-nvidia/1/name"));
        Assert.DoesNotContain(NameKey("/gpu-nvidia/1"), rig.Keys);
        Assert.Equal(SensorStatus.Live, rig.Status("/gpu-nvidia/0/name"));

        // A new card: its name comes with the next catalogue.
        tree.Hardware.Add(new("/gpu-amd/0", "AMD Radeon RX 7900 XTX", "GpuAmd", 5, null));
        tree.Sensors.Add(new("/gpu-amd/0/load/0", "GPU Core", "Load", 0, "/gpu-amd/0", 30f, 0f, 100f));
        rig.Poll(LibreSource.Interval);
        rig.Poll(LibreSource.Interval);
        Assert.Contains(NameKey("/gpu-amd/0"), rig.Keys);
        Assert.Equal(SensorStatus.Live, rig.Status("/gpu-amd/0/name"));
        Assert.Equal("AMD Radeon RX 7900 XTX", Text(rig, "/gpu-amd/0"));
    }

    [Fact]
    public void StorageOff_WithdrawsTheDrivesNames_AtOnce_AndOff_EveryName()
    {
        using var rig = TreeRig(NamesTree.Pc());
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("/nvme/0/name"));

        rig.Settings = rig.Settings with { Storage = false };
        // At the same poll as the drive's sensors: unlisted at once, not stale for 30 s; before it, as they are.
        for (var polls = 1; rig.Keys.Contains("libre:/nvme/0/data/24"); polls++)
        {
            Assert.True(polls <= 3, "the drive's sensors stay listed with storage off");
            rig.Poll(LibreSource.Interval);
            Assert.Equal(rig.Keys.Contains("libre:/nvme/0/data/24"), rig.Keys.Contains(NameKey("/nvme/0")));
            Assert.Equal(rig.Status("/nvme/0/data/24"), rig.Status("/nvme/0/name"));
        }

        Assert.DoesNotContain(NameKey("/nvme/0"), rig.Keys);
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/nvme/0/name"));
        Assert.Equal(SensorStatus.Unavailable, rig.Status("/nvme/0/data/24"));
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/name"));

        rig.Settings = rig.Settings with { Enabled = false };
        rig.Poll(LibreSource.Interval);
        Assert.Empty(rig.Keys);
        Assert.All(NamesTree.TopLevel, h => Assert.Equal(SensorStatus.Unavailable, rig.Status(h.Id + "/name")));
    }

    [Fact]
    public void AFailedRound_LeavesTheNamesStale_AsTheReadings()
    {
        var tree = NamesTree.Pc();
        using var rig = TreeRig(tree);
        rig.Open();
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/name"));

        tree.Throw = true; // LibreHardwareMonitor failed the read: treeFailed
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Stale, rig.Status("/amdcpu/0/load/0"));
        Assert.All(NamesTree.TopLevel, h => Assert.Equal(SensorStatus.Stale, rig.Status(h.Id + "/name")));
        Assert.Equal("AMD Ryzen 7 8845HS", Text(rig, "/amdcpu/0"));

        tree.Throw = false;
        rig.Poll(LibreSource.Interval);
        Assert.Equal(SensorStatus.Live, rig.Status("/amdcpu/0/name"));
    }
}
