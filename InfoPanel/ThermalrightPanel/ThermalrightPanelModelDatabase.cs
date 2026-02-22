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
        public const int TROFEO_PRODUCT_ID_916 = 0x5408;  // 9.16" - WinUSB bulk (LY chipset)
        public const int TROFEO_PRODUCT_ID_LY1 = 0x5409;  // 9.16" - WinUSB bulk (LY1 chipset)
        public const int ALI_PRODUCT_ID = 0x5406;          // ALi chipset - WinUSB bulk, raw RGB565

        // Alternate Trofeo vendor ID (0x0418) — same HID protocol as 0x0416
        public const int TROFEO_VENDOR_ID_2 = 0x0418;
        public const int TROFEO_PRODUCT_ID_5303 = 0x5303;  // 64-byte HID reports
        public const int TROFEO_PRODUCT_ID_5304 = 0x5304;  // 512-byte HID reports

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
            (TROFEO_VENDOR_ID, TROFEO_PRODUCT_ID_LY1),
            (TROFEO_VENDOR_ID, ALI_PRODUCT_ID),
            (TROFEO_VENDOR_ID_2, TROFEO_PRODUCT_ID_5303),
            (TROFEO_VENDOR_ID_2, TROFEO_PRODUCT_ID_5304),
            (SCSI_VENDOR_ID, SCSI_PRODUCT_ID)
        };

        // Device identifiers returned in init response
        public const string IDENTIFIER_V1 = "SSCRM-V1"; // Grand / Hydro / Hyper / Peerless Vision 240/360 (480x480)
        public const string IDENTIFIER_V3 = "SSCRM-V3"; // Wonder / Rainbow Vision 360 (2400x1080) — SUB byte differentiates

        // SUB byte (init response byte[28]) for SSCRM-V3 models
        public const byte WONDER_360_SUB_BYTE  = 0x01; // Wonder Vision 360
        public const byte RAINBOW_360_SUB_BYTE = 0x02; // Rainbow Vision 360
        public const string IDENTIFIER_V4 = "SSCRM-V4"; // TL-M10 Vision (1920x462)

        public static readonly Dictionary<ThermalrightPanelModel, ThermalrightPanelModelInfo> Models = new()
        {
            [ThermalrightPanelModel.PeerlessVision360] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.PeerlessVision360,
                Name = "Grand / Hydro / Hyper / Peerless Vision 240/360",
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
            },

            // --- TrofeoBulk LY1 (PID 0x5409) ---
            [ThermalrightPanelModel.TrofeoVision916LY1] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.TrofeoVision916LY1,
                Name = "Trofeo Vision 9.16\" (LY1)",
                DeviceIdentifier = "",  // Identified by unique VID/PID
                Width = 1920,
                Height = 480,
                RenderWidth = 1920,
                RenderHeight = 480,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = TROFEO_PRODUCT_ID_LY1,
                TransportType = ThermalrightTransportType.WinUsb,
                ProtocolType = ThermalrightProtocolType.TrofeoBulkLY1
            },

            // --- ALi chipset (PID 0x5406) ---
            [ThermalrightPanelModel.AliVision320x240] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AliVision320x240,
                Name = "ALi Vision 320x240",
                DeviceIdentifier = "",
                Width = 320,
                Height = 240,
                RenderWidth = 320,
                RenderHeight = 240,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = ALI_PRODUCT_ID,
                TransportType = ThermalrightTransportType.WinUsb,
                ProtocolType = ThermalrightProtocolType.Ali,
                PixelFormat = ThermalrightPixelFormat.Rgb565
            },
            [ThermalrightPanelModel.AliVision320x320] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.AliVision320x320,
                Name = "ALi Vision 320x320",
                DeviceIdentifier = "",
                Width = 320,
                Height = 320,
                RenderWidth = 320,
                RenderHeight = 320,
                VendorId = TROFEO_VENDOR_ID,
                ProductId = ALI_PRODUCT_ID,
                TransportType = ThermalrightTransportType.WinUsb,
                ProtocolType = ThermalrightProtocolType.Ali,
                PixelFormat = ThermalrightPixelFormat.Rgb565
            },

            // =========================================================================
            // ChiZhu bulk (87AD:70DB) models — identified by PM byte[24] and SUB byte[28]
            // From TRCC USB_ID1_1=257 table (ThreadSendDeviceData)
            // =========================================================================

            // --- 480x480 ---
            [ThermalrightPanelModel.CoreVision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.CoreVision,
                Name = "Core Vision",
                Width = 480, Height = 480, RenderWidth = 480, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.HyperVision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.HyperVision,
                Name = "Hyper Vision",
                Width = 480, Height = 480, RenderWidth = 480, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.RP130Vision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.RP130Vision,
                Name = "RP130 Vision",
                Width = 480, Height = 480, RenderWidth = 480, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LM16SE] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LM16SE,
                Name = "LM16 SE",
                Width = 480, Height = 480, RenderWidth = 480, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LF10V] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LF10V,
                Name = "LF10V",
                Width = 480, Height = 480, RenderWidth = 480, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LM19SE] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LM19SE,
                Name = "LM19 SE",
                Width = 480, Height = 480, RenderWidth = 480, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.GrandVisionBulk] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.GrandVisionBulk,
                Name = "Grand Vision",
                Width = 480, Height = 480, RenderWidth = 480, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 640x480 ---
            [ThermalrightPanelModel.MjolnirVision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.MjolnirVision,
                Name = "Mjolnir Vision",
                Width = 640, Height = 480, RenderWidth = 640, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.FrozenWarframeUltra] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenWarframeUltra,
                Name = "Frozen Warframe Ultra",
                Width = 640, Height = 480, RenderWidth = 640, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.FrozenVisionV2] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.FrozenVisionV2,
                Name = "Frozen Vision V2",
                Width = 640, Height = 480, RenderWidth = 640, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.StreamVision] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.StreamVision,
                Name = "Stream Vision",
                Width = 640, Height = 480, RenderWidth = 640, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.MjolnirVisionPro] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.MjolnirVisionPro,
                Name = "Mjolnir Vision Pro",
                Width = 640, Height = 480, RenderWidth = 640, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 854x480 ---
            [ThermalrightPanelModel.LC2JD] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LC2JD,
                Name = "LC2JD",
                Width = 854, Height = 480, RenderWidth = 854, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LF19] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LF19,
                Name = "LF19",
                Width = 854, Height = 480, RenderWidth = 854, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LD8] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LD8,
                Name = "LD8",
                Width = 854, Height = 480, RenderWidth = 854, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 960x540 ---
            [ThermalrightPanelModel.LC3] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LC3,
                Name = "LC3",
                Width = 960, Height = 540, RenderWidth = 960, RenderHeight = 540,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LF16] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LF16,
                Name = "LF16",
                Width = 960, Height = 540, RenderWidth = 960, RenderHeight = 540,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LF18] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LF18,
                Name = "LF18",
                Width = 960, Height = 540, RenderWidth = 960, RenderHeight = 540,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LD6] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LD6,
                Name = "LD6",
                Width = 960, Height = 540, RenderWidth = 960, RenderHeight = 540,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.CZ2] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.CZ2,
                Name = "CZ2",
                Width = 960, Height = 540, RenderWidth = 960, RenderHeight = 540,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 800x480 ---
            [ThermalrightPanelModel.LF17] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LF17,
                Name = "LF17",
                Width = 800, Height = 480, RenderWidth = 800, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 960x320 ---
            [ThermalrightPanelModel.PC1] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.PC1,
                Name = "PC1",
                Width = 960, Height = 320, RenderWidth = 960, RenderHeight = 320,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LC9] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LC9,
                Name = "LC9",
                Width = 960, Height = 320, RenderWidth = 960, RenderHeight = 320,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 640x172 ---
            [ThermalrightPanelModel.LC7] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LC7,
                Name = "LC7",
                Width = 640, Height = 172, RenderWidth = 640, RenderHeight = 172,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LC8] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LC8,
                Name = "LC8",
                Width = 640, Height = 172, RenderWidth = 640, RenderHeight = 172,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 1280x480 ---
            [ThermalrightPanelModel.LM24] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LM24,
                Name = "LM24",
                Width = 1280, Height = 480, RenderWidth = 1280, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LM24B] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LM24B,
                Name = "LM24",
                Width = 1280, Height = 480, RenderWidth = 1280, RenderHeight = 480,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 1600x720 ---
            [ThermalrightPanelModel.LM22] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LM22,
                Name = "LM22",
                Width = 1600, Height = 720, RenderWidth = 1600, RenderHeight = 720,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LM27] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LM27,
                Name = "LM27",
                Width = 1600, Height = 720, RenderWidth = 1600, RenderHeight = 720,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LM30] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LM30,
                Name = "LM30",
                Width = 1600, Height = 720, RenderWidth = 1600, RenderHeight = 720,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 1920x462 ---
            [ThermalrightPanelModel.LF14] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LF14,
                Name = "LF14",
                Width = 1920, Height = 462, RenderWidth = 1920, RenderHeight = 462,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LD7] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LD7,
                Name = "LD7",
                Width = 1920, Height = 462, RenderWidth = 1920, RenderHeight = 462,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },
            [ThermalrightPanelModel.LD10] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LD10,
                Name = "LD10",
                Width = 1920, Height = 462, RenderWidth = 1920, RenderHeight = 462,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
            },

            // --- 1920x440 ---
            [ThermalrightPanelModel.LD9] = new ThermalrightPanelModelInfo
            {
                Model = ThermalrightPanelModel.LD9,
                Name = "LD9",
                Width = 1920, Height = 440, RenderWidth = 1920, RenderHeight = 440,
                VendorId = THERMALRIGHT_VENDOR_ID, ProductId = THERMALRIGHT_PRODUCT_ID,
                ProtocolType = ThermalrightProtocolType.ChiZhu
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

        /// <summary>
        /// Get model info for ChiZhu bulk (87AD:70DB) devices by PM byte[24] and SUB byte[28].
        /// From TRCC USB_ID1_1=257 table (ThreadSendDeviceData).
        /// This is separate from HID GetModelByPM which uses byte[5].
        /// </summary>
        public static ThermalrightPanelModelInfo? GetModelByChiZhuPM(byte pm, byte sub)
        {
            return (pm, sub) switch
            {
                // PM 1: SSCRM identifier-based models (legacy, sub selects resolution tier)
                (1, 48) => Models[ThermalrightPanelModel.LM22],        // 1600x720
                (1, 49) => Models[ThermalrightPanelModel.LF14],        // 1920x462

                // PM 3: Core Vision 480x480
                (3, _) => Models[ThermalrightPanelModel.CoreVision],

                // PM 4: 480x480 variants by sub
                (4, 1) => Models[ThermalrightPanelModel.HyperVision],
                (4, 2) => Models[ThermalrightPanelModel.RP130Vision],
                (4, 3) => Models[ThermalrightPanelModel.LM16SE],
                (4, 4) => Models[ThermalrightPanelModel.LF10V],
                (4, 5) => Models[ThermalrightPanelModel.LM19SE],

                // PM 5: Mjolnir Vision 640x480
                (5, _) => Models[ThermalrightPanelModel.MjolnirVision],

                // PM 6: 640x480 variants
                (6, 1) => Models[ThermalrightPanelModel.FrozenWarframeUltra],
                (6, _) => Models[ThermalrightPanelModel.FrozenVisionV2],

                // PM 7: 640x480 variants
                (7, 1) => Models[ThermalrightPanelModel.StreamVision],
                (7, _) => Models[ThermalrightPanelModel.MjolnirVisionPro],

                // PM 9: 854x480
                (9, >= 5) => Models[ThermalrightPanelModel.LF19],
                (9, _) => Models[ThermalrightPanelModel.LC2JD],

                // PM 10: 960x540 variants
                (10, 5) => Models[ThermalrightPanelModel.LF16],
                (10, 6) => Models[ThermalrightPanelModel.LF18],
                (10, 7) => Models[ThermalrightPanelModel.LD6],
                (10, _) => Models[ThermalrightPanelModel.LC3],

                // PM 11: 854x480
                (11, 6) => Models[ThermalrightPanelModel.LD8],

                // PM 12: 800x480
                (12, _) => Models[ThermalrightPanelModel.LF17],

                // PM 13: 960x320
                (13, _) => Models[ThermalrightPanelModel.PC1],

                // PM 15: 640x172
                (15, 1) => Models[ThermalrightPanelModel.LC7],
                (15, _) => Models[ThermalrightPanelModel.LC8],

                // PM 16: 960x540
                (16, _) => Models[ThermalrightPanelModel.CZ2],

                // PM 17: 960x320
                (17, 2) => Models[ThermalrightPanelModel.LC9],

                // PM 32 (0x20): 320x320 RGB565 big-endian
                (0x20, _) => Models[ThermalrightPanelModel.ChiZhuVision320x320],

                // PM 64: 1600x720 variants
                (64, 1) => Models[ThermalrightPanelModel.LM22],
                (64, 2) => Models[ThermalrightPanelModel.LM27],
                (64, 3) => Models[ThermalrightPanelModel.LM30],

                // PM 65: 1920x462 variants
                (65, 1) or (65, 2) => Models[ThermalrightPanelModel.LF14],
                (65, 3) => Models[ThermalrightPanelModel.LD7],
                (65, 4) => Models[ThermalrightPanelModel.LD10],

                // PM 66: 1920x462 variants
                (66, 3) or (66, 4) => Models[ThermalrightPanelModel.LD7],

                // PM 68: 1280x480
                (68, _) => Models[ThermalrightPanelModel.LM24],

                // PM 69: 1920x440
                (69, 2) => Models[ThermalrightPanelModel.LD9],

                // PM 128: 1280x480
                (128, _) => Models[ThermalrightPanelModel.LM24B],

                // PM 129: Grand Vision 480x480
                (129, _) => Models[ThermalrightPanelModel.GrandVisionBulk],

                _ => null
            };
        }
    }
}
