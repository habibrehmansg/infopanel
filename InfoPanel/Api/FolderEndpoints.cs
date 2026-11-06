using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Security;

namespace InfoPanel.Api;

/// <summary>
/// Opening the engine's own folders (spec/docs/api.md, "Folders"), for the session token only:
/// <c>POST /api/folders/{folder}/open</c> opens the data folder (<c>data</c>), its logs folder (<c>logs</c>) or its plugins
/// folder (<c>plugins</c>, created when it isn't there, never through a link) in the system's file manager through
/// <see cref="IFolderOpener"/> and answers 204. Any other name is 404: the engine opens only its own folders, never a path
/// a client gives. An opener that fails, or a plugins folder that is a link or can't be created, is logged and answered 500.
/// </summary>
public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/folders/{folder}/open", (string folder, DataPaths paths, IFolderOpener opener, ILoggerFactory loggers) =>
        {
            if (PathOf(folder, paths) is not { } path)
                return Problems.NotFound($"No folder '{folder}': the engine opens its data folder (data), its logs folder (logs) and its plugins folder (plugins).");

            try
            {
                if (folder == "plugins")
                    Create(path);
                opener.Open(path);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger(typeof(FolderEndpoints).FullName!).LogError(ex, "Could not open the {Folder} folder {Path} in the file manager", folder, path);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Internal Server Error",
                    detail: $"The engine couldn't open its {folder} folder; see its log.");
            }

            return Results.NoContent();
        }).RequireScope(ApiScope.Session);
    }

    /// <summary>The folder a name stands for: exactly <c>data</c>, <c>logs</c> or <c>plugins</c>, else none.</summary>
    private static string? PathOf(string folder, DataPaths paths) => folder switch
    {
        "data" => paths.Root,
        "logs" => paths.LogsDirectory,
        "plugins" => paths.PluginsDirectory,
        _ => null,
    };

    /// <summary>Creates the folder at <paramref name="path"/> when nothing is there; a link there (to anything, or nothing) is refused, never followed.</summary>
    /// <exception cref="IOException">It is a link, or it can't be created.</exception>
    private static void Create(string path)
    {
        if (new FileInfo(path).LinkTarget != null)
            throw new IOException($"{path} is a link, which the engine doesn't follow");
        Directory.CreateDirectory(path);
    }
}
