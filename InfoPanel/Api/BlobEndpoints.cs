using InfoPanel.Documents;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// Blobs (spec/docs/api.md, "Blobs"): <c>POST /api/blobs</c> stores bytes and answers <c>blobInfo</c> (201 new, 200
/// already stored, 413 over <c>mediaBytes</c>); <c>HEAD</c> and <c>GET /api/blobs/{id}</c> answer its size and bytes.
/// </summary>
public static class BlobEndpoints
{
    public static void MapBlobEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/blobs");

        api.MapPost("", async (HttpRequest request, Workspace workspace, CancellationToken ct) =>
        {
            var limit = workspace.Limits.MediaBytes;
            var (body, over) = await ReadBodyAsync(request, limit, ct);
            if (over)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload Too Large",
                    detail: $"A blob is at most {limit} bytes (mediaBytes); nothing was stored.");
            }

            var stored = workspace.PutBlob(body);
            return Json(stored.Info, stored.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }).WithOwnBodyLimit();

        api.MapMethods("/{id}", [HttpMethods.Head], (string id, Workspace workspace, HttpContext context) =>
        {
            if (!BlobStore.IsValidId(id) || workspace.Blobs.Size(id) is not { } size)
                return NotFound(id);
            context.Response.ContentLength = size;
            context.Response.ContentType = "application/octet-stream";
            return Results.Empty;
        });

        api.MapGet("/{id}", (string id, Workspace workspace) =>
            BlobStore.IsValidId(id) && workspace.Blobs.OpenRead(id) is { } stream
                ? Results.Stream(stream, "application/octet-stream")
                : NotFound(id));
    }

    private static IResult NotFound(string id) => Security.Problems.NotFound($"No blob '{id}'.");
}
