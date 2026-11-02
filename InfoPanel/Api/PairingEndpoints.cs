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
/// exchange, and the proofs, <see cref="ChallengeAsync"/> and <see cref="ProveAsync"/> (<see cref="PairingProofs"/>). Every answer is <c>Cache-Control: no-store</c>; nothing here logs a code, a secret or the token.
/// </summary>
public static class PairingEndpoints
{
    private const string CodeRequestSchema = SpecSchemas.Api + "#/$defs/pairingCodeRequest";
    private const string ChallengeRequestSchema = SpecSchemas.Api + "#/$defs/pairingChallengeRequest";
    private const string ProofRequestSchema = SpecSchemas.Api + "#/$defs/pairingProofRequest";

    /// <summary>One text for every failed proof, so a prober learns nothing from which check failed.</summary>
    public const string RefusedProof = "This pairing proof isn't valid.";

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

    /// <summary>
    /// <c>POST /auth/pairing/challenge</c> (<c>pairingChallengeRequest</c>): 200 <c>pairingChallenge</c>, the engine's nonce
    /// and proof, for a pairing the engine has (a replaced one's too); 404 for any other; 422 for a body that breaks its
    /// definition.
    /// </summary>
    public static async Task<IResult> ChallengeAsync(HttpRequest request, PairingProofs proofs, Workspace workspace, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (body, refused) = await ReadAsync<PairingChallengeRequest>(request, workspace, ChallengeRequestSchema, ct);
        if (refused != null)
            return refused;
        if (Bytes(body!.Nonce) is not { } clientNonce)
            return Invalid("/nonce", "schema", "A nonce is 32 bytes as 43 base64url characters.");

        return proofs.Challenge(body.Pairing, clientNonce) is { } challenge
            ? Json(new PairingChallenge { Nonce = PairingStore.Base64Url(challenge.Nonce), Proof = PairingStore.Base64Url(challenge.Proof) })
            : Problems.NotFound($"No pairing '{body.Pairing}'.");
    }

    /// <summary>
    /// <c>POST /auth/pairing/proof</c> (<c>pairingProofRequest</c>): 200 <c>pairingSession</c>, the run's session token and
    /// instance with the engine's proof over them, once the client's proof checks; 403 with one text for any failed check
    /// (a challenge gone, expired or another pairing's, a replaced pairing, a proof that doesn't check); 422 for a body that
    /// breaks its definition.
    /// </summary>
    public static async Task<IResult> ProveAsync(HttpRequest request, PairingProofs proofs, Workspace workspace, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (body, refused) = await ReadAsync<PairingProofRequest>(request, workspace, ProofRequestSchema, ct);
        if (refused != null)
            return refused;
        if (Bytes(body!.Nonce) is not { } engineNonce)
            return Invalid("/nonce", "schema", "A nonce is 32 bytes as 43 base64url characters.");
        if (Bytes(body.Proof) is not { } proof)
            return Invalid("/proof", "schema", "A proof is 32 bytes as 43 base64url characters.");

        return proofs.Prove(body.Pairing, engineNonce, proof) is { } session
            ? Json(new PairingSession { Token = session.Token, Instance = session.Instance, Proof = PairingStore.Base64Url(session.Proof) })
            : Problem(new ProblemBody { Title = "Forbidden", Status = StatusCodes.Status403Forbidden, Detail = RefusedProof }, StatusCodes.Status403Forbidden);
    }

    /// <summary>A pairing request's body: held to <c>tokenRequestBytes</c>, JSON, and its definition; else the 422 to answer.</summary>
    private static async Task<(T? Body, IResult? Refused)> ReadAsync<T>(HttpRequest request, Workspace workspace, string schema, CancellationToken ct)
    {
        var limits = workspace.Limits;
        var (bytes, over) = await ReadBodyAsync(request, limits.TokenRequestBytes, ct);
        if (over)
            return (default, Invalid("", "limit.tokenRequestBytes", $"The body is over the limit of {limits.TokenRequestBytes} bytes (tokenRequestBytes)."));
        using var json = ParseJson(bytes);
        if (json == null)
            return (default, Invalid("", "json", "The body isn't JSON."));
        var check = SpecSchemas.Strict.Validate(json.RootElement, schema, limits.JsonDepth);
        if (!check.IsValid)
            return (default, InvalidBody([.. check.Errors.Select(ErrorBody)]));
        return (SpecJson.Read<T>(json.RootElement), null);
    }

    /// <summary>32 bytes from 43 base64url characters (the schema's pattern checked first); null when they don't decode to 32.</summary>
    private static byte[]? Bytes(string text)
    {
        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(text);
            return bytes.Length == PairingProofs.NonceBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
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
