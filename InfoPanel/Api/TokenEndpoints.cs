using InfoPanel.Documents;
using InfoPanel.Security;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// Integration tokens (spec/docs/api.md, "Authentication"), for the session token only: <c>POST /api/tokens</c> creates
/// one and answers its secret, once (201 <c>createdToken</c>); <c>GET /api/tokens</c> lists them without secrets
/// (<c>tokenInfo</c>); <c>DELETE /api/tokens/{id}</c> revokes one (204, 404 when there is none).
/// </summary>
public static class TokenEndpoints
{
    private const string CreateTokenSchema = SpecSchemas.Api + "#/$defs/createTokenRequest";

    public static void MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/tokens");

        api.MapGet("", (TokenStore tokens, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Json(tokens.List().Select(Info).ToList());
        }).RequireScope(ApiScope.Session);

        api.MapPost("", async (HttpRequest request, TokenStore tokens, Workspace workspace, HttpContext context, CancellationToken ct) =>
        {
            // The body's size as received, then the formats' JSON rules (jsonDepth included) and the schema, as every body.
            var limits = workspace.Limits;
            var (body, over) = await ReadBodyAsync(request, limits.TokenRequestBytes, ct);
            if (over)
                return Invalid("", "limit.tokenRequestBytes", $"The body is over the limit of {limits.TokenRequestBytes} bytes (tokenRequestBytes).");
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");

            var check = SpecSchemas.Strict.Validate(json.RootElement, CreateTokenSchema, limits.JsonDepth);
            if (!check.IsValid)
            {
                return Problem(new InvalidProblem
                {
                    Title = "Invalid request",
                    Detail = "The request breaks the formats; see errors.",
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Code = "invalid",
                    Errors = [.. check.Errors.Select(ErrorBody)],
                }, StatusCodes.Status422UnprocessableEntity);
            }

            var create = SpecJson.Read<CreateTokenRequest>(json.RootElement);
            // The schema's pattern, without the trailing line break a .NET "$" lets through.
            if (!TokenStore.IsValidName(create.Name))
                return Invalid("/name", "schema", "A token name is 1 to 64 letters, digits, spaces, dots, underscores and hyphens, without a space at either end.");
            var scopes = create.Scopes.Aggregate(TokenScopes.None, (all, name) => all | TokenStore.ParseScope(name)!.Value);
            TokenRecord record;
            string secret;
            try
            {
                (record, secret) = tokens.Create(create.Name, scopes);
            }
            catch (TokensFullException full)
            {
                // The stored tokens are held to tokensBytes (spec/docs/limits.md): nothing was created.
                return Invalid("", "limit.tokensBytes", $"The stored tokens would be {full.Bytes} bytes, over the limit of {full.Limit} (tokensBytes); revoke tokens to make room.");
            }
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
