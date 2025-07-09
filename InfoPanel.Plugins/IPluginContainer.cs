namespace InfoPanel.Plugins
{
    public interface IPluginContainer
    {
        string Id { get; }
        string Name { get; }
        bool IsEphemeralPath { get; }
        List<IPluginData> Entries { get; }
    }
}
