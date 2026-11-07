using System.Text;

namespace InfoPanel.Tests.Panels;

/// <summary>A BeadaPanel's GetPanelInfo reply, laid out as InfoPanel 1.4 parses it (the Core tests' vectors hold the same).</summary>
internal static class PanelReplies
{
    public static byte[] Info(byte panelLink, byte platform, byte modelCode, ushort resX, ushort resY, string serial = "A1B2C3")
    {
        var r = new byte[100];
        Encoding.ASCII.GetBytes("STATUS-LINK").CopyTo(r, 0);
        r[11] = 0x01;
        r[12] = 0x01; // GetPanelInfo
        r[20] = 0x51; // firmware 849
        r[21] = 0x03;
        r[22] = panelLink;
        r[23] = 3; // StatusLink version
        r[24] = platform;
        r[25] = modelCode;
        Encoding.ASCII.GetBytes(serial).CopyTo(r, 26);
        r[90] = (byte)resX;
        r[91] = (byte)(resX >> 8);
        r[92] = (byte)resY;
        r[93] = (byte)(resY >> 8);
        r[98] = 100;
        r[99] = 100;
        return r;
    }
}
