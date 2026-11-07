using InfoPanel.Models;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// Reads the current value of a sensor. Returns false when the sensor is unknown or has no reading.
    /// </summary>
    public interface ISensorReader
    {
        /// <summary>A consumer's read: it puts the sensor in use, for a source whose reads start work (the weather).</summary>
        bool TryRead(SensorKey key, out SensorReading reading);

        /// <summary>
        /// <see cref="TryRead"/> without putting the sensor in use: the history's own sampling, which no consumer asked
        /// for at that moment. The same as <see cref="TryRead"/> unless a read starts work of its own.
        /// </summary>
        bool TryPeek(SensorKey key, out SensorReading reading) => TryRead(key, out reading);

        /// <summary>
        /// A consumer uses the sensor's value without reading it now (a graph's draw of samples already taken): it puts
        /// the sensor in use as a <see cref="TryRead"/> does. Nothing unless a read starts work of its own.
        /// </summary>
        void NoteUse(SensorKey key)
        {
        }
    }
}
