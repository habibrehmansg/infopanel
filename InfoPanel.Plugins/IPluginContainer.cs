namespace InfoPanel.Plugins
{
    public interface IPluginContainer
    {
        string Id { get; }
        string Name { get; }
        List<IPluginText> Text { get; }
        List<IPluginSensor> Sensors { get; }
    }
}
