using InfoPanel.SensorHelper;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Sensors.Libre;

/// <summary>
/// A running sensor helper as the Libre source talks to it: the protocol's four requests (<see cref="SensorHelperClient"/>)
/// and the helper's end. Disposing it is the engine asking the helper to stop: the pipe closes and the helper's
/// process ends (killed if it doesn't within <see cref="LibreHelperProcess.ExitWait"/>).
/// </summary>
public interface ILibreHelper : IAsyncDisposable
{
    Task<HelloAnswer> HelloAsync(CancellationToken cancel);

    Task<CatalogueAnswer> CatalogueAsync(CancellationToken cancel);

    Task<HelperSample> SampleAsync(CancellationToken cancel);

    Task<ConfigureAnswer> ConfigureAsync(bool storage, int storageInterval, CancellationToken cancel);

    /// <summary>Completes with the helper's exit code (null when it has none, a replay's) when it ends.</summary>
    Task<int?> Exited { get; }
}

/// <summary>Starts a sensor helper and connects to it: the helper's process on Windows, a recording in a replay, a fake in tests.</summary>
public interface ILibreHelperLauncher
{
    /// <summary>
    /// A helper, started and connected, whose client hands each answer to <paramref name="observer"/> (a recording).
    /// Throws <see cref="LibreHelperMissingException"/> when there is no helper to start, and anything else when it
    /// started but didn't serve.
    /// </summary>
    Task<ILibreHelper> StartAsync(SensorHelperClient.IObserver? observer, CancellationToken cancel);
}

/// <summary>The helper's executable isn't where the engine looks for it (a build that doesn't carry it).</summary>
public sealed class LibreHelperMissingException(string path)
    : FileNotFoundException($"The sensor helper isn't at {path}.", path);

/// <summary>A helper over a connected <see cref="SensorHelperClient"/>, ending when <paramref name="exited"/> completes.</summary>
public class LibreHelperConnection(SensorHelperClient client, Task<int?> exited) : ILibreHelper
{
    public SensorHelperClient Client { get; } = client;

    public Task<int?> Exited { get; } = exited;

    public Task<HelloAnswer> HelloAsync(CancellationToken cancel) => Client.HelloAsync(cancel);

    public Task<CatalogueAnswer> CatalogueAsync(CancellationToken cancel) => Client.CatalogueAsync(cancel);

    public Task<HelperSample> SampleAsync(CancellationToken cancel) => Client.SampleAsync(cancel);

    public Task<ConfigureAnswer> ConfigureAsync(bool storage, int storageInterval, CancellationToken cancel) =>
        Client.ConfigureAsync(storage, storageInterval, cancel);

    public virtual ValueTask DisposeAsync() => Client.DisposeAsync();
}
