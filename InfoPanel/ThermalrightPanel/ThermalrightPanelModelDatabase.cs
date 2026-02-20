using System.Collections.Generic;

namespace InfoPanel.ThermalrightPanel
{
    public static class ThermalrightPanelModelDatabase
    {
        // Primary Thermalright VID/PID (most panels)
        public const int THERMALRIGHT_VENDOR_ID = 0x87AD;
        public const int THERMALRIGHT_PRODUCT_ID = 0x70DB;

        // Trofeo Vision panels
        public const int TROFEO_VENDOR_ID = 0x0416;
        public const int TROFEO_PRODUCT_ID_686 = 0x5302;  // 6.86" - HID transport
        public const int TROFEO_PRODUCT_ID_916 = 0x5408;  // 9.16" - HID transport

        // SCSI pass-through panels (Elite Vision / Frozen Warframe SCSI variant)
        public const int SCSI_VENDOR_ID = 0x0402;
        public const int SCSI_PRODUCT_ID = 0x3922;

        // HID identifier string reported by Trofeo Vision 6.86" (init response bytes 20-27)
        public const string TROFEO_686_HID_IDENTIFIER = "BP21940";

        // All supported VID/PID pairs for device scanning
        public static readonly (int Vid, int Pid)[] SupportedDevices =
        {
            (THERMALRIGHT_VENDOR_ID, THERMALRIGHT_PRODUCT_ID),
            (TROFEO_VENDOR_ID, TROFEO_PRODUCT_ID_686),
            (TROFEO_VENDOR_ID, TROFEO_PRODUCT_ID_916),
            (SCSI_VENDOR_ID, SCSI_PRODUCT_ID)
        };

        // Device identifiers returned in init response
        public const string IDENTIFIER_V1 = "SSCRM-V1"; // Grand / Hydro / Peerless Vision 240/360 (480x480)
        public const string IDENTIFIER_V3 = "SSCRM-V3"; // Wonder / Rainbow Vision 360 (2400x1080) — SUB byte differentiates

        // SUB byte (init response byte[36]) for SSCRM-V3 models
        public const byte WONDER_360_SUB_BYTE  = 0x01; // Wonder Vision 360
        public const byte RAINBOW_360_SUB_BYTE = 0x02; // Rainbow Vision 360
        public const string IDENTIFIER_V4 = "SSCRM-V4"; // TL-M10 Vision (1920x462)

        public static readonly Dictionary<ThermalrightPanelModel, ThermalrightPanelModelInfo> Models = new()
        {
            [ThermalrightPanelModel.PeerlessVision360] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.PeerlessVision360,
                Name = "Grand / Hydro / Peerless Vision 240/360",
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
                Name = "Wonder Vision 360 6.67\"",
                DeviceIdentifier = IDENTIFIER_V3,
                Width = 2400,
                Height = 1080,
                RenderWidth = 1600,  // TRCC uses 1600x720
                RenderHeight = 720,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID,
                SubByte = WONDER_360_SUB_BYTE
            },
            [ThermalrightPanelModel.RainbowVision360] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.RainbowVision360,
                Name = "Rainbow Vision 360 6.67\"",
                DeviceIdentifier = IDENTIFIER_V3,
                Width = 2400,
                Height = 1080,
                RenderWidth = 1600,  // Same panel hardware as Wonder Vision 360
                RenderHeight = 720,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID,
                SubByte = RAINBOW_360_SUB_BYTE
            },
            [ThermalrightPanelModel.TLM10Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TLM10Vision,
                Name = "TL-M10 Vision",
                DeviceIdentifier = IDENTIFIER_V4,
                Width = 1920,
                Height = 462,
                RenderWidth = 1920,
                RenderHeight = 462,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID
            },
            [ThermalrightPanelModel.TrofeoVision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision,
                Name = "Trofeo Vision 6.86\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,  // "BP21940" - from HID init response bytes 20-27
                Width = 1280,
                Height = 480,
                RenderWidth = 1280,
                RenderHeight = 480,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo
            },
            [ThermalrightPanelModel.TrofeoVision916] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision916,
                Name = "Trofeo Vision 9.16\"",
                DeviceIdentifier = "",  // Identified by unique VID/PID
                Width = 1920,
                Height = 480,
                RenderWidth = 1920,
                RenderHeight = 480,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_916,
                TransportType = ThermalrightTransportType.WinUsb,
                ProtocolType = ThermalrightProtocolType.TrofeoBulk  // 02 FF init, 4096-byte JPEG frames
            },
            [ThermalrightPanelModel.EliteVisionScsi] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.EliteVisionScsi,
                Name = "Elite Vision (SCSI)",
                DeviceIdentifier = "",  // Resolution detected at runtime from SCSI poll response
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = SCSI_VENDOR_ID,
                ProductId = SCSI_PRODUCT_ID,
                TransportType = ThermalrightTransportType.Scsi,
                PixelFormat = ThermalrightPixelFormat.Rgb565BigEndian
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
        /// Get display resolution from HID init response PM byte (byte[5]).
        /// Based on TRCC protocol: PM → FBL → resolution mapping.
        /// </summary>
        public static (int Width, int Height, string SizeName)? GetResolutionFromPM(byte pm)
        {
            return pm switch
            {
                128 => (1280, 480, "6.86\""),   // FBL 128
                65 => (1920, 462, "9.16\""),     // FBL 192
                _ => null
            };
        }

        /// <summary>
        /// Get display resolution from SCSI poll response byte[0].
        /// Based on USBLCD.exe protocol: poll byte → resolution mapping.
        /// </summary>
        public static (int Width, int Height)? GetResolutionFromScsiPollByte(byte pollByte)
        {
            return pollByte switch
            {
                0x24 => (240, 240),  // '$'
                0x32 => (320, 240),  // '2'
                0x33 => (320, 240),  // '3'
                0x64 => (320, 320),  // 'd'
                0x65 => (320, 320),  // 'e'
                _ => null
            };
        }

        /// <summary>
        /// Get model info by device identifier string (e.g., "SSCRM-V1", "SSCRM-V3", "SSCRM-V4")
        /// and optional SUB byte for disambiguation (e.g., SSCRM-V3 + SUB=0x01 = Wonder, SUB=0x02 = Rainbow).
        /// </summary>
        public static ThermalrightPanelModelInfo? GetModelByIdentifier(string identifier, byte? subByte = null)
        {
            // If SUB byte provided, try exact match first (identifier + SUB)
            if (subByte.HasValue)
            {
                foreach (var model in Models.Values)
                {
                    if (model.DeviceIdentifier == identifier && model.SubByte.HasValue && model.SubByte.Value == subByte.Value)
                        return model;
                }
            }

            // Fallback: match by identifier only (first match wins — for unique identifiers like V1/V4,
            // or when SUB byte is unknown/not in database)
            foreach (var model in Models.Values)
            {
                if (model.DeviceIdentifier == identifier)
                    return model;
            }
            return null;
        }
    }
}
