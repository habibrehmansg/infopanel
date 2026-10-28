using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Api;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Preview;
using InfoPanel.Spec;
using InfoPanel.Tests.Preview;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Contract;

/// <summary>
/// The preview socket against spec/openapi.json's <c>x-infopanel-socket</c>: the subprotocol is the engine's, the
/// messages the engine writes are <c>serverMessage</c>s, and the socket checks the test clients run catch what isn't.
/// </summary>
public class SocketConformanceTests(EngineFactory factory) : IClassFixture<EngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheSocketOperation_NamesTheEnginesSubprotocol_AndTheMessageDefinitions()
    {
        var socket = ApiContract.Instance.Socket;
        Assert.Equal(EngineRuntime.WebSocketProtocol, socket.Subprotocol);
        Assert.Equal(SpecSchemas.Api + "#/$defs/clientMessage", socket.ClientMessage);
        Assert.Equal(SpecSchemas.Api + "#/$defs/serverMessage", socket.ServerMessage);
        Assert.Equal(SpecSchemas.Api + "#/$defs/frameHeader", socket.FrameHeader);
        Assert.Equal(["auth", "subscribe", "view", "unsubscribe"], ApiContract.MessageTypes(socket.ClientMessage));
        Assert.Equal(["hello", "doc", "profiles", "error"], ApiContract.MessageTypes(socket.ServerMessage));
    }

    [Fact]
    public void TheMessagesTheEngineWrites_AreServerMessages()
    {
        // As the socket handler writes them: the spec model with the format's options.
        foreach (var message in new object[]
                 {
                     new HelloMessage { Type = "hello", Protocol = ReadEndpoints.Protocol },
                     new ProfilesMessage { Type = "profiles", Version = 1 },
                     new ErrorMessage { Type = "error", Message = "No profile 'x'." },
                     new DocEvent { Type = "doc", ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Dirty = false, CanUndo = false, CanRedo = false },
                 })
        {
            Assert.Null(SocketContract.CheckServerText(JsonSerializer.Serialize(message, message.GetType(), SpecJson.Options)));
        }
    }

    [Theory]
    [InlineData("""{"type":"hello","protocol":2,"engine":"x"}""")]
    [InlineData("""{"type":"hello"}""")]
    [InlineData("""{"type":"welcome","protocol":2}""")]
    [InlineData("""{"type":"error","message":7}""")]
    [InlineData("""not json""")]
    public void AServerMessageOutsideTheSpec_IsCaught(string text) => Assert.NotNull(SocketContract.CheckServerText(text));

    [Theory]
    [InlineData("""{"type":"subscribe","profileId":"3dd19c0e-675a-4591-b096-83417853861f","scale":0}""")]
    [InlineData("""{"type":"subscribe"}""")]
    [InlineData("""{"type":"view","maxFps":2.5}""")]
    [InlineData("""{"type":"dance"}""")]
    public void AClientMessageOutsideTheSpec_IsCaught(string text) => Assert.NotNull(SocketContract.CheckClientText(text));

    [Fact]
    public void AClientMessageOverTheCap_IsCaught()
    {
        Assert.Null(SocketContract.CheckClientText("""{"type":"unsubscribe","pad":""}"""));
        Assert.NotNull(SocketContract.CheckClientText("{\"type\":\"unsubscribe\",\"pad\":\"" + new string('x', PreviewSocketHandler.MaxClientMessageBytes) + "\"}"));
    }

    [Fact]
    public void AFrameHeaderOutsideTheSpec_IsCaught()
    {
        var header = new FrameHeader
        {
            ProfileId = "3dd19c0e-675a-4591-b096-83417853861f", Rev = 1, Tick = 0, FrameId = 0, Encoding = "png", Scale = 1,
            PixelWidth = 10, PixelHeight = 10, ProfileWidth = 10, ProfileHeight = 10, ItemBounds = [],
        };
        Assert.Null(SocketContract.CheckFrame(FrameCodec.Encode(header, [1, 2, 3])));
        Assert.NotNull(SocketContract.CheckFrame(FrameCodec.Encode(header with { Scale = 5 }, [1, 2, 3])));
        Assert.NotNull(SocketContract.CheckFrame([1, 2, 3]));
    }

    [Fact]
    public async Task AWholeMaxFps_InTheSpellingsAnIntegerAllows_IsAccepted()
    {
        using var api = factory.CreateApiClient();
        var id = JsonDocument.Parse(await api.GetStringAsync("/api/profiles", Ct)).RootElement[0].GetProperty("id").GetString();
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);

        // clientMessage's maxFps is a JSON Schema integer, which 10.0 and 1e1 are.
        await client.SendTextAsync($$"""{"type":"subscribe","profileId":"{{id}}","scale":0.25,"maxFps":10.0}""", Ct);
        await client.ReceiveTextAsync("doc", Ct);
        await client.ReceiveFrameAsync(Ct);
        await client.SendTextAsync("""{"type":"view","maxFps":1e1}""", Ct);
        // The engine answers messages in order: an error for either would come before the resubscription's doc.
        await client.SendTextAsync($$"""{"type":"subscribe","profileId":"{{id}}","scale":0.25,"maxFps":3e1}""", Ct);
        await client.ReceiveTextAsync("doc", Ct);

        Assert.DoesNotContain("error", client.ReceivedTypes);
        Assert.Equal(["subscribe", "view"], client.SentTypes.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task MaxFps_IsReadAsClientMessagesInteger_AsTheDoubleNearestItsText()
    {
        using var api = factory.CreateApiClient();
        var id = JsonDocument.Parse(await api.GetStringAsync("/api/profiles", Ct)).RootElement[0].GetProperty("id").GetString();
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);

        // Whole numbers from 1 to 2^31 - 1, in any spelling, and numbers whose nearest double is one: accepted (an error for
        // any would come before the doc).
        foreach (var fps in new[] { "30.0", "3e1", "2147483647", "0.99999999999999999", "1.00000000000000000000000000001", "2147483647.0000001" })
            await client.SendTextAsync($$"""{"type":"view","maxFps":{{fps}}}""", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);
        Assert.DoesNotContain("error", client.ReceivedTypes);

        // Not a whole double, over the maximum, or past the 64-bit range: each answered with an error about maxFps. Sent
        // without a subscription and for an absent profile, so no frames follow either way: a value the engine took would
        // draw "No profile" instead.
        await client.SendAsync(new { type = "unsubscribe" }, Ct);
        foreach (var fps in new[] { "0.9999999999999999", "2147483647.0000005", "2147483648", "2.5", "1e400" })
        {
            await client.SendTextAsync($$"""{"type":"subscribe","profileId":"00000000-0000-4000-8000-0000000000aa","maxFps":{{fps}}}""", Ct);
            await client.ReceiveTextAsync("error", Ct, e => e.GetProperty("message").GetString()!.Contains("maxFps", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task MaxFps_PastThe64BitRange_IsAnError()
    {
        // 1e400 rounds to infinity as a double: not a number the formats read, so not a whole number from 1 to 2^31 - 1.
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        foreach (var fps in new[] { "1e400", "-1e400" })
        {
            await client.SendTextAsync($$"""{"type":"subscribe","profileId":"00000000-0000-4000-8000-0000000000aa","maxFps":{{fps}}}""", Ct);
            await client.ReceiveTextAsync("error", Ct, e => e.GetProperty("message").GetString()!.Contains("maxFps", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AnyPositiveScale_IsReadAndLimited_AsFrameSizeSays()
    {
        // A small canvas, so the largest scale is a small frame.
        using var api = factory.CreateApiClient();
        var id = TestDocs.NewId();
        using (var created = await api.PostAsync("/api/profiles", TestDocs.Json(new JsonObject { ["id"] = id, ["name"] = "Scales", ["width"] = 100, ["height"] = 50 }), Ct))
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        var (first, _) = await client.ReceiveFrameAsync(Ct, h => h.Scale == 0.25);

        // Beyond single precision either way: the largest and the smallest scale (docs/api.md#frame-size), not an error.
        var largest = FrameScale.Normalize(FrameScale.Max, first.ProfileWidth, first.ProfileHeight);
        var smallest = FrameScale.Normalize(FrameScale.Min, first.ProfileWidth, first.ProfileHeight);
        await client.SendTextAsync("""{"type":"view","scale":1e100}""", Ct);
        await client.ReceiveFrameAsync(Ct, h => h.Scale == largest);
        // 1e-100 is a frameRequestScale: JSON Schema compares numbers by value, and so does the client's clientMessage check.
        await client.SendTextAsync("""{"type":"view","scale":1e-100}""", Ct);
        await client.ReceiveFrameAsync(Ct, h => h.Scale == smallest);
        Assert.DoesNotContain("error", client.ReceivedTypes);

        // A number is the double nearest its text: 1e400 is past the 64-bit range and 1e-400 is 0, so neither is a
        // positive number, and each is answered with an error about scale.
        foreach (var scale in new[] { "1e400", "1e-400", "-0" })
        {
            await client.SendTextAsync($$"""{"type":"view","scale":{{scale}}}""", Ct);
            await client.ReceiveTextAsync("error", Ct, e => e.GetProperty("message").GetString()!.Contains("scale", StringComparison.Ordinal));
        }
    }

    /// <summary>A WebSocket connect on the TestServer that the engine refuses, with the error the client sees.</summary>
    private async Task<string> RefusedConnect(string origin, string token)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add(EngineRuntime.WebSocketProtocol);
        client.SubProtocols.Add(EngineRuntime.WebSocketAuthPrefix + token);
        client.ConfigureRequest = request => request.Headers.Origin = origin;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri("ws://localhost/ws"), Ct));
        return error.Message;
    }

    /// <summary>
    /// Waits for the check of a refused upgrade: it runs once the response has gone out, so the client may see the
    /// refusal first.
    /// </summary>
    private static async Task Checked(Func<bool> done)
    {
        for (var clock = System.Diagnostics.Stopwatch.StartNew(); !done() && clock.Elapsed < TimeSpan.FromSeconds(5);)
            await Task.Delay(10, Ct);
    }

    [Fact]
    public async Task ARefusedUpgrade_IsCheckedLikeAnyResponse()
    {
        // A wrong token: the socket operation's declared 401. Another origin: the security layer's 403 problem.
        Assert.Contains("401", await RefusedConnect(EngineFactory.DevOrigin, "not-the-token"));
        await Checked(() => factory.Contract.Seen.Contains(("GET /ws", 401)));
        Assert.Contains(("GET /ws", 401), factory.Contract.Seen);
        Assert.Contains("403", await RefusedConnect("http://evil.example", EngineFactory.Token));
        await Checked(() => factory.Contract.Refusals.Contains(("GET /ws", 403)));
        Assert.Contains(("GET /ws", 403), factory.Contract.Refusals);
        Assert.Empty(factory.Contract.Violations);
    }

    [Fact]
    public async Task ARefusedUpgrade_IsCheckedOnKestrelToo()
    {
        using var engine = new EngineFactory();
        using var live = InfoPanel.Tests.Editing.LiveEngine.Start(engine);
        foreach (var (origin, token, status) in new[] { (EngineFactory.DevOrigin, "not-the-token", "401"), ("http://evil.example", EngineFactory.Token, "403") })
        {
            using var client = new System.Net.WebSockets.ClientWebSocket();
            client.Options.AddSubProtocol(EngineRuntime.WebSocketProtocol);
            client.Options.AddSubProtocol(EngineRuntime.WebSocketAuthPrefix + token);
            client.Options.SetRequestHeader("Origin", origin);
            var error = await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(() => client.ConnectAsync(new Uri($"ws://127.0.0.1:{live.Port}/ws"), Ct));
            Assert.Contains(status, error.Message);
        }

        await Checked(() => engine.Contract.Seen.Contains(("GET /ws", 401)) && engine.Contract.Refusals.Contains(("GET /ws", 403)));

        // And an accepted one is the socket's 101, as it starts.
        await using (var socket = await live.Subscribe(JsonDocument.Parse(await live.Client().GetStringAsync("/api/profiles", Ct)).RootElement[0].GetProperty("id").GetString()!, Ct))
            await socket.Until(() => socket.Docs.Count > 0, TimeSpan.FromSeconds(5), "a doc", Ct);
        Assert.Contains(("GET /ws", 401), engine.Contract.Seen);
        Assert.Contains(("GET /ws", 403), engine.Contract.Refusals);
        Assert.Contains(("GET /ws", 101), engine.Contract.Seen);
        Assert.Empty(engine.Contract.Violations);
    }

    /// <summary>
    /// Answers every <c>/ws</c> upgrade with a 401 whose problem body isn't JSON, inside the contract check; with
    /// <paramref name="thenCancelled"/>, then throws <see cref="OperationCanceledException"/>, as a client that went away
    /// can make the engine do.
    /// </summary>
    private sealed class InvalidRefusal(bool thenCancelled = false) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Path.StartsWithSegments("/ws") || !ContractCheck.IsUpgrade(context))
                {
                    await nextMiddleware(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync("not a problem", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                if (thenCancelled)
                    throw new OperationCanceledException("The client went away.");
            });
            next(app);
        };
    }

    [Fact]
    public async Task ARefusedUpgradeOutsideTheSpec_FailsItsHostsTeardown_ThoughItsClientSawOnlyTheRefusal()
    {
        var engine = new EngineFactory();
        var host = engine.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new InvalidRefusal())));
        var client = host.Server.CreateWebSocketClient();
        client.SubProtocols.Add(EngineRuntime.WebSocketProtocol);
        client.SubProtocols.Add(EngineRuntime.WebSocketAuthPrefix + EngineFactory.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri("ws://localhost/ws"), Ct));

        // Nothing waits for the check here: the teardown does.
        var error = await Assert.ThrowsAsync<ContractViolationException>(async () => await engine.DisposeAsync());
        Assert.Contains("GET /ws", error.Message);
        Assert.Contains("isn't JSON", error.Message);
    }

    [Fact]
    public async Task ARefusedUpgradeOutsideTheSpec_FailsItsHostsTeardown_WhenItsHandlerThrowsAfterItWentOut()
    {
        var engine = new EngineFactory();
        var host = engine.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new InvalidRefusal(thenCancelled: true))));
        var client = host.Server.CreateWebSocketClient();
        client.SubProtocols.Add(EngineRuntime.WebSocketProtocol);
        client.SubProtocols.Add(EngineRuntime.WebSocketAuthPrefix + EngineFactory.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri("ws://localhost/ws"), Ct));

        var error = await Assert.ThrowsAsync<ContractViolationException>(async () => await engine.DisposeAsync());
        Assert.Contains("GET /ws", error.Message);
        Assert.Contains("isn't JSON", error.Message);
    }

    [Theory]
    [InlineData("""{"type":"welcome"}""", "welcome")]
    // JSON reads the lone surrogate, but decoding the string for the check throws: that fails the disposal too.
    [InlineData("""{"type":"\uD800"}""", "check of a message")]
    public async Task ALiveSocket_FailsItsDisposal_OnAMessageOutsideTheSpec_AfterTheLastWait(string outside, string reported)
    {
        // A peer on real Kestrel that answers the subscribe with a hello, then sends a message no serverMessage allows.
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var peer = builder.Build();
        peer.UseWebSockets();
        peer.Map("/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(EngineRuntime.WebSocketProtocol);
            var buffer = new byte[4096];
            await socket.ReceiveAsync(buffer, context.RequestAborted);
            foreach (var text in new[] { """{"type":"hello","protocol":2}""", outside })
                await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), System.Net.WebSockets.WebSocketMessageType.Text, true, context.RequestAborted);
            sent.SetResult();
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                if ((await socket.ReceiveAsync(buffer, context.RequestAborted)).MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", context.RequestAborted);
            }
        });
        await peer.StartAsync(Ct);
        var address = new Uri(peer.Urls.Single());

        var live = await InfoPanel.Tests.Editing.LiveSocket.Connect(new Uri($"ws://127.0.0.1:{address.Port}/ws"), EngineFactory.Token,
            "3dd19c0e-675a-4591-b096-83417853861f", 0.5, 30, Ct);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await live.DisposeAsync());
        Assert.Contains(reported, error.InnerException?.Message);
    }

    [Fact]
    public async Task ThePreviewSocket_SendsOnlyServerMessagesAndFrames()
    {
        using var api = factory.CreateApiClient();
        var profiles = JsonDocument.Parse(await api.GetStringAsync("/api/profiles", Ct)).RootElement;
        var id = profiles[0].GetProperty("id").GetString();

        // PreviewClient checks every message against the spec as it receives it.
        await using var client = await PreviewClient.ConnectAsync(factory, Ct);
        await client.ReceiveTextAsync("hello", Ct);
        await client.SendAsync(new { type = "subscribe", profileId = id, scale = 0.25, maxFps = 10 }, Ct);
        await client.ReceiveTextAsync("doc", Ct);
        await client.ReceiveFrameAsync(Ct);
        foreach (var type in new[] { "hello", "doc", "frame" })
            Assert.Contains(type, client.ReceivedTypes);
        Assert.Contains("subscribe", client.SentTypes);
    }
}
