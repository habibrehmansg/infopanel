using InfoPanel.DataApi;
using InfoPanel.Monitors;
using InfoPanel.Plugins;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Xunit;

namespace InfoPanel.Tests
{
    [Collection("PluginMonitor.SENSORHASH")]
    public class DataApiStreamTests
    {
        private const string BaseUrl = "http://127.0.0.1:3921";

        private static void AddReading(string pluginId, string containerId, IPluginData data)
        {
            string hashKey = $"/{pluginId}/{containerId}/{data.Id}";
            PluginMonitor.SENSORHASH[hashKey] = new PluginMonitor.PluginReading
            {
                Id = hashKey,
                Name = data.Name,
                ContainerId = containerId,
                ContainerName = containerId,
                PluginId = pluginId,
                PluginName = pluginId,
                Data = data,
            };
        }

        private static void Seed()
        {
            PluginMonitor.SENSORHASH.Clear();
            AddReading("InfoPanel.AudioMeter", "meter", new PluginSensor("left", "Left", 0.5f, "dB"));
            AddReading("InfoPanel.AudioMeter", "meter", new PluginSensor("right", "Right", 0.25f, "dB"));
            var table = new DataTable();
            table.Columns.Add("Name", typeof(PluginText));
            table.Rows.Add(new PluginText("n", "a"));
            AddReading("InfoPanel.Extras", "procs", new PluginTable("top", table, ""));
        }

        [Fact]
        public void Plugin_prefix_returns_only_matching_entries()
        {
            Seed();
            var builder = new SensorSnapshotBuilder(new PushStore());

            var result = builder.GetMatching("plugins.infopanel.audiometer.", BaseUrl);

            Assert.Equal(["plugins.InfoPanel.AudioMeter.meter.left", "plugins.InfoPanel.AudioMeter.meter.right"], result.Keys.Order());
            Assert.Equal(0.5, result["plugins.InfoPanel.AudioMeter.meter.left"].Value);
            Assert.Equal("plugin:/InfoPanel.AudioMeter/meter/left", result["plugins.InfoPanel.AudioMeter.meter.left"].Ref);
        }

        [Fact]
        public void Plugin_prefix_reads_current_values()
        {
            Seed();
            var builder = new SensorSnapshotBuilder(new PushStore());
            builder.GetMatching("plugins.", BaseUrl);

            AddReading("InfoPanel.AudioMeter", "meter", new PluginSensor("left", "Left", 0.75f, "dB"));

            Assert.Equal(0.75, builder.GetMatching("plugins.", BaseUrl)["plugins.InfoPanel.AudioMeter.meter.left"].Value);
        }

        [Fact]
        public void Other_prefixes_filter_the_shared_snapshot()
        {
            Seed();
            var store = new PushStore();
            store.Apply("ext", new Dictionary<string, DataApiValue>
            {
                ["ext.a"] = new() { Value = 1 },
                ["other.b"] = new() { Value = 2 },
            }, replace: false);
            var builder = new SensorSnapshotBuilder(store);

            var result = builder.GetMatching("EXT.", BaseUrl);

            Assert.Equal(["ext.a"], result.Keys);
        }

        [Fact]
        public void Stream_json_is_serialized_once_per_snapshot()
        {
            Seed();
            var builder = new SensorSnapshotBuilder(new PushStore());

            string first = builder.GetStreamJson(BaseUrl);
            Assert.Same(first, builder.GetStreamJson(BaseUrl));
            Assert.Contains("\"plugins.InfoPanel.AudioMeter.meter.left\"", first);
            Assert.DoesNotContain("\"ref\"", first);

            builder.Invalidate();
            Assert.NotSame(first, builder.GetStreamJson(BaseUrl));
        }
    }
}
