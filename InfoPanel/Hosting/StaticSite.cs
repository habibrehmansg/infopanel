using InfoPanel.Security;
using Microsoft.Extensions.FileProviders;

namespace InfoPanel.Hosting;

/// <summary>
/// Serves the web client's built files: <c>UseDefaultFiles</c> + <c>UseStaticFiles</c> over the web root, and
/// <c>index.html</c> for every other non-file path except <c>/api</c>, <c>/ws</c> and <c>/auth</c>, which 404
/// (<c>/api</c> as problem+json). Without a web root the API still works and pages say how to build the web client.
/// </summary>
public static class StaticSite
{
    public const string NotBuiltMessage = """
        InfoPanel engine is running, but the web client isn't built.

        Build it from a source checkout:
          cd client && npm ci && npm run build

        then restart the engine (it finds client/dist), or pass --web-root <folder>.
        For development, run `npm run dev` in client/ and open the dev-server link the engine logs.
        """;

    public static void UseWebClientFiles(this WebApplication app, string? webRoot)
    {
        if (webRoot == null)
            return;

        var files = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
    }

    public static void MapWebClientFallback(this WebApplication app, string? webRoot)
    {
        app.MapFallback("/api/{**rest}", (HttpContext context) =>
            Problems.NotFound($"No API endpoint {context.Request.Method} {context.Request.Path}."));
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
