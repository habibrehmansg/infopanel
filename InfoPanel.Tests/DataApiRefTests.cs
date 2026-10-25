using InfoPanel.DataApi;
using InfoPanel.Monitors;
using LibreHardwareMonitor.Hardware;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace InfoPanel.Tests
{
    [Collection("LibreMonitor.SENSORHASH")]
    public class DataApiRefTests
    {
        [Fact]
        public void Plugin_ref_is_the_plugin_sensor_id()
        {
            Assert.Equal("plugin:/InfoPanel.RTSS/rtss/1% low fps", SensorSnapshotBuilder.PluginRef("/InfoPanel.RTSS/rtss/1% low fps"));
        }

        [Fact]
        public void HwInfo_ref_is_remote_parent_instance_and_entry()
        {
            var reading = new HWHash.HWINFO_HASH { RemoteIndex = -1, ParentID = 4026531840, ParentInstance = 0, SensorID = 16777216 };

            Assert.Equal("hwinfo:-1/4026531840/0/16777216", SensorSnapshotBuilder.HwInfoRef(reading));
        }

        [Fact]
        public void HwInfo_tree_groups_readings_by_device_with_their_keys_and_refs()
        {
            var cpu = new HWHash.HWINFO_HASH { RemoteIndex = -1, ParentID = 100, ParentInstance = 0, ParentNameDefault = "CPU [#0]: AMD", SensorID = 1, NameDefault = "Core 0 Power", IndexOrder = 0 };
            var smart1 = new HWHash.HWINFO_HASH { RemoteIndex = -1, ParentID = 200, ParentInstance = 0, ParentNameDefault = "S.M.A.R.T.: A", SensorID = 7, NameDefault = "Drive Temperature", IndexOrder = 1 };
            var smart2 = new HWHash.HWINFO_HASH { RemoteIndex = -1, ParentID = 200, ParentInstance = 1, ParentNameDefault = "S.M.A.R.T.: B", SensorID = 7, NameDefault = "Drive Temperature", IndexOrder = 2 };
            var cpu2 = cpu with { SensorID = 2, NameDefault = "Core 1 Power", IndexOrder = 3 };

            var tree = SensorSnapshotBuilder.HwInfoTree(
            [
                ("hwinfo.core_0_power", "Core 0 Power", cpu),
                ("hwinfo.drive_temperature", "Drive Temperature", smart1),
                ("hwinfo.drive_temperature__s_m_a_r_t___b", "Drive Temperature", smart2),
                ("hwinfo.core_1_power", "Core 1 Power", cpu2),
            ]);

            // Same sensor id, different instance: two devices. Devices keep HWiNFO's order.
            Assert.Equal(["CPU [#0]: AMD", "S.M.A.R.T.: A", "S.M.A.R.T.: B"], tree.Select(d => d.Name));
            Assert.Equal("-1/100/0", tree[0].Id);
            Assert.Equal(1u, tree[2].Instance);
            Assert.Equal(["hwinfo.core_0_power", "hwinfo.core_1_power"], tree[0].Entries.Select(e => e.Key));
            Assert.Equal("hwinfo:-1/200/1/7", tree[2].Entries[0].Ref);
            Assert.Equal("sensor", tree[0].Entries[0].Type);
        }

        [Fact]
        public void HwInfo_strings_are_read_as_utf8_up_to_the_first_nul()
        {
            // "°C" as HWiNFO's shared memory holds it (seen on a live capture), then the field's padding
            var raw = new byte[16];
            new byte[] { 0xC2, 0xB0, 0x43 }.CopyTo(raw, 0);

            Assert.Equal("°C", HWHash.Utf8String(raw));
            Assert.Equal("", HWHash.Utf8String(new byte[4]));
            Assert.Equal("", HWHash.Utf8String(null));
        }

        [Fact]
        public void HwInfo_tree_is_empty_without_readings()
        {
            Assert.Empty(SensorSnapshotBuilder.HwInfoTree([]));
        }

        [Fact]
        public void Libre_ref_is_the_identifier_InfoPanel_reads_for_it()
        {
            LibreMonitor.SENSORHASH.Clear();
            var gpu = FakeHardware.Create("/gpu-nvidia/0", "NVIDIA GeForce RTX 4090");
            var pass = new Dictionary<string, ISensor>();
            LibreMonitor.Store(FakeSensor.Create("/gpu-nvidia/0/load/3", "GPU Memory", gpu), pass);
            LibreMonitor.Store(FakeSensor.Create("/gpu-nvidia/0/load/3", "GPU Bus", gpu), pass);
            LibreMonitor.Store(FakeSensor.Create("/gpu-nvidia/0/load/0", "GPU Core", gpu), pass);

            var refs = SensorSnapshotBuilder.LibreKeys()
                .ToDictionary(l => l.Sensor.Name, l => SensorSnapshotBuilder.LibreRef(l.HashKey));

            // A profile bound to /gpu-nvidia/0/load/3 reads the later sensor; the
            // earlier one keeps a ref no profile can hold.
            Assert.Equal("libre:/gpu-nvidia/0/load/3", refs["GPU Bus"]);
            Assert.Equal("libre:/gpu-nvidia/0/load/3/GPU Memory", refs["GPU Memory"]);
            Assert.Equal("libre:/gpu-nvidia/0/load/0", refs["GPU Core"]);
            LibreMonitor.SENSORHASH.Clear();
        }

        [Fact]
        public void Sensors_endpoint_sends_ref_and_streams_leave_it_out()
        {
            var value = new DataApiValue { Value = 42, Unit = "%", Name = "GPU Core", Source = "infopanel", Ref = "libre:/gpu-nvidia/0/load/0" };

            var full = JsonSerializer.Serialize(value, DataApiTask.JsonOptions);
            var stream = JsonSerializer.Serialize(new { sensors = new Dictionary<string, DataApiValue> { ["k"] = value } }, DataApiTask.StreamJsonOptions);

            Assert.Contains("\"ref\":\"libre:/gpu-nvidia/0/load/0\"", full);
            Assert.DoesNotContain("ref", stream);
            Assert.Contains("\"value\":42", stream);
            Assert.Contains("\"source\":\"infopanel\"", stream);
        }

        [Fact]
        public void Values_without_a_ref_omit_it()
        {
            var json = JsonSerializer.Serialize(new DataApiValue { Value = 1, Source = "system" }, DataApiTask.JsonOptions);

            Assert.DoesNotContain("ref", json);
        }
    }
}
