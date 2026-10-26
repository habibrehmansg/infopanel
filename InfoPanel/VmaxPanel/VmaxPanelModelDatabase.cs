using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.VmaxPanel
{
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

        public static VmaxPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vid && m.ProductId == pid);
        }
    }
}
