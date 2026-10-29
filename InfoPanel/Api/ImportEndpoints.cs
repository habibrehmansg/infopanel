using InfoPanel.Documents;
using InfoPanel.Import;
using InfoPanel.Persistence;
using InfoPanel.Security;
using InfoPanel.Sensors;
using InfoPanel.Spec;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>
/// One import read at a time: the requests that read 1.x data or an archive and store its blobs wait for each other, so
/// one engine never holds more than one import's budget of bytes and one archive's temporary file at once.
/// </summary>
public sealed class ImportGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IResult> RunAsync(Func<Task<IResult>> read, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await read();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}

/// <summary>
/// The 1.x import (spec/docs/import.md), under <c>/api/import/v1</c>: the migration's state (<c>GET</c>, the <c>read</c>
/// scope) and its run-once record (<c>PUT</c>), one profile of the 1.x folder and a 1.x <c>.infopanel</c> file read into
/// their envelopes (<c>POST</c>), which store blobs (all three <c>edit</c>). The engine only reads the 1.x folder; it never
/// installs anything: a client creates profiles from the envelopes. And a profile archive opened
/// (<c>POST /api/import/archive</c>, spec/docs/archive.md, <c>edit</c>): its blobs stored, its document answered.
/// </summary>
public static class ImportEndpoints
{
    private const string RecordRequestSchema = SpecSchemas.Api + "#/$defs/v1MigrationRequest";

    public static void MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/import/v1");

        api.MapGet("", (V1Folder folder, DataPaths paths) => Json(folder.GetState(paths))).RequireScope(ApiScope.Read);

        api.MapPut("", async (HttpRequest request, V1Folder folder, DataPaths paths, ImportLimits limits, Workspace workspace, CancellationToken ct) =>
        {
            // The body's size as received, then the formats' JSON rules and the outcomes' count before the schema, as every body.
            var (body, over) = await ReadBodyAsync(request, limits.RecordBytes, ct);
            if (over)
                return Invalid("", "limit.recordBytes", $"The body is over the limit of {limits.RecordBytes} bytes (recordBytes).");
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.TryGetProperty("profiles", out var outcomes) && outcomes.ValueKind == System.Text.Json.JsonValueKind.Array
                && outcomes.GetArrayLength() > limits.RecordOutcomes)
            {
                return Invalid("/profiles", "limit.recordOutcomes", $"The record has {outcomes.GetArrayLength()} profile outcomes; at most {limits.RecordOutcomes} are allowed (recordOutcomes).");
            }

            var check = SpecSchemas.Strict.Validate(json.RootElement, RecordRequestSchema, workspace.Limits.JsonDepth);
            if (!check.IsValid)
                return InvalidBody([.. check.Errors.Select(ErrorBody)]);

            var report = SpecJson.Read<V1MigrationRequest>(json.RootElement);
            try
            {
                return Json(folder.WriteRecord(paths, report.Profiles, report.Settings, DateTimeOffset.UtcNow));
            }
            catch (ImportRefusal refusal)
            {
                return Refused(refusal);
            }
        }).RequireScope(ApiScope.Edit);

        api.MapPost("/profiles/{v1Id}", (string v1Id, V1Folder folder, Workspace workspace, SensorHub sensors, ImportLimits limits, ImportGate gate, CancellationToken ct) =>
        {
            if (!Guid.TryParseExact(v1Id, "D", out var id))
                return Task.FromResult(NoSuchProfile(v1Id));
            return gate.RunAsync(() =>
            {
                try
                {
                    return Task.FromResult(folder.ReadProfile(id, new V1ReadContext(workspace, sensors, limits)) is { } envelope
                        ? Json(envelope)
                        : NoSuchProfile(v1Id));
                }
                catch (ImportRefusal refusal)
                {
                    return Task.FromResult(Refused(refusal));
                }
            }, ct);
        }).RequireScope(ApiScope.Edit);

        api.MapPost("/archive", (HttpRequest request, Workspace workspace, SensorHub sensors, ImportLimits limits, ImportGate gate, CancellationToken ct) =>
            ReadArchiveBody(request, limits, gate, archive => Json(V1Reader.ReadArchive(archive, new V1ReadContext(workspace, sensors, limits))), ct))
            .WithOwnBodyLimit().RequireScope(ApiScope.Edit);

        // A profile archive (spec/docs/archive.md): checked, its blobs stored, its document answered; a client creates
        // the profile from it.
        app.MapPost("/api/import/archive", (HttpRequest request, Workspace workspace, ImportLimits limits, ImportGate gate, CancellationToken ct) =>
            ReadArchiveBody(request, limits, gate, archive => EnvelopeJson(ProfileArchive.Open(archive, workspace, limits)), ct))
            .WithOwnBodyLimit().RequireScope(ApiScope.Edit);
    }

    /// <summary>
    /// An archive request's body, buffered into a temporary file within <see cref="ImportLimits.ArchiveBytes"/> (413 over
    /// it, read no further), then read by <paramref name="read"/> under the gate; a refusal is 422.
    /// </summary>
    internal static Task<IResult> ReadArchiveBody(HttpRequest request, ImportLimits limits, ImportGate gate, Func<FileStream, IResult> read, CancellationToken ct)
    {
        if (request.ContentLength > limits.ArchiveBytes)
            return Task.FromResult(TooLarge(limits));
        return gate.RunAsync(async () =>
        {
            FileStream archive;
            try
            {
                archive = await TempFile.BufferAsync(request.Body, limits.ArchiveBytes, ct);
            }
            catch (ImportBodyTooLargeException)
            {
                return TooLarge(limits);
            }

            await using (archive)
            {
                try
                {
                    return read(archive);
                }
                catch (ImportRefusal refusal)
                {
                    return Refused(refusal);
                }
            }
        }, ct);
    }

    private static IResult TooLarge(ImportLimits limits) =>
        Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload Too Large",
            detail: $"An archive is at most {limits.ArchiveBytes} bytes (archiveBytes); nothing was read or stored.");

    private static IResult NoSuchProfile(string v1Id) => Problems.NotFound($"The 1.x folder has no profile '{v1Id}'.");

    /// <summary>An import refused as a whole: 422 <c>invalidProblem</c> with its errors (spec/docs/import.md).</summary>
    internal static IResult Refused(ImportRefusal refusal) =>
        InvalidBody([.. refusal.Errors.Select(e => new ValidationErrorBody { Path = e.Path, Code = e.Code, Message = e.Message })]);

    private static IResult InvalidBody(List<ValidationErrorBody> errors) => Problem(new InvalidProblem
    {
        Title = "Invalid request",
        Detail = errors.Count == 1 ? errors[0].Message : "The request breaks the formats; see errors.",
        Status = StatusCodes.Status422UnprocessableEntity,
        Code = "invalid",
        Errors = errors,
    }, StatusCodes.Status422UnprocessableEntity);
}
