using System.Text.Json;
using InfoPanel.Documents;
using InfoPanel.Images;
using InfoPanel.Output;
using InfoPanel.Scenes;
using InfoPanel.Security;
using InfoPanel.Spec;
using SkiaSharp;
using static InfoPanel.Api.ApiResults;

namespace InfoPanel.Api;

/// <summary>One preview drawn at a time per engine: a second, while one is drawn, is refused (409).</summary>
public sealed class PreviewGate
{
    private int _held;

    /// <summary>True when the caller now holds the gate (and must <see cref="Exit"/>); false while another holds it.</summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _held, 0);
}

/// <summary>The bounds of a preview (spec/docs/api.md, "Previews").</summary>
public sealed class PreviewLimits
{
    /// <summary>The longest side the picture may have, in pixels: the width (the schema's) and the height it gives.</summary>
    public const int MaxSide = 1280;

    /// <summary>The frames the states may have together: 5 seconds at 10 a second.</summary>
    public const int MaxFrames = 50;

    /// <summary>
    /// How long drawing and encoding may take, on the wall clock: past it the work stops at its next frame or encoding
    /// and the request is refused (<c>preview.time</c>).
    /// </summary>
    public TimeSpan Time { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tests: called on the recording's thread once its frames are drawn, with the recording's cancellation.</summary>
    internal Action<CancellationToken>? Drawn { get; init; }
}

/// <summary>
/// Where a preview's media comes from: the stored pictures only (nothing fetched; no video is played). Each recording
/// opens media of its own over them (<see cref="LoopMedia"/>), never the running outputs' shared cache.
/// </summary>
public sealed class PreviewMedia(IMediaAssetStore store)
{
    /// <summary>A recording's own media, within <paramref name="limits"/>, reading nothing once <paramref name="cancel"/> is cancelled.</summary>
    public LoopMedia Open(SpecLimits limits, CancellationToken cancel) => new(store, limits, cancel);
}

/// <summary>
/// <c>POST /api/preview</c> (the <c>preview</c> feature, spec/docs/api.md "Previews"): a document the request gives,
/// checked as a create checks one, drawn with the readings the request gives (no sensor, no media provider: nothing is
/// fetched) as an animated WebP loop or a PNG still, and kept nowhere: nothing is stored, listed or announced. The
/// <c>edit</c> scope's, as every request that takes a document. One at a time (<see cref="PreviewGate"/>), on a thread of
/// its own below normal priority, within <see cref="PreviewLimits"/>.
/// </summary>
public static class PreviewEndpoints
{
    private const string RequestSchema = SpecSchemas.Api + "#/$defs/previewRequest";

    /// <summary>The id a preview's document is drawn under: its own is optional, as a create's.</summary>
    private const string AnyId = "00000000-0000-4000-8000-000000000000";

