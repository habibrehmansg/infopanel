using InfoPanel.Models;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// Reads the current value of a sensor. Returns false when the sensor is unknown or has no reading.
    /// </summary>
    public interface ISensorReader
    {
        bool TryRead(SensorKey key, out SensorReading reading);
    }
}
