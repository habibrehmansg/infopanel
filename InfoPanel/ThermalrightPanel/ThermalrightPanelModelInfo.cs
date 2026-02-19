namespace InfoPanel.ThermalrightPanel
{
    public enum ThermalrightTransportType
    {
        WinUsb,
        Hid
    }

    public enum ThermalrightProtocolType
    {
        ChiZhu,      // 12 34 56 78 magic, 64-byte header + JPEG (Peerless/Wonder/TL-M10)
        Trofeo,      // DA DB DC DD magic, 512-byte chunked packets (Trofeo Vision HID)
        TrofeoBulk   // 02 FF init, 4096-byte JPEG frames (Trofeo Vision 9.16" WinUSB)
    }

    public class ThermalrightPanelModelInfo
    {
        public ThermalrightPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public string DeviceIdentifier { get; init; } = "";  // e.g., "SSCRM-V1", "SSCRM-V3"
        public int Width { get; init; }      // Native resolution
        public int Height { get; init; }
        public int RenderWidth { get; init; }   // Actual render resolution (may differ from native)
        public int RenderHeight { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }
        public ThermalrightTransportType TransportType { get; init; } = ThermalrightTransportType.WinUsb;
        public ThermalrightProtocolType ProtocolType { get; init; } = ThermalrightProtocolType.ChiZhu;
        public ThermalrightPixelFormat PixelFormat { get; init; } = ThermalrightPixelFormat.Jpeg;
        public byte? PmByte { get; init; }  // HID init response PM byte (byte[5]) for Trofeo HID panels
        public byte? SubByte { get; init; } // ChiZhu init response SUB byte (byte[36]) for SSCRM-V3 differentiation

        public override string ToString() => $"{Name} ({RenderWidth}x{RenderHeight}) - {DeviceIdentifier}";
    }
}
