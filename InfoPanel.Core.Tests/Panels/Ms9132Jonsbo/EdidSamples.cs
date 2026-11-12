using System.Text;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// EDID blocks for the MS9132's tests. 1.4.x has no captured EDID, so these are synthetic: valid EDID 1.3 block 0s (the
/// header, a manufacturer id of three letters, the version, a digital input, the preferred detailed timing at 0x36 with
/// its sync inside its blanking, the monitor name descriptor, the checksum) built from the values 1.4.x names: the Jonsbo
/// DS339's 376 × 960 at 29.16 MHz named <c>VMAXB01</c> (1.4.x's <c>0f81b8f</c>, from the vendor app's capture), the VMAX
/// 4.6"'s 320 × 960 (its mode index 178, JonsboMs9132Device.cs:91-104), and sizes 1.4.x's mode table has or hasn't.
/// Blocks that aren't valid are in <see cref="Malformed"/>.
/// </summary>
internal static class EdidSamples
{
    /// <summary>A block 0: its header, a timing of <paramref name="width"/> × <paramref name="height"/> at <paramref name="clock10kHz"/>, and <paramref name="name"/> (none: no name descriptor) at <paramref name="nameAt"/>.</summary>
    public static byte[] Block(int width, int height, int clock10kHz = 2916, string? name = null, int nameAt = 0x48)
    {
        var edid = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        // The manufacturer id "SYN" (a synthetic block), three letters of 5 bits each, A = 1, big-endian; the parser
        // doesn't read it, nor the rest up to the timing.
        var manufacturer = (('S' - 'A' + 1) << 10) | (('Y' - 'A' + 1) << 5) | ('N' - 'A' + 1);
        edid[8] = (byte)(manufacturer >> 8);
        edid[9] = (byte)(manufacturer & 0xFF);
        edid[18] = 1; // EDID 1.3
        edid[19] = 3;
        edid[20] = 0x80; // a digital input
        edid[0x36] = (byte)(clock10kHz & 0xFF);
        edid[0x37] = (byte)(clock10kHz >> 8);
        edid[0x38] = (byte)(width & 0xFF);
        edid[0x39] = 40; // horizontal blanking
        edid[0x3A] = (byte)(((width >> 8) & 0x0F) << 4);
        edid[0x3B] = (byte)(height & 0xFF);
        edid[0x3C] = 20; // vertical blanking
        edid[0x3D] = (byte)(((height >> 8) & 0x0F) << 4);
        edid[0x3E] = 8; // horizontal sync: 8 after the active pixels, 16 wide (inside its 40 of blanking)
        edid[0x3F] = 16;
        edid[0x40] = (4 << 4) | 4; // vertical sync: 4 lines after, 4 high (inside its 20)
        edid[0x41] = 0; // their high bits
        edid[0x47] = 0x1E; // digital separate sync, both polarities positive
        if (name != null)
        {
            edid[nameAt + 3] = 0xFC;
            var text = Encoding.ASCII.GetBytes(name.Length < 13 ? name + "\n" : name);
            for (var i = 0; i < 13; i++)
                edid[nameAt + 5 + i] = i < text.Length ? text[i] : (byte)0x20;
        }
        var sum = 0;
        for (var i = 0; i < 127; i++)
            sum += edid[i];
        edid[127] = (byte)(256 - sum % 256);
        return edid;
    }

    /// <summary>The Jonsbo DS339's: 376 × 960 at 29.16 MHz, named <c>VMAXB01</c>.</summary>
    public static byte[] Ds339 => Block(376, 960, 2916, "VMAXB01");

    /// <summary>The VMAX 4.6"'s: 320 × 960 (its name isn't known: none).</summary>
    public static byte[] Vmax => Block(320, 960, 2500);

    /// <summary>A panel neither family has: 480 × 480, a size 1.4.x's mode table has (143), named in the third descriptor.</summary>
    public static byte[] Unknown => Block(480, 480, 1800, "PANEL480", nameAt: 0x5A);

    /// <summary>Blocks that aren't valid EDIDs, kept apart from the valid ones: the parser's edge cases.</summary>
    public static class Malformed
    {
        /// <summary>The DS339's block with byte 7 of its header wrong (1.4.x's check ignores it).</summary>
        public static byte[] BadHeader
        {
            get
            {
                var edid = Ds339;
                edid[7] = 0x01;
                return edid;
            }
        }

        /// <summary>A preferred timing with no active width.</summary>
        public static byte[] ZeroWidth => Block(0, 960, 2916, "ZERO");
    }

    /// <summary>
    /// The 4-byte reply data an MS9132 gives an EDID read of <paramref name="edid"/> (<c>B5 C0 xx</c>): the report id, the
    /// echoed opcode and address, the 4 bytes at xx and a fifth (JonsboMs9132Device.cs:285-286).
    /// </summary>
    public static byte[] Reply(ReadOnlySpan<byte> report9, byte[]? edid)
    {
        var reply = new byte[9];
        report9[..4].CopyTo(reply);
        if (edid != null && report9[1] == 0xB5 && report9[2] == 0xC0 && report9[3] + 4 <= 128)
            edid.AsSpan(report9[3], 4).CopyTo(reply.AsSpan(4));
        return reply;
    }
}
