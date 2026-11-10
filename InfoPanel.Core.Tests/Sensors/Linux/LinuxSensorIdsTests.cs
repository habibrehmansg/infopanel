using InfoPanel.Sensors.Linux;
using Xunit;

namespace InfoPanel.Core.Tests.Sensors.Linux;

/// <summary>
/// The Linux sensor ids' grammar: <c>hwmon/v1/{chip}/{anchor}[~{secondary}]/{channel}</c> and
/// <c>thermal/v1/{anchor}[~{secondary}]/temp</c>, each anchor <c>{kind}+{token}[+{token}…]</c>, each token its value
/// percent-encoded (UTF-8 bytes outside <c>A–Z a–z 0–9 . _ -</c> as upper-case <c>%HH</c>). Built and parsed both ways,
/// and parsed strictly: only the form a build gives.
/// </summary>
public sealed class LinuxSensorIdsTests
{
    [Theory]
    [InlineData("it87.2624", "it87.2624")]
    [InlineData("wireview_hwmon", "wireview_hwmon")]
    [InlineData("SMBus I801 adapter", "SMBus%20I801%20adapter")]
    [InlineData(@"\_TZ_.TZ00", "%5C_TZ_.TZ00")]
    [InlineData("pci+0000-00-1f.4", "pci%2B0000-00-1f.4")]
    [InlineData("Serial % + ~ / space", "Serial%20%25%20%2B%20%7E%20%2F%20space")]
    [InlineData("é", "%C3%A9")]
    [InlineData(".", "%2E")]
    [InlineData("..", "%2E%2E")]
    [InlineData("disk.", "disk%2E")]
    [InlineData("CON", "%43ON")]
    [InlineData("lpt1.x", "%6Cpt1.x")]
    public void A_token_is_its_value_percent_encoded_and_decodes_back(string value, string token)
    {
        Assert.Equal(token, LinuxSensorIds.Encode(value));
        Assert.True(LinuxSensorIds.TryDecode(token, out var decoded));
        Assert.Equal(value, decoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a%")]
    [InlineData("a%2")]
    [InlineData("a%2fb")]
    [InlineData("%FF")]
    [InlineData("%41")]
    [InlineData("a b")]
    [InlineData("a+b")]
    [InlineData("CON")]
    [InlineData("..")]
    [InlineData("disk.")]
    public void A_token_a_build_would_not_give_is_refused(string token)
    {
        Assert.False(LinuxSensorIds.TryDecode(token, out _));
    }

    [Fact]
    public void A_hwmon_id_builds_from_its_parts_and_parses_back_to_them()
    {
        var anchor = new LinuxAnchor("i2c", "SMBus I801 adapter", "002d");
        var secondary = new LinuxAnchor("adapter", "pci+0000-00-1f.4");

        var id = LinuxSensorIds.Hwmon("nct7802", anchor, secondary, "temp1");

        Assert.Equal("hwmon/v1/nct7802/i2c+SMBus%20I801%20adapter+002d~adapter+pci%2B0000-00-1f.4/temp1", id);
        Assert.True(LinuxSensorIds.TryParse(id, out var parsed));
        Assert.Equal(LinuxSensorFamily.Hwmon, parsed.Family);
        Assert.Equal("nct7802", parsed.Chip);
        Assert.Equal(anchor, parsed.Anchor);
        Assert.Equal(new[] { "SMBus I801 adapter", "002d" }, parsed.Anchor.Tokens);
        Assert.Equal(secondary, parsed.Secondary);
        Assert.Equal("temp1", parsed.Channel);
        Assert.Equal(id, parsed.ToString());
    }

    [Fact]
    public void A_thermal_id_builds_from_its_parts_and_parses_back_to_them()
    {
        var anchor = new LinuxAnchor("type", "acpitz");
        var secondary = new LinuxAnchor("acpi", @"\_TZ_.TZ00");

        var id = LinuxSensorIds.Thermal(anchor, secondary);

        Assert.Equal("thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp", id);
        Assert.True(LinuxSensorIds.TryParse(id, out var parsed));
        Assert.Equal(LinuxSensorFamily.Thermal, parsed.Family);
        Assert.Null(parsed.Chip);
        Assert.Equal(anchor, parsed.Anchor);
        Assert.Equal(secondary, parsed.Secondary);
        Assert.Equal("temp", parsed.Channel);
        Assert.Equal(id, parsed.ToString());
        Assert.Equal("thermal/v1/type+iwlwifi/temp", LinuxSensorIds.Thermal(new LinuxAnchor("type", "iwlwifi"), null));
    }

    [Theory]
    [InlineData("hwmon/v1/it8696/platform+it87.2624/fan1")]
    [InlineData("hwmon/v1/wireview/platform+wireview_hwmon/curr1")]
    [InlineData("hwmon/v1/wireview/platform+wireview_hwmon/curr5")]
    [InlineData("hwmon/v1/coretemp/platform+coretemp.0/temp1")]
    [InlineData("hwmon/v1/acpitz/type+acpitz~acpi+%5C_TZ_.TZ00/temp1")]
    [InlineData("hwmon/v1/iwlwifi/type+iwlwifi/temp1")]
    [InlineData("hwmon/v1/r8169/pci+0000-ab-00.0~role+mdio+00/temp1")]
    [InlineData("hwmon/v1/nvme/nvme-serial+253652BEC1AD~pci+0000-81-00.0/temp1")]
    [InlineData("hwmon/v1/nvme/nvme-serial+Serial%20%25%20%2B%20%7E%20%2F%20space~pci+0000-01-00.0/temp1")]
    [InlineData("hwmon/v1/nvme/pci+0000-01-00.0/temp1")]
    [InlineData("hwmon/v1/drivetemp/block-wwid+naa.5000c500a1b2c3d4/temp1")]
    [InlineData("hwmon/v1/drivetemp/block-serial+WD-123/temp1")]
    [InlineData("hwmon/v1/corsaircpro/usb-serial+1b1c+0c10+FX0003/fan1")]
    [InlineData("hwmon/v1/corsaircpro/usb-port+1b1c+0c10+pci%2B0000-00-14.0+3.2/fan1")]
    [InlineData("hwmon/v1/nct7802/i2c+SMBus%20I801%20adapter+002d~adapter+pci%2B0000-00-1f.4/temp1")]
    [InlineData("hwmon/v1/acpi_fan/name+acpi_fan/fan1")]
    [InlineData("hwmon/v1/x/name+x~of+cpu/in0")]
    [InlineData("hwmon/v1/x/name+x~port+1/power2")]
    [InlineData("hwmon/v1/x/name+x~meta+a/freq1")]
    [InlineData("hwmon/v1/x/name+x/humidity10")]
    [InlineData("thermal/v1/type+iwlwifi/temp")]
    [InlineData("thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp")]
    public void A_built_id_parses_and_builds_again_as_it_was(string id)
    {
        Assert.True(LinuxSensorIds.TryParse(id, out var parsed));
        Assert.Equal(id, parsed.ToString());
        var rebuilt = parsed.Family == LinuxSensorFamily.Hwmon
            ? LinuxSensorIds.Hwmon(parsed.Chip!, parsed.Anchor, parsed.Secondary, parsed.Channel)
            : LinuxSensorIds.Thermal(parsed.Anchor, parsed.Secondary);
        Assert.Equal(id, rebuilt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hwmon/v1/")]
    [InlineData("thermal/v1/")]
    [InlineData("hwmon/v2/it8696/platform+it87.2624/fan1")]
    [InlineData("hwmon/v1/it8696/platform+it87.2624")]
    [InlineData("hwmon/v1/it8696/platform+it87.2624/fan1/x")]
    [InlineData("hwmon/v1/../name+x/temp1")]
    [InlineData("hwmon/v1/%FF/name+x/temp1")]
    [InlineData("hwmon/v1/a%/name+x/temp1")]
    [InlineData("hwmon/v1/a%2fb/name+x/temp1")]
    [InlineData("hwmon/v1/CON/name+x/temp1")]
    [InlineData("hwmon/v1//name+x/temp1")]
    [InlineData("hwmon/v1/nvme/name+nvme/temp01")]
    [InlineData("hwmon/v1/nvme/name+x/temp-1")]
    [InlineData("hwmon/v1/nvme/name+x/temp")]
    [InlineData("hwmon/v1/nvme/name+x/pwm1")]
    [InlineData("hwmon/v1/nvme/other+x/temp1")]
    [InlineData("hwmon/v1/nvme/name+x~other+x/temp1")]
    [InlineData("hwmon/v1/nvme/name+x~pci+a~pci+b/temp1")]
    [InlineData("hwmon/v1/nvme/name/temp1")]
    [InlineData("hwmon/v1/nvme/name+/temp1")]
    [InlineData("hwmon/v1/nvme/name+x+/temp1")]
    [InlineData("hwmon/v1/nvme/name+x~/temp1")]
    [InlineData("hwmon/v1/nvme/pci+x~name+x/temp1")]
    [InlineData("thermal/v1/type+acpitz/temp1")]
    [InlineData("thermal/v1/acpitz/type+acpitz/temp")]
    [InlineData("system/cpu/total")]
    public void A_malformed_id_does_not_parse(string? id)
    {
        Assert.False(LinuxSensorIds.TryParse(id, out _));
    }

    [Fact]
    public void Building_refuses_what_the_grammar_has_no_place_for()
    {
        var name = new LinuxAnchor("name", "x");
        Assert.Throws<ArgumentException>(() => LinuxSensorIds.Hwmon("", name, null, "temp1"));
        Assert.Throws<ArgumentException>(() => LinuxSensorIds.Hwmon("x", name, null, "temp01"));
        Assert.Throws<ArgumentException>(() => LinuxSensorIds.Hwmon("x", new LinuxAnchor("other", "x"), null, "temp1"));
        Assert.Throws<ArgumentException>(() => LinuxSensorIds.Hwmon("x", name, new LinuxAnchor("name", "x"), "temp1"));
        Assert.Throws<ArgumentException>(() => LinuxSensorIds.Hwmon("x", new LinuxAnchor("name"), null, "temp1"));
        Assert.Throws<ArgumentException>(() => LinuxSensorIds.Hwmon("x", new LinuxAnchor("name", ""), null, "temp1"));
    }
}
