// V1 oracle, test-only: the model tables of InfoPanel 1.4.x c71f0f00, copied verbatim from the files and lines cited
// above each part (the namespaces folded into this one): InfoPanel/JonsboPanel/JonsboPanelModel.cs,
// JonsboPanelModelInfo.cs and JonsboPanelModelDatabase.cs; InfoPanel/VmaxPanel/VmaxPanelModel.cs, VmaxPanelModelInfo.cs
// and VmaxPanelModelDatabase.cs.
namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle
{
    // JonsboPanelModel.cs:3-14
    public enum JonsboPanelModel
    {
        Unknown,
        DS916,          // VID 0x33C3, PID 0xF101, 462x1920 native portrait ("VMAXB16" identity), CDC raw-JPEG
        DS339,          // VID 0x345F, PID 0x9132 (MacroSilicon MS9132), 376x960 native portrait, RGB888 USB display
    }

    public enum JonsboTransportType
    {
        Serial,         // HLVMAX CDC ACM COM port, raw JPEG frames
        Ms9132,         // MacroSilicon MS9132: HID control plane + bulk RGB888 frames
    }

    // JonsboPanelModelInfo.cs:3-25
    public class JonsboPanelModelInfo
    {
        public JonsboPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }
        public JonsboTransportType TransportType { get; init; } = JonsboTransportType.Serial;
        public byte Vic { get; init; }
        public int MaxJpegBytes { get; init; } = 256 * 1024;
        public int DefaultFrameRate { get; init; } = 25;
    }

    // JonsboPanelModelDatabase.cs:14-63
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

    // VmaxPanelModel.cs:3-7
    public enum VmaxPanelModel
    {
        Unknown,
        Vmax46Inch,
    }

    // VmaxPanelModelInfo.cs:3-11
    public class VmaxPanelModelInfo
    {
        public VmaxPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }
    }

    // VmaxPanelModelDatabase.cs:6-33
    public static class VmaxPanelModelDatabase
    {
        public const int VMAX_VENDOR_ID = 0x345F;
        public const int VMAX_PRODUCT_ID_46INCH = 0x9132;

        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (VMAX_VENDOR_ID, VMAX_PRODUCT_ID_46INCH),
        ];

        public static readonly Dictionary<VmaxPanelModel, VmaxPanelModelInfo> Models = new()
        {
            [VmaxPanelModel.Vmax46Inch] = new VmaxPanelModelInfo
            {
                Model = VmaxPanelModel.Vmax46Inch,
                Name = "VMAX 4.6\" LCD",
                Width = 320,
                Height = 960,
                VendorId = VMAX_VENDOR_ID,
                ProductId = VMAX_PRODUCT_ID_46INCH,
            },
        };
    }
}