    public static void MapPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/preview", async (HttpRequest request, HttpContext context, Workspace workspace, RenderService render,
            PreviewGate gate, PreviewLimits limits, PreviewMedia media, TimeProvider time, CancellationToken ct) =>
        {
            // Read as a create's body is, up to documentBytes plus the read allowance: room for the request around it.
            var (body, over) = await ReadBodyAsync(request, DocumentStore.ReadCap(workspace.Limits), ct);
            if (over)
                return Invalid("/document", "limit.documentBytes", $"The document is over the {workspace.Limits.DocumentBytes}-byte limit.");
            using var json = ParseJson(body);
            if (json == null)
                return Invalid("", "json", "The body isn't JSON.");
            var root = json.RootElement;
            // The document's nesting is counted from the document, as a create counts it (limit.jsonDepth at /document).
            var check = SpecSchemas.StructureOnly.ValidateMembers(root, RequestSchema, workspace.Limits.JsonDepth);
            if (!check.IsValid)
                return InvalidBody([.. check.Errors.Select(ErrorBody)]);

            var document = root.GetProperty("document");
            if (workspace.CheckImportDocument(document) is { Count: > 0 } invalid)
                return InvalidBody([.. invalid.Select(Body)]);

            var states = root.GetProperty("states").EnumerateArray().Select(State).ToList();
            var frames = states.Sum(s => s.Frames);
            if (frames > PreviewLimits.MaxFrames)
                return Invalid("/states", "preview.frames", $"The states have {frames} frames together; at most {PreviewLimits.MaxFrames} are allowed.");
            var profile = document.GetProperty("profile");
            // Every integer is read as the format reads one: any spelling of a whole number (16.0, 1e0).
            var (width, height, scale) = LoopRecorder.Size(SpecJson.Read<int>(profile.GetProperty("width")),
                SpecJson.Read<int>(profile.GetProperty("height")), SpecJson.Read<int>(root.GetProperty("width")));
            if (height > PreviewLimits.MaxSide)
                return Invalid("/width", "preview.height", $"At {width} pixels wide the picture is {height} high; at most {PreviewLimits.MaxSide} is allowed: ask for a narrower one.");

            var still = root.GetProperty("answer").GetString() == "still";
            var maxBytes = SpecJson.Read<long>(root.GetProperty("maxBytes"));
            SKColor? background = root.TryGetProperty("background", out var colour) && SKColor.TryParse(colour.GetString(), out var parsed) ? parsed : null;
            if (still)
                states = [states[0] with { Frames = 1 }];
            using var drawn = WithId(document);

            if (!gate.TryEnter())
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict",
                    detail: "Another preview is being drawn; the engine draws one at a time. Ask again when it is done.");
            try
            {
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
                bounded.CancelAfter(limits.Time);
                var start = time.GetUtcNow();
                var answer = await Task.Factory.StartNew(() =>
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    return Draw(drawn.RootElement, states, render.SceneOptions, media, workspace.Limits, limits, start, scale, background,
                        still, maxBytes, bounded.Token);
                }, bounded.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                // The recording's thread has ended (the await returns only then); past its time, nothing is answered.
                bounded.Token.ThrowIfCancellationRequested();
                context.Response.Headers.CacheControl = "no-store";
                return answer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Invalid("", "preview.time", $"The preview took longer than the {limits.Time.TotalSeconds:0.#} seconds the engine allows; nothing was answered: ask for a smaller one.");
            }
            catch (SceneDocumentException e)
            {
                return InvalidBody([.. e.Check.Errors.Select(x => Body(x with { Path = "/document" + x.Path }))]);
            }
            finally
            {
                gate.Exit();
            }
        }).WithOwnBodyLimit().RequireScope(ApiScope.Edit);
    }

    /// <summary>Records the states and answers the still's PNG or the loop's WebP at the best quality within <paramref name="maxBytes"/>.</summary>
    private static IResult Draw(JsonElement document, IReadOnlyList<LoopState> states, SceneRuntimeOptions live, PreviewMedia media,
        SpecLimits specLimits, PreviewLimits limits, DateTimeOffset start, float scale, SKColor? background, bool still, long maxBytes,
        CancellationToken cancel)
    {
        RecordedLoop loop;
        // The recording's own media, decoded on this thread and let go with the recording.
        using (var own = media.Open(specLimits, cancel))
            loop = LoopRecorder.Record(document, states, inputs => LoopRecorder.Options(live, inputs, own), start, scale, background, cancel);
        using var _ = loop;
        limits.Drawn?.Invoke(cancel);
        cancel.ThrowIfCancellationRequested();
        var first = loop.Frames[0].Bitmap;
        if (still)
        {
            var png = LoopRecorder.Png(first);
            cancel.ThrowIfCancellationRequested();
            return png.Length <= maxBytes
                ? Results.File(png, "image/png")
                : TooLarge($"The still is {png.Length} bytes, over maxBytes ({maxBytes}): ask for a narrower one.");
        }

        var encoded = WebpLoopEncoder.EncodeWithin(loop.Runs(), maxBytes, cancel);
        if (encoded.Webp is not { } webp)
            return TooLarge($"The loop is {encoded.Size} bytes at its smallest (lossy 70), over maxBytes ({maxBytes}): ask for a narrower one.");
        return Json(new PreviewLoop
        {
            Webp = Convert.ToBase64String(webp),
            KeyFrames = encoded.KeyFrames,
            Quality = encoded.Quality is { } q ? $"lossy{q}" : "lossless",
            Width = first.Width,
            Height = first.Height,
        });
    }

    private static IResult TooLarge(string message) => Invalid("/maxBytes", "preview.size", message);

    private static ValidationErrorBody Body(SpecError e) => new() { Path = e.Path, Code = e.Code, Message = e.Message };

    /// <summary>A state of the request as the recorder takes it: each reading for an input of its own type.</summary>
    private static LoopState State(JsonElement state)
    {
        var readings = new Dictionary<string, LoopReading>(StringComparer.Ordinal);
        foreach (var reading in state.GetProperty("readings").EnumerateObject())
        {
            var value = reading.Value;
            readings[reading.Name] = value.TryGetProperty("value", out var number)
                ? new LoopReading("number", new SceneReading
                {
                    Status = SceneInputStatus.Live,
                    Now = number.GetDouble(),
                    Min = number.GetDouble(),
                    Max = number.GetDouble(),
                    Avg = number.GetDouble(),
                    Unit = value.TryGetProperty("unit", out var unit) ? unit.GetString()! : "",
                })
                : value.TryGetProperty("text", out var text)
                    ? new LoopReading("text", new SceneReading { Status = SceneInputStatus.Live, Text = text.GetString()! })
                    : new LoopReading("table", new SceneReading
                    {
                        Status = SceneInputStatus.Live,
                        Table = new SceneTable([.. value.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!)],
                            [.. value.GetProperty("rows").EnumerateArray().Select(r => (IReadOnlyList<string>)[.. r.EnumerateArray().Select(c => c.GetString()!)])]),
                    });
        }
        return new LoopState(SpecJson.Read<int>(state.GetProperty("frames")), readings);
    }

    /// <summary>The document with <see cref="AnyId"/> as its id (a create's document may leave its own out).</summary>
    private static JsonDocument WithId(JsonElement document)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = SpecJson.SchemaDepth + 1 }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", AnyId);
            foreach (var property in document.EnumerateObject())
            {
                if (!property.NameEquals("id"))
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(buffer.ToArray(), SpecJson.DocumentOptions);
    }
}
