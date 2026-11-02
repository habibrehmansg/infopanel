using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Security;
using InfoPanel.Spec;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// Pairing (spec/docs/api.md, "Pairing"). For the session token only: <c>GET /api/pairings</c> lists the paired clients
/// without secrets (<c>pairingInfo</c>), <c>DELETE /api/pairings/{id}</c> removes one and the pairings it replaced (204,
/// 404 when there is no such pairing listed); both 409 when <c>pairings.json</c> couldn't be read. With no token, under
/// <c>/auth/pairing</c> (mapped by <see cref="AuthEndpoints.MapAuth"/>): <see cref="ExchangeCodeAsync"/>, a one-time code's
/// exchange. Every answer is <c>Cache-Control: no-store</c>; nothing here logs a code, a secret or the token.
/// </summary>
public static class PairingEndpoints
{
    private const string CodeRequestSchema = SpecSchemas.Api + "#/$defs/pairingCodeRequest";

    /// <summary>One text for an unknown, expired and used code, so a prober can't tell them apart.</summary>
    public const string RefusedCode = "This pairing code isn't valid: it is unknown, expired or used. Open the client from InfoPanel again.";

    public static void MapPairingEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/pairings");

        api.MapGet("", (PairingStore pairings, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return pairings.Readable ? Json(pairings.List().Select(Info).ToList()) : Unreadable(pairings);
        }).RequireScope(ApiScope.Session);

        api.MapDelete("/{id}", (string id, PairingStore pairings, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!pairings.Readable)
                return Unreadable(pairings);
            return pairings.Remove(id) ? Results.NoContent() : Problems.NotFound($"No paired client '{id}'.");
        }).RequireScope(ApiScope.Session);
    }

    /// <summary>
    /// <c>POST /auth/pairing/code</c> (<c>pairingCodeRequest</c>): a code's first use within its time answers 201
    /// <c>pairingGrant</c>, the run's session token and instance and a new pairing (none when <c>pairings.json</c> can't be
    /// read or written: the client connects for this run only); <c>replaces</c> hands the client's old pairing over. An
    /// unknown, expired or used code is 403 with one text; a second use also removes the pairing the first made, with a
    /// warning. A body over <c>tokenRequestBytes</c>, not JSON or breaking its definition is 422.
    /// </summary>
    public static async Task<IResult> ExchangeCodeAsync(HttpRequest request, PairingCodes codes, PairingStore pairings, EngineRuntime runtime,
        Workspace workspace, ILoggerFactory loggers, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var limits = workspace.Limits;
        var (body, over) = await ReadBodyAsync(request, limits.TokenRequestBytes, ct);
        if (over)
            return Invalid("", "limit.tokenRequestBytes", $"The body is over the limit of {limits.TokenRequestBytes} bytes (tokenRequestBytes).");
        using var json = ParseJson(body);
        if (json == null)
            return Invalid("", "json", "The body isn't JSON.");
        var check = SpecSchemas.Strict.Validate(json.RootElement, CodeRequestSchema, limits.JsonDepth);
        if (!check.IsValid)
            return InvalidBody([.. check.Errors.Select(ErrorBody)]);

        var exchange = SpecJson.Read<PairingCodeRequest>(json.RootElement);
        // The schema's patterns, without the trailing line break a .NET "$" lets through.
        if (!TokenStore.IsValidName(exchange.Name))
            return Invalid("/name", "schema", "A name is 1 to 64 letters, digits, spaces, dots, underscores and hyphens, without a space at either end.");

        var (outcome, created) = Redeem(codes, pairings, exchange.Code, exchange.Name, exchange.Replaces, runtime.Port,
            loggers.CreateLogger(typeof(PairingEndpoints)));
        return outcome == CodeOutcome.Paired
            ? Json(new PairingGrant
            {
                Pairing = created is null ? null : Info(created) with { Secret = created.Secret },
                Token = runtime.Token,
                Instance = workspace.Instance,
            }, StatusCodes.Status201Created)
            : RefusedCodeProblem();
    }

    /// <summary>
    /// A code's use, as <see cref="ExchangeCodeAsync"/> answers it: its first use makes the pairing (none when
    /// <c>pairings.json</c> can't be read, or its write fails before the file changes: the client connects for this run
    /// only); a second use removes the pairing the first made, with a warning.
    /// </summary>
    internal static (CodeOutcome Outcome, PairingRecord? Created) Redeem(PairingCodes codes, PairingStore pairings, string code, string name,
        string? replaces, int port, ILogger logger)
    {
        PairingRecord? created = null;
        var use = codes.Redeem(code, () =>
        {
            try
            {
                created = pairings.Create(name, replaces, port);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The client connects for this run, unpaired, as when the file can't be read.
                logger.LogWarning(ex, "Could not keep a new paired client in {Path}", pairings.FilePath);
            }

            return created?.Id;
        });

        if (use.Outcome == CodeOutcome.Reused)
        {
            if (use.PairingId is { } id && pairings.RemoveAny(id))
                logger.LogWarning("A pairing code was used twice; the paired client {PairingId} its first use made was removed", id);
            else
                logger.LogWarning("A pairing code was used twice");
        }

        return (use.Outcome, use.Outcome == CodeOutcome.Paired ? created : null);
    }

    private static IResult RefusedCodeProblem() =>
        Problem(new ProblemBody { Title = "Forbidden", Status = StatusCodes.Status403Forbidden, Detail = RefusedCode }, StatusCodes.Status403Forbidden);

    private static IResult Unreadable(PairingStore pairings) =>
        Problem(new ProblemBody
        {
            Title = "Conflict",
            Status = StatusCodes.Status409Conflict,
            Detail = $"The engine couldn't read its paired clients in {pairings.FilePath} when it started, so it keeps none this run. Fix the file or delete it, then restart.",
        }, StatusCodes.Status409Conflict);

    private static PairingInfo Info(PairingRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Created = TokenStore.Format(record.Created),
        LastUsed = record.LastUsed is { } used ? TokenStore.Format(used) : null,
    };
}
