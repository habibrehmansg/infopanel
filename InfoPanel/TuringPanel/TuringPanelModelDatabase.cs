using Flurl.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace InfoPanel.TuringPanel
{
    public static class TuringPanelModelDatabase
    {
        public static readonly Dictionary<TuringPanelModel, TuringPanelModelInfo> Models = new()
        {
            [TuringPanelModel.TURING_3_5] = new TuringPanelModelInfo { Model = TuringPanelModel.TURING_3_5, Name = "Turing Smart Screen 3.5\"", Width = 320, Height = 480, VendorId = 0x1a86, ProductId = 0x5722 },
            [TuringPanelModel.XUANFANG_3_5] = new TuringPanelModelInfo { Model = TuringPanelModel.XUANFANG_3_5, Name = "XuanFang 3.5\"", Width = 320, Height = 480, VendorId = 0x1a86, ProductId = 0x5722 },
            [TuringPanelModel.REV_2INCH] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_2INCH, Name = "Turing Smart Screen 2.1\"", Width = 480, Height = 480, VendorId = 0x1d6b, ProductId = 0x0121 },
            [TuringPanelModel.REV_5INCH] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_5INCH, Name = "Turing Smart Screen 5\"", Width = 800, Height = 480, VendorId = 0x1d6b, ProductId = 0x0106 },
            [TuringPanelModel.REV_8INCH] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_8INCH, Name = "Turing Smart Screen 8\" Rev 1.0", Width = 480, Height = 1920, VendorId = 0x0525, ProductId = 0xa4a7 },
            [TuringPanelModel.REV_8INCH_USB] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_8INCH_USB, Name = "Turing Smart Screen 8\" Rev 1.1", Width = 480, Height = 1920, VendorId = 0x1cbe, ProductId = 0x0088, IsUsbDevice = true }
        };

        public static bool TryGetModelInfo(int vendorId, int productId, bool isUsbDevice, out TuringPanelModelInfo modelInfo)
        {
            modelInfo = Models.Values.FirstOrDefault(m => m.VendorId == vendorId && m.ProductId == productId && m.IsUsbDevice == isUsbDevice)!;
            return modelInfo != null;
        }
    }
}
