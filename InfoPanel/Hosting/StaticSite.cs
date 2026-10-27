using InfoPanel.Security;
using Microsoft.Extensions.FileProviders;

namespace InfoPanel.Hosting;

/// <summary>
/// Serves the web client's built files: <c>UseDefaultFiles</c> + <c>UseStaticFiles</c> over the web root, and
/// <c>index.html</c> for every other non-file path except <c>/api</c>, <c>/ws</c> and <c>/auth</c>, which 404
/// (<c>/api</c> as problem+json). Without a web root the API still works and pages say how to point the engine at
/// a built client.
/// </summary>
public static class StaticSite
{
    public const string NotBuiltMessage = """
        InfoPanel engine is running, but no web client is installed.

        The API is available. To serve the editor, restart the engine with the folder
        that holds a built web client (the one with its index.html):
          InfoPanel --web-root <folder>

        Without --web-root, the engine serves the wwwroot folder next to it
        if that folder contains an index.html.
        """;

    public static void UseClientFiles(this WebApplication app, string? webRoot)
    {
        if (webRoot == null)
            return;

        var files = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    }

    public static void MapClientFallback(this WebApplication app, string? webRoot)
    {
        // Any token may learn that a path doesn't exist; it reads and changes nothing.
        app.MapFallback("/api/{**rest}", (HttpContext context) =>
            Problems.NotFound($"No API endpoint {context.Request.Method} {context.Request.Path}.")).RequireScope(ApiScope.Read);
        app.MapFallback("/ws/{**rest}", () => Results.NotFound());
        app.MapFallback("/auth/{**rest}", () => Results.NotFound());

        if (webRoot == null)
        {
            app.MapFallback(() => Results.Text(NotBuiltMessage, "text/plain"));
            return;
        }

        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = new PhysicalFileProvider(webRoot) });
    }
}
