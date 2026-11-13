namespace InfoPanel.Plugins
{
    /// <summary>A sensor's value with its minimum, maximum and average, as the plugin host reads them.</summary>
    internal readonly record struct PluginSensorReading(float Value, float ValueMin, float ValueMax, float ValueAvg)
    {
        /// <summary>
        /// <paramref name="sensor"/>'s value and statistics: a <see cref="PluginSensor"/>'s read at once, so never a value
        /// beside another's statistics; any other sensor's read member by member, its value first.
        /// </summary>
        internal static PluginSensorReading Of(IPluginSensor sensor) =>
            sensor is PluginSensor whole
                ? whole.Read()
                : new PluginSensorReading(sensor.Value, sensor.ValueMin, sensor.ValueMax, sensor.ValueAvg);
    }
}
