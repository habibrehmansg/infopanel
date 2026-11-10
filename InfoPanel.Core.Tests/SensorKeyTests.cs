using InfoPanel.Enums;
using InfoPanel.Models;
using InfoPanel.Sensors;
using InfoPanel.Sensors.Clock;
using InfoPanel.Sensors.Drives;
using InfoPanel.Sensors.HwInfo.Gadget;
using InfoPanel.Sensors.Linux;
using InfoPanel.Sensors.MacOS;
using InfoPanel.Sensors.Network;
using InfoPanel.Sensors.SystemInfo;
using InfoPanel.Sensors.Volume;
using InfoPanel.Sensors.Weather;
using Xunit;

namespace InfoPanel.Core.Tests;

public class SensorKeyTests
{
    public static TheoryData<string> CanonicalKeys =>
    [
        "hwinfo:-1:1:0:7",
        "hwinfo:3:4294967295:12:0",
        "libre:/amdcpu/0/load/0",
        "infopanel:/mock/gpu/gpu-temp",
        "plugin:/weather/main:with:colons",
        // User plugins keep their form, the plain id "infopanel" (no dot) and a look-alike included.
        "plugin:/infopanel/x",
        "plugin:/infopanelx.tools/me/score",
        "plugin:/acme.infopanel.tools/me/score",
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
        Assert.Equal("infopanel:/mock/gpu/gpu-temp", SensorKey.Plugin("/infopanel.mock/gpu/gpu-temp").ToString());
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

    /// <summary>
    /// Every built-in source's keys, as the source makes them, print as <c>infopanel:/{source}/{path}</c> (the source id
    /// without <c>infopanel.</c>) and parse back to the same key: one key a source, the odd paths included, and every
    /// entry of the sources whose catalogue is fixed.
    /// </summary>
    [Fact]
    public void EveryBuiltInSourcesKeys_PrintInTheEnginesOwnForm_AndParseBack()
    {
        (SensorKey Key, string Text)[] keys =
        [
            (MacSensorSource.KeyOf("cpu/load"), "infopanel:/macos/cpu/load"),
            (MockSensorCatalog.KeyOf("gpu.temp"), "infopanel:/mock/gpu/gpu-temp"),
            (MockSensorCatalog.TopProcessesKey, "infopanel:/mock/system/top-processes"),
            (ClockSensorSource.KeyOf("12-hour"), "infopanel:/clock/12-hour"),
            (DriveSource.KeyOf("c", "free_space"), "infopanel:/drives/c/free_space"),
            (DriveSource.KeyOf("", "name"), "infopanel:/drives//name"),
            (NetworkSource.KeyOf("en0", "ipv4-0"), "infopanel:/network/en0/ipv4-0"),
            (NetworkSource.PublicKeyOf("ipv6"), "infopanel:/network/public/ipv6"),
            (VolumeSource.KeyOf("default", "volume"), "infopanel:/volume/default/volume"),
            (SystemInfoSource.KeyOf("uptime", "formatted"), "infopanel:/system-info/uptime/formatted"),
            (WeatherSource.KeyOf("feels_like"), "infopanel:/weather/feels_like"),
            (EngineSource.KeyOf("cpu"), "infopanel:/engine/cpu"),
            (HwInfoGadgetSource.KeyOf("CPU/Temp"), "infopanel:/hwinfo-gadget/CPU/Temp"),
            (HwInfoGadgetSource.KeyOf("Fan%2F1/Speed: RPM~2"), "infopanel:/hwinfo-gadget/Fan%2F1/Speed: RPM~2"),
            (LinuxSensorSource.KeyOf("hwmon/v1/it8696/platform+it87.2624/fan1"), "infopanel:/linux/hwmon/v1/it8696/platform+it87.2624/fan1"),
            (LinuxSensorSource.KeyOf("thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp"), "infopanel:/linux/thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp"),
        ];
        var all = keys.Select(k => (k.Key, k.Text))
            .Concat(MockSensorCatalog.Sensors.Select(s => (s.Key, "infopanel:/mock/" + s.GroupSlug + "/" + s.Id.Replace('.', '-'))))
            .Concat(ClockSensorSource.Entries.Select(e => (ClockSensorSource.KeyOf(e.Id), "infopanel:/clock/" + e.Id)))
            .Concat(WeatherSource.Entries.Select(e => (WeatherSource.KeyOf(e.Id), "infopanel:/weather/" + e.Id)))
            .Concat(EngineSource.Entries.Select(e => (EngineSource.KeyOf(e.Id), "infopanel:/engine/" + e.Id)))
            .Concat(SystemInfoSource.Entries.Select(e => (SystemInfoSource.KeyOf(e.Id[..e.Id.IndexOf('/')], e.Id[(e.Id.IndexOf('/') + 1)..]), "infopanel:/system-info/" + e.Id)));

        foreach (var (key, text) in all)
        {
            Assert.Equal(text, key.ToString());
            Assert.True(SensorKey.TryParse(text, out var parsed), text);
            Assert.Equal(key, parsed);
            Assert.Equal(text, parsed.ToString());
        }
    }

    /// <summary>
    /// The engine's own form is the only way into its sources' keys: the plugin form of a built-in key (any case) is
    /// refused, and so is every <c>infopanel:</c> text that isn't canonical, while its well-formed neighbour parses.
    /// </summary>
    [Fact]
    public void TryParse_RefusesThePluginFormOfABuiltInKey_AndEveryNonCanonicalBuiltInKey()
    {
        Assert.True(SensorKey.TryParse("infopanel:/x/y", out var key));
        Assert.Equal(SensorKey.Plugin("/infopanel.x/y"), key);

        string[] refused =
        [
            "plugin:/infopanel.x/y", "plugin:/InfoPanel.x/y", "plugin:/INFOPANEL.mock/gpu/gpu-temp", "plugin:/infopanel.mock/gpu/gpu-temp",
            "Infopanel:/x/y", "INFOPANEL:/x/y", "infopanel:", "infopanel:/", "infopanel://x/y", "infopanel:/x/", "infopanel:/x", "infopanel:x/y",
            "infopanel:/MacOS/y", "infopanel:/x-/y", "infopanel:/-x/y", "infopanel:/x--y/z", "infopanel:/x.y/z", "infopanel:/x_y/z",
        ];
        foreach (var text in refused)
            Assert.False(SensorKey.TryParse(text, out _), text);
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
