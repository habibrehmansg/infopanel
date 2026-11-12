using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using InfoPanel.Panels.Sdk;

namespace InfoPanel.Panels.Ms9132Jonsbo;

/// <summary>
/// The MacroSilicon MS9132's wire, as InfoPanel 1.4 spoke it to the Jonsbo DS339 and the VMAX 4.6": its control plane is
/// 8-byte HID feature reports (each set, then got back, as the vendor's app does), its frames go to a bulk OUT endpoint of
/// the vendor interface 3 in 64 KiB writes. What the two families share is here; each family's own sequences are in
/// <see cref="JonsboMs9132"/> and <see cref="VmaxMs9132"/>.
/// </summary>
internal static class Ms9132Protocol
{
    /// <summary>A report's payload: an opcode and seven bytes.</summary>
    public const int PayloadLength = 8;

    /// <summary>A feature report as sent and got: the report id (0), then the payload.</summary>
    public const int FeatureLength = 1 + PayloadLength;

    /// <summary>1.4's HID read and write timeouts (JonsboMs9132Device.cs:160-161, VmaxUsbDevice.cs:89,214-215).</summary>
    public const int HidTimeoutMs = 1000;

    /// <summary>The vendor's video interface ("msusb video"), its frames' way in (JonsboMs9132Device.cs:195, VmaxUsbDevice.cs:88).</summary>
    public const int VideoInterface = 3;

    /// <summary>The bulk OUT endpoint frames go to when the interface names no other (VmaxUsbDevice.cs:166, JonsboMs9132Device.cs:198).</summary>
    public const byte DefaultWriteEndpoint = 0x04;

    /// <summary>Frames are written in 64 KiB transfers, as the vendor's app does (JonsboMs9132Device.cs:43, VmaxUsbDevice.cs:90).</summary>
    public const int ChunkSize = 65536;

    /// <summary>A frame's header and trailer, 8 bytes each.</summary>
    public const int HeaderLength = 8;

    /// <inheritdoc cref="HeaderLength"/>
    public const int TrailerLength = 8;

    /// <summary>
    /// The most time a frame's write takes, its writes and reports each given what is left of it: inside the panel host's
    /// 5 s a call (1.4 gave each 64 KiB write its own timeout, so a stalling panel could hold one frame for over a minute).
    /// </summary>
    public static readonly TimeSpan WriteBudget = TimeSpan.FromSeconds(4);

    /// <summary>EDID block 0's size.</summary>
    public const int EdidLength = 128;

    /// <summary>A frame's trailer: <c>FF C0</c> and six zeros (JonsboMs9132Device.cs:396-400, VmaxUsbDevice.cs:16).</summary>
    public static ReadOnlySpan<byte> Trailer => [0xFF, 0xC0, 0, 0, 0, 0, 0, 0];

    /// <summary>The feature report 1.4 set for <paramref name="payload"/>: report id 0, then its 8 bytes (JonsboMs9132Device.cs:276-278).</summary>
    public static byte[] Feature(ReadOnlySpan<byte> payload)
    {
        var report = new byte[FeatureLength];
        payload[..Math.Min(payload.Length, PayloadLength)].CopyTo(report.AsSpan(1));
        return report;
    }

    /// <summary>
    /// The data a reply carries: its 5 bytes after the report id, the opcode and the address it echoes
    /// (JonsboMs9132Device.cs:285-286).
    /// </summary>
    public static ReadOnlySpan<byte> ReplyData(ReadOnlySpan<byte> reply) => reply[4..FeatureLength];

    /// <summary>
    /// Whether <paramref name="reply"/>, <paramref name="read"/> bytes got, is the bridge's answer to
    /// <paramref name="payload"/>: the whole report, echoing its opcode and address ("Reply layout mirrors the request",
    /// JonsboMs9132Device.cs:285). Asked of the probe's identity reads only (<see cref="IsIdentityRead"/>); any other
    /// reply need only be the whole report (<see cref="IsWhole"/>).
    /// </summary>
    public static bool Answers(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> reply, int read) =>
        IsWhole(read) && reply.Length >= FeatureLength && payload.Length >= 3 && reply.Slice(1, 3).SequenceEqual(payload[..3]);

    /// <summary>
    /// Whether a reply of <paramref name="read"/> bytes is the whole report: all 1.4 asked of a reply it didn't read
    /// (it got each report back and looked at none but the reads', JonsboMs9132Device.cs:272-287, VmaxUsbDevice.cs:258-270).
    /// </summary>
    public static bool IsWhole(int read) => read == FeatureLength;

