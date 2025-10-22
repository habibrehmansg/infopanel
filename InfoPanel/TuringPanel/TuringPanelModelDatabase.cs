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
            [TuringPanelModel.REV_8INCH] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_8INCH, Name = "Turing Smart Screen 8.8\" Rev 1.0", Width = 480, Height = 1920, VendorId = 0x0525, ProductId = 0xa4a7 },
            [TuringPanelModel.REV_88INCH_USB] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_88INCH_USB, Name = "Turing Smart Screen 8.8\" Rev 1.1", Width = 480, Height = 1920, VendorId = 0x1cbe, ProductId = 0x0088, IsUsbDevice = true },
            [TuringPanelModel.REV_8INCH_USB] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_8INCH_USB, Name = "Turing Smart Screen 8\"", Width = 800, Height = 1280, VendorId = 0x1cbe, ProductId = 0x0080, IsUsbDevice = true },
            [TuringPanelModel.REV_92INCH_USB] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_92INCH_USB, Name = "Turing Smart Screen 9.2\"", Width = 464, Height = 1920, VendorId = 0x1cbe, ProductId = 0x0092, IsUsbDevice = true },
            [TuringPanelModel.REV_5INCH_USB] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_5INCH_USB, Name = "Turing Smart Screen 5\"", Width = 720, Height = 1280, VendorId = 0x1cbe, ProductId = 0x0050, IsUsbDevice = true },
            [TuringPanelModel.REV_16INCH_USB] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_16INCH_USB, Name = "Turing Smart Screen 1.6\"", Width = 400, Height = 400, VendorId = 0x1cbe, ProductId = 0x0016, IsUsbDevice = true },
            [TuringPanelModel.REV_21INCH_USB] = new TuringPanelModelInfo { Model = TuringPanelModel.REV_21INCH_USB, Name = "Turing Smart Screen 2.1\"", Width = 480, Height = 480, VendorId = 0x1cbe, ProductId = 0x0021, IsUsbDevice = true },
        };

        public static bool TryGetModelInfo(int vendorId, int productId, bool isUsbDevice, out TuringPanelModelInfo modelInfo)
        {
            modelInfo = Models.Values.FirstOrDefault(m => m.VendorId == vendorId && m.ProductId == productId && m.IsUsbDevice == isUsbDevice)!;
            return modelInfo != null;
        }
    }
}
