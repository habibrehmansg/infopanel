using System.Text;
using InfoPanel.Core.Tests.Panels.Jl.V1Oracle;
using InfoPanel.Panels.Jl;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Jl;

/// <summary>
/// The protocol's byte vectors: each pure function the port carries is fed the inputs 1.4.x's own function is
/// fed, copied verbatim into <c>V1Oracle/JlSerialDevice.cs</c> (1.4.x c71f0f00, <c>InfoPanel/JlPanel/JlSerialDevice.cs</c>),
/// and the bytes compared: the command frames, the light's clamp, the JPEG envelope and its terminator, the pipeline reset,
/// the reply's framing and the device info's JSON.
/// </summary>
public sealed class JlProtocolTests
{
    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);

    private static V1JlSerialDevice Oracle()
    {
        var oracle = V1JlSerialDevice.Open()!;
        oracle.Port.Writes.Clear(); // the pipeline reset, checked on its own
        return oracle;
    }

    // ---- The constants (:28-37) ----

    [Fact]
    public void TheCommandsAndTheSerialSettings_Are1_4s()
    {
        Assert.Equal(
            (V1JlSerialDevice.CMD_RESTART, V1JlSerialDevice.CMD_SET_LIGHT, V1JlSerialDevice.CMD_GET_DEVICE_INFO, V1JlSerialDevice.CMD_KEEP_ALIVE, V1JlSerialDevice.CMD_SET_REGION, V1JlSerialDevice.CMD_CLOSE),
            (JlProtocol.Restart, JlProtocol.SetLight, JlProtocol.GetDeviceInfo, JlProtocol.KeepAliveCommand, JlProtocol.SetRegion, JlProtocol.CloseCommand));
        Assert.Equal((V1JlSerialDevice.BaudRate, V1JlSerialDevice.ReadTimeoutMs, V1JlSerialDevice.WriteTimeoutMs),
            (JlProtocol.Baud, JlProtocol.ReadTimeoutMs, JlProtocol.WriteTimeoutMs));
    }

    // ---- Command frames: BuildFrame (:102-118), written by SendCommand (:121-126) ----

    [Fact]
    public void EachCommand1_4Sends_IsByteForByte_1_4s()
    {
        var opened = V1JlSerialDevice.Open()!; // :82-86, the pipeline reset written once the port opens
        Assert.Equal(Hex(Assert.Single(opened.Port.Writes)), Hex(JlProtocol.PipelineReset.ToArray()));

        var oracle = Oracle();
        oracle.SendKeepAlive();                                      // :210
        Assert.Null(oracle.GetDeviceInfo());                         // :192, the query written (no reply queued)
        oracle.SetBrightness(37);                                    // :213-217
        oracle.SendCommand(V1JlSerialDevice.CMD_RESTART);           // 0x01, never sent by 1.4.x
        oracle.SendCommand(V1JlSerialDevice.CMD_SET_REGION, Encoding.UTF8.GetBytes("sulcs")); // 0x20, never sent by 1.4.x
        oracle.Dispose();                                            // :262, close
        var writes = oracle.Port.Writes.Select(Hex).ToList();

        Assert.Equal(
            [Hex(JlProtocol.KeepAliveFrame), Hex(JlProtocol.DeviceInfoQuery), Hex(JlProtocol.Light(37)), Hex(JlProtocol.RestartFrame), Hex(JlProtocol.Region("sulcs")), Hex(JlProtocol.CloseFrame)],
            writes);
    }

    public static TheoryData<int, int> FrameCases() => new()
    {
        { 0x06, 0 }, { 0x03, 1 }, { 0x20, 2 }, { 0x20, 5 }, { 0x7F, 250 }, { 0xFF, 251 }, { 0x00, 256 }, { 0x23, 1000 }, { 0x11, 65_528 },
    };

    [Theory]
    [MemberData(nameof(FrameCases))]
    public void AnyFrame_IsByteForByte_1_4s_TheChecksumWrappingAt16Bits(int command, int length)
    {
        foreach (var fill in new[] { "random", "ff" })
        {
            var payload = new byte[length];
            if (fill == "ff")
                Array.Fill(payload, (byte)0xFF); // a sum far past 16 bits
            else
                new Random(length * 31 + command).NextBytes(payload);

            Assert.Equal(Hex(V1JlSerialDevice.BuildFrame((byte)command, payload)), Hex(JlProtocol.Frame((byte)command, payload)));
        }
    }

    [Fact]
    public void TheLight_IsClampedTo0To100_As1_4Did()
    {
        var oracle = Oracle();
        int[] values = [int.MinValue, -1000, -1, .. Enumerable.Range(0, 101), 101, 255, 256, 1000, int.MaxValue];
        foreach (var value in values)
        {
            oracle.SetBrightness(value);
            Assert.Equal(Hex(oracle.Port.Writes[^1]), Hex(JlProtocol.Light(value)));
        }
    }

    [Fact]
    public void TheKeepAlive_IsTheFramingRules_NotTheProtocolNotesBytes()
    {
        // JL-PROTOCOL.md:144 gives the keep-alive as 55 AA 07 00 11 11 01 ("sum = 0x111"); the code's rule (:113-116), which
        // 1.4.x sent, sums 55 + AA + 07 + 00 + 11 = 0x117. The port sends what the code sent.
        Assert.Equal("55aa0700111701", Hex(JlProtocol.KeepAliveFrame));
        Assert.Equal(Hex(V1JlSerialDevice.BuildFrame(V1JlSerialDevice.CMD_KEEP_ALIVE, [])), Hex(JlProtocol.KeepAliveFrame));
    }

    // ---- The JPEG envelope: SendJpegFrame (:223-250) ----

    public static TheoryData<int> EnvelopeLengths() => new() { 0, 1, 2, 3, 100, 4096, 80 * 1024, 80 * 1024 + 1, 200_000 };

    [Theory]
    [MemberData(nameof(EnvelopeLengths))]
    public void AFramesWrites_AreTheKeepAlive_TheEnvelope_AndTheTerminator_ByteForByte_1_4s(int length)
    {
        foreach (var fill in new[] { "random", "ff" })
        {
            var jpeg = new byte[length];
            if (fill == "ff")
                Array.Fill(jpeg, (byte)0xFF);
            else
                new Random(length).NextBytes(jpeg);
            var oracle = Oracle();
            oracle.SendJpegFrame(jpeg);
            var writes = oracle.Port.Writes;

            var envelope = new byte[JlProtocol.EnvelopeLength(length)];
            var written = JlProtocol.Envelope(jpeg, envelope);

            Assert.Equal(3, writes.Count);
            Assert.Equal(Hex(writes[0]), Hex(JlProtocol.KeepAliveFrame));     // :228
            Assert.Equal(Hex(writes[1]), Hex(envelope.AsSpan(0, written).ToArray())); // :230-247
            Assert.Equal(envelope.Length, written);
            Assert.Equal(Hex(writes[2]), Hex(JlProtocol.FrameTerminator.ToArray())); // :249
        }
    }

    [Fact]
    public void TheEnvelope_FillsOnlyItsOwnLength_OfABiggerBuffer()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9 };
        var buffer = new byte[64];
        Array.Fill(buffer, (byte)0xEE);

        var written = JlProtocol.Envelope(jpeg, buffer);

        var oracle = Oracle();
        oracle.SendJpegFrame(jpeg);
        Assert.Equal(Hex(oracle.Port.Writes[1]), Hex(buffer.AsSpan(0, written).ToArray()));
        Assert.All(buffer.Skip(written), b => Assert.Equal(0xEE, b));
    }

    // ---- Replies: ReadFrame (:140-185) ----

    private static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

    private const string Info = "{\"status\":200,\"uid\":\"A1B2C3\",\"model\":\"TXW818-ST7701S-4.0inch\",\"version\":\"Ver1.0\",\"width\":480,\"height\":960,\"angle\":0,\"region\":\"sulcs\"}";

    public static TheoryData<string, string> Replies()
    {
        var info = V1JlSerialDevice.BuildFrame(0x06, Json(Info));
        var status = V1JlSerialDevice.BuildFrame(0x03, [0x01]);
        var empty = V1JlSerialDevice.BuildFrame(0x06, []);
        return new()
        {
            { "a device info", Hex(info) },
            { "a status byte", Hex(status) },
            { "no payload", Hex(empty) },
            { "noise before it", "00135513" + Hex(info) },
            { "0x55 not followed by 0xAA", "5500" + Hex(info) },
            { "a length under 7", "55aa0600060000" },
            { "the longest length, cut short", "55aaffff06" + new string('7', 200) },
            { "no checksum", Hex(info[..^2]) },
            { "half a checksum", Hex(info[..^1]) },
            { "the payload cut short", Hex(info[..20]) },
            { "only the magic", "55aa" },
            { "noise only", "0102030405aa" },
            { "nothing", "" },
            { "two frames: the first", Hex(status) + Hex(info) },
            { "a frame then noise", Hex(status) + "ffd9ffd9" },
        };
    }

    [Theory]
    [MemberData(nameof(Replies))]
    public void AReply_ReadsAs1_4ReadIt(string name, string hex)
    {
        var bytes = Convert.FromHexString(hex);
        var oracle = Oracle();
        foreach (var b in bytes)
            oracle.Port.Input.Enqueue(b);
        var expected = oracle.ReadFrame(); // null: a timeout, or a frame it didn't take

        var state = JlProtocol.ParseReply(bytes, out var payload);

        Assert.True((expected == null) == (state != JlReply.Complete), $"{name}: 1.4.x read {(expected == null ? "nothing" : Hex(expected))}, the port {state}");
        if (expected != null)
            Assert.Equal(Hex(expected), Hex(payload!));
    }

    [Fact]
    public void EveryPrefixOfAReply_IsIncomplete_SoTheReaderReadsOn()
    {
        // 1.4.x's ReadFrame read byte by byte, each waiting up to its timeout (:146-175): a reply that comes in parts is
        // read whole. The port's parse says Incomplete for every prefix, and Invalid only where 1.4.x gave up on bytes it had.
        var info = V1JlSerialDevice.BuildFrame(0x06, Json(Info));
        for (var n = 0; n < info.Length; n++)
            Assert.Equal(JlReply.Incomplete, JlProtocol.ParseReply(info.AsSpan(0, n), out _));
        Assert.Equal(JlReply.Invalid, JlProtocol.ParseReply(Convert.FromHexString("5500"), out _));
        Assert.Equal(JlReply.Invalid, JlProtocol.ParseReply(Convert.FromHexString("55aa0600"), out _));
    }

    // ---- The device info: GetDeviceInfo (:190-207) ----

    public static TheoryData<string, string> DeviceInfos() => new()
    {
        { "the protocol notes' reply", Info },
        { "the strip", "{\"status\":200,\"uid\":\"S9\",\"model\":\"strip\",\"version\":\"3.2\",\"width\":1920,\"height\":462,\"angle\":90}" },
        { "names in any case", "{\"Status\":200,\"UID\":\"x\",\"Width\":800,\"HEIGHT\":480}" },
        { "padded with NULs", Info + "\0\0\0" },
        { "a refusal", "{\"status\":500}" },
        { "members it doesn't know", "{\"status\":200,\"width\":480,\"height\":960,\"fps\":60,\"extra\":{\"a\":[1,2]}}" },
        { "no members", "{}" },
        { "JSON null", "null " },
        { "not JSON", "hello world" },
        { "cut short", "{\"status\":200,\"wid" },
        { "a number as text", "{\"status\":200,\"width\":\"480\"}" },
        { "four bytes", "{}  " },
        { "a fraction", "{\"status\":200,\"width\":480.5}" },
    };

    [Theory]
    [MemberData(nameof(DeviceInfos))]
    public void ADeviceInfo_ParsesAs1_4ParsedIt(string name, string json)
    {
        var payload = Json(json);
        var oracle = Oracle();
        foreach (var b in V1JlSerialDevice.BuildFrame(0x06, payload))
            oracle.Port.Input.Enqueue(b);
        var expected = oracle.GetDeviceInfo();

        var info = JlProtocol.ParseDeviceInfo(payload);

        Assert.True((expected == null) == (info == null), $"{name}: 1.4.x {(expected == null ? "read none" : "read one")}, the port {(info == null ? "none" : "one")}");
        if (expected != null)
            Assert.Equal(
                (expected.Status, expected.Uid, expected.Model, expected.Version, expected.Width, expected.Height, expected.Angle, expected.Region),
                (info!.Status, info.Uid, info.Model, info.Version, info.Width, info.Height, info.Angle, info.Region));
    }

    [Fact]
    public void NoPayload_IsNoDeviceInfo()
    {
        Assert.Null(JlProtocol.ParseDeviceInfo(null));
        Assert.Null(JlProtocol.ParseDeviceInfo([]));
    }
}
