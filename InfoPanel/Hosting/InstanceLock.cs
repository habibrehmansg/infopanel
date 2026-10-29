using InfoPanel.Persistence;

namespace InfoPanel.Hosting;

/// <summary>
/// Holds <c>{data}/engine.lock</c> open and locked for the engine's lifetime (<see cref="RegularFile.OpenLock"/>: on
/// macOS and Linux an advisory <c>flock</c>, which every .NET process honours; on Windows an open that shares nothing),
/// so a second engine on the same data folder can't start. Only a regular file is locked: a FIFO, a device or a link
/// whose target can't be reached there can't hold the folder, and nothing is created through such a link. The file is
/// left in place on exit: deleting it could let two engines lock different files of the same name.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>The lock, or null with why not.</summary>
    public static InstanceLock? TryAcquire(string path, out string? error) => TryAcquire(path, out error, out _);

    /// <summary>The lock, or null with why not: <paramref name="held"/> when another engine holds it, else it can't be taken.</summary>
    public static InstanceLock? TryAcquire(string path, out string? error, out bool held)
    {
        held = false;
        try
        {
            var stream = RegularFile.OpenLock(path);
            error = null;
            return new InstanceLock(stream);
        }
        catch (LockHeldException ex)
        {
            held = true;
            error = $"Another InfoPanel engine is already using the data folder '{Path.GetDirectoryName(path)}' ({ex.Message}).";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Can't lock '{path}': {ex.Message}";
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
