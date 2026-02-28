namespace InfoPanel.ThermalrightPanel
{
    public enum ThermalrightPanelModel
    {
        Unknown,
        // Grand / Hydro / Hyper / Peerless Vision 240/360 - 3.95" (480x480) - responds with SSCRM-V1
        PeerlessVision360,
        // Wonder Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3, SUB=0x01
        WonderVision360,
        // Rainbow Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3, SUB=0x02
        RainbowVision360,
        // Levita Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3, SUB=0x03
        LevitaVision360,
        // TL-M10 Vision - 9.16" (1920x462) - responds with SSCRM-V4
        TLM10Vision,
        // Trofeo Vision 6.86" - HID (VID 0x0416 / PID 0x5302)
        // Resolution determined from init response PM byte
        TrofeoVision,
        // Trofeo Vision 9.16" - USB bulk (VID 0x0416 / PID 0x5408, LY chipset)
        TrofeoVision916,
        // Trofeo Vision 9.16" - USB bulk (VID 0x0416 / PID 0x5409, LY1 chipset)
        TrofeoVision916LY1,

        // Frozen Warframe SE - HID (VID 0x0416 / PID 0x5302), PM byte 0x3A, sub 0x00 -> 240x320 (portrait)
        FrozenWarframe,
        // LM26 - HID (VID 0x0416 / PID 0x5302), PM byte 0x3A, sub !=0
        FrozenWarframeLM26,

        // Generic HID Trofeo panels identified only by PM byte (model unknown)
        // PM 0x20 -> 320x320, RGB565, big-endian
        TrofeoVision320,
        // PM 0x40 -> 1600x720
        TrofeoVision1600x720,
        // PM 0x0A -> 960x540
        TrofeoVision960x540,
        // PM 0x0C -> 800x480
        TrofeoVision800x480,

        // HID 0x5302 models identified by PM byte (names from TRCC protocol analysis)
        AssassinSpirit120Vision,  // PM 0x24 (36)  -> 240x240, RGB565 (SPI)
        FrozenWarframe49,         // PM 0x31 (49)  -> 240x320, RGB565 (SPI, TRCC: "Frozen Warframe")
        AS120Vision,              // PM 0x32 (50)  -> 320x320, Jpeg  (TRCC: "Frozen Warframe")
        AS120VisionB,             // PM 0x33 (51)  -> 320x320, Jpeg  (TRCC: "Frozen Warframe")
        BA120Vision,              // PM 0x34 (52)  -> 320x240, RGB565 (SPI, TRCC: "BA120 Vision")
        BA120VisionB,             // PM 0x35 (53)  -> 320x320, Jpeg  (TRCC: "LF20/LF21/LF22")
        LC5,                      // PM 0x36 (54)  -> 360x360, Jpeg  (TRCC: "LC5" fan LCD)
        EliteVision1920,          // PM 0x41 (65)  -> 1920x462, Jpeg (TRCC: "Elite Vision 9.16\"")
        FrozenWarframePro,        // PM 0x64 (100) -> 320x320, Jpeg  (TRCC: "Frozen Warframe Pro/LM22")
        EliteVisionHid,           // PM 0x65 (101) -> 320x320, Jpeg  (TRCC: "Elite Vision/LF14")

        // ChiZhu bulk (87AD:70DB) PM=0x20 variant — 320x320, RGB565
        ChiZhuVision320x320,

        // SCSI pass-through (VID 0x0402 / PID 0x3922) — Elite Vision 360 2.73", resolution detected at runtime from poll response
        EliteVisionScsi,

        // ALi chipset (VID 0x0416 / PID 0x5406) — F5 protocol, raw RGB565 pixels
        AliVision320x240,    // Device type 54 -> 320x240
        AliVision320x320,    // Device type 101/102 -> 320x320

        // ChiZhu bulk (87AD:70DB) models from TRCC USB_ID1_1=257 table
        // 480x480
        CoreVision,           // PM 3 -> 480x480
        HyperVision,          // PM 4, sub 1 -> 480x480
        RP130Vision,          // PM 4, sub 2 -> 480x480
        LM16SE,               // PM 4, sub 3 -> 480x480
        LF10V,                // PM 4, sub 4 -> 480x480
        LM19SE,               // PM 4, sub 5 -> 480x480
        GrandVisionBulk,      // PM 129 -> 480x480
        // 640x480
        MjolnirVision,        // PM 5 -> 640x480
        FrozenWarframeUltra,  // PM 6, sub 1 -> 640x480
        FrozenVisionV2,       // PM 6, sub 2 -> 640x480
        StreamVision,         // PM 7, sub 1 -> 640x480
        MjolnirVisionPro,     // PM 7, sub 2 -> 640x480
        // 854x480
        LC2JD,                // PM 9, sub <5 -> 854x480
        LF19,                 // PM 9, sub >=5 -> 854x480
        LD8,                  // PM 11, sub 6 -> 854x480
        // 960x540
        LC3,                  // PM 10, sub <5 -> 960x540
        LF16,                 // PM 10, sub 5 -> 960x540
        LF18,                 // PM 10, sub 6 -> 960x540
        LD6,                  // PM 10, sub 7 -> 960x540
        CZ2,                  // PM 16 -> 960x540
        // 800x480
        LF17,                 // PM 12 -> 800x480
        // 960x320
        PC1,                  // PM 13 -> 960x320
        LC9,                  // PM 17, sub 2 -> 960x320
        // 640x172
        LC7,                  // PM 15, sub 1 -> 640x172
        LC8,                  // PM 15, sub 2 -> 640x172
        // 1280x480
        LM24,                 // PM 68 -> 1280x480
        LM24B,                // PM 128 -> 1280x480
        // 1600x720
        LM22,                 // PM 1, sub 48 or PM 64, sub 1 -> 1600x720
        LM27,                 // PM 64, sub 2 -> 1600x720
        LM30,                 // PM 64, sub 3 -> 1600x720
        // 1920x462
        LF14,                 // PM 1, sub 49 or PM 65, sub 1-2 -> 1920x462
        LD7,                  // PM 65, sub 3 or PM 66, sub 3-4 -> 1920x462
        LD10,                 // PM 65, sub 4 -> 1920x462
        // 1920x440
        LD9,                  // PM 69, sub 2 -> 1920x440

        // Backward compatibility alias (was renamed to TrofeoVision)
        TrofeoVision686 = TrofeoVision,
    }
}
