using InfoPanel.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace InfoPanel.Security;

/// <summary>
/// The engine's request checks, after host filtering:
/// <list type="bullet">
/// <item>An <c>Origin</c> header, when present on any request (any method, WebSocket upgrades included), must be
/// <c>http://127.0.0.1:{port}</c>, <c>http://localhost:{port}</c> or a <c>--dev-origin</c>; otherwise 403.</item>
/// <item>An origin given with <c>--allow-origin</c> gets CORS on <c>/api</c> and <c>/healthz</c> (<see cref="CrossOrigin"/>);
/// its preflights are answered here, before the token check.</item>
/// <item><c>/api/*</c> needs <c>Authorization: Bearer {token}</c>. <c>/ws</c> needs the WebSocket subprotocol
/// <c>infopanel.v1</c>, and the token either as the subprotocol <c>auth.{token}</c> (browsers can't set headers on a
/// WebSocket) or, when no <c>auth.*</c> subprotocol is offered, in the first message after the upgrade
/// (<see cref="PendingWebSocketAuthItem"/>, checked by <c>/ws</c> itself). Otherwise 401, before any upgrade; an
/// <c>auth.*</c> subprotocol with a wrong token is always refused. There is no cookie: browsers send loopback cookies
/// to every port on the host, so another local server would receive it. Everything else (static files,
/// <c>/healthz</c>, <c>/auth/{token}</c>) is public.</item>
/// </list>
/// </summary>
public sealed class SecurityMiddleware(RequestDelegate next, EngineRuntime runtime)
{
    /// <summary>
    /// Set in <see cref="HttpContext.Items"/> on a <c>/ws</c> request that offers <c>infopanel.v1</c> and no
    /// <c>auth.*</c> subprotocol: the socket is accepted and must authenticate with its first message.
    /// </summary>
    public const string PendingWebSocketAuthItem = "InfoPanel.WebSocketAuthPending";

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (request.Headers.TryGetValue("Origin", out var origins))
        {
            if (origins.Count != 1 || !runtime.IsAllowedOrigin(origins[0] ?? ""))
                return Problems.Write(context, StatusCodes.Status403Forbidden, "Forbidden", "This origin may not call the engine.");

            if (CrossOrigin.Handle(context, runtime, origins[0]!))
                return Task.CompletedTask;
        }

        if (request.Path.StartsWithSegments("/api") && !HasBearerToken(request))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Problems.Write(context, StatusCodes.Status401Unauthorized, "Unauthorized",
                "Open InfoPanel from the link it logs, or send the session token as a Bearer token.");
        }

        if (request.Path.StartsWithSegments("/ws"))
        {
            switch (WebSocketToken(request))
            {
                case TokenCheck.Valid:
                    break;
                case TokenCheck.Deferred:
                    context.Items[PendingWebSocketAuthItem] = true;
                    break;
                default:
                    return Problems.Write(context, StatusCodes.Status401Unauthorized, "Unauthorized",
                        $"Connect with the WebSocket subprotocol '{EngineRuntime.WebSocketProtocol}' and either the subprotocol " +
                        $"'{EngineRuntime.WebSocketAuthPrefix}{{token}}' or a first message {{\"type\":\"auth\",\"token\":\"…\"}}.");
            }
        }

        return next(context);
    }

    private enum TokenCheck { Invalid, Valid, Deferred }

    private bool HasBearerToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        return authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
            && runtime.IsValidToken(authorization[bearer.Length..].Trim());
    }

    /// <summary>
    /// <c>Sec-WebSocket-Protocol</c> must list <c>infopanel.v1</c>. With an <c>auth.*</c> subprotocol the token must be
    /// valid; with none, the check is deferred to the first message. <c>/ws</c> accepts with <c>infopanel.v1</c> only,
    /// never echoing the token.
    /// </summary>
    private TokenCheck WebSocketToken(HttpRequest request)
    {
        var protocols = request.Headers.GetCommaSeparatedValues(HeaderNames.SecWebSocketProtocol);
        if (!protocols.Contains(EngineRuntime.WebSocketProtocol, StringComparer.Ordinal))
            return TokenCheck.Invalid;

        var offered = false;
        var valid = false;
        foreach (var protocol in protocols)
        {
            if (protocol.StartsWith(EngineRuntime.WebSocketAuthPrefix, StringComparison.Ordinal))
            {
                offered = true;
                valid |= runtime.IsValidToken(protocol[EngineRuntime.WebSocketAuthPrefix.Length..]);
            }
        }

        return valid ? TokenCheck.Valid : offered ? TokenCheck.Invalid : TokenCheck.Deferred;
    }
}

/// <summary>
/// <c>GET /auth/{token}</c>: a valid token redirects to <c>/#token={token}</c>. The web client reads the fragment (which is
/// never sent to a server), keeps the token in <c>localStorage</c> (per origin, so per port), removes it from the
/// address bar and sends it as a Bearer token. No cookie is set.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/{token}", (string token, EngineRuntime runtime, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!runtime.IsValidToken(token))
            {
                return Results.Text("This sign-in link is not valid. Use the link the running engine logged.",
                    "text/plain", statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Redirect("/#token=" + runtime.Token);
        });
    }
}

/// <summary>Writes <c>application/problem+json</c> responses.</summary>
public static class Problems
{
    public static Task Write(HttpContext context, int status, string title, string? detail = null) =>
        Results.Problem(detail: detail, statusCode: status, title: title).ExecuteAsync(context);

    public static IResult NotFound(string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not Found");

    /// <summary>
    /// Under <c>/api</c>: an unhandled exception becomes a 500 problem, and an error status with no body
    /// (unmatched route, wrong method, bad parameter) gets a problem body.
    /// </summary>
    public static IApplicationBuilder UseApiProblems(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            // Thrown for unbindable parameters when RouteHandlerOptions.ThrowOnBadRequest (Development).
            context.Response.Clear();
            await Write(context, ex.StatusCode, ReasonPhrases.GetReasonPhrase(ex.StatusCode), ex.Message);
            return;
        }
        catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestServices.GetRequiredService<ILogger<SecurityMiddleware>>()
                .LogError(ex, "Unhandled error in {Method} {Path}", context.Request.Method, context.Request.Path);
            context.Response.Clear();
            await Write(context, StatusCodes.Status500InternalServerError, "Internal Server Error", "The engine hit an error; see its log.");
            return;
        }

        var response = context.Response;
        if (!response.HasStarted && response.StatusCode >= 400 && response.ContentType == null && response.ContentLength == null)
        {
            var title = ReasonPhrases.GetReasonPhrase(response.StatusCode);
            await Write(context, response.StatusCode, string.IsNullOrEmpty(title) ? "Error" : title);
        }
    });
}
