using System.Text;
using InfoPanel.Core.Tests.Panels.Thermaltake.V1Oracle;
using InfoPanel.Panels.Thermaltake;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermaltake;

/// <summary>
/// The protocol's byte vectors (§2.2 (1)): each pure function the port carries is fed the inputs 1.4.x's own function is
/// fed, copied verbatim into <c>V1Oracle/ThermaltakeHidDevice.cs</c> (1.4.x c71f0f00,
/// <c>InfoPanel/ThermaltakePanel/ThermaltakeHidDevice.cs</c>), and the bytes compared; the framing also against the
/// three packets 1.4.x's first version carried, captured from the vendor's software.
/// </summary>
public sealed class ThermaltakeProtocolTests
{
    private static readonly long[] Dates = [0, 1_731_400_000_000, 1_731_400_000_999, 9_999_999_999_999];

    public static TheoryData<long> DateCases() => new(Dates);

    // ---- Commands: BuildCommandPacket (:201-240), written by WritePacket (:262-271) ----

    [Theory]
    [MemberData(nameof(DateCases))]
    public void TheHandshakesAndTheScreenOffsCommands_AreByteForByte_1_4s(long date)
    {
        var oracle = new V1ThermaltakeHidDevice { Now = () => date };
        // Handshake (:88, :93-94) then DisableRealtimeDisplay (:150-151), one device: SeqNumber 100, 101, 102.
        var conn = oracle.BuildCommandPacket("POST conn 1", hasBody: false);
        var enable = oracle.BuildCommandPacket("POST realtimeDisplay 1", body: "{\"enable\":true}", contentType: "json");
        var disable = oracle.BuildCommandPacket("POST realtimeDisplay 1", body: "{\"enable\":false}", contentType: "json");

        Assert.Equal(conn, ThermaltakeProtocol.Conn(100, date));
        Assert.Equal(enable, ThermaltakeProtocol.RealtimeDisplay(true, 101, date));
        Assert.Equal(disable, ThermaltakeProtocol.RealtimeDisplay(false, 102, date));
        Assert.Equal(ThermaltakeProtocol.PacketSize, conn.Length);
    }

    [Fact]
    public void Commands_AnySeqNumberAndShape_AreByteForByte_1_4s()
    {
        // Every combination BuildCommandPacket takes: a body with and without a content type, no body, hasBody false
        // with a body (left out), and SeqNumbers past three digits.
        var oracle = new V1ThermaltakeHidDevice { Now = () => 1_731_400_123_456 };
        var cases = new (string Command, string? Body, string? Type, bool HasBody)[]
        {
            ("POST conn 1", null, null, false),
            ("POST conn 1", null, null, true),
            ("POST realtimeDisplay 1", "{\"enable\":true}", "json", true),
            ("POST brightness 1", "{\"value\":37}", "json", true),
            ("POST brightness 1", "{\"value\":37}", null, true),
            ("POST brightness 1", "{\"value\":37}", "json", false),
            ("POST x 1", "", "json", true),
        };
        var seq = 100;
        for (var round = 0; round < 300; round++)
        {
            var (command, body, type, hasBody) = cases[round % cases.Length];
            var expected = oracle.BuildCommandPacket(command, body, type, hasBody);
            Assert.Equal(expected, ThermaltakeProtocol.Command(command, seq++, 1_731_400_123_456, body, type, hasBody));
        }
    }

    [Fact]
    public void ReportsPutTheNullReportIdFirst_AsWritePacketDoes()
    {
        // WritePacket (:262-271): report id 0, then the 1024-byte packet; 1025 bytes. Through the oracle's handshake.
        var oracle = new V1ThermaltakeHidDevice { Now = () => 5 };
        oracle.Handshake(); // no replies: the conn's read times out and the handshake stops after one write
        var report = Assert.Single(oracle.Reports);

        Assert.Equal(report, ThermaltakeProtocol.Report(ThermaltakeProtocol.Conn(100, 5)));
        Assert.Equal(ThermaltakeProtocol.ReportSize, report.Length);
    }

    // ---- The framing against the vendor's captures (1.4.x f432334d, ThermaltakeHidDevice.cs:24-39) ----

    public static TheoryData<string, string> Captures() => new()
    {
        // :24-27, CONN_PACKET
        { "conn", "5a0048504f535420636f6e6e20310d0a5365714e756d6265723d3131320d0a" +
                  "436f6e74656e74547970653d6a736f6e0d0a436f6e74656e744c656e677468" +
                  "3d3234300d0a0d0a075a00" },
        // :29-33, REALTIME_ENABLE_PACKET
        { "realtimeDisplay true", "5a0061504f5354207265616c74696d65446973706c617920310d0a5365714e" +
                                  "756d6265723d3131320d0a436f6e74656e74547970653d6a736f6e0d0a436f" +
                                  "6e74656e744c656e6774683d31350d0a0d0a7b22656e61626c65223a747275" +
                                  "657d085a00" },
    };

    /// <summary>f432334d's third packet, :35-39, REALTIME_DISABLE_PACKET.</summary>
    private const string FirstVersionsDisable = "5a0062504f5354207265616c74696d65446973706c617920310d0a5365714e" +
                                                "756d6265723d3131320d0a436f6e74656e74547970653d6a736f6e0d0a436f" +
                                                "6e74656e744c656e6774683d31360d0a0d0a7b22656e61626c65223a66616c" +
                                                "73657d035a00";

