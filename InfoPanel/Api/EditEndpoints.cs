using InfoPanel.Editing;
using InfoPanel.Persistence;
using InfoPanel.Security;
using System.Text.Json.Nodes;

namespace InfoPanel.Api;

public sealed record CreateProfileRequest(string? Name, int? Width, int? Height);

public sealed record PatchRequest(JsonObject? Set, string? CoalesceKey);

public sealed record AddItemRequest(string? Kind, string? ParentId, int? Index, JsonObject? Set, string? CoalesceKey);

public sealed record ItemPatchRequest(string? Id, JsonObject? Set);

public sealed record PatchItemsRequest(List<ItemPatchRequest?>? Patches, string? CoalesceKey, bool? Record);

public sealed record MoveItemRequest(string? ParentId, int? Index);

/// <summary>
/// The write side of the REST API (plan §6.3): profiles, items, undo/redo/save/revert, import and export. All under
/// <c>/api</c>, behind the Origin and Bearer checks of <c>SecurityMiddleware</c>. Errors are problem+json: 400 for an
/// invalid request (with the offending <c>key</c>), 404 for an unknown profile or item, 409 for an impossible edit.
/// </summary>
public static class EditEndpoints
{
    /// <summary>The largest <c>.infopanel</c> upload <c>POST /api/import</c> accepts.</summary>
    public const long MaxImportBytes = 256L * 1024 * 1024;

