using InfoPanel.Api;
using InfoPanel.Spec;

namespace InfoPanel.Security;

/// <summary>What an endpoint needs from its caller's token.</summary>
public enum ApiScope
{
    /// <summary>Reads: every token has it.</summary>
    Read,

    /// <summary>Changes a document, a blob or a profile.</summary>
    Edit,

    /// <summary>Only the session token: the token endpoints themselves.</summary>
    Session,
}

/// <summary>An endpoint's declared <see cref="ApiScope"/> (<see cref="ApiScopes.RequireScope{TBuilder}"/>).</summary>
public sealed record ApiScopeMetadata(ApiScope Scope);

/// <summary>
/// Scopes fail closed: after routing, a caller whose token lacks the scope its endpoint declares gets 403
/// <c>scopeProblem</c>, and an endpoint that declares none is treated as <see cref="ApiScope.Session"/>. Requests
/// without a <see cref="Caller"/> (the public paths, and a <c>/ws</c> upgrade that authenticates with its first message,
/// which <c>/ws</c> checks itself) pass; <see cref="SecurityMiddleware"/> has already refused <c>/api</c> without a token.
/// </summary>
public sealed class ScopeFilter(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (Caller.Of(context) is not { } caller || context.GetEndpoint() is not { } endpoint)
            return next(context);

        var scope = endpoint.Metadata.GetMetadata<ApiScopeMetadata>()?.Scope ?? ApiScope.Session;
        return caller.Allows(scope) ? next(context) : ApiScopes.Forbidden(scope).ExecuteAsync(context);
    }
}

public static class ApiScopes
{
    /// <summary>403 <c>scopeProblem</c>: the token is valid but lacks <paramref name="scope"/>.</summary>
    public static IResult Forbidden(ApiScope scope) => ApiResults.Problem(new ScopeProblem
    {
        Title = "Forbidden",
        Detail = scope == ApiScope.Session
            ? "Only the session token may do this."
            : $"This token doesn't have the '{Name(scope)}' scope.",
        Status = StatusCodes.Status403Forbidden,
        Code = "scope",
        Scope = Name(scope),
    }, StatusCodes.Status403Forbidden);

    public static string Name(ApiScope scope) => scope switch
    {
        ApiScope.Read => "read",
        ApiScope.Edit => "edit",
        _ => "session",
    };

    /// <summary>Declares the scope an endpoint (or every endpoint of a group) needs.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, ApiScope scope) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ApiScopeMetadata(scope));
}
