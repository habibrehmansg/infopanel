namespace InfoPanel.ThermalrightPanel
{
    public enum ThermalrightTransportType
    {
        WinUsb,
        Hid,
        Scsi
    }

    public enum ThermalrightProtocolType
    {
        ChiZhu,      // 12 34 56 78 magic, 64-byte header + JPEG (Peerless/Wonder/TL-M10)
        Trofeo,      // DA DB DC DD magic, 512-byte chunked packets (Trofeo Vision HID)
        TrofeoBulk,  // 02 FF init, 4096-byte JPEG frames (Trofeo Vision 9.16" WinUSB, LY chipset)
        TrofeoBulkLY1, // 02 FF init, 512-byte packets, variable writes (LY1 chipset, PID 0x5409)
        Ali          // F5 magic, 16-byte header + raw RGB565 pixels (ALi chipset, PID 0x5406)
    }

    public enum ThermalrightPixelFormat
    {
        Jpeg,          // JPEG compressed — header byte[6]=0x00, width at [8-9], height at [10-11]
        Rgb565,        // Raw 16-bit pixels LE — header byte[6]=0x01, height at [8-9], width at [10-11]
        Rgb565BigEndian // Raw 16-bit pixels BE (byte-swapped) — used by 320x320 panels
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
        public byte? SubByte { get; init; } // ChiZhu init response SUB byte (byte[28]) for SSCRM-V3 differentiation

        public override string ToString() => $"{Name} ({RenderWidth}x{RenderHeight}) - {DeviceIdentifier}";
    }
}
