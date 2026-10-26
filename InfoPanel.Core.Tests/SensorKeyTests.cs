using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests;

public class SensorKeyTests
{
    public static TheoryData<string> CanonicalKeys =>
    [
        "hwinfo:-1:1:0:7",
        "hwinfo:3:4294967295:12:0",
        "libre:/amdcpu/0/load/0",
        "plugin:/infopanel.mock/gpu/gpu-temp",
        "plugin:/weather/main:with:colons",
    ];

    [Theory]
    [MemberData(nameof(CanonicalKeys))]
    public void ParseThenFormat_RoundTrips(string text)
    {
        Assert.True(SensorKey.TryParse(text, out var key));
        Assert.Equal(text, key.ToString());
    }

    [Fact]
    public void FormatThenParse_RoundTrips_AllTypes()
    {
        SensorKey[] keys =
        [
            SensorKey.HwInfo(-1, 1, 0, 7),
            SensorKey.Libre("/amdcpu/0/load/0"),
            SensorKey.Plugin("/infopanel.mock/gpu/gpu-temp"),
        ];

        foreach (var key in keys)
        {
            Assert.True(SensorKey.TryParse(key.ToString(), out var parsed));
            Assert.Equal(key, parsed);
        }
    }

    [Fact]
    public void Factories_SetTypeAndId()
    {
        Assert.Equal(new SensorKey(SensorType.HwInfo, "-1:1:0:7"), SensorKey.HwInfo(-1, 1, 0, 7));
        Assert.Equal("hwinfo:-1:1:0:7", SensorKey.HwInfo(-1, 1, 0, 7).ToString());
        Assert.Equal("libre:/amdcpu/0/load/0", SensorKey.Libre("/amdcpu/0/load/0").ToString());
        Assert.Equal("plugin:/infopanel.mock/gpu/gpu-temp", SensorKey.Plugin("/infopanel.mock/gpu/gpu-temp").ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(":")]
    [InlineData("hwinfo")]
    [InlineData("hwinfo:")]
    [InlineData("hwinfo:1:2:3")]
    [InlineData("hwinfo:1:2:3:4:5")]
    [InlineData("hwinfo:a:1:0:7")]
    [InlineData("hwinfo:-1:-1:0:7")]
    [InlineData("hwinfo:-1:1:0: 7")]
    [InlineData("hwinfo:-1:1::7")]
    [InlineData("hwinfo:-1:1:0:4294967296")]
    [InlineData("libre:")]
    [InlineData("plugin:")]
    [InlineData("libre")]
    [InlineData(":/amdcpu/0/load/0")]
    [InlineData("Plugin:/x")]
    [InlineData("other:/x")]
    public void TryParse_RejectsMalformed(string? text)
    {
        Assert.False(SensorKey.TryParse(text, out _));
    }

    [Fact]
    public void SensorKeys_Of_MapsBindings()
    {
        var hwInfo = new SensorDisplayItem { SensorType = SensorType.HwInfo, HwInfoRemoteIndex = 2, Id = 1, Instance = 0, EntryId = 7 };
        var libre = new BarDisplayItem { SensorType = SensorType.Libre, LibreSensorId = "/amdcpu/0/load/0" };
        var plugin = new GaugeDisplayItem { SensorType = SensorType.Plugin, PluginSensorId = "/p/c/e" };
        var table = new TableSensorDisplayItem { SensorType = SensorType.HwInfo, PluginSensorId = "/p/t/e" };

        Assert.Equal(SensorKey.HwInfo(2, 1, 0, 7), SensorKeys.Of(hwInfo));
        Assert.Equal(SensorKey.Libre("/amdcpu/0/load/0"), SensorKeys.Of(libre));
        Assert.Equal(SensorKey.Plugin("/p/c/e"), SensorKeys.Of(plugin));
        Assert.Equal(SensorKey.Plugin("/p/t/e"), SensorKeys.Of(table));
        Assert.Null(SensorKeys.Of(new BarDisplayItem { SensorType = SensorType.Plugin }));
        Assert.Null(SensorKeys.Of(new TextDisplayItem()));
    }

    [Fact]
    public void SensorKeys_ToBindingProps_RoundTripsThroughOf()
    {
        var props = SensorKeys.ToBindingProps(SensorKey.HwInfo(-1, 1, 0, 7), "CPU");

        Assert.Equal("CPU", props["SensorName"]);
        Assert.Equal(SensorType.HwInfo, props["SensorType"]);
        Assert.Equal(-1, props["HwInfoRemoteIndex"]);
        Assert.Equal(1u, props["Id"]);
        Assert.Equal(0u, props["Instance"]);
        Assert.Equal(7u, props["EntryId"]);

        Assert.Equal("/p/c/e", SensorKeys.ToBindingProps(SensorKey.Plugin("/p/c/e"), "x")["PluginSensorId"]);
        Assert.Equal("/l", SensorKeys.ToBindingProps(SensorKey.Libre("/l"), "x")["LibreSensorId"]);
    }
}
