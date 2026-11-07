using Xunit;
using InfoPanel.Panels.BeadaPanel;
using V = InfoPanel.Core.Tests.Panels.BeadaPanel.BeadaPanelVectors;

namespace InfoPanel.Core.Tests.Panels.BeadaPanel;

/// <summary>BeadaPanel's messages, info reply and model table against vectors worked by hand (<see cref="BeadaPanelVectors"/>).</summary>
public class BeadaPanelProtocolTests
{
    [Fact]
    public void Status_link_messages_are_byte_exact()
    {
        Assert.Equal(V.GetPanelInfo, BeadaPanelProtocol.StatusLink(StatusLinkType.GetPanelInfo));
        Assert.Equal(V.PanelLinkReset, BeadaPanelProtocol.StatusLink(StatusLinkType.PanelLinkReset));
        Assert.Equal(V.SetBacklight(100), BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [100]));
        Assert.Equal(V.SetBacklight(0), BeadaPanelProtocol.StatusLink(StatusLinkType.SetBacklight, [0]));
    }

    [Fact]
    public void Panel_link_messages_are_byte_exact()
    {
        Assert.Equal(V.EndMediaStream, BeadaPanelProtocol.PanelLink(PanelLinkType.EndMediaStream));
        Assert.Equal(V.LegacyCommand2, BeadaPanelProtocol.PanelLink(PanelLinkType.LegacyCommand2));
        Assert.Equal(V.StartMediaStream800x480, BeadaPanelProtocol.PanelLink(PanelLinkType.StartMediaStream, "image/x-raw, format=BGR16, width=800, height=480, framerate=0/1"u8));
        Assert.Equal(V.LegacyCommand1_480x320, BeadaPanelProtocol.PanelLink(PanelLinkType.LegacyCommand1, "video/x-raw, format=RGB16, width=480, height=320, framerate=0/1"u8));
    }

    [Fact]
    public void A_format_string_goes_only_into_types_1_and_5()
    {
        // PanelLinkMessage.cs:37-44: other types carry no format string, so their bytes (and checksum) don't change.
        Assert.Equal(V.EndMediaStream, BeadaPanelProtocol.PanelLink(PanelLinkType.EndMediaStream, "video/x-raw"u8));
    }

    [Fact]
    public void A_format_string_longer_than_256_bytes_is_refused()
    {
        Assert.Throws<ArgumentException>(() => BeadaPanelProtocol.PanelLink(PanelLinkType.StartMediaStream, new byte[257]));
        Assert.Equal(270, BeadaPanelProtocol.PanelLink(PanelLinkType.StartMediaStream, new byte[256]).Length);
    }

    [Fact]
    public void The_checksum_sums_a_last_odd_byte_alone()
    {
        // StatusLinkMessage.cs:55-57. Words: 0x0201, then 0x03 alone: 0x0204, inverted 0xFDFB.
        Assert.Equal(0xFDFB, BeadaPanelProtocol.Checksum([0x01, 0x02, 0x03]));
        // Folding twice: 0xFFFF + 0xFFFF = 0x1FFFE → 0xFFFF → inverted 0.
        Assert.Equal(0x0000, BeadaPanelProtocol.Checksum([0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Theory]
    // BeadaPanelDeviceTask.cs:180: PanelLink version 1 sends (byte)(b / 100.0 × 75 + 25), others b.
    [InlineData(100, 1, 100)]
    [InlineData(50, 1, 62)]
    [InlineData(0, 1, 25)]
    [InlineData(1, 1, 25)]
    [InlineData(100, 2, 100)]
    [InlineData(50, 2, 50)]
    [InlineData(0, 2, 0)]
    public void Brightness_maps_to_the_backlight_byte(int brightness, byte panelLink, byte expected)
    {
        Assert.Equal(expected, BeadaPanelProtocol.BrightnessByte(brightness, panelLink));
    }

    [Fact]
    public void Brightness_outside_0_to_100_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BeadaPanelProtocol.BrightnessByte(101, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => BeadaPanelProtocol.BrightnessByte(-1, 2));
    }

    [Fact]
    public void Write_through_streams_the_models_size_and_others_the_reported_one()
    {
        // BeadaPanelDeviceTask.cs:139-150 and PanelLinkMessage.cs:53-63.
        var writeThrough = Parse(V.InfoReply(panelLink: 2, platform: 1, modelCode: 0, resX: 1024, resY: 600));
        Assert.True(writeThrough.WriteThrough);
        Assert.Equal((800, 480), (writeThrough.Width, writeThrough.Height));
        Assert.Equal(V.WriteThrough800x480, BeadaPanelProtocol.FormatString(writeThrough));
        Assert.Equal(V.StartMediaStream800x480, BeadaPanelProtocol.StartStream(writeThrough));
        Assert.Equal(V.EndMediaStream, BeadaPanelProtocol.EndStream(writeThrough));

        var platform2 = Parse(V.InfoReply(panelLink: 1, platform: 2, modelCode: 0, resX: 1024, resY: 600));
        Assert.True(platform2.WriteThrough);
        Assert.Equal(V.LegacyCommand1_800x480, BeadaPanelProtocol.StartStream(platform2));
        Assert.Equal(V.LegacyCommand2, BeadaPanelProtocol.EndStream(platform2));

        var reported = Parse(V.InfoReply(panelLink: 2, platform: 0, modelCode: 3, resX: 480, resY: 320));
        Assert.False(reported.WriteThrough);
        Assert.Equal((480, 320), (reported.Width, reported.Height));
        Assert.Equal(V.Reported480x320, BeadaPanelProtocol.FormatString(reported));
        Assert.Equal(V.StartMediaStream480x320, BeadaPanelProtocol.StartStream(reported));
    }

    [Fact]
    public void The_info_reply_is_parsed_field_by_field()
    {
        var reply = V.InfoReply(panelLink: 2, platform: 1, modelCode: 24, resX: 1920, resY: 480, serial: "934072005C", firmware: 0x0315, statusLink: 3);
        var info = BeadaPanelInfo.Parse(reply, out var error, out var code);
        Assert.Equal(BeadaPanelInfoError.None, error);
        Assert.Equal(24, code);
        Assert.NotNull(info);
        Assert.Equal(0x0315, info.Firmware);
        Assert.Equal(2, info.PanelLinkVersion);
        Assert.Equal(3, info.StatusLinkVersion);
        Assert.Equal(1, info.Platform);
        Assert.Equal("Y", info.Model.Id);
        Assert.Equal("934072005C", info.Serial);
        Assert.Equal((1920, 480), (info.ResolutionX, info.ResolutionY));
        Assert.Equal(4096u, info.StorageKb);
        Assert.Equal(100, info.MaxBrightness);
        Assert.Equal(80, info.CurrentBrightness);
        // Model Y is 480 × 1920 (BeadaPanelModelDatabase.cs:29); write-through streams that size.
        Assert.Equal((480, 1920), (info.Width, info.Height));
    }

    [Fact]
    public void A_reply_shorter_than_100_bytes_is_refused()
    {
        var reply = V.InfoReply(2, 1, 0, 800, 480);
        Assert.Null(BeadaPanelInfo.Parse(reply.AsSpan(0, 99), out var error, out _));
        Assert.Equal(BeadaPanelInfoError.Short, error);
    }

    [Fact]
    public void A_reply_without_the_status_link_header_is_refused()
    {
        var reply = V.InfoReply(2, 1, 0, 800, 480);
        reply[0] = (byte)'X';
        Assert.Null(BeadaPanelInfo.Parse(reply, out var error, out _));
        Assert.Equal(BeadaPanelInfoError.Header, error);
    }

    [Fact]
    public void A_reply_of_another_type_is_refused()
    {
        var reply = V.InfoReply(2, 1, 0, 800, 480);
        reply[12] = 2;
        Assert.Null(BeadaPanelInfo.Parse(reply, out var error, out _));
        Assert.Equal(BeadaPanelInfoError.Type, error);
    }

    [Theory]
    [InlineData(1)] // Model7: in the enum, not in the table (BeadaPanelModelType.cs:12)
    [InlineData(5)]
    [InlineData(27)]
    [InlineData(255)]
    public void An_unknown_model_code_is_refused_with_its_code(byte modelCode)
    {
        Assert.Null(BeadaPanelInfo.Parse(V.InfoReply(2, 1, modelCode, 800, 480), out var error, out var code));
        Assert.Equal(BeadaPanelInfoError.UnknownModel, error);
        Assert.Equal(modelCode, code);
    }

    [Theory]
    // BeadaPanelParser.cs:48-53: ASCII, NULs then white space trimmed, empty unless ^[a-zA-Z0-9]+$.
    [InlineData("ABC123", "ABC123")]
    [InlineData("  ABC123  ", "ABC123")]
    [InlineData("ABC-123", "")]
    [InlineData("AB C", "")]
    [InlineData("", "")]
    public void The_serial_is_trimmed_and_kept_only_when_alphanumeric(string written, string expected)
    {
        var info = BeadaPanelInfo.Parse(V.InfoReply(2, 1, 0, 800, 480, serial: written), out _, out _);
        Assert.NotNull(info);
        Assert.Equal(expected, info.Serial);
    }

    [Fact]
    public void A_serial_with_a_byte_above_ascii_is_dropped()
    {
        var reply = V.InfoReply(2, 1, 0, 800, 480, serial: "AB");
        reply[28] = 0xC3;
        var info = BeadaPanelInfo.Parse(reply, out _, out _);
        Assert.Equal("", info!.Serial);
    }

    [Fact]
    public void The_model_table_holds_the_21_models_of_1_4()
    {
        // BeadaPanelModelDatabase.cs:13-33 with the codes of BeadaPanelModelType.cs:11-32.
        (byte Code, string Id, int W, int H)[] expected =
        [
            (17, "2", 480, 480), (18, "2W", 480, 480), (3, "3", 320, 480), (4, "4", 480, 800), (13, "3C", 480, 320),
            (14, "4C", 800, 480), (10, "5C", 800, 480), (0, "5", 800, 480), (11, "5T", 800, 480), (20, "5S", 480, 854),
            (2, "6", 480, 1280), (15, "6C", 1280, 480), (16, "6S", 1280, 480), (12, "7C", 800, 480), (19, "7S", 1280, 400),
            (21, "8", 480, 1920), (24, "Y", 480, 1920), (22, "11", 440, 1920), (25, "X", 440, 1920), (23, "9", 462, 1920),
            (26, "Z", 462, 1920),
        ];
        Assert.Equal(21, BeadaPanelModel.All.Count);
        foreach (var (code, id, w, h) in expected)
            Assert.Equal(new BeadaPanelModel(code, id, w, h), BeadaPanelModel.ByCode(code));
        Assert.Null(BeadaPanelModel.ByCode(1));
    }

    private static BeadaPanelInfo Parse(byte[] reply) =>
        BeadaPanelInfo.Parse(reply, out _, out _) ?? throw new InvalidOperationException("the reply didn't parse");
}
