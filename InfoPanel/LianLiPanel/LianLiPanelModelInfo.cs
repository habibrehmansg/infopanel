namespace InfoPanel.LianLiPanel
{
    public class LianLiPanelModelInfo
    {
        public LianLiPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }
        public bool IsUsbDevice { get; init; } = true;
        public bool RequiresPortraitRotationOffset { get; init; }
    }
}
