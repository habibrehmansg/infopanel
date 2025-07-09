namespace InfoPanel.Plugins
{
    public interface IPluginContainer
    {
        string Id { get; }
        string Name { get; }
        bool IsEmphemeralPath { get; }
        List<IPluginData> Entries { get; }
    }
}
