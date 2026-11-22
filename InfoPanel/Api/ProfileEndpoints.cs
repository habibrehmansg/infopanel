using InfoPanel.Documents;
using InfoPanel.Import;
using InfoPanel.Output;
using InfoPanel.Security;
using InfoPanel.Spec;
using System.Text.Json;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// Profiles and their documents (spec/docs/api.md, commit.md): list, create, duplicate, delete, read, export (the
/// document, and the profile archive of spec/docs/archive.md), thumbnail, commit, undo, redo, revert and save. All under <c>/api</c>, behind the Origin and Bearer checks of
/// <c>SecurityMiddleware</c>; reads need the <c>read</c> scope, and every request that changes a profile <c>edit</c>. A request on a profile is checked in order: the profile exists (404), it can be opened
/// (409 <c>profileStatusProblem</c>), the body (422), then what the request itself checks. Errors are
/// <c>application/problem+json</c>.
/// </summary>
public static class ProfileEndpoints
{
    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/profiles");

        api.MapGet("", (Workspace workspace) => Json(workspace.List())).RequireScope(ApiScope.Read);

        api.MapPost("", async (HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            // Read as a stored document is, up to documentBytes plus the read allowance: room for the envelope around it.
            var (body, over) = await ReadBodyAsync(request, DocumentStore.ReadCap(workspace.Limits), ct);
            if (over)
                return Invalid("/document", "limit.documentBytes", $"The document is over the {workspace.Limits.DocumentBytes}-byte limit.");
            return Created(workspace.Create(body));
        }).WithOwnBodyLimit().RequireScope(ApiScope.Edit);

        api.MapPost("/{id}/duplicate", async (string id, HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var found = workspace.Find(id);
            if (Refused(id, found) is { } refused)
                return refused;

            var (body, over) = await ReadBodyAsync(request, workspace.Limits.RequestBytes, ct);
            if (over)
                return OverRequestBytes(workspace);
            var outcome = workspace.Duplicate(found.Document!, body);
            return outcome.NotFound ? ProfileNotFound(id) : Created(outcome);
        }).RequireScope(ApiScope.Edit);

