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

        // SCSI pass-through panels (Elite Vision 360 / Frozen Warframe SCSI variant)
        public const int SCSI_VENDOR_ID = 0x0402;
        public const int SCSI_PRODUCT_ID = 0x3922;

        // HID identifier string reported by Trofeo HID panels (init response bytes 20-27)
        // Both 6.86" and 2.4" report "BP21940" — PM byte distinguishes them
        public const string TROFEO_686_HID_IDENTIFIER = "BP21940";

        // PM byte (init response byte[5]) for Trofeo HID panels
        public const byte TROFEO_686_PM_BYTE  = 0x80;  // 128 -> 1280x480 (6.86")
        public const byte TROFEO_240_PM_BYTE  = 0x3A;  //  58 -> 320x240  (2.4")
        public const byte TROFEO_320_PM_BYTE  = 0x20;  //  32 -> 320x320  (big-endian RGB565)
        public const byte TROFEO_1600_PM_BYTE = 0x40;  //  64 -> 1600x720
        public const byte TROFEO_960_PM_BYTE  = 0x0A;  //  10 -> 960x540
        public const byte TROFEO_800_PM_BYTE  = 0x0C;  //  12 -> 800x480

        // HID 0x5302 PM bytes (names and resolutions from TRCC protocol analysis)
        public const byte TROFEO_ASSASSIN120_PM_BYTE = 0x24;  //  36 -> 240x240, RGB565 (SPI)
        public const byte TROFEO_FW49_PM_BYTE        = 0x31;  //  49 -> 240x320, RGB565 (SPI, "Frozen Warframe")
        public const byte TROFEO_AS120_PM_BYTE       = 0x32;  //  50 -> 320x320, Jpeg ("Frozen Warframe")
        public const byte TROFEO_AS120B_PM_BYTE      = 0x33;  //  51 -> 320x320, Jpeg ("Frozen Warframe")
        public const byte TROFEO_BA120_PM_BYTE       = 0x34;  //  52 -> 320x240, RGB565 (SPI, "BA120 Vision")
        public const byte TROFEO_BA120B_PM_BYTE      = 0x35;  //  53 -> 320x320, Jpeg ("LF20/LF21/LF22")
        public const byte TROFEO_LC5_PM_BYTE         = 0x36;  //  54 -> 360x360, Jpeg ("LC5" fan LCD)
        public const byte TROFEO_ELITE_1920_PM_BYTE  = 0x41;  //  65 -> 1920x462, Jpeg ("Elite Vision 9.16\"")
        public const byte TROFEO_FWPRO_PM_BYTE       = 0x64;  // 100 -> 320x320, Jpeg ("Frozen Warframe Pro/LM22")
        public const byte TROFEO_ELITE_PM_BYTE       = 0x65;  // 101 -> 320x320, Jpeg ("Elite Vision/LF14")

        // ChiZhu bulk 87AD:70DB PM byte (at offset 24 of 1024-byte init response)
        public const byte CHIZHU_320X320_PM_BYTE = 0x20;  //  32 -> 320x320, RGB565 big-endian

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
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,  // "BP21940" - shared with 2.4", PM byte distinguishes
                Width = 1280,
                Height = 480,
                RenderWidth = 1280,
                RenderHeight = 480,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_686_PM_BYTE
            },
            [ThermalrightPanelModel.FrozenWarframe] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenWarframe,
                Name = "Frozen Warframe SE",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,  // "BP21940" - shared with 6.86", PM byte distinguishes
                Width = 240,
                Height = 320,
                RenderWidth = 240,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,  // Uses raw RGB565, not JPEG
                PmByte = TROFEO_240_PM_BYTE,
                SubByte = 0x00  // Sub byte 0 = Frozen Warframe SE
            },
            [ThermalrightPanelModel.FrozenWarframeLM26] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenWarframeLM26,
                Name = "LM26",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 240,
                Height = 320,
                RenderWidth = 240,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_240_PM_BYTE  // Sub byte !=0 = LM26 (no SubByte = wildcard fallback)
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
            [ThermalrightPanelModel.TrofeoVision320] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision320,
                Name = "Trofeo Vision 320x320",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565BigEndian,
                PmByte = TROFEO_320_PM_BYTE
            },
            [ThermalrightPanelModel.TrofeoVision1600x720] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision1600x720,
                Name = "Trofeo Vision 1600x720",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 1600,
                Height = 720,
                RenderWidth = 1600,
                RenderHeight = 720,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_1600_PM_BYTE
            },
            [ThermalrightPanelModel.TrofeoVision960x540] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision960x540,
                Name = "Trofeo Vision 960x540",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 960,
                Height = 540,
                RenderWidth = 960,
                RenderHeight = 540,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_960_PM_BYTE
            },
            [ThermalrightPanelModel.TrofeoVision800x480] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision800x480,
                Name = "Trofeo Vision 800x480",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 800,
                Height = 480,
                RenderWidth = 800,
                RenderHeight = 480,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_800_PM_BYTE
            },
            [ThermalrightPanelModel.AssassinSpirit120Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AssassinSpirit120Vision,
                Name = "Assassin Spirit 120 Vision 1.54\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 240,
                Height = 240,
                RenderWidth = 240,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_ASSASSIN120_PM_BYTE
            },
            // PM 0x32 (50): TRCC bulk table = "Frozen Warframe" 320x320 Jpeg
            [ThermalrightPanelModel.AS120Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AS120Vision,
                Name = "Frozen Warframe",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_AS120_PM_BYTE
            },
            // PM 0x33 (51): TRCC bulk table = "Frozen Warframe" 320x320 Jpeg
            [ThermalrightPanelModel.AS120VisionB] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AS120VisionB,
                Name = "Frozen Warframe",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_AS120B_PM_BYTE
            },
            [ThermalrightPanelModel.BA120Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.BA120Vision,
                Name = "BA120 Vision 2.4\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_BA120_PM_BYTE
            },
            // PM 0x35 (53): TRCC bulk table = "LF20/LF21/LF22" 320x320 Jpeg (sub 0=LF20, 1=LF21, 2=LF22)
            [ThermalrightPanelModel.BA120VisionB] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.BA120VisionB,
                Name = "LF20",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_BA120B_PM_BYTE
            },
            // PM 0x64 (100): TRCC bulk table = "Frozen Warframe Pro/LM22" 320x320 Jpeg (sub 0=FW Pro, 1=LM22)
            [ThermalrightPanelModel.FrozenWarframePro] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenWarframePro,
                Name = "Frozen Warframe Pro",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_FWPRO_PM_BYTE
            },
            // PM 0x65 (101): TRCC bulk table = "Elite Vision/LF14" 320x320 Jpeg (sub 0=Elite Vision, 1=LF14)
            [ThermalrightPanelModel.EliteVisionHid] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.EliteVisionHid,
                Name = "Elite Vision",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_ELITE_PM_BYTE
            },
            // PM 0x31 (49): TRCC HID table = "Frozen Warframe" — SPI device, not in bulk table
            [ThermalrightPanelModel.FrozenWarframe49] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenWarframe49,
                Name = "Frozen Warframe",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 240,
                Height = 320,
                RenderWidth = 240,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PixelFormat = ThermalrightPixelFormat.Rgb565,
                PmByte = TROFEO_FW49_PM_BYTE
            },
            // PM 0x36 (54): TRCC = "LC5" fan LCD, 360x360
            [ThermalrightPanelModel.LC5] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LC5,
                Name = "LC5",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 360,
                Height = 360,
                RenderWidth = 360,
                RenderHeight = 360,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_LC5_PM_BYTE
            },
            // PM 0x41 (65 dec): TRCC bulk table = "Elite Vision" 1920x462
            [ThermalrightPanelModel.EliteVision1920] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.EliteVision1920,
                Name = "Elite Vision 9.16\"",
                DeviceIdentifier = TROFEO_686_HID_IDENTIFIER,
                Width = 1920,
                Height = 462,
                RenderWidth = 1920,
                RenderHeight = 462,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_686,
                TransportType = ThermalrightTransportType.Hid,
                ProtocolType = ThermalrightProtocolType.Trofeo,
                PmByte = TROFEO_ELITE_1920_PM_BYTE
            },
            [ThermalrightPanelModel.ChiZhuVision320x320] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.ChiZhuVision320x320,
                Name = "ChiZhu Vision 320x320",
                DeviceIdentifier = "",  // Identified by VID/PID + PM byte at offset 24 of ChiZhu bulk response
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = THERMALRIGHT_VENDOR_ID,
                ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu,
                PixelFormat = ThermalrightPixelFormat.Rgb565BigEndian
            },
            [ThermalrightPanelModel.EliteVisionScsi] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.EliteVisionScsi,
                Name = "Elite Vision 360 2.73\"",
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
            ThermalrightPanelModelInfo? match = null;
            foreach (var model in Models.Values)
            {
                if (model.VendorId == vid && model.ProductId == pid)
                {
                    if (match != null)
                        return null; // Multiple models share this VID/PID — can't determine at scan time
                    match = model;
                }
            }
            return match;
        }

        /// <summary>
        /// Get display resolution from HID init response PM byte (byte[5]).
        /// Based on TRCC protocol: PM → FBL → resolution mapping.
        /// </summary>
        public static (int Width, int Height, string SizeName)? GetResolutionFromPM(byte pm)
        {
            return pm switch
            {
                TROFEO_686_PM_BYTE         => (1280, 480,  "6.86\""),
                TROFEO_ELITE_1920_PM_BYTE  => (1920, 462,  "9.16\""),
                TROFEO_240_PM_BYTE         => (240,  320,  "2.4\""),
                TROFEO_320_PM_BYTE         => (320,  320,  "320x320"),
                TROFEO_1600_PM_BYTE        => (1600, 720,  "1600x720"),
                TROFEO_960_PM_BYTE         => (960,  540,  "960x540"),
                TROFEO_800_PM_BYTE         => (800,  480,  "800x480"),
                TROFEO_ASSASSIN120_PM_BYTE => (240,  240,  "240x240"),
                TROFEO_FW49_PM_BYTE        => (240,  320,  "240x320"),
                TROFEO_AS120_PM_BYTE       => (320,  320,  "320x320"),
                TROFEO_AS120B_PM_BYTE      => (320,  320,  "320x320"),
                TROFEO_BA120_PM_BYTE       => (320,  240,  "320x240"),
                TROFEO_BA120B_PM_BYTE      => (320,  320,  "320x320"),
                TROFEO_LC5_PM_BYTE         => (360,  360,  "360x360"),
                TROFEO_FWPRO_PM_BYTE       => (320,  320,  "320x320"),
                TROFEO_ELITE_PM_BYTE       => (320,  320,  "320x320"),
                _ => null
            };
        }

        /// <summary>
        /// Get model info by HID PM byte and optional sub byte.
        /// If sub is provided, first tries exact match (PM + SubByte), then falls back to PM-only
        /// match where SubByte is null (wildcard). This allows PM 0x3A sub 0 → FW SE vs sub !=0 → LM26.
        /// </summary>
        public static ThermalrightPanelModelInfo? GetModelByPM(byte pm, byte? sub = null)
        {
            // First pass: exact match on PM + SubByte (if sub provided)
            if (sub.HasValue)
            {
                foreach (var model in Models.Values)
                {
                    if (model.PmByte.HasValue && model.PmByte.Value == pm &&
                        model.SubByte.HasValue && model.SubByte.Value == sub.Value)
                        return model;
                }
            }

            // Second pass: PM match with null SubByte (wildcard fallback)
            foreach (var model in Models.Values)
            {
                if (model.PmByte.HasValue && model.PmByte.Value == pm && !model.SubByte.HasValue)
                    return model;
            }
            return null;
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
