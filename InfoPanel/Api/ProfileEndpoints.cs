using InfoPanel.Documents;
using InfoPanel.Output;
using InfoPanel.Security;
using InfoPanel.Spec;
using System.Text.Json;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// Profiles and their documents (spec/docs/api.md, commit.md): list, create, duplicate, delete, read, export,
/// thumbnail, commit, undo, redo, revert and save. All under <c>/api</c>, behind the Origin and Bearer checks of
/// <c>SecurityMiddleware</c>; reads need the <c>read</c> scope, and every request that changes a profile <c>edit</c>. A request on a profile is checked in order: the profile exists (404), it can be opened
/// (409 <c>profileStatusProblem</c>), the body (422), then what the request itself checks. Errors are
/// <c>application/problem+json</c>.
/// </summary>
public static class ProfileEndpoints
{
    /// <summary>The largest body of an undo, redo, revert or duplicate: a few members.</summary>
    private const int SmallBodyBytes = 64 * 1024;

    /// <summary>A create's envelope around its document: <c>{"id": …, "document": …}</c>, with room to spare.</summary>
    private const int CreateEnvelopeBytes = 64 * 1024;

    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/profiles");

        api.MapGet("", (Workspace workspace) => Json(workspace.List())).RequireScope(ApiScope.Read);

        api.MapPost("", async (HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var (body, over) = await ReadBodyAsync(request, workspace.Limits.DocumentBytes + CreateEnvelopeBytes, ct);
            if (over)
                return Invalid("/document", "limit.documentBytes", $"The document is over the {workspace.Limits.DocumentBytes}-byte limit.");
            return Created(workspace.Create(body));
        }).WithOwnBodyLimit().RequireScope(ApiScope.Edit);

        api.MapPost("/{id}/duplicate", async (string id, HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var found = workspace.Find(id);
            if (Refused(id, found) is { } refused)
                return refused;

            var (body, over) = await ReadBodyAsync(request, SmallBodyBytes, ct);
            if (over)
                return Invalid("", "schema", "A duplicate's body is {\"id\"}.");
            var outcome = workspace.Duplicate(found.Document!, body);
            return outcome.NotFound ? ProfileNotFound(id) : Created(outcome);
        }).RequireScope(ApiScope.Edit);

        api.MapDelete("/{id}", (string id, Workspace workspace) =>
            workspace.Delete(id) ? Results.NoContent() : ProfileNotFound(id)).RequireScope(ApiScope.Edit);

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
            return Results.Stream(document.Content, document.Stored ? "application/octet-stream" : "application/json");
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

            // The exact bytes: the opId digest covers every byte, and the size is the document's to check. A body
            // refused from its Content-Length alone isn't read, so it is answered here, as the document would.
            var (body, over) = await ReadBodyAsync(request, workspace.Limits.CommitBytes, ct);
            if (over && body.Length == 0)
                return Invalid("", "limit.commitBytes", $"{request.ContentLength} is over the limit of {workspace.Limits.CommitBytes} (commitBytes).");
            return Acting(id, () => Outcome(found.Document!.Commit(body)));
        }).WithOwnBodyLimit().RequireScope(ApiScope.Edit);

        MapHistory(api, "undo", (doc, body) => doc.Undo(body));
        MapHistory(api, "redo", (doc, body) => doc.Redo(body));
        MapHistory(api, "revert", (doc, body) => doc.Revert(body));

        api.MapPost("/{id}/save", (string id, Workspace workspace) =>
            WithDocument(workspace, id, doc => Acting(id, () => Json(doc.Save())))).RequireScope(ApiScope.Edit);
    }

    /// <summary>Undo, redo or revert: a <c>historyRequest</c> body, answered with <c>commitResult</c>, 409 or 422.</summary>
    private static void MapHistory(RouteGroupBuilder api, string name, Func<ProfileDocument, JsonElement, CommitOutcome> act) =>
        api.MapPost("/{id}/" + name, async (string id, HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var found = workspace.Find(id);
            if (Refused(id, found) is { } refused)
                return refused;

            var (body, over) = await ReadBodyAsync(request, SmallBodyBytes, ct);
            using var json = over ? null : ParseJson(body);
            if (json == null)
                return Invalid("", over ? "schema" : "json", over ? "The body is {\"instance\", \"baseRev\"}." : "The body isn't JSON.");
            return Acting(id, () => Outcome(act(found.Document!, json.RootElement)));
        }).RequireScope(ApiScope.Edit);

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
