using System.Text.Json;

namespace InfoPanel.Hosting;

/// <summary>
/// <c>{data}/runtime.json</c>: <c>{pid, port, url}</c> of the running engine, for launchers and companions.
/// Written owner-only (0600) on Unix through a temp file, and deleted on clean shutdown. It is a hint: a crash leaves it
/// behind and its pid may be reused, so whether an engine runs on the folder is its lock (<c>engine.lock</c>), and which
/// run answers is <c>GET /api/session</c> (its <c>instance</c>) at that URL (docs/2.0/data-folder.md).
/// </summary>
public static class RuntimeFile
{
    public sealed record Content(int Pid, int Port, string Url);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(string path, Content content)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try
        {
            using (var stream = new FileStream(temp, options))
                JsonSerializer.Serialize(stream, content, Json);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not delete {Path}", path);
        }
    }
}
