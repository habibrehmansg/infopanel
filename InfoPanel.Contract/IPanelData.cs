namespace InfoPanel.Contract
{
    public interface IPanelData //interface that both the plugin loader and each plugin must implement
    {
        Task<PanelData> GetData();
    }
}
