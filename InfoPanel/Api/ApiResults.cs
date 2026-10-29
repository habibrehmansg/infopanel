using InfoPanel.Documents;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using System.Text.Json;

namespace InfoPanel.Api;

/// <summary>Bodies of the engine API (spec/schemas/api.schema.json), written with the format's JSON options.</summary>
internal static class ApiResults
{
    public const string ProblemJson = "application/problem+json";

    public static IResult Json<T>(T body, int status = StatusCodes.Status200OK) =>
        Results.Json(body, SpecJson.Options, "application/json", status);

    /// <summary>
    /// The format's options with room for one level more: a body that wraps a document (<c>documentResponse</c>), whose
    /// own depth may be the whole <c>jsonDepth</c>. For writing only; nothing is read with them.
    /// </summary>
    private static readonly JsonSerializerOptions EnvelopeOptions = new(SpecJson.Options) { MaxDepth = SpecJson.SchemaDepth + 1 };

    /// <summary>A body that wraps a document, written with <see cref="EnvelopeOptions"/>.</summary>
    public static IResult EnvelopeJson<T>(T body) => Results.Json(body, EnvelopeOptions, "application/json", StatusCodes.Status200OK);

    /// <summary>A problem body (<c>application/problem+json</c>) with the status it carries.</summary>
    public static IResult Problem<T>(T problem, int status) => Results.Json(problem, SpecJson.Options, ProblemJson, status);

    /// <summary>A commit, undo, redo or revert answer: 200 <c>commitResult</c>, 409 or 422.</summary>
    public static IResult Outcome(CommitOutcome outcome) => outcome switch
    {
        { Result: { } result } => Json(result),
        { Conflict: { } conflict } => Problem(conflict, StatusCodes.Status409Conflict),
        { Expired: { } expired } => Problem(expired, StatusCodes.Status409Conflict),
        _ => Problem(outcome.Invalid!, StatusCodes.Status422UnprocessableEntity),
    };

    /// <summary>422 <c>invalidProblem</c> with one error.</summary>
    public static IResult Invalid(string path, string code, string message) => Problem(new InvalidProblem
    {
        Title = "Invalid request",
        Detail = message,
        Status = StatusCodes.Status422UnprocessableEntity,
        Code = "invalid",
        Errors = [new ValidationErrorBody { Path = path, Code = code, Message = message }],
    }, StatusCodes.Status422UnprocessableEntity);

    /// <summary>
    /// A schema error as every body reports it (spec/docs/validation.md#codes): <c>schema</c> (a repeated member name
    /// included), except the count of the errors not listed after the first 100 (<c>more</c>, at <c>""</c>), a number that
    /// rounds to ±∞ or a string that isn't Unicode text (<c>json</c>), and content nested too deeply (<c>limit.jsonDepth</c>).
    /// </summary>
    public static ValidationErrorBody ErrorBody(SchemaError e) => new()
    {
        Path = e.InstancePath,
        Code = e.Keyword switch
        {
            "more" or "json" => e.Keyword,
            "depth" => "limit.jsonDepth",
            _ => "schema",
        },
        Message = e.Keyword is "more" or "json" ? e.Message : $"{e.Message} ({e.Keyword})",
    };

    public static IResult ProfileNotFound(string id) => Security.Problems.NotFound($"No profile '{id}'.");

    /// <summary>
    /// The request's body, read up to <paramref name="maxBytes"/>, with or without a <c>Content-Length</c>; over it when
    /// longer, and then empty. At most <paramref name="maxBytes"/> + 1 bytes are read (none when the declared length is
    /// over), and none of a body over the limit is kept.
    /// </summary>
    public static async Task<(byte[] Bytes, bool Over)> ReadBodyAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        if (request.ContentLength > maxBytes)
            return ([], true);

        using var buffer = new MemoryStream();
        var chunk = new byte[(int)Math.Min(81920, maxBytes + 1)];
        while (true)
        {
            // Never more than the allowance left plus one byte: the extra byte is what tells a body is over.
            var room = (int)Math.Min(chunk.Length, maxBytes + 1 - buffer.Length);
            var read = await request.Body.ReadAsync(chunk.AsMemory(0, room), ct);
            if (read == 0)
                break;
            if (buffer.Length + read > maxBytes)
                return ([], true);
            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }

    /// <summary>Lifts Kestrel's own body size limit for an endpoint that reads its body with <see cref="ReadBodyAsync"/>.</summary>
    public static RouteHandlerBuilder WithOwnBodyLimit(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new UnlimitedBody());

    private sealed class UnlimitedBody : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => null;
    }

    /// <summary>A body parsed as JSON at any depth (the checks behind it count the depth), or null when it isn't JSON.</summary>
    public static JsonDocument? ParseJson(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes, SpecJson.DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
