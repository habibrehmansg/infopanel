using InfoPanel.Hosting;

namespace InfoPanel.Security;

/// <summary>
/// CORS for web clients served from another origin, given with <c>--allow-origin</c> (exact origins only, off by
/// default). <see cref="SecurityMiddleware"/> calls it after the Origin check, so a disallowed origin is refused with
/// 403 before this runs and never sees a CORS header; the engine's own origin and <c>--dev-origin</c>s are same-origin
/// (or proxied) and get none either.
/// <list type="bullet">
/// <item>Paths: <c>/api</c> and <c>/healthz</c>. <c>/ws</c> needs no CORS: its upgrade passes the Origin check like
/// any request, and the WebSocket middleware's allowed origins include these.</item>
/// <item>A preflight (<c>OPTIONS</c> with <c>Access-Control-Request-Method</c>) is answered 204 before the token check
/// (browsers send preflights without credentials), with the exact origin, the API's methods and request headers,
/// <c>Max-Age</c>, <c>Vary: Origin</c>, and <c>Access-Control-Allow-Private-Network: true</c> when the preflight asks
/// for it. The browser enforces the lists.</item>
/// <item>Every other response on those paths (errors and 401s included, so a client can tell a rejected token from an
/// engine that isn't there) carries <c>Access-Control-Allow-Origin</c>, <c>Vary: Origin</c> and the exposed headers.
/// Never <c>Access-Control-Allow-Credentials</c>: the token travels in <c>Authorization</c>, not in a cookie.</item>
/// </list>
/// </summary>
public static class CrossOrigin
{
    public const string AllowedMethods = "GET, POST, PATCH, DELETE";
    public const string AllowedHeaders = "Authorization, Content-Type";
    /// <summary><c>Location</c> (201 Created) and <c>Content-Disposition</c> (export file name).</summary>
    public const string ExposedHeaders = "Location, Content-Disposition";
    public const string MaxAgeSeconds = "600";

    /// <summary>The paths that answer cross-origin clients.</summary>
    public static bool AppliesTo(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/healthz");

    /// <summary>
    /// For a request from a <c>--allow-origin</c> origin to a CORS path: answers a preflight (returns true, the
    /// response is complete) or arranges the CORS headers on the response (returns false, the pipeline goes on).
    /// </summary>
    public static bool Handle(HttpContext context, EngineRuntime runtime, string origin)
    {
        var request = context.Request;
        if (!runtime.IsCrossOrigin(origin) || !AppliesTo(request.Path))
            return false;

        var response = context.Response;
        if (HttpMethods.IsOptions(request.Method) && request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            response.StatusCode = StatusCodes.Status204NoContent;
            response.Headers.AccessControlAllowOrigin = origin;
            response.Headers.AccessControlAllowMethods = AllowedMethods;
            response.Headers.AccessControlAllowHeaders = AllowedHeaders;
            response.Headers.AccessControlMaxAge = MaxAgeSeconds;
            response.Headers.Vary = "Origin";
            if (string.Equals(request.Headers["Access-Control-Request-Private-Network"], "true", StringComparison.OrdinalIgnoreCase))
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
            return true;
        }

        // On starting, so the headers survive Response.Clear() (UseApiProblems) and apply to every status.
        response.OnStarting(() =>
        {
            response.Headers.AccessControlAllowOrigin = origin;
            response.Headers.AccessControlExposeHeaders = ExposedHeaders;
            response.Headers.Append("Vary", "Origin");
            return Task.CompletedTask;
        });
        return false;
    }
}
