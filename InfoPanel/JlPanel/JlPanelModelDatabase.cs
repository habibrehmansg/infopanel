using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.JlPanel
{
    /// <summary>
    /// Jungle Leopard / Hongtai-based cooler displays.
    /// Reference platform: JL Chill Arc 360 (480x960). Same firmware/protocol is shared
    /// across ~80 white-label SKUs (MSI EZ Display, Thermaltake LCD, ZOTAC, Jonsbo, ...).
    /// Transport is USB CDC ACM (virtual COM port) at 2 Mbaud.
    /// </summary>
    public static class JlPanelModelDatabase
    {
        public const int JL_VENDOR_ID = 0x33C3;
        public const int JL_PRODUCT_ID_CHILL_ARC_360 = 0x7788;

        // OEM PIDs in the 0x7791..0x7810 range share the same protocol but vary in
        // resolution. The reference firmware also has an SPI variant on 0x7793 that
        // uses RGB565 over a CH340 USB-UART; that one is intentionally NOT supported
        // here yet (different transport).
        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (JL_VENDOR_ID, JL_PRODUCT_ID_CHILL_ARC_360),
        ];

        public static readonly Dictionary<JlPanelModel, JlPanelModelInfo> Models = new()
        {
            [JlPanelModel.ChillArc360] = new JlPanelModelInfo
            {
                Model = JlPanelModel.ChillArc360,
                Name = "JL Chill Arc 360",
                Width = 480,
                Height = 960,
                VendorId = JL_VENDOR_ID,
                ProductId = JL_PRODUCT_ID_CHILL_ARC_360,
                MaxJpegBytes = 80 * 1024,
                DefaultFrameRate = 30,
            },
            [JlPanelModel.StripDisplay] = new JlPanelModelInfo
            {
                Model = JlPanelModel.StripDisplay,
                Name = "JL Strip Display",
                Width = 1920,
                Height = 462,
                VendorId = JL_VENDOR_ID,
                ProductId = JL_PRODUCT_ID_CHILL_ARC_360, // OEM SKUs vary; matched by resolution
                MaxJpegBytes = 80 * 1024,
                DefaultFrameRate = 60,
            },
        };

        public static JlPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
