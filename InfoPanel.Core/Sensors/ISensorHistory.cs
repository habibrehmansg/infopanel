using System.Collections.Generic;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// Sampled history of sensor values, used by graphs and donuts.
    /// </summary>
    public interface ISensorHistory
    {
        /// <summary>
        /// Returns the samples for <paramref name="key"/>, oldest first, at most 4096.
        /// Calling it registers interest, so an implementation starts sampling keys it has not seen.
        /// </summary>
        IReadOnlyList<double> GetSamples(SensorKey key);
    }
}
