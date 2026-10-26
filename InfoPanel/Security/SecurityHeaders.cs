namespace InfoPanel.Security;

/// <summary>
/// Adds the security headers to every response. Registered as the first <see cref="IStartupFilter"/>, so it wraps
/// the whole pipeline, host filtering included, and its <c>OnStarting</c> callback survives
/// <c>Response.Clear()</c>.
/// </summary>
public sealed class SecurityHeadersStartupFilter : IStartupFilter
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' blob: data:; connect-src 'self'; style-src 'self' 'unsafe-inline'";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            var response = context.Response;
            response.OnStarting(() =>
            {
                response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
                response.Headers.XFrameOptions = "DENY";
                response.Headers.XContentTypeOptions = "nosniff";
                // The token travels in /auth/{token} and /#token=…: never let it leak through a Referer.
                response.Headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            return nextMiddleware(context);
        });
        next(app);
    };
}
