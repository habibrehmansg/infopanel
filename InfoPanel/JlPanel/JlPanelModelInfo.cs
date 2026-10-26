namespace InfoPanel.JlPanel
{
    public class JlPanelModelInfo
    {
        public JlPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";
        public int Width { get; init; }
        public int Height { get; init; }
        public int VendorId { get; init; }
        public int ProductId { get; init; }

        /// <summary>Soft cap on JPEG payload size in bytes (~80 KB for Chill Arc 360).</summary>
        public int MaxJpegBytes { get; init; } = 80 * 1024;

        /// <summary>Default frame rate for this panel (60 for strip, 30 otherwise per spec).</summary>
        public int DefaultFrameRate { get; init; } = 30;
    }
}