        // The document's tag is required, as one query parameter, checked first as a query parameter is (400); then the
        // profile (404) and the tag (409 instance).
        api.MapDelete("/{id}", (string id, HttpRequest request, Workspace workspace) =>
            TagOf(request) is not { } instance
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request",
                    detail: "Name the document's tag once: DELETE /api/profiles/{id}?instance={tag}, the instance of its entry or document.")
                : workspace.Delete(id, instance) switch
                {
                    { Deleted: true } => Results.NoContent(),
                    { Conflict: { } conflict } => Problem(conflict, StatusCodes.Status409Conflict),
                    _ => ProfileNotFound(id),
                }).RequireScope(ApiScope.Edit);

        api.MapGet("/{id}", (string id, Workspace workspace) => WithDocument(workspace, id, doc =>
        {
            var state = doc.Read();
            return EnvelopeJson(new DocumentResponse
            {
                Instance = doc.Instance,
                Rev = state.Generation.Rev,
                Dirty = state.Dirty,
                CanUndo = state.CanUndo,
                CanRedo = state.CanRedo,
                Recovered = state.RecoveredText,
                Document = state.Document,
            });
        })).RequireScope(ApiScope.Read);

        api.MapGet("/{id}/document", (string id, Workspace workspace, HttpContext context) =>
        {
            var (document, problem) = workspace.Export(id);
            if (problem != null)
                return Problem(problem, StatusCodes.Status409Conflict);
            if (document == null)
                return ProfileNotFound(id);

            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(document, "application/json");
        }).RequireScope(ApiScope.Read);

        // The profile archive (spec/docs/archive.md): built in a temporary file before anything is sent, which goes when the
        // answer has been written. 409 for a profile that can't be opened, and for an archive its import would refuse.
        api.MapGet("/{id}/archive", (string id, Workspace workspace, ImportLimits limits, HttpContext context) =>
        {
            ExportedArchive? archive;
            ProfileStatusProblem? problem;
            try
            {
                (archive, problem) = ProfileArchive.Export(workspace, id, DateTimeOffset.UtcNow, limits);
            }
            catch (ImportRefusal refusal)
            {
                return Problem(new ExportProblem
                {
                    Title = "Conflict",
                    Detail = refusal.Message,
                    Status = StatusCodes.Status409Conflict,
                    Code = "export",
                    Errors = [.. refusal.Errors.Select(e => new ValidationErrorBody { Path = e.Path, Code = e.Code, Message = e.Message })],
                }, StatusCodes.Status409Conflict);
            }
            if (problem != null)
                return Problem(problem, StatusCodes.Status409Conflict);
            if (archive == null)
                return ProfileNotFound(id);

            context.Response.Headers.CacheControl = "no-store";
            return Results.File(archive.Content, "application/zip", archive.FileName);
        }).RequireScope(ApiScope.Read);

        api.MapGet("/{id}/thumbnail", (string id, int? w, Workspace workspace, RenderService render, HttpContext context) =>
            WithDocument(workspace, id, doc =>
            {
                // From the tick a running output last drew, without advancing it; else a one-off snapshot.
                var png = render.RenderOnce(doc, w ?? Thumbnail.DefaultWidth);
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(png, "image/png");
            })).RequireScope(ApiScope.Read);

        api.MapPost("/{id}/commit", async (string id, HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var found = workspace.Find(id);
            if (Refused(id, found) is { } refused)
                return refused;

            // The exact bytes: the opId digest covers every byte. A body over commitBytes, from its Content-Length or
            // as read, isn't kept: it is answered here, as the document would answer it.
            var (body, over) = await ReadBodyAsync(request, workspace.Limits.CommitBytes, ct);
            if (over)
                return Invalid("", "limit.commitBytes", $"The body is over the limit of {workspace.Limits.CommitBytes} bytes (commitBytes).");
            return Acting(id, () => Outcome(found.Document!.Commit(body)));
        }).WithOwnBodyLimit().RequireScope(ApiScope.Edit);

        MapHistory(api, "undo", (doc, body) => doc.Undo(body));
        MapHistory(api, "redo", (doc, body) => doc.Redo(body));
        MapHistory(api, "revert", (doc, body) => doc.Revert(body));

        // Without a body (no bytes) unguarded; with one, a historyRequest checked as undo checks it (spec/docs/commit.md).
        api.MapPost("/{id}/save", async (string id, HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var found = workspace.Find(id);
            if (Refused(id, found) is { } refused)
                return refused;

            var (body, over) = await ReadBodyAsync(request, workspace.Limits.RequestBytes, ct);
            if (over)
                return OverRequestBytes(workspace);
            if (body.Length == 0)
                return Acting(id, () => Json(found.Document!.Save()));
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            return Acting(id, () => Outcome(found.Document!.Save(json.RootElement)));
        }).RequireScope(ApiScope.Edit);

        MapVersions(api);
    }

    /// <summary>
    /// A profile's earlier saves (spec/docs/api.md, "Earlier saves"; the <c>profiles.versions</c> feature): the list, a
    /// version's picture and its restore. <c>n</c> is checked first, as a query parameter is (400), and only as a
    /// number: a version is found by that number in the store's listing, never by a path the request gives. Then the
    /// profile (404, 409), the version (404, 409 with the version's own status and problem), and for the restore the
    /// body and what undo checks.
    /// </summary>
    private static void MapVersions(RouteGroupBuilder api)
    {
        api.MapGet("/{id}/versions", (string id, Workspace workspace) =>
        {
            var (found, problem, list) = workspace.Versions(id);
            return problem != null ? Problem(problem, StatusCodes.Status409Conflict) : found ? Json(list!) : ProfileNotFound(id);
        }).RequireScope(ApiScope.Read);

        // Drawn one at a time, through the gate previews are drawn through (a second meanwhile is 409 problem, as a
        // preview's), admitted by the process budget with its reservation held through the encoding.
        api.MapGet("/{id}/versions/{n}/thumbnail", (string id, string n, int? w, Workspace workspace, RenderService render, PreviewGate gate,
            HttpContext context) =>
        {
            if (VersionNumberOf(n) is not { } number)
                return BadVersionNumber();
            var found = workspace.FindVersion(id, number);
            if (Refused(id, number, found) is { } refused)
                return refused;
            if (!gate.TryEnter())
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
                    detail: "Another earlier save's picture or a preview is being drawn; the engine draws one at a time. Ask again when it is done.");
            byte[] png;
            try
            {
                png = render.RenderOnce(found.Version!, w ?? Thumbnail.DefaultWidth);
            }
            finally
            {
                gate.Exit();
            }
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(png, "image/png");
        }).RequireScope(ApiScope.Read);

        api.MapPost("/{id}/versions/{n}/restore", async (string id, string n, HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            if (VersionNumberOf(n) is not { } number)
                return BadVersionNumber();
            var found = workspace.FindVersion(id, number);
            if (Refused(id, number, found) is { } refused)
                return refused;

            var (body, over) = await ReadBodyAsync(request, workspace.Limits.RequestBytes, ct);
            if (over)
                return OverRequestBytes(workspace);
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            return Acting(id, () => Outcome(found.Document!.RestoreVersion(json.RootElement, found.Version!)));
        }).RequireScope(ApiScope.Edit);
    }

    /// <summary>A version number as the path gives it: 1 to 9,007,199,254,740,991 in ASCII decimal digits, no sign, no leading zero; null otherwise.</summary>
    private static long? VersionNumberOf(string n) =>
        n.Length is >= 1 and <= 16 && n[0] != '0' && n.All(char.IsAsciiDigit)
        && long.Parse(n, System.Globalization.CultureInfo.InvariantCulture) is var number && number <= DocumentStore.MaxListedVersion
            ? number
            : null;

    private static IResult BadVersionNumber() =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad Request",
            detail: "A version's number is a whole number from 1 to 9,007,199,254,740,991, in ASCII decimal digits with no sign and no leading zero.");

    /// <summary>404 for no profile or no such version, 409 for a profile or version that can't be opened; null when both can.</summary>
    private static IResult? Refused(string id, long n, Workspace.VersionLookup found) =>
        found.Problem is { } problem ? Problem(problem, StatusCodes.Status409Conflict)
        : found.Found ? null
        : found.Document is null ? ProfileNotFound(id)
        : Security.Problems.NotFound($"Profile '{id}' has no version {n}.");

    /// <summary>
    /// The <c>instance</c> query parameter: given once, 1–64 characters (the schema's <c>instanceId</c>); null otherwise.
    /// </summary>
    private static string? TagOf(HttpRequest request) =>
        request.Query.TryGetValue("instance", out var values) && values.Count == 1
        && values[0] is { } tag && tag.EnumerateRunes().Count() is >= 1 and <= 64
            ? tag
            : null;

    /// <summary>Undo, redo or revert: a <c>historyRequest</c> body, answered with <c>commitResult</c>, 409 or 422.</summary>
    private static void MapHistory(RouteGroupBuilder api, string name, Func<ProfileDocument, JsonElement, CommitOutcome> act) =>
        api.MapPost("/{id}/" + name, async (string id, HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var found = workspace.Find(id);
            if (Refused(id, found) is { } refused)
                return refused;

            var (body, over) = await ReadBodyAsync(request, workspace.Limits.RequestBytes, ct);
            if (over)
                return OverRequestBytes(workspace);
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            return Acting(id, () => Outcome(act(found.Document!, json.RootElement)));
        }).RequireScope(ApiScope.Edit);

    /// <summary>422 for a body over <c>requestBytes</c> as received (spec/docs/limits.md, "Request limits").</summary>
    private static IResult OverRequestBytes(Workspace workspace) =>
        Invalid("", "limit.requestBytes", $"The body is over the limit of {workspace.Limits.RequestBytes} bytes (requestBytes).");

    private static IResult WithDocument(Workspace workspace, string id, Func<ProfileDocument, IResult> act)
    {
        var found = workspace.Find(id);
        return Refused(id, found) ?? act(found.Document!);
    }

    /// <summary>404 for no profile, 409 for one that can't be opened; null when its document is open.</summary>
    private static IResult? Refused(string id, ProfileLookup found) =>
        !found.Found ? ProfileNotFound(id) : found.Problem is { } problem ? Problem(problem, StatusCodes.Status409Conflict) : null;

    /// <summary>Runs an action on a document that may be deleted meanwhile: then 404, as if it had been gone before.</summary>
    private static IResult Acting(string id, Func<IResult> act)
    {
        try
        {
            return act();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or KeyNotFoundException)
        {
            return ProfileNotFound(id);
        }
    }

    private static IResult Created(CreateOutcome outcome) => outcome switch
    {
        { Entry: { } entry } => Results.Json(entry, SpecJson.Options, "application/json", StatusCodes.Status201Created),
        { Exists: { } exists } => Problem(exists, StatusCodes.Status409Conflict),
        { StatusProblem: { } status } => Problem(status, StatusCodes.Status409Conflict),
        _ => Problem(outcome.Invalid!, StatusCodes.Status422UnprocessableEntity),
    };
}
