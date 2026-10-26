namespace InfoPanel.VmaxPanel
{
    public class VmaxPanelModelInfo
    {
        public VmaxPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }
    }
}
