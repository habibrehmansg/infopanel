using InfoPanel.Hosting;
using InfoPanel.Persistence;
using InfoPanel.Security;

namespace InfoPanel.Api;

/// <summary>
/// Opening the engine's own folders (spec/docs/api.md, "Folders"), for the session token only:
/// <c>POST /api/folders/{folder}/open</c> opens the data folder (<c>data</c>) or its logs folder (<c>logs</c>) in the
/// system's file manager through <see cref="IFolderOpener"/> and answers 204. Any other name is 404: the engine opens only
/// its own folders, never a path a client gives. An opener that fails is logged and answered 500.
/// </summary>
public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/folders/{folder}/open", (string folder, DataPaths paths, IFolderOpener opener, ILoggerFactory loggers) =>
        {
            if (PathOf(folder, paths) is not { } path)
                return Problems.NotFound($"No folder '{folder}': the engine opens its data folder (data) and its logs folder (logs).");

            try
            {
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

    /// <summary>The folder a name stands for: exactly <c>data</c> or <c>logs</c>, else none.</summary>
    private static string? PathOf(string folder, DataPaths paths) => folder switch
    {
        "data" => paths.Root,
        "logs" => paths.LogsDirectory,
        _ => null,
    };
}
