using System.Collections.Generic;

namespace InfoPanel.ThermalrightPanel
{
    public static class ThermalrightPanelModelDatabase
    {
        public const int THERMALRIGHT_VENDOR_ID = 0x87AD;
        public const int THERMALRIGHT_PRODUCT_ID = 0x70DB;

        // Device identifiers returned in init response
        public const string IDENTIFIER_V1 = "SSCRM-V1"; // Peerless Vision 360 (480x480)
        public const string IDENTIFIER_V3 = "SSCRM-V3"; // Wonder Vision 360 (1600x720)

        public static readonly Dictionary<ThermalrightPanelModel, ThermalrightPanelModelInfo> Models = new()
        {
            [ThermalrightPanelModel.PeerlessVision360] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.PeerlessVision360,
                Name = "Peerless Vision 360",
                DeviceIdentifier = IDENTIFIER_V1,
                Width = 480,
                Height = 480,
                RenderWidth = 480,
                RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID
            },
            [ThermalrightPanelModel.WonderVision360] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.WonderVision360,
                Name = "Wonder Vision 360",
                DeviceIdentifier = IDENTIFIER_V3,
                Width = 2400,
                Height = 1080,
                RenderWidth = 1600,  // TRCC uses 1600x720
                RenderHeight = 720,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID
            }
        };

        public static ThermalrightPanelModelInfo? GetModelByVidPid(int vid, int pid)
        {
            foreach (var model in Models.Values)
            {
                if (model.VendorId == vid && model.ProductId == pid)
                    return model;
            }
            return null;
        }

        /// <summary>
        /// Get model info by device identifier string (e.g., "SSCRM-V1", "SSCRM-V3")
        /// </summary>
        public static ThermalrightPanelModelInfo? GetModelByIdentifier(string identifier)
        {
            foreach (var model in Models.Values)
            {
                if (model.DeviceIdentifier == identifier)
                    return model;
            }
            return null;
        }
    }
}
