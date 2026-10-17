namespace InfoPanel.ValkyriePanel
{
    public enum ValkyriePanelModel { V360 }

    public sealed class ValkyriePanelModelInfo
    {
        public ValkyriePanelModel Model { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
    }

    public static class ValkyriePanelModelDatabase
    {
        public const int VendorId = 0x345F;
        public const int ProductId = 0x9132;

        public static readonly System.Collections.Generic.Dictionary<ValkyriePanelModel, ValkyriePanelModelInfo> Models = new()
        {
            [ValkyriePanelModel.V360] = new()
            {
                Model = ValkyriePanelModel.V360, Name = "Valkyrie V360 LCD", Width = 320, Height = 240
            }
        };
    }
}
