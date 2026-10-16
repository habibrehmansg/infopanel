using InfoPanel.DataApi;
using InfoPanel.Monitors;
using LibreHardwareMonitor.Hardware;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace InfoPanel.Tests
{
    public class HardwareNamesTests
    {
        [Fact]
        public void Numbers_identical_devices_in_identifier_order()
        {
            var names = SensorSnapshotBuilder.HardwareNames(
            [
                ("/nvme/10", "KINGSTON SFYRD2000G"),
                ("/nvme/2", "KINGSTON SFYRD2000G"),
                ("/nvme/3", "KINGSTON  SFYRD2000G"),
                ("/ssd/0", "WDC WDS100T2B0A"),
            ]);

            // /nvme/2 before /nvme/3 before /nvme/10 (numbers, not text); names are cleaned before comparing
            Assert.Equal("KINGSTON SFYRD2000G", names["/nvme/2"]);
            Assert.Equal("KINGSTON SFYRD2000G #2", names["/nvme/3"]);
            Assert.Equal("KINGSTON SFYRD2000G #3", names["/nvme/10"]);
            Assert.Equal("WDC WDS100T2B0A", names["/ssd/0"]);
        }

        [Fact]
        public void Gives_one_name_per_device_however_many_sensors_it_has()
        {
            var names = SensorSnapshotBuilder.HardwareNames(
            [
                ("/nvme/2", "Drive"),
                ("/nvme/2", "Drive"),
                ("/nvme/3", "Drive"),
            ]);

            Assert.Equal(2, names.Count);
            Assert.Equal("Drive #2", names["/nvme/3"]);
        }

        [Fact]
        public void Slugs_numbered_devices_to_double_underscore_keys()
        {
            Assert.Equal("kingston_sfyrd2000g__2", SensorSnapshotBuilder.Slug("KINGSTON SFYRD2000G #2"));
        }
    }

    [Collection("LibreMonitor.SENSORHASH")]
    public class LibreMonitorStoreTests
    {
        [Fact]
        public void Keeps_both_sensors_that_share_an_identifier()
        {
            LibreMonitor.SENSORHASH.Clear();
            var pass = new Dictionary<string, ISensor>();
            var memory = FakeSensor.Create("/gpu-nvidia/0/load/3", "GPU Memory");
            var bus = FakeSensor.Create("/gpu-nvidia/0/load/3", "GPU Bus");

            LibreMonitor.Store(memory, pass);
            LibreMonitor.Store(bus, pass);

            // the later keeps the identifier, as before; the earlier is no longer lost
            Assert.Same(bus, LibreMonitor.SENSORHASH["/gpu-nvidia/0/load/3"]);
            Assert.Same(memory, LibreMonitor.SENSORHASH["/gpu-nvidia/0/load/3/GPU Memory"]);
            LibreMonitor.SENSORHASH.Clear();
        }

        [Fact]
        public void Stores_a_sensor_seen_again_once()
        {
            LibreMonitor.SENSORHASH.Clear();
            var pass = new Dictionary<string, ISensor>();
            var core = FakeSensor.Create("/amdcpu/0/power/0", "Package");

            LibreMonitor.Store(core, pass);
            LibreMonitor.Store(core, pass);

            Assert.Single(LibreMonitor.SENSORHASH);
            LibreMonitor.SENSORHASH.Clear();
        }
    }

    /// <summary>
    /// An ISensor that answers Identifier and Name, enough for the key logic,
    /// plus Hardware, SensorType and Index when built with a hardware.
    /// </summary>
    public class FakeSensor : DispatchProxy
    {
        private string _id = "";
        private string _name = "";
        private IHardware? _hardware;

        public static ISensor Create(string identifier, string name, IHardware? hardware = null)
        {
            var sensor = Create<ISensor, FakeSensor>();
            var fake = (FakeSensor)(object)sensor;
            fake._id = identifier;
            fake._name = name;
            fake._hardware = hardware;
            return sensor;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Identifier" => new Identifier(_id.TrimStart('/').Split('/')),
            "get_Name" => _name,
            "get_Hardware" => _hardware,
            "get_SensorType" => SensorType.Load,
            "get_Index" => 0,
            _ => null,
        };
    }

    /// <summary>An IHardware that answers only Identifier and Name.</summary>
    public class FakeHardware : DispatchProxy
    {
        private string _id = "";
        private string _name = "";

        public static IHardware Create(string identifier, string name)
        {
            var hardware = Create<IHardware, FakeHardware>();
            var fake = (FakeHardware)(object)hardware;
            fake._id = identifier;
            fake._name = name;
            return hardware;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Identifier" => new Identifier(_id.TrimStart('/').Split('/')),
            "get_Name" => _name,
            _ => null,
        };
    }
}
