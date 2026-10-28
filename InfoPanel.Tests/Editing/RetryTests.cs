using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using Xunit;
using static InfoPanel.Tests.ReadEndpointTests;
using static InfoPanel.Tests.TestDocs;

namespace InfoPanel.Tests.Editing;

/// <summary>
/// Answers lost on the way back (spec/docs/commit.md, "opId: retries"; api.md, "Blobs"): a test middleware lets the
/// engine apply a request, holds its answer and aborts the connection, and the client retries with the identical bytes.
/// A commit replays its stored result, an undo is refused (it has no <c>opId</c>) and the client reads the state, a blob
/// upload answers 200. After a restart a retry is refused for its <c>instance</c>, and once the remembered window has
/// passed it, as <c>expired</c>.
/// </summary>
public sealed class RetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACommitWhoseAnswerIsLost_ReplaysItsResultOnRetry_WithoutANewRevisionOrStep()
    {
        using var engine = new EngineFactory();
        var drop = new ResponseDropper();
        using var live = LiveEngine.Start(engine, drop.Install);
        using var api = live.Client();
        var id = await CreateAsync(api, Doc("Retry", 300, 100, Scene("a", 0, 0)), Ct);
        var instance = await LiveEngine.Instance(api, Ct);
        var bytes = LiveEngine.Commit(instance, 1, null, Place("a", 50, 25, baseRev: 1));

        var held = await drop.Lose(() => LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct), $"/api/profiles/{id}/commit");
        Assert.Equal(StatusCodes.Status200OK, held.Status);
        Assert.Equal(2, (await Document(api, id)).GetProperty("rev").GetInt64());

        // Before the retry, another commit places the item elsewhere: revision 3.
        var newer = await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", LiveEngine.Commit(instance, 2, null, Place("a", 70, 35, baseRev: 2)), Ct));
        Assert.Equal(3, newer.GetProperty("rev").GetInt64());

        // The retry: the stored result as it was when applied (revision 2), marked replayed; it doesn't apply again, so
        // the newer placement, the revision and the history stay as the newer commit left them.
        var replay = await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct));
        Assert.True(Valid(replay, "commitResult"));
        var expected = JsonNode.Parse(held.Body)!.AsObject();
        expected["replayed"] = true;
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected), replay), $"held {held.Body}, replayed {replay}");
        Assert.Equal(2, replay.GetProperty("rev").GetInt64());
        var again = await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct));
        Assert.Equal(replay.GetRawText(), again.GetRawText());

        var state = await Document(api, id);
        Assert.Equal((3, true, true, false), (state.GetProperty("rev").GetInt64(), state.GetProperty("dirty").GetBoolean(),
            state.GetProperty("canUndo").GetBoolean(), state.GetProperty("canRedo").GetBoolean()));
        Assert.Equal((70, 35), Placement(state, "a"));

        // Two steps, the newer on top: the first undo goes back to the lost commit's placement, the second to before it,
        // and there is nothing left to undo.
        var undone = await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/undo", LiveEngine.History(instance, 3), Ct));
        Assert.Equal((4, true, true), (undone.GetProperty("rev").GetInt64(), undone.GetProperty("dirty").GetBoolean(), undone.GetProperty("canUndo").GetBoolean()));
        Assert.Equal((50, 25), Placement(await Document(api, id), "a"));
        undone = await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/undo", LiveEngine.History(instance, 4), Ct));
        Assert.Equal((5, false, false), (undone.GetProperty("rev").GetInt64(), undone.GetProperty("dirty").GetBoolean(), undone.GetProperty("canUndo").GetBoolean()));
        Assert.Equal((0, 0), Placement(await Document(api, id), "a"));
    }

    [Fact]
    public async Task AnUndoWhoseAnswerIsLost_IsRefusedOnRetry_AndTheClientReadsThatItWasDone()
    {
        using var engine = new EngineFactory();
        var drop = new ResponseDropper();
        using var live = LiveEngine.Start(engine, drop.Install);
        using var api = live.Client();
        var id = await CreateAsync(api, Doc("Retry", 300, 100, Scene("a", 0, 0)), Ct);
        var instance = await LiveEngine.Instance(api, Ct);
        await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", LiveEngine.Commit(instance, 1, null, Place("a", 10, 0, baseRev: 1)), Ct));
        await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", LiveEngine.Commit(instance, 2, null, Place("a", 20, 0, baseRev: 2)), Ct));
        var bytes = LiveEngine.History(instance, 3);

        var held = await drop.Lose(() => LiveEngine.PostBytes(api, $"/api/profiles/{id}/undo", bytes, Ct), $"/api/profiles/{id}/undo");
        Assert.Equal(StatusCodes.Status200OK, held.Status);
        Assert.Equal(4, JsonDocument.Parse(held.Body).RootElement.GetProperty("rev").GetInt64());

        // The retry finds the document past its baseRev: 409, with what changed after it (the undo's own change).
        using var retry = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/undo", bytes, Ct);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var problem = await LiveEngine.Body(retry, Ct);
        Assert.True(Valid(problem, "conflictProblem"));
        Assert.Equal(4, problem.GetProperty("rev").GetInt64());
        Assert.Equal(["op=- id=a changed rev=4 fields=placement"], EditOracle.Conflicts(problem));

        // The client reads the state: undone once (a back at 10, not 0), with the step to redo.
        var state = await Document(api, id);
        Assert.Equal((4, true, true), (state.GetProperty("rev").GetInt64(), state.GetProperty("canUndo").GetBoolean(), state.GetProperty("canRedo").GetBoolean()));
        Assert.Equal(10, Placement(state, "a").X);
    }

    [Fact]
    public async Task ABlobUploadWhoseAnswerIsLost_Is200OnRetry_WithTheSameInfo()
    {
        using var engine = new EngineFactory();
        var drop = new ResponseDropper();
        using var live = LiveEngine.Start(engine, drop.Install);
        using var api = live.Client();
        var png = Png(SKColors.Teal);

        var held = await drop.Lose(() => api.PostAsync("/api/blobs", new ByteArrayContent(png), Ct), "/api/blobs");
        Assert.Equal(StatusCodes.Status201Created, held.Status);

        using var retry = await api.PostAsync("/api/blobs", new ByteArrayContent(png), Ct);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var info = await LiveEngine.Body(retry, Ct);
        Assert.True(Valid(info, "blobInfo"));
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(held.Body).RootElement, info), $"held {held.Body}, retried {info}");
        Assert.Equal(png, await api.GetByteArrayAsync($"/api/blobs/{info.GetProperty("id").GetString()}", Ct));
    }

    [Fact]
    public async Task ARetryAfterARestart_Is409Instance_AndTheClientStartsOverFromAFreshRead()
    {
        using var engine = new EngineFactory();
        var drop = new ResponseDropper();
        string id;
        byte[] bytes;
        using (var first = LiveEngine.Start(engine, drop.Install))
        {
            using var api = first.Client();
            id = await CreateAsync(api, Doc("Retry", 300, 100, Scene("a", 0, 0)), Ct);
            bytes = LiveEngine.Commit(await LiveEngine.Instance(api, Ct), 1, null, Place("a", 50, 0, baseRev: 1));
            var held = await drop.Lose(() => LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct), $"/api/profiles/{id}/commit");
            Assert.Equal(StatusCodes.Status200OK, held.Status);
        }

        using var second = LiveEngine.Start(engine);
        using var client = second.Client();
        using var retry = await LiveEngine.PostBytes(client, $"/api/profiles/{id}/commit", bytes, Ct);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var problem = await LiveEngine.Body(retry, Ct);
        Assert.True(Valid(problem, "conflictProblem"));
        Assert.Equal(["op=- id=- instance rev=- fields="], EditOracle.Conflicts(problem));

        // The fresh read: the saved document of the new instance (the lost change was never saved).
        var state = await Document(client, id);
        Assert.Equal((1, false), (state.GetProperty("rev").GetInt64(), state.GetProperty("dirty").GetBoolean()));
        Assert.Equal(0, Placement(state, "a").X);
    }

    [Fact]
    public async Task ARetryOlderThanTheRememberedWindow_Is409Expired()
    {
        // commit.md asks an engine to remember at least the last 1,000 opIds and lets it remember more; the host has no
        // setting for the count, so this asserts this engine's configured window, exactly 1,000.
        const int window = 1000;
        using var engine = new EngineFactory();
        var drop = new ResponseDropper();
        using var live = LiveEngine.Start(engine, drop.Install);
        using var api = live.Client();
        var id = await CreateAsync(api, Doc("Retry", 300, 100, Scene("a", 0, 0)), Ct);
        var instance = await LiveEngine.Instance(api, Ct);
        var bytes = LiveEngine.Commit(instance, 1, null, Place("a", -1, 0, baseRev: 1));
        await drop.Lose(() => LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct), $"/api/profiles/{id}/commit");

        long rev = 2;
        async Task Commits(int count)
        {
            for (var i = 0; i < count; i++)
                rev = (await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", LiveEngine.Commit(instance, rev, null, Place("a", rev, 0, baseRev: rev)), Ct)))
                    .GetProperty("rev").GetInt64();
        }

        // With window - 1 later commits its opId is still remembered: the retry replays.
        await Commits(window - 1);
        Assert.True((await Ok(await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct))).GetProperty("replayed").GetBoolean());

        // One more and it is forgotten: its baseRev is older than the window's start, so it is refused, and nothing applies.
        await Commits(1);
        using var retry = await LiveEngine.PostBytes(api, $"/api/profiles/{id}/commit", bytes, Ct);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var problem = await LiveEngine.Body(retry, Ct);
        Assert.True(Valid(problem, "expiredProblem"));
        Assert.Equal(("expired", rev), (problem.GetProperty("code").GetString(), problem.GetProperty("rev").GetInt64()));
        var state = await Document(api, id);
        Assert.Equal(rev, state.GetProperty("rev").GetInt64());
        Assert.Equal(rev - 1, Placement(state, "a").X);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        using (response)
        {
            var body = await LiveEngine.Body(response, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
            return body;
        }
    }

    private static Task<JsonElement> Document(HttpClient api, string id) => api.GetFromJsonAsync<JsonElement>($"/api/profiles/{id}", Ct);

    private static (double X, double Y) Placement(JsonElement documentResponse, string item)
    {
        var placement = documentResponse.GetProperty("document").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == item).GetProperty("placement");
        return (placement.GetProperty("x").GetDouble(), placement.GetProperty("y").GetDouble());
    }

    internal static byte[] Png(SKColor color, int size = 4)
    {
        using var bitmap = new SKBitmap(size, size);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>
/// A test middleware, first in the engine's pipeline (ahead of the contract check): for a request it was armed for, the engine handles the request
/// in full while its answer goes to a buffer, then the connection is aborted, so the client never sees the answer.
/// </summary>
internal sealed class ResponseDropper
{
    private readonly object _gate = new();
    private string? _path;
    private TaskCompletionSource<Held>? _held;

    /// <summary>What the engine answered to the lost request.</summary>
    public sealed record Held(int Status, string Body);

    /// <summary>
    /// Outermost, outside the contract check too, so the check reads the answer the engine wrote: inside it, the
    /// check would read the empty body this middleware leaves it.
    /// </summary>
    public void Install(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.Insert(0, ServiceDescriptor.Singleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new Filter(this))));

    /// <summary>Sends a request whose answer is lost on <paramref name="path"/>; returns what the engine answered.</summary>
    public async Task<Held> Lose(Func<Task<HttpResponseMessage>> send, string path)
    {
        var held = new TaskCompletionSource<Held>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _path = path;
            _held = held;
        }

        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            using var response = await send();
        });
        return await held.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private TaskCompletionSource<Held>? Take(HttpContext context)
    {
        lock (_gate)
        {
            if (_held == null || !HttpMethods.IsPost(context.Request.Method) || context.Request.Path != _path)
                return null;
            var held = _held;
            _held = null;
            _path = null;
            return held;
        }
    }

    private sealed class Filter(ResponseDropper dropper) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, handle) =>
            {
                if (dropper.Take(context) is not { } held)
                {
                    await handle(context);
                    return;
                }

                var original = context.Features.GetRequiredFeature<IHttpResponseBodyFeature>();
                using var buffer = new MemoryStream();
                context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));
                try
                {
                    await handle(context);
                }
                finally
                {
                    context.Features.Set(original);
                }

                held.SetResult(new Held(context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(buffer.ToArray())));
                context.Abort();
            });
            next(app);
        };
    }
}
