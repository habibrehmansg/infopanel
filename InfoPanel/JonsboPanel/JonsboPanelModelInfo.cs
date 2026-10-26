namespace InfoPanel.JonsboPanel
{
    public class JonsboPanelModelInfo
    {
        public JonsboPanelModel Model { get; init; }
        public string Name { get; init; } = "Unknown Model";

        /// <summary>Native panel resolution (portrait for the DS916: 462x1920).
        /// The handshake identity string overrides these at runtime.</summary>
        public int Width { get; init; }
        public int Height { get; init; }

        public int VendorId { get; init; }
        public int ProductId { get; init; }

        public JonsboTransportType TransportType { get; init; } = JonsboTransportType.Serial;

        /// <summary>CEA/custom VIC for MS9132 mode-set (171 = 376x960 on the DS339).</summary>
        public byte Vic { get; init; }

        /// <summary>Soft cap on JPEG payload size in bytes (OEM app streams ~120 KB frames). Serial transport only.</summary>
        public int MaxJpegBytes { get; init; } = 256 * 1024;

        public int DefaultFrameRate { get; init; } = 25;
    }
}