    public static void MapEditEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/profiles", (CreateProfileRequest? body, Workspace workspace) => Edit(() =>
        {
            if (body == null)
                return Invalid("body", "Send {name, width, height}.");
            if (body.Width is not int width)
                return Invalid("width", "'width' is required.");
            if (body.Height is not int height)
                return Invalid("height", "'height' is required.");

            var summary = workspace.Create(body.Name, width, height);
            return Results.Created($"/api/profiles/{summary.Id}", summary);
        }));

        api.MapPatch("/profiles/{id}", (string id, PatchRequest? body, Workspace workspace) => WithDoc(workspace, id, doc =>
            body?.Set is { } set ? Results.Ok(doc.PatchProfile(set, body.CoalesceKey)) : Invalid("set", "Send {set:{...}}.")));

        api.MapDelete("/profiles/{id}", (string id, Workspace workspace) =>
            Guid.TryParse(id, out var guid) && workspace.Delete(guid) ? Results.NoContent() : ProfileNotFound(id));

        api.MapPost("/profiles/{id}/duplicate", (string id, Workspace workspace) => Edit(() =>
            Guid.TryParse(id, out var guid) && workspace.Duplicate(guid) is { } summary
                ? Results.Created($"/api/profiles/{summary.Id}", summary)
                : ProfileNotFound(id)));

        api.MapPost("/profiles/{id}/save", (string id, Workspace workspace) => Edit(() =>
            Guid.TryParse(id, out var guid) && workspace.Save(guid) is { } result ? Results.Ok(result) : ProfileNotFound(id)));

        api.MapPost("/profiles/{id}/revert", (string id, Workspace workspace) => WithDoc(workspace, id, doc => Results.Ok(doc.Revert())));
        api.MapPost("/profiles/{id}/undo", (string id, Workspace workspace) => WithDoc(workspace, id, doc => Results.Ok(doc.Undo())));
        api.MapPost("/profiles/{id}/redo", (string id, Workspace workspace) => WithDoc(workspace, id, doc => Results.Ok(doc.Redo())));

        api.MapGet("/profiles/{id}/export", async (string id, Workspace workspace, TimeProvider time, HttpContext context) =>
        {
            if (!Guid.TryParse(id, out var guid))
                return ProfileNotFound(id);

            // ZipArchive writes synchronously, which the response body refuses: build the archive in a temp file.
            var temp = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose);
            try
            {
                var profile = workspace.Export(guid, temp);
                if (profile == null)
                {
                    await temp.DisposeAsync();
                    return ProfileNotFound(id);
                }

                temp.Position = 0;
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(temp, "application/zip", InfoPanelArchive.FileNameFor(profile, time.GetUtcNow()));
            }
            catch
            {
                await temp.DisposeAsync();
                throw;
            }
        });

        api.MapPost("/import", async (HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            if (request.ContentLength > MaxImportBytes)
                return Problem(StatusCodes.Status413PayloadTooLarge, "Payload Too Large", $"Archives over {MaxImportBytes / (1024 * 1024)} MB are refused.");
            if (!request.HasFormContentType)
                return Invalid("file", "Send the .infopanel file as multipart/form-data.");

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (Exception ex) when (ex is InvalidDataException || (ex is IOException && ex is not BadHttpRequestException))
            {
                // A missing boundary or a truncated or malformed body. BadHttpRequestException (an IOException) keeps
                // its own status, such as 413 for a body over the limit.
                return Invalid("file", "The upload is not valid multipart/form-data: " + ex.Message);
            }

            if (form.Files.Count != 1)
                return Invalid("file", "Send exactly one .infopanel file.");

            await using var temp = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose);
            await using (var upload = form.Files[0].OpenReadStream())
                await upload.CopyToAsync(temp, ct);
            temp.Position = 0;

            try
            {
                var (summary, _) = workspace.Import(temp);
                return Results.Created($"/api/profiles/{summary.Id}", summary);
            }
            catch (InvalidDataException ex)
            {
                return Invalid("file", ex.Message);
            }
        })
        .WithMetadata(new RequestSizeLimitMetadata(MaxImportBytes))
        .WithFormOptions(multipartBodyLengthLimit: MaxImportBytes);

        api.MapPost("/profiles/{id}/items", (string id, AddItemRequest? body, Workspace workspace) => WithDoc(workspace, id, doc =>
        {
            if (body?.Kind is not { } kind)
                return Invalid("kind", "'kind' is required.");
            if (!TryParseOptionalId(body.ParentId, out var parentId))
                return ItemNotFound(body.ParentId!, "parentId");

            return Results.Ok(doc.AddItem(kind, parentId, body.Index, body.Set, body.CoalesceKey));
        }));

        api.MapPatch("/profiles/{id}/items", (string id, PatchItemsRequest? body, Workspace workspace) => WithDoc(workspace, id, doc =>
        {
            if (body?.Patches is not { } requested)
                return Invalid("patches", "Send {patches:[{id, set}]}.");

            var patches = new List<ItemPatch>(requested.Count);
            foreach (var patch in requested)
            {
                if (patch?.Set is not { } set)
                    return Invalid("set", "Every patch needs {id, set:{...}}.");
                if (!Guid.TryParse(patch.Id, out var itemId))
                    return ItemNotFound(patch.Id ?? "", "id");
                patches.Add(new ItemPatch(itemId, set));
            }

            return Results.Ok(doc.PatchItems(patches, body.CoalesceKey, body.Record ?? true));
        }));

        api.MapDelete("/profiles/{id}/items/{itemId}", (string id, string itemId, Workspace workspace) => WithDoc(workspace, id, doc =>
            Guid.TryParse(itemId, out var item) ? Results.Ok(doc.RemoveItem(item)) : ItemNotFound(itemId, "id")));

        api.MapPost("/profiles/{id}/items/{itemId}/move", (string id, string itemId, MoveItemRequest? body, Workspace workspace) =>
            WithDoc(workspace, id, doc =>
            {
                if (!Guid.TryParse(itemId, out var item))
                    return ItemNotFound(itemId, "id");
                if (body?.Index is not int index)
                    return Invalid("index", "Send {parentId: string|null, index}.");
                if (!TryParseOptionalId(body.ParentId, out var parentId))
                    return ItemNotFound(body.ParentId!, "parentId");

                return Results.Ok(doc.MoveItem(item, parentId, index));
            }));

        api.MapPost("/profiles/{id}/items/{itemId}/duplicate", (string id, string itemId, Workspace workspace) => WithDoc(workspace, id, doc =>
            Guid.TryParse(itemId, out var item) ? Results.Ok(doc.DuplicateItem(item)) : ItemNotFound(itemId, "id")));
    }

    /// <summary>Runs <paramref name="edit"/> on the profile's document; unknown profiles are 404.</summary>
    private static IResult WithDoc(Workspace workspace, string id, Func<ProfileDocument, IResult> edit) =>
        Edit(() => Guid.TryParse(id, out var guid) && workspace.Open(guid) is { } doc ? edit(doc) : ProfileNotFound(id));

    /// <summary>Maps a refused edit to its problem: 400 invalid, 404 not found, 409 conflict, with the offending key.</summary>
    private static IResult Edit(Func<IResult> edit)
    {
        try
        {
            return edit();
        }
        catch (EditException ex)
        {
            var (status, title) = ex.Error switch
            {
                EditError.NotFound => (StatusCodes.Status404NotFound, "Not Found"),
                EditError.Conflict => (StatusCodes.Status409Conflict, "Conflict"),
                _ => (StatusCodes.Status400BadRequest, "Bad Request"),
            };
            return Problem(status, title, ex.Message, ex.Key);
        }
    }

    private static bool TryParseOptionalId(string? value, out Guid? id)
    {
        id = null;
        if (value == null)
            return true;
        if (!Guid.TryParse(value, out var parsed))
            return false;
        id = parsed;
        return true;
    }

    private static IResult Invalid(string key, string detail) => Problem(StatusCodes.Status400BadRequest, "Bad Request", detail, key);

    private static IResult ProfileNotFound(string id) => Problems.NotFound($"No profile '{id}'.");

    private static IResult ItemNotFound(string id, string key) =>
        Problem(StatusCodes.Status404NotFound, "Not Found", $"No item '{id}' in this profile.", key);

    private static IResult Problem(int status, string title, string detail, string? key = null) =>
        TypedResults.Problem(detail: detail, statusCode: status, title: title,
            extensions: key == null ? null : new Dictionary<string, object?> { ["key"] = key });

    private sealed class RequestSizeLimitMetadata(long limit) : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize { get; } = limit;
    }
}
