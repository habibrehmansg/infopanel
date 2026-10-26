using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.JonsboPanel
{
    /// <summary>
    /// Jonsbo displays ("HLVMAX" / Artinchip family).
    /// Reference platform: Jonsbo DS916 (9.16", 462x1920 native portrait).
    /// Transport is USB CDC ACM (virtual COM port); the line coding is ignored by the
    /// firmware — frames are plain bulk writes. Protocol reverse-engineered from the
    /// OEM JONSBO-AIO app + USB capture (see JONSBO-PROTOCOL.md):
    ///   handshake F0 A5 5A 0F -> 26-byte ASCII identity, then raw JPEG per frame.
    /// </summary>
    public static class JonsboPanelModelDatabase
    {
        public const int JONSBO_VENDOR_ID = 0x33C3;   // Artinchip Technology (shared with Hongtai/JL SKUs)
        public const int JONSBO_PRODUCT_ID_DS916 = 0xF101;

        public const int MS9132_VENDOR_ID = 0x345F;   // MacroSilicon
        public const int MS9132_PRODUCT_ID = 0x9132;

        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (JONSBO_VENDOR_ID, JONSBO_PRODUCT_ID_DS916),
            (MS9132_VENDOR_ID, MS9132_PRODUCT_ID),
        ];

        public static readonly Dictionary<JonsboPanelModel, JonsboPanelModelInfo> Models = new()
        {
            [JonsboPanelModel.DS916] = new JonsboPanelModelInfo
            {
                Model = JonsboPanelModel.DS916,
                Name = "Jonsbo DS916",
                Width = 462,     // native portrait; identity string overrides at runtime
                Height = 1920,
                VendorId = JONSBO_VENDOR_ID,
                ProductId = JONSBO_PRODUCT_ID_DS916,
                TransportType = JonsboTransportType.Serial,
                MaxJpegBytes = 256 * 1024,
                DefaultFrameRate = 25,
            },
            // DS339: MacroSilicon MS9132 USB display bridge driving a 376x960 portrait panel.
            // Mode-set uses the Jonsbo custom VIC 171 (from the OEM app's EDID timing table),
            // frames are raw BGR888 (verified against a USB capture of the OEM app).
            [JonsboPanelModel.DS339] = new JonsboPanelModelInfo
            {
                Model = JonsboPanelModel.DS339,
                Name = "Jonsbo DS339",
                Width = 376,     // native portrait
                Height = 960,
                VendorId = MS9132_VENDOR_ID,
                ProductId = MS9132_PRODUCT_ID,
                TransportType = JonsboTransportType.Ms9132,
                Vic = 171,
                DefaultFrameRate = 15,
            },
        };

        public static JonsboPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
