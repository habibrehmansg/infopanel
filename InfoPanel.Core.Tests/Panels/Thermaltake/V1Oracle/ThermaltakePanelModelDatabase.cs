// V1 oracle, test-only: InfoPanel 1.4.x c71f0f00's Thermaltake model table, copied verbatim from
// InfoPanel/ThermaltakePanel/ThermaltakePanelModel.cs:3-8 (the enum), ThermaltakePanelModelInfo.cs:3-11 and
// ThermaltakePanelModelDatabase.cs:6-46 (the table and its lookup), in one file, under the oracle's namespace.
namespace InfoPanel.Core.Tests.Panels.Thermaltake.V1Oracle
{
    // ThermaltakePanelModel.cs:3-8
    public enum ThermaltakePanelModel
    {
        Unknown,
        ToughLiquid6Inch,           // PID 0x2347, 1480x720
        AsrockPhantomGaming360LCD,  // VID 0x26CE, PID 0x0A10, 480x480
    }

    // ThermaltakePanelModelInfo.cs:3-11
    public class ThermaltakePanelModelInfo
    {
        public ThermaltakePanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }
    }

    // ThermaltakePanelModelDatabase.cs:6-46
    public static class ThermaltakePanelModelDatabase
    {
        public const int THERMALTAKE_VENDOR_ID = 0x264A;
        public const int THERMALTAKE_PRODUCT_ID_6INCH = 0x2347;

        public const int ASROCK_VENDOR_ID = 0x26CE;
        public const int ASROCK_PRODUCT_ID_PG360 = 0x0A10;

        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (THERMALTAKE_VENDOR_ID, THERMALTAKE_PRODUCT_ID_6INCH),
            (ASROCK_VENDOR_ID, ASROCK_PRODUCT_ID_PG360),
        ];

        public static readonly Dictionary<ThermaltakePanelModel, ThermaltakePanelModelInfo> Models = new()
        {
            [ThermaltakePanelModel.ToughLiquid6Inch] = new ThermaltakePanelModelInfo
            {
                Model = ThermaltakePanelModel.ToughLiquid6Inch,
                Name = "Thermaltake 6\" LCD",
                Width = 1480,
                Height = 720,
                VendorId = THERMALTAKE_VENDOR_ID,
                ProductId = THERMALTAKE_PRODUCT_ID_6INCH,
            },
            [ThermaltakePanelModel.AsrockPhantomGaming360LCD] = new ThermaltakePanelModelInfo
            {
                Model = ThermaltakePanelModel.AsrockPhantomGaming360LCD,
                Name = "ASRock Phantom Gaming 360 LCD",
                Width = 480,
                Height = 480,
                VendorId = ASROCK_VENDOR_ID,
                ProductId = ASROCK_PRODUCT_ID_PG360,
            },
        };

        public static ThermaltakePanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
