using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.ThermaltakePanel
{
    public static class ThermaltakePanelModelDatabase
    {
        public const int THERMALTAKE_VENDOR_ID = 0x264A;
        public const int THERMALTAKE_PRODUCT_ID_6INCH = 0x2347;

        public static readonly (int Vid, int Pid)[] SupportedDevices =
        [
            (THERMALTAKE_VENDOR_ID, THERMALTAKE_PRODUCT_ID_6INCH),
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
        };

        public static ThermaltakePanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
