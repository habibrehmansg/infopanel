namespace InfoPanel.Plugins
{
    public interface IPluginSensor: IPluginData
    {
        float Value { get; set; }
        string? Unit { get; }
    }
}
