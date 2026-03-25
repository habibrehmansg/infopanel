namespace InfoPanel.ThermaltakePanel
{
    public class ThermaltakePanelModelInfo
    {
        public ThermaltakePanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }
    }
}
