using InfoPanel.Security;
using InfoPanel.Spec;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// Integration tokens (spec/docs/api.md, "Authentication"), for the session token only: <c>POST /api/tokens</c> creates
/// one and answers its secret, once (201 <c>createdToken</c>); <c>GET /api/tokens</c> lists them without secrets
/// (<c>tokenInfo</c>); <c>DELETE /api/tokens/{id}</c> revokes one (204, 404 when there is none).
/// </summary>
public static class TokenEndpoints
{
    /// <summary>The largest body of a create: a name and two scopes, with room to spare.</summary>
    private const int MaxBodyBytes = 4096;

    /// <summary>How deep a create's body is checked: an object holding an array of strings.</summary>
    private const int MaxBodyDepth = 4;

    private const string CreateTokenSchema = SpecSchemas.Api + "#/$defs/createTokenRequest";

    public static void MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/tokens");

        api.MapGet("", (TokenStore tokens, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Json(tokens.List().Select(Info).ToList());
        }).RequireScope(ApiScope.Session);

        api.MapPost("", async (HttpRequest request, TokenStore tokens, HttpContext context, CancellationToken ct) =>
        {
            var (body, over) = await ReadBodyAsync(request, MaxBodyBytes, ct);
            using var json = over ? null : ParseJson(body);
            if (json == null)
                return Invalid("", over ? "schema" : "json", over ? "The body is {\"name\", \"scopes\"}." : "The body isn't JSON.");

            var check = SpecSchemas.Strict.Validate(json.RootElement, CreateTokenSchema, MaxBodyDepth);
            if (!check.IsValid)
            {
                return Problem(new InvalidProblem
                {
                    Title = "Invalid request",
                    Detail = "The request breaks the formats; see errors.",
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Code = "invalid",
                    Errors = [.. check.Errors.Select(e => new ValidationErrorBody
                    {
                        Path = e.InstancePath,
                        Code = e.Keyword == "depth" ? "limit.jsonDepth" : "schema",
                        Message = $"{e.Message} ({e.Keyword})",
                    })],
                }, StatusCodes.Status422UnprocessableEntity);
            }

            var create = SpecJson.Read<CreateTokenRequest>(json.RootElement);
            // The schema's pattern, without the trailing line break a .NET "$" lets through.
            if (!TokenStore.IsValidName(create.Name))
                return Invalid("/name", "schema", "A token name is 1 to 64 letters, digits, spaces, dots, underscores and hyphens, without a space at either end.");
            var scopes = create.Scopes.Aggregate(TokenScopes.None, (all, name) => all | TokenStore.ParseScope(name)!.Value);
            var (record, secret) = tokens.Create(create.Name, scopes);
            context.Response.Headers.CacheControl = "no-store";
            return Json(Info(record) with { Token = secret }, StatusCodes.Status201Created);
        }).RequireScope(ApiScope.Session);

        api.MapDelete("/{id}", (string id, TokenStore tokens) =>
            tokens.Revoke(id) ? Results.NoContent() : Problems.NotFound($"No token '{id}'.")).RequireScope(ApiScope.Session);
    }

    private static TokenInfo Info(TokenRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Kind = record.Kind,
        Scopes = TokenStore.ScopeNames(record.Scopes),
        Created = TokenStore.Format(record.Created),
        LastUsed = record.LastUsed is { } used ? TokenStore.Format(used) : null,
    };
}
