using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Preview;
using InfoPanel.Tests.Contract;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>The engine with a short first-message authentication timeout.</summary>
public sealed class ShortAuthTimeoutEngineFactory : EngineFactory
{
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromMilliseconds(600);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton(new PreviewSocketOptions(AuthTimeout)));
    }
}

/// <summary>
/// <c>/ws</c> connections that offer <c>infopanel.v1</c> without <c>auth.{token}</c>: accepted, silent until a first
/// <c>{"type":"auth","token":…}</c> message, closed with 1008 otherwise.
/// </summary>
public class FirstMessageAuthTests(ShortAuthTimeoutEngineFactory factory) : IClassFixture<ShortAuthTimeoutEngineFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<WebSocket> Connect(string origin = EngineFactory.CrossOrigin)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.SubProtocols.Add(EngineRuntime.WebSocketProtocol);
        client.ConfigureRequest = request => request.Headers.Origin = origin;
        var socket = await client.ConnectAsync(new Uri("ws://localhost/ws"), Ct);
        Assert.Equal(EngineRuntime.WebSocketProtocol, socket.SubProtocol);
        return socket;
    }

    private static Task Send(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, Ct);

    private static Task SendAuth(WebSocket socket, string token) =>
        Send(socket, JsonSerializer.Serialize(new { type = "auth", token }));

    /// <summary>The next message: (type, text) for text, ("binary", "") for a frame, ("close", description) for a close.</summary>
    private static async Task<(string Kind, string Text, WebSocketCloseStatus? Status)> Receive(WebSocket socket, TimeSpan? timeout = null)
    {
        var receive = ReceiveTask(socket);
        var finished = await Task.WhenAny(receive, Task.Delay(timeout ?? TimeSpan.FromSeconds(5), Ct));
        Assert.Same(receive, finished);
        return await receive;
    }

    private static async Task<(string Kind, string Text, WebSocketCloseStatus? Status)> ReceiveTask(WebSocket socket)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                return ("close", result.CloseStatusDescription ?? "", result.CloseStatus);
            buffer.Write(chunk, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            // What the engine sends is held to the spec: a frame message, or a serverMessage.
            if (result.MessageType == WebSocketMessageType.Binary)
                return SocketContract.CheckFrame(buffer.ToArray()) is { } frame ? throw new InvalidOperationException(frame) : ("binary", "", null);
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            if (SocketContract.CheckServerText(text) is { } outside)
                throw new InvalidOperationException(outside);
            return (JsonDocument.Parse(text).RootElement.GetProperty("type").GetString()!, text, null);
        }
    }

    private static async Task AssertClosedWithPolicyViolation(WebSocket socket, string reason)
    {
        var (kind, text, status) = await Receive(socket);
        Assert.Equal("close", kind);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
        Assert.Equal(reason, text);
    }

    [Fact]
    public async Task AuthMessage_WithTheToken_StartsTheSession_AndNothingIsSentBefore()
    {
        using var socket = await Connect();

        // Nothing arrives while the connection is unauthenticated.
        var first = ReceiveTask(socket);
        await Task.Delay(ShortAuthTimeoutEngineFactory.AuthTimeout / 3, Ct);
        Assert.False(first.IsCompleted);

        await SendAuth(socket, EngineFactory.Token);
        var hello = await first.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("hello", hello.Kind);

        // Past the timeout, the session is still open and works.
        await Task.Delay(ShortAuthTimeoutEngineFactory.AuthTimeout, Ct);
        using var api = factory.CreateApiClient();
        var profiles = await api.GetFromJsonAsync<JsonElement>("/api/profiles", Ct);
        var id = profiles[0].GetProperty("id").GetString();
        await Send(socket, JsonSerializer.Serialize(new { type = "subscribe", profileId = id, scale = 1 }));
        var doc = await Receive(socket);
        Assert.Equal("doc", doc.Kind);
        Assert.Equal("binary", (await Receive(socket)).Kind);

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Ct);
    }

    [Fact]
    public async Task AuthMessage_FromADevOrigin_AlsoWorks()
    {
        using var socket = await Connect(EngineFactory.DevOrigin);
        await SendAuth(socket, EngineFactory.Token);
        Assert.Equal("hello", (await Receive(socket)).Kind);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Ct);
    }

    [Theory]
    [InlineData("not-the-token")]
    [InlineData("")]
    [InlineData(EngineFactory.Token + "x")]
    public async Task AuthMessage_WithAWrongToken_ClosesWith1008(string token)
    {
        using var socket = await Connect();
        await SendAuth(socket, token);
        await AssertClosedWithPolicyViolation(socket, PreviewSocketHandler.AuthFailedToken);
    }

    [Fact]
    public async Task NoMessage_ClosesWith1008_AfterTheTimeout()
    {
        using var socket = await Connect();
        var started = Environment.TickCount64;

        await AssertClosedWithPolicyViolation(socket, PreviewSocketHandler.AuthFailedTimeout);
        Assert.True(Environment.TickCount64 - started >= ShortAuthTimeoutEngineFactory.AuthTimeout.TotalMilliseconds - 50);
    }

    [Theory]
    [InlineData("""{"type":"subscribe","profileId":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"type":"auth"}""")]
    [InlineData("""{"type":"auth","token":42}""")]
    [InlineData("""{"token":"test-token_0123456789"}""")]
    [InlineData("""["auth","test-token_0123456789"]""")]
    [InlineData("auth test-token_0123456789")]
    [InlineData("""{"type":"auth","token":"\uD800"}""")]
    [InlineData("""{"type":"\uDC00","token":"test-token_0123456789"}""")]
    public async Task AnotherFirstMessage_ClosesWith1008(string message)
    {
        using var socket = await Connect();
        await Send(socket, message);
        await AssertClosedWithPolicyViolation(socket, PreviewSocketHandler.AuthFailedMessage);
    }

    [Fact]
    public async Task EmptyFragmentsWithoutEnd_CloseWith1008_AtTheTimeout_AndTheDrainIsBounded()
    {
        using var socket = await Connect();
        var started = Environment.TickCount64;
        using var stop = new CancellationTokenSource();

        // A client that never finishes its first message: an endless stream of empty non-final text fragments,
        // kept up after the server's close too, so neither the auth wait nor the close drain ever goes idle.
        var flood = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    await socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, false, stop.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
            {
            }
        }, Ct);

        try
        {
            var (kind, text, status) = await Receive(socket, TimeSpan.FromSeconds(10));
            Assert.Equal("close", kind);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, status);
            Assert.Equal(PreviewSocketHandler.AuthFailedTimeout, text);
            Assert.InRange(Environment.TickCount64 - started,
                (long)ShortAuthTimeoutEngineFactory.AuthTimeout.TotalMilliseconds - 50, 5000);

            // The server stops draining (2 s) and drops the connection, which ends the flood.
            Assert.Same(flood, await Task.WhenAny(flood, Task.Delay(TimeSpan.FromSeconds(10), Ct)));
        }
        finally
        {
            stop.Cancel();
        }
    }

    [Fact]
    public async Task ABinaryFirstMessage_ClosesWith1008()
    {
        using var socket = await Connect();
        await socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"auth","token":"{{EngineFactory.Token}}"}"""), WebSocketMessageType.Binary, true, Ct);
        await AssertClosedWithPolicyViolation(socket, PreviewSocketHandler.AuthFailedMessage);
    }

    [Fact]
    public async Task AnOversizedFirstMessage_ClosesWith1008()
    {
        using var socket = await Connect();
        var padding = new string(' ', PreviewSocketHandler.MaxClientMessageBytes);
        await Send(socket, $$"""{"type":"auth","token":"{{EngineFactory.Token}}"}""" + padding);
        await AssertClosedWithPolicyViolation(socket, PreviewSocketHandler.AuthFailedMessage);
    }

    [Fact]
    public async Task SubprotocolAuth_StillWorks_FromTheCrossOrigin_WithNoAuthMessage()
    {
        await using var client = await PreviewClient.ConnectAsync(factory, Ct,
            EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + EngineFactory.Token);
        Assert.Equal(EngineRuntime.WebSocketProtocol, client.Socket.SubProtocol);
        await client.ReceiveTextAsync("hello", Ct);

        // Well past the first-message timeout: still open.
        await Task.Delay(ShortAuthTimeoutEngineFactory.AuthTimeout * 2, Ct);
        Assert.Equal(WebSocketState.Open, client.Socket.State);
    }

    [Theory]
    [InlineData("https://other.example")]
    [InlineData("null")]
    public async Task FirstMessageAuth_FromAnotherOrigin_Is403BeforeTheUpgrade(string origin)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Connect(origin));
        Assert.Contains("403", error.Message);
    }
}
