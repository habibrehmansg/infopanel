using System.Text;

namespace InfoPanel.Core.Tests.Panels.BeadaPanel;

/// <summary>
/// BeadaPanel messages worked out by hand from InfoPanel 1.4's code (the <c>1.4.x</c> branch), so the tests compare
/// bytes against vectors rather than against the code under test:
/// <list type="bullet">
/// <item>STATUS-LINK: <c>InfoPanel/BeadaPanel/StatusLink/StatusLinkMessage.cs:20-48</c>: "STATUS-LINK" at 0–10,
/// version 1 at 11, type at 12, 0 at 13, sequence 0 (u16) at 14, total length (u16 LE) at 16, checksum (u16 LE) over
/// bytes 0–17 at 18, payload from 20. Types: <c>StatusLinkMessageType.cs:11-16</c>.</item>
/// <item>PANEL-LINK: <c>InfoPanel/BeadaPanel/PanelLink/PanelLinkMessage.cs:26-51</c>: 270 bytes, "PANEL-LINK" at 0–9,
/// version 1 at 10, type at 11, the format string from 12 (types 1 and 5), checksum over 0–267 at 268. Types:
/// <c>PanelLinkMessageType.cs:11-16</c>. Format strings: <c>PanelLinkMessage.cs:53-63</c>.</item>
/// <item>Checksum: <c>StatusLinkMessage.cs:50-65</c>: the sum of little-endian words, folded twice, inverted.</item>
/// </list>
/// The checksums, by hand. STATUS-LINK's words over bytes 0–17: 5453 + 5441 + 5355 + 4C2D + 4E49 + 014B ("K", version
/// 1) = 197AA; then (type, 0) and the length. GetPanelInfo: + 0001 + 0014 = 197BF, folded 97C0, inverted 683F → 3F 68.
/// PanelLinkReset: + 0002 + 0014 = 197C0 → 97C1 → 683E → 3E 68. SetBacklight (21 bytes): + 0003 + 0015 = 197C2 → 97C3
/// → 683C → 3C 68 (the payload isn't summed). PANEL-LINK without a format: 4150 + 454E + 2D4C + 494C + 4B4E = 14884,
/// then (1, type): EndMediaStream + 0601 = 14E85 → 4E86 → B179 → 79 B1; LegacyCommand2 + 0201 = 14A85 → 4A86 → B579
/// → 79 B5. With a format string the string's words join the sum; those four checksums were worked the same way and
/// cross-checked with an independent script.
/// </summary>
internal static class BeadaPanelVectors
{
    public static readonly byte[] GetPanelInfo =
        [0x53, 0x54, 0x41, 0x54, 0x55, 0x53, 0x2D, 0x4C, 0x49, 0x4E, 0x4B, 0x01, 0x01, 0x00, 0x00, 0x00, 0x14, 0x00, 0x3F, 0x68];

    public static readonly byte[] PanelLinkReset =
        [0x53, 0x54, 0x41, 0x54, 0x55, 0x53, 0x2D, 0x4C, 0x49, 0x4E, 0x4B, 0x01, 0x02, 0x00, 0x00, 0x00, 0x14, 0x00, 0x3E, 0x68];

    /// <summary>SetBacklight with its one payload byte.</summary>
    public static byte[] SetBacklight(byte value) =>
        [0x53, 0x54, 0x41, 0x54, 0x55, 0x53, 0x2D, 0x4C, 0x49, 0x4E, 0x4B, 0x01, 0x03, 0x00, 0x00, 0x00, 0x15, 0x00, 0x3C, 0x68, value];

    public const string WriteThrough800x480 = "image/x-raw, format=BGR16, width=800, height=480, framerate=0/1";
    public const string Reported480x320 = "video/x-raw, format=RGB16, width=480, height=320, framerate=0/1";

    /// <summary>A PANEL-LINK message from its parts: the header, the type, the format string's ASCII and the checksum.</summary>
    public static byte[] PanelLink(byte type, string format, byte checksumLow, byte checksumHigh)
    {
        var bytes = new byte[270];
        Encoding.ASCII.GetBytes("PANEL-LINK").CopyTo(bytes, 0);
        bytes[10] = 0x01;
        bytes[11] = type;
        Encoding.ASCII.GetBytes(format).CopyTo(bytes, 12);
        bytes[268] = checksumLow;
        bytes[269] = checksumHigh;
        return bytes;
    }

    public static byte[] EndMediaStream => PanelLink(6, "", 0x79, 0xB1);

    public static byte[] LegacyCommand2 => PanelLink(2, "", 0x79, 0xB5);

    public static byte[] StartMediaStream800x480 => PanelLink(5, WriteThrough800x480, 0xE3, 0x10);

    public static byte[] LegacyCommand1_800x480 => PanelLink(1, WriteThrough800x480, 0xE3, 0x14);

    public static byte[] StartMediaStream480x320 => PanelLink(5, Reported480x320, 0xC7, 0x1B);

    public static byte[] LegacyCommand1_480x320 => PanelLink(1, Reported480x320, 0xC7, 0x1F);

    /// <summary>
    /// A GetPanelInfo reply (<c>InfoPanel/BeadaPanel/BeadaPanelParser.cs:16-87</c>): the 20-byte header with type 1, then
    /// firmware (u16 LE) at payload 0, PanelLink version 2, StatusLink version 3, platform 4, model code 5, the serial's
    /// ASCII at 6–69, resolution X and Y (u16 LE) at 70 and 72, storage KB (u32 LE) at 74, max and current brightness at
    /// 78 and 79.
    /// </summary>
    public static byte[] InfoReply(byte panelLink, byte platform, byte modelCode, ushort resX, ushort resY,
        string serial = "A1B2C3", ushort firmware = 0x0102, byte statusLink = 1)
    {
        var r = new byte[100];
        Encoding.ASCII.GetBytes("STATUS-LINK").CopyTo(r, 0);
        r[11] = 0x01;
        r[12] = 0x01;
        r[20] = (byte)firmware;
        r[21] = (byte)(firmware >> 8);
        r[22] = panelLink;
        r[23] = statusLink;
        r[24] = platform;
        r[25] = modelCode;
        Encoding.ASCII.GetBytes(serial).CopyTo(r, 26);
        r[90] = (byte)resX;
        r[91] = (byte)(resX >> 8);
        r[92] = (byte)resY;
        r[93] = (byte)(resY >> 8);
        r[94] = 0x00;
        r[95] = 0x10; // 4096 KB
        r[98] = 100;
        r[99] = 80;
        return r;
    }
}