    /// <summary>
    /// Whether <paramref name="payload"/> is one of the reads the probe tells the bridge by: an EDID read or the panel's
    /// status (<see cref="IsEdidRead"/>, <see cref="IsStatusRead"/>).
    /// </summary>
    public static bool IsIdentityRead(ReadOnlySpan<byte> payload) => IsEdidRead(payload) || IsStatusRead(payload);

    /// <summary>Whether <paramref name="payload"/> reads 4 EDID bytes (<c>B5 C0 xx</c>, at offset xx of the block; JonsboMs9132Device.cs:220-221).</summary>
    public static bool IsEdidRead(ReadOnlySpan<byte> payload) =>
        payload.Length >= 3 && payload[0] == 0xB5 && payload[1] == 0xC0 && payload[2] + 4 <= EdidLength;

    /// <summary>Whether <paramref name="payload"/> reads the panel's status, register 0x32 (<c>B5 00 32</c>; JonsboMs9132Device.cs:88).</summary>
    public static bool IsStatusRead(ReadOnlySpan<byte> payload) =>
        payload.Length >= 3 && payload[0] == 0xB5 && payload[1] == 0x00 && payload[2] == 0x32;

    /// <summary>The EDID read <paramref name="payload"/>'s 4 bytes, from its reply, at their offset in <paramref name="edid"/> (JonsboMs9132Device.cs:223).</summary>
    public static void TakeEdid(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> reply, Span<byte> edid) =>
        ReplyData(reply)[..4].CopyTo(edid[payload[2]..]);

    /// <summary>The 32 reads of EDID block 0, 4 bytes each (<c>B5 C0 00</c> to <c>B5 C0 7C</c>; JonsboMs9132Device.cs:76).</summary>
    public static IReadOnlyList<byte[]> EdidReads { get; } =
        [.. Enumerable.Range(0, 32).Select(i => new byte[] { 0xB5, 0xC0, (byte)(i * 4), 0, 0, 0, 0, 0 })];

    /// <summary>
    /// The bulk OUT endpoint frames go to, among the video interface's: <c>0x04</c> when it has it, else the last OUT
    /// endpoint it lists, else <c>0x04</c> (VmaxUsbDevice.cs:164-192, over the claimed interface's endpoints).
    /// </summary>
    public static byte WriteEndpoint(IReadOnlyList<PanelUsbEndpoint> endpoints)
    {
        var endpoint = DefaultWriteEndpoint;
        foreach (var e in endpoints)
        {
            if (e.IsIn)
                continue;
            if (e.Address == DefaultWriteEndpoint)
                return e.Address;
            endpoint = e.Address;
        }
        return endpoint;
    }

    /// <summary>A frame's bytes on the wire: its header, its BGR888 pixels, its trailer.</summary>
    public static int FrameLength(int width, int height) => HeaderLength + width * height * 3 + TrailerLength;
}

/// <summary>
/// The Jonsbo DS339's sequences on the MS9132, InfoPanel 1.4's (JonsboMs9132Device.cs, from a capture of the vendor's
/// app): the power-up, register and EDID block, the mode set, the output enable sent after the second frame, the status
/// poll after each frame, the power-off, and the frame's header.
/// </summary>
internal static class JonsboMs9132
{
    /// <summary>A frame's bulk writes' timeout (JonsboMs9132Device.cs:44).</summary>
    public const int WriteTimeoutMs = 5000;

    /// <summary>The pause after the mode set before the first frame (JonsboMs9132Device.cs:47).</summary>
    public static readonly TimeSpan FirstFrameDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>The frame after which the output is turned on (JonsboPanelDeviceTask.cs:394).</summary>
    public const int EnableVideoAfter = 2;

