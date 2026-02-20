namespace InfoPanel.ThermalrightPanel
{
    public enum ThermalrightPanelModel
    {
        Unknown,
        // Grand / Hydro / Peerless Vision 240/360 - 3.95" (480x480) - responds with SSCRM-V1
        PeerlessVision360,
        // Wonder Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3, SUB=0x01
        WonderVision360,
        // Rainbow Vision 360 - 6.67" (2400x1080) - responds with SSCRM-V3, SUB=0x02
        RainbowVision360,
        // TL-M10 Vision - 9.16" (1920x462) - responds with SSCRM-V4
        TLM10Vision,
        // Trofeo Vision 6.86" - HID (VID 0x0416 / PID 0x5302)
        // Resolution determined from init response PM byte
        TrofeoVision,
        // Trofeo Vision 9.16" - USB bulk (VID 0x0416 / PID 0x5408)
        TrofeoVision916,

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

        // Backward compatibility alias (was renamed to TrofeoVision)
        TrofeoVision686 = TrofeoVision,
    }
}
