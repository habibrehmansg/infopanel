using System.Collections.Generic;

namespace InfoPanel.Sensors;

/// <summary>
/// A source that keeps recent values of its sensors, so a history that starts tracking a key can begin
/// with them instead of empty (the mock source backfills two minutes so graphs aren't empty on load).
/// </summary>
public interface ISensorBackfill
{
    /// <summary>Recent values of <paramref name="key"/>, oldest first; empty when there are none.</summary>
    IReadOnlyList<double> GetBackfill(SensorKey key);
}