    /// <summary>The power-up, register setup and EDID read block (JonsboMs9132Device.cs:50-77).</summary>
    public static IReadOnlyList<byte[]> Startup { get; } =
    [
        [0xB5, 0xFF, 0x00, 0, 0, 0, 0, 0],
        [0xB5, 0xF0, 0x00, 0, 0, 0, 0, 0],
        [0xB5, 0x00, 0x31, 0, 0, 0, 0, 0],
        [0xB5, 0x00, 0x30, 0, 0, 0, 0, 0],
        [0xB5, 0xF4, 0x39, 0, 0, 0, 0, 0],
        [0xF5, 0x00, 0x1F, 0xE0, 0, 0, 0, 0],
        [0xF5, 0x00, 0x1F, 0xE8, 0, 0, 0, 0],
        [0xF5, 0x00, 0x1F, 0xF0, 0, 0, 0, 0],
        [0xF5, 0x00, 0x1F, 0xF8, 0, 0, 0, 0],
        [0xA6, 0x07, 0x01, 0x02, 0, 0, 0, 0], // power on
        [0xB5, 0xC4, 0x54, 0, 0, 0, 0, 0],
        [0xA6, 0x05, 0x00, 0, 0, 0, 0, 0], // video off
        [0xB5, 0xC5, 0x55, 0, 0, 0, 0, 0],
        [0xB5, 0xF0, 0x05, 0, 0, 0, 0, 0],
        [0xB6, 0xF0, 0x05, 0x20, 0, 0, 0, 0], // the display control register, its output still off
        [0xC5, 0xB0, 0x00, 0, 0, 0, 0, 0],
        [0xC6, 0xB0, 0xDF, 0, 0, 0, 0, 0],
        [0xC5, 0xA0, 0x00, 0, 0, 0, 0, 0],
        [0xC6, 0xA0, 0xE4, 0, 0, 0, 0, 0],
        [0xC5, 0xA0, 0x00, 0, 0, 0, 0, 0],
        [0xC6, 0xA0, 0xF0, 0, 0, 0, 0, 0],
        [0xB5, 0xF0, 0x16, 0, 0, 0, 0, 0],
        [0xB6, 0xF0, 0x16, 0x00, 0, 0, 0, 0],
        [0xB5, 0x00, 0x32, 0, 0, 0, 0, 0], // the panel's status
        .. Ms9132Protocol.EdidReads,
    ];

    /// <summary>The output turned on, once frames flow (JonsboMs9132Device.cs:80-86).</summary>
    public static IReadOnlyList<byte[]> EnableVideo { get; } =
    [
        [0xB5, 0xF0, 0x05, 0, 0, 0, 0, 0],
        [0xB6, 0xF0, 0x05, 0x30, 0, 0, 0, 0], // the display control register, its output bit set
        [0xA6, 0x05, 0x01, 0, 0, 0, 0, 0], // video on
        [0xB5, 0xC5, 0x55, 0, 0, 0, 0, 0],
    ];

    /// <summary>The panel's status, register 0x32: 1 is connected (JonsboMs9132Device.cs:88,305-310); polled after each frame (:316-320).</summary>
    public static ReadOnlySpan<byte> PanelStatus => [0xB5, 0x00, 0x32, 0, 0, 0, 0, 0];

    /// <summary>The power-off (JonsboMs9132Device.cs:363).</summary>
    public static ReadOnlySpan<byte> PowerOff => [0xA6, 0x07, 0, 0, 0, 0, 0, 0];

    /// <summary>The mode set for a <paramref name="width"/> × <paramref name="height"/> panel at mode index <paramref name="vic"/>, ending at "accept frames" (JonsboMs9132Device.cs:326-339).</summary>
    public static IReadOnlyList<byte[]> SetMode(int width, int height, byte vic)
    {
        byte wHi = (byte)(width >> 8), wLo = (byte)(width & 0xFF);
        byte hHi = (byte)(height >> 8), hLo = (byte)(height & 0xFF);
        return
        [
            PanelStatus.ToArray(),
            [0xB5, 0xF4, 0x39, 0, 0, 0, 0, 0],
            [0xA6, 0x03, 0x03, 0, 0, 0, 0, 0],
            [0xB5, 0xC5, 0x58, 0, 0, 0, 0, 0],
            [0xA6, 0x01, wHi, wLo, hHi, hLo, 0x11, 0x00], // in: RGB888
            [0xB5, 0xC5, 0x56, 0, 0, 0, 0, 0],
            [0xA6, 0x02, vic, 0x00, wHi, wLo, hHi, hLo], // out: the mode index
            [0xB5, 0xC5, 0x57, 0, 0, 0, 0, 0],
            [0xA6, 0x04, 0x01, 0, 0, 0, 0, 0], // accept frames
        ];
    }

    /// <summary>The pause after a report, in milliseconds, from the vendor's capture (JonsboMs9132Device.cs:290-303).</summary>
    public static int DelayMs(ReadOnlySpan<byte> payload) => (payload[0], payload[1]) switch
    {
        (0xA6, 0x07) => 105, // power on and off
        (0xA6, 0x05) => 15, // video off and on
        (0xA6, 0x03) => 15,
        (0xA6, 0x01) => 15, // input info
        (0xA6, 0x02) => 25, // output info
        (0xB5, 0x00) when payload[2] == 0x32 => 108,
        _ => 3,
    };

