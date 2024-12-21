namespace InfoPanel.Plugins
{
    public interface IPluginContainer
    {
        string Id { get; }
        string Name { get; }
        List<IPluginData> Entries { get; }
    }
}