    [Fact]
    public void TheFirstVersionsDisablePacket_HadAChecksumTheRuleDoesntGive_TheTipsIsPorted()
    {
        // The first version's disable packet is the enable capture with its body changed: its length (0x62) follows
        // the rule, its checksum (0x03) doesn't (the rule gives 0x55). The tip (c71f0f00) builds every command and so
        // sends 0x55; the port follows the tip. Which the panel takes is for a tester's trace.
        var capture = Convert.FromHexString(FirstVersionsDisable);
        var content = capture.AsSpan(3, capture.Length - 6).ToArray();
        var framed = ThermaltakeProtocol.Frame(content);

        Assert.Equal((0x62, 0x03), (capture[2], capture[^3]));
        Assert.Equal((0x62, 0x55), (framed[2], framed[3 + content.Length]));
    }

    [Theory]
    [MemberData(nameof(Captures))]
    public void Frame_GivesTheVendorsCapturedPackets_LengthAndChecksum(string name, string hex)
    {
        // The captures' content (between the length byte and the checksum) framed by the port gives the capture, padded
        // to a packet: the length is the content's plus 5 and the checksum is (length + the content's bytes) mod 256.
        var capture = Convert.FromHexString(hex);
        var content = capture.AsSpan(3, capture.Length - 6).ToArray();
        var expected = new byte[ThermaltakeProtocol.PacketSize];
        capture.CopyTo(expected, 0);

        Assert.True(Encoding.ASCII.GetString(content).StartsWith("POST " + name.Split(' ')[0], StringComparison.Ordinal), name);
        Assert.Equal(expected, ThermaltakeProtocol.Frame(content));
    }

    [Fact]
    public void Frame_RefusesContentItsLengthByteCantHold()
    {
        // 1.4.x's length is one byte (:221): its commands are far shorter, and a longer content would wrap it. The port
        // refuses it rather than send a packet whose length lies.
        Assert.Equal(ThermaltakeProtocol.PacketSize, ThermaltakeProtocol.Frame(new byte[ThermaltakeProtocol.MaxContent]).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => ThermaltakeProtocol.Frame(new byte[ThermaltakeProtocol.MaxContent + 1]));
    }

    // ---- Image chunks: SendJpegFrame (:115-141) ----

    public static TheoryData<int> FrameLengths() => new(1, 23, 999, 1000, 1001, 2500, 64_999, 256_000, 256_001, 300_123);

    [Theory]
    [MemberData(nameof(FrameLengths))]
    public void AJpegsChunks_AreByteForByte_1_4s(int length)
    {
        var jpeg = new byte[length];
        new Random(length).NextBytes(jpeg);
        var oracle = new V1ThermaltakeHidDevice();
        oracle.SendJpegFrame(jpeg);

        var total = ThermaltakeProtocol.ChunkCount(length);
        Assert.Equal(oracle.Reports.Count, total);
        var report = new byte[ThermaltakeProtocol.ReportSize];
        for (var chunk = 0; chunk < total; chunk++)
        {
            Array.Fill(report, (byte)0xEE); // a buffer reused from a frame before: every byte is written
            ThermaltakeProtocol.ImageReport(jpeg, chunk, total, report);
            Assert.True(oracle.Reports[chunk].AsSpan().SequenceEqual(report), $"chunk {chunk} of {total}");
        }
    }

    [Fact]
    public void ChunkCount_IsTheThousandsRoundedUp_AndNoneForNothing()
    {
        Assert.Equal((0, 1, 1, 2, 65_535), (ThermaltakeProtocol.ChunkCount(0), ThermaltakeProtocol.ChunkCount(1),
            ThermaltakeProtocol.ChunkCount(1000), ThermaltakeProtocol.ChunkCount(1001), ThermaltakeProtocol.ChunkCount(65_535_000)));
        var oracle = new V1ThermaltakeHidDevice();
        oracle.SendJpegFrame([]);
        Assert.Empty(oracle.Reports); // 1.4.x sends nothing for an empty frame
    }

    // ---- Replies: ReadResponse (:163-193) and the check in SendPacketAndCheckResponse (:248-249) ----

    public static TheoryData<string, int> Replies() => new()
    {
        { "00", 1 },
        { "005a0011", 4 }, // 4 bytes read: too short, no answer
        { "005a001132", 5 },
        { "005a00203230300d0a4f4b0d0a00", 14 }, // "200\r\nOK\r\n" framed
        { "005a00203230300d0a4f4b0d0a00", 8 }, // read shorter than the report: only what was read
        { "00" + "00000000", 5 }, // nothing printable: no answer
        { "00ff7f1f20093332300a0d32ff30307e", 16 }, // control and high bytes skipped, CR and LF kept: "320\n\r200~" holds 200
        { "00" + "32" + "00" + "3030", 5 }, // "2" "00": nulls are skipped, so this reads "200"
        { "323030" + "343034", 6 }, // the first byte (the report id) is never read, even when it is text
        { "00" + Convert.ToHexString(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found")), 23 },
    };

    [Theory]
    [MemberData(nameof(Replies))]
    public void AReplysText_AndWhetherItSays200_AreAs1_4ReadsThem(string hex, int read)
    {
        var report = new byte[ThermaltakeProtocol.ReportSize];
        Convert.FromHexString(hex).CopyTo(report, 0);
        var oracle = new V1ThermaltakeHidDevice();
        oracle.Replies.Enqueue((report, read));
        var expected = oracle.ReadResponse();

        var text = ThermaltakeProtocol.ResponseText(report, read);
        Assert.Equal(expected, text);
        Assert.Equal(expected != null && expected.Contains("200"), ThermaltakeProtocol.Accepted(text));
    }
}
