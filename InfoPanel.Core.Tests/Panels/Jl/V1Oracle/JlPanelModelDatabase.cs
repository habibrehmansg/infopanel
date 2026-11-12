// V1 oracle, test-only: InfoPanel 1.4.x c71f0f00's JL model table, copied verbatim from InfoPanel/JlPanel/JlPanelModel.cs:3-8
// (the enum), JlPanelModelInfo.cs:3-17 and JlPanelModelDatabase.cs:12-56 (the table and its lookup), in one file, under the
// oracle's namespace.
namespace InfoPanel.Core.Tests.Panels.Jl.V1Oracle
{
    // JlPanelModel.cs:3-8
    public enum JlPanelModel
    {
        Unknown,
        ChillArc360,        // VID 0x33C3, PID 0x7788, 480x960 CDC JPEG
        StripDisplay,       // VID 0x33C3, PID 0x7791..0x7810, 1920x462 CDC JPEG (high frame rate)
    }

    // JlPanelModelInfo.cs:3-17
    public class JlPanelModelInfo
    {
        public JlPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }

        /// <summary>Soft cap on JPEG payload size in bytes (~80 KB for Chill Arc 360).</summary>
        public int MaxJpegBytes { get; init; } = 80 * 1024;

        /// <summary>Default frame rate for this panel (60 for strip, 30 otherwise per spec).</summary>
        public int DefaultFrameRate { get; init; } = 30;
    }

    // JlPanelModelDatabase.cs:12-56
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
