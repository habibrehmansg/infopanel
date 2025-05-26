namespace InfoPanel.Plugins
{
    public interface IPluginSensor: IPluginData
    {
        float Value { get; set; }
        float ValueMin { get => 0; }
        float ValueMax { get => 0; }
        float ValueAvg { get => 0; }
        string? Unit { get; }
    }
}