    /// <summary>
    /// The mode index (the vendor's "VIC") of a panel <paramref name="width"/> × <paramref name="height"/>, from the vendor
    /// SDK's timing table and the VMAX's capture (JonsboMs9132Device.cs:95-109); null when it has none.
    /// </summary>
    public static byte? VicOf(int width, int height) => (width, height) switch
    {
        (480, 480) => 143,
        (240, 320) => 147,
        (320, 240) => 148,
        (240, 240) => 156,
        (480, 272) => 159,
        (360, 960) => 160,
        (376, 960) => 171,
        (320, 960) => 178,
        (600, 1600) => 177,
        _ => null,
    };

    /// <summary>
    /// A frame's header: <c>FF 00</c>, x / 16, y (big-endian), width / 16 rounded down, height with bit 15 set, big-endian
    /// (JonsboMs9132Device.cs:383-392).
    /// </summary>
    public static void Header(int width, int height, Span<byte> header)
    {
        header[0] = 0xFF;
        header[1] = 0x00;
        header[2] = 0x00; // x / 16
        header[3] = 0x00; // y
        header[4] = 0x00;
        header[5] = (byte)(width / 16);
        var flagged = height | 0x8000;
        header[6] = (byte)(flagged >> 8);
        header[7] = (byte)(flagged & 0xFF);
    }

    /// <summary>A frame on the wire: its header, the BGR888 pixels, the trailer (JonsboMs9132Device.cs:379-400).</summary>
    public static void Frame(ReadOnlySpan<byte> bgr, int width, int height, Span<byte> frame)
    {
        var pixels = width * height * 3;
        Header(width, height, frame);
        bgr[..pixels].CopyTo(frame[Ms9132Protocol.HeaderLength..]);
        Ms9132Protocol.Trailer.CopyTo(frame[(Ms9132Protocol.HeaderLength + pixels)..]);
    }
}

/// <summary>
/// The VMAX 4.6"'s sequences on the MS9132, InfoPanel 1.4's (VmaxUsbDevice.cs, from a capture of the vendor's app): its
/// streaming start, its frame's fixed header, its status poll, and its screen switch.
/// </summary>
internal static class VmaxMs9132
{
    /// <summary>A frame's bulk writes' timeout (VmaxUsbDevice.cs:89).</summary>
    public const int WriteTimeoutMs = 1000;

    /// <summary>The pauses around the display enable at the streaming start (VmaxUsbDevice.cs:91,233,240).</summary>
    public const int StreamingDelayMs = 25;

    /// <summary>The pulses after the start's block, and the pause after each (VmaxUsbDevice.cs:226-230).</summary>
    public const int Pulses = 42;

    /// <inheritdoc cref="Pulses"/>
    public const int PulseGapMs = 2;

    /// <summary>How often the status is polled after a frame, at most (VmaxUsbDevice.cs:381).</summary>
    public static readonly TimeSpan StatusEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>The frame's header, fixed at the 4.6"'s 320 × 960 (VmaxUsbDevice.cs:15).</summary>
    public static ReadOnlySpan<byte> Prefix => [0xFF, 0x00, 0x00, 0x00, 0x00, 0x14, 0x03, 0xC0];

