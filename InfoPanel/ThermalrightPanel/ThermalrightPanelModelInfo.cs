namespace InfoPanel.ThermalrightPanel
{
    public class ThermalrightPanelModelInfo
    {
        public ThermalrightPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }

        public override string ToString() => $"{Name} ({Width}x{Height}) - VID: {VendorId:X4}, PID: {ProductId:X4}";
    }
}
