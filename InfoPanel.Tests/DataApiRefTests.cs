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