    /// <summary>The status (VmaxUsbDevice.cs:17).</summary>
    public static ReadOnlySpan<byte> Status => [0xB5, 0x00, 0x32, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>The pulse (VmaxUsbDevice.cs:18).</summary>
    public static ReadOnlySpan<byte> Pulse => [0xB5, 0xC5, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>The display enable (VmaxUsbDevice.cs:19).</summary>
    public static ReadOnlySpan<byte> DisplayEnable => [0xA6, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>The streaming start's block: the power-up, registers, the EDID read and the mode set at 320 × 960, mode index 178 (VmaxUsbDevice.cs:20-86).</summary>
    public static IReadOnlyList<byte[]> Startup { get; } =
    [
        [0xB5, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0x00, 0x31, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0x00, 0x30, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xF2, 0x42, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xF4, 0x39, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xF5, 0x00, 0x1F, 0xE0, 0x00, 0x00, 0x00, 0x00],
        [0xF5, 0x00, 0x1F, 0xE8, 0x00, 0x00, 0x00, 0x00],
        [0xF5, 0x00, 0x1F, 0xF0, 0x00, 0x00, 0x00, 0x00],
        [0xF5, 0x00, 0x1F, 0xF8, 0x00, 0x00, 0x00, 0x00],
        [0xA6, 0x07, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xC4, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xA6, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xC5, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB6, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xC5, 0xB0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xC6, 0xB0, 0xDF, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xC5, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xC6, 0xA0, 0xE4, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xC5, 0xA0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xC6, 0xA0, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xF0, 0x16, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB6, 0xF0, 0x16, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0x00, 0x32, 0x00, 0x00, 0x00, 0x00, 0x00],
        .. Ms9132Protocol.EdidReads,
        [0xB5, 0xF4, 0x39, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xA6, 0x03, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xC5, 0x58, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xA6, 0x01, 0x01, 0x40, 0x03, 0xC0, 0x11, 0x00],
        [0xB5, 0xC5, 0x56, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xA6, 0x02, 0xB2, 0x00, 0x01, 0x40, 0x03, 0xC0],
        [0xB5, 0xC5, 0x57, 0x00, 0x00, 0x00, 0x00, 0x00],
    ];

    /// <summary>The pause after a report of the start's block or the screen switch, in milliseconds (VmaxUsbDevice.cs:272-293).</summary>
    public static int DelayMs(ReadOnlySpan<byte> payload) =>
        payload.Length < 3 ? 1 : (payload[0], payload[1], payload[2]) switch
        {
            (0xA6, 0x07, 0x01) => 105,
            (0xA6, 0x05, 0x00) => 15,
            (0xB5, 0x00, 0x32) => 108,
            _ => 1,
        };

    /// <summary>The screen switch, on or off (VmaxUsbDevice.cs:367-373).</summary>
    public static IReadOnlyList<byte[]> ScreenSwitch(bool on) =>
    [
        [0xB5, 0xF0, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB6, 0xF0, 0x05, on ? (byte)0x10 : (byte)0x00, 0x00, 0x00, 0x00, 0x00],
        [0xA6, 0x05, on ? (byte)0x01 : (byte)0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
        [0xB5, 0xC5, 0x55, 0x00, 0x00, 0x00, 0x00, 0x00],
    ];

    /// <summary>A frame on the wire: the fixed header, the BGR888 pixels, the trailer (VmaxUsbDevice.cs:314-322).</summary>
    public static void Frame(ReadOnlySpan<byte> bgr, Span<byte> frame)
    {
        Prefix.CopyTo(frame);
        bgr.CopyTo(frame[Ms9132Protocol.HeaderLength..]);
        Ms9132Protocol.Trailer.CopyTo(frame[(Ms9132Protocol.HeaderLength + bgr.Length)..]);
    }
}

/// <summary>
/// EDID block 0 as the MS9132 reads it from its panel, parsed as 1.4 parsed it (JonsboMs9132Device.cs:231-266): its
/// preferred timing's active size, the monitor name descriptor, and the pixel clock. Null from <see cref="Parse"/> when
/// its header isn't recognised (bytes 1, 2 and 6 <c>FF</c>, byte 7 <c>00</c>, as 1.4 checked).
/// </summary>
internal sealed record Edid(byte[] Block, int Width, int Height, string? Name, double PixelClockMHz)
{
    /// <summary>Whether the preferred timing gives a size (both sides over 0).</summary>
    public bool HasSize => Width > 0 && Height > 0;

    public static Edid? Parse(ReadOnlySpan<byte> block)
    {
        if (block.Length < Ms9132Protocol.EdidLength)
            return null;
        if (block[1] != 0xFF || block[2] != 0xFF || block[6] != 0xFF || block[7] != 0x00)
            return null;
        // The preferred detailed timing at 0x36: the active width and height, each a low byte and the high bits of a shared byte.
        var width = ((block[0x36 + 4] & 0xF0) << 4) | block[0x36 + 2];
        var height = ((block[0x36 + 7] & 0x30) << 4) | block[0x36 + 5];
        string? name = null;
        // Descriptors 2 to 4 (0x48, 0x5A, 0x6C): tag 0xFC is the monitor name.
        for (var offset = 0x48; offset <= 0x6C; offset += 18)
        {
            if (block[offset] != 0 || block[offset + 1] != 0 || block[offset + 3] != 0xFC)
                continue;
            name = Encoding.ASCII.GetString(block.Slice(offset + 5, 13)).Split('\n')[0].Trim();
            break;
        }
        var clock = ((block[0x37] << 8) | block[0x36]) / 100.0;
        return new Edid(block[..Ms9132Protocol.EdidLength].ToArray(), width, height, name, clock);
    }

    /// <summary>What 1.4 logged of it (JonsboMs9132Device.cs:260-265): its size, clock and name, then the whole block in hexadecimal.</summary>
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"EDID {Width}x{Height} at {PixelClockMHz:F2} MHz, panel name {Name ?? "(none)"}; block 0: {Convert.ToHexString(Block)}");
}
