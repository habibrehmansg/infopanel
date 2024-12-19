namespace InfoPanel.Plugins
{
    public interface IPlugin
    {
        string Name { get; }
        void Initialize();
        List<IPluginSensor> GetData();
        Task UpdateAsync();
        void Close();
    }
}
