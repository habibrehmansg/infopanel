namespace InfoPanel.JonsboPanel
{
    public enum JonsboPanelModel
    {
        Unknown,
        DS916,          // VID 0x33C3, PID 0xF101, 462x1920 native portrait ("VMAXB16" identity), CDC raw-JPEG
        DS339,          // VID 0x345F, PID 0x9132 (MacroSilicon MS9132), 376x960 native portrait, RGB888 USB display
    }

    public enum JonsboTransportType
    {
        Serial,         // HLVMAX CDC ACM COM port, raw JPEG frames
        Ms9132,         // MacroSilicon MS9132: HID control plane + bulk RGB888 frames
    }
}
