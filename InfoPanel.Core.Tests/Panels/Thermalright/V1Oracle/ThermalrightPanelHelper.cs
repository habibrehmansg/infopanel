// V1 oracle, test-only: the model choices of InfoPanel 1.4.x c71f0f00, InfoPanel/ThermalrightPanel/ThermalrightPanelHelper.cs
// (the scan), copied verbatim from the lines cited above each part, but for these changes: log lines are left out and a
// probe's reply is passed in as (response, bytesRead) in place of the USB read.
using System.Text;

namespace InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle
{
    public static class V1PanelHelper
    {
        // :136-146, the model a HID device starts with.
        public static ThermalrightPanelModelInfo? HidScanModel(int vendorId, int productId)
        {
            var modelInfo = ThermalrightPanelModelDatabase.GetModelByVidPid(vendorId, productId);
            if (modelInfo == null)
            {
                modelInfo = ThermalrightPanelModelDatabase.Models.Values
                    .FirstOrDefault(m => m.VendorId == vendorId && m.ProductId == productId);
            }
            return modelInfo;
        }

        // :348-381, the ChiZhu probe's reply.
        public static ThermalrightPanelModelInfo? ProbeWinUsbModel(byte[] response, int bytesRead)
        {
            if (bytesRead < 12)
            {
                return null;
            }

            if (bytesRead >= 8 &&
                response[4] == 0xA1 && response[5] == 0xA2 &&
                response[6] == 0xA3 && response[7] == 0xA4)
            {
                return null;
            }

            byte? pm = bytesRead >= 25 ? response[24] : null;
            byte? sub = bytesRead >= 29 ? response[28] : null;

            if (pm.HasValue && sub.HasValue)
            {
                var model = ThermalrightPanelModelDatabase.GetModelByChiZhuPM(pm.Value, sub.Value);
                if (model != null) return model;
            }

            var identifier = Encoding.ASCII.GetString(response, 4, 8).TrimEnd('\0');
            return ThermalrightPanelModelDatabase.GetModelByIdentifier(identifier, sub);
        }

        // :471-492, the TrofeoBulk probe's reply.
        public static ThermalrightPanelModelInfo? ProbeTrofeoBulkModel(byte[] response, int bytesRead)
        {
            if (bytesRead < 21)
            {
                return null;
            }

            byte b20 = response[20];

            var targetModel = b20 switch
            {
                0x05 => ThermalrightPanelModel.TrofeoVision113,
                >= 0x02 and <= 0x03 => ThermalrightPanelModel.TrofeoVision916V2,
                _ => ThermalrightPanelModel.TrofeoVision916,
            };

            if (ThermalrightPanelModelDatabase.Models.TryGetValue(targetModel, out var info))
                return info;
            return null;
        }
    }
}
