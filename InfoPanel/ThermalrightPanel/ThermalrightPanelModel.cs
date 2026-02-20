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

        // SCSI pass-through (VID 0x0402 / PID 0x3922) — Elite Vision 360 2.73", resolution detected at runtime from poll response
        EliteVisionScsi,

        // Backward compatibility alias (was renamed to TrofeoVision)
        TrofeoVision686 = TrofeoVision,
    }
}
