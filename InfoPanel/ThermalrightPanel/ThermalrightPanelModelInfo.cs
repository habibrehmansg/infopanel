namespace InfoPanel.ThermalrightPanel
{
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

        public override string ToString() => $"{Name} ({RenderWidth}x{RenderHeight}) - {DeviceIdentifier}";
    }
}
