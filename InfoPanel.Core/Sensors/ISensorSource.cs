using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors
{
    /// <summary>
    /// A provider of sensors (mock sensors now; HWiNFO, Libre, plugins and macOS later).
    /// </summary>
    public interface ISensorSource : ISensorReader
    {
        string Id { get; }

        bool Handles(SensorKey key);

        IReadOnlyList<SensorInfo> GetSensors();

        Task StartAsync(CancellationToken ct);

        Task StopAsync(CancellationToken ct);
    }
}
