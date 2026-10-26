using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InfoPanel.LianLiPanel
{
    public static partial class LianLiPanelModelDatabase
    {
        public const int LianLiVendorId = 0x1cbe;

        public static readonly Dictionary<LianLiPanelModel, LianLiPanelModelInfo> Models = new()
        {
            [LianLiPanelModel.UniversalScreen88Inch] = new LianLiPanelModelInfo
            {
                Model = LianLiPanelModel.UniversalScreen88Inch,
                Name = "Lian Li Universal Screen 8.8\"",
                Width = 480,
                Height = 1920,
                VendorId = LianLiVendorId,
                ProductId = 0xa088,
            },
            [LianLiPanelModel.UniversalScreen92Inch] = new LianLiPanelModelInfo
            {
                Model = LianLiPanelModel.UniversalScreen92Inch,
                Name = "Lian Li Universal Screen 9.2\"",
                Width = 464,
                Height = 1920,
                VendorId = LianLiVendorId,
                ProductId = 0xa092,
            },
            [LianLiPanelModel.HydroShiftIIOledCurve] = new LianLiPanelModelInfo
            {
                Model = LianLiPanelModel.HydroShiftIIOledCurve,
                Name = "Lian Li HydroShift II OLED Curve",
                Width = 2288,
                Height = 1080,
                VendorId = LianLiVendorId,
                ProductId = 0xa068,
            },
            [LianLiPanelModel.HydroShiftIILcd] = new LianLiPanelModelInfo
            {
                Model = LianLiPanelModel.HydroShiftIILcd,
                Name = "Lian Li HydroShift II LCD",
                Width = 480,
                Height = 480,
                VendorId = LianLiVendorId,
                ProductId = 0xa034,
            },
        };

        public static LianLiPanelModelInfo? GetModelByVidPid(int vendorId, int productId)
        {
            return Models.Values.FirstOrDefault(m => m.VendorId == vendorId && m.ProductId == productId);
        }

        public static LianLiPanelModelInfo? GetModelByDeviceId(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            var match = VidPidRegex().Match(deviceId);
            if (!match.Success)
            {
                return null;
            }

            var vendorId = int.Parse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
            var productId = int.Parse(match.Groups[2].Value, System.Globalization.NumberStyles.HexNumber);
            return GetModelByVidPid(vendorId, productId);
        }

        public static bool IsLianLiDeviceId(string? deviceId)
        {
            return GetModelByDeviceId(deviceId) != null;
        }

        [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
        private static partial Regex VidPidRegex();
    }
}
