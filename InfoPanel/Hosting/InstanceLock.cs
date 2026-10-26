namespace InfoPanel.Hosting;

/// <summary>
/// Holds <c>{data}/engine.lock</c> open with <see cref="FileShare.None"/> for the engine's lifetime, so a second
/// engine on the same data folder can't start (on macOS and Linux .NET backs this with an advisory
/// <c>flock</c>, which every .NET process honours). The file is left in place on exit: deleting it could let
/// two engines lock different files of the same name.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static InstanceLock? TryAcquire(string path, out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            error = null;
            return new InstanceLock(stream);
        }
        catch (IOException ex)
        {
            error = $"Another InfoPanel engine is already using the data folder '{Path.GetDirectoryName(path)}' ({ex.Message}).";
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"Can't lock '{path}': {ex.Message}";
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
