using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using InfoPanel.Hosting;
using InfoPanel.Preview;
using InfoPanel.Spec;
using InfoPanel.Tests.Contract;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// A test client for <c>/ws</c>: JSON text out, text and binary frame messages in, each checked against the spec
/// (<see cref="SocketContract"/>). A text message the engine sends must be a <c>serverMessage</c> and a frame's header a
/// <c>frameHeader</c>, or the receive fails. A message the client sends that isn't a <c>clientMessage</c> (a test of the
/// engine's refusals) must be answered with an <c>error</c> message or a close before the client is disposed.
/// </summary>
internal sealed class PreviewClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly List<string> _unanswered = [];
    private readonly HashSet<string> _sent = [];
    private readonly HashSet<string> _received = [];

    private PreviewClient(WebSocket socket)
    {
        Socket = socket;
    }

    public WebSocket Socket { get; }

    /// <summary>The <c>type</c> of every valid message sent.</summary>
    public IReadOnlyCollection<string> SentTypes => _sent;

    /// <summary>The <c>type</c> of every text message received, and <c>frame</c> once a frame was.</summary>
    public IReadOnlyCollection<string> ReceivedTypes => _received;

    public static async Task<PreviewClient> ConnectAsync<T>(WebApplicationFactory<T> factory, CancellationToken ct, params string[] protocols)
        where T : class
    {
        var client = factory.Server.CreateWebSocketClient();
        foreach (var protocol in protocols.Length > 0 ? protocols : [EngineRuntime.WebSocketProtocol, EngineRuntime.WebSocketAuthPrefix + EngineFactory.Token])
            client.SubProtocols.Add(protocol);
        client.ConfigureRequest = request => request.Headers.Origin = EngineFactory.DevOrigin;
        return new PreviewClient(await client.ConnectAsync(new Uri("ws://localhost/ws"), ct));
    }

    public Task SendAsync(object message, CancellationToken ct) => SendTextAsync(JsonSerializer.Serialize(message), ct);

    public Task SendTextAsync(string text, CancellationToken ct)
    {
        if (SocketContract.CheckClientText(text) is null)
            _sent.Add(SocketContract.TypeOf(text)!);
        else
            _unanswered.Add(text);
        return Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>The next message: JSON text, a decoded frame, or a close.</summary>
    public async Task<Message> ReceiveAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var result = await Socket.ReceiveAsync(chunk, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _unanswered.Clear();
                return new Message(null, null, default, result.CloseStatus);
            }

            buffer.Write(chunk, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(buffer.ToArray());
                if (SocketContract.CheckServerText(text) is { } violation)
                    Assert.Fail($"The engine sent a text message outside the spec. {violation}");
                var type = SocketContract.TypeOf(text)!;
                _received.Add(type);
                if (type == "error" && _unanswered.Count > 0)
                    _unanswered.RemoveAt(0);
                return new Message(JsonDocument.Parse(text).RootElement.Clone(), null, default, null);
            }

            var bytes = buffer.ToArray();
            if (SocketContract.CheckFrame(bytes) is { } frameViolation)
                Assert.Fail($"The engine sent a frame outside the spec. {frameViolation}");
            _received.Add("frame");
            var (header, payload) = FrameCodec.Decode(bytes);
            return new Message(null, header, payload, null);
        }
    }

    /// <summary>Skips messages until a text message of <paramref name="type"/> that matches <paramref name="match"/>.</summary>
    public async Task<JsonElement> ReceiveTextAsync(string type, CancellationToken ct, Func<JsonElement, bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var message = await ReceiveAsync(ct, Remaining(deadline));
            Assert.Null(message.CloseStatus);
            if (message.Json is { } json && json.GetProperty("type").GetString() == type && (match == null || match(json)))
                return json;
        }
    }

    /// <summary>Skips messages until a frame that matches <paramref name="match"/>.</summary>
    public async Task<(FrameHeader Header, ReadOnlyMemory<byte> Png)> ReceiveFrameAsync(CancellationToken ct, Func<FrameHeader, bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var message = await ReceiveAsync(ct, Remaining(deadline));
            Assert.Null(message.CloseStatus);
            if (message.Frame is { } header && (match == null || match(header)))
                return (header, message.Payload);
        }
    }

    private static TimeSpan Remaining(DateTime deadline)
    {
        var left = deadline - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    public async ValueTask DisposeAsync()
    {
        // A message sent outside the spec is the engine's to refuse: an error message (or a close) answers it.
        if (_unanswered.Count > 0 && Socket.State == WebSocketState.Open)
        {
            try
            {
                while (_unanswered.Count > 0 && (await ReceiveAsync(CancellationToken.None, TimeSpan.FromSeconds(2))).CloseStatus is null)
                {
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }

        var unanswered = _unanswered.ToList();
        try
        {
            if (Socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        Socket.Dispose();
        Assert.True(unanswered.Count == 0,
            $"Sent outside the spec (not a clientMessage), and the engine answered no error: {string.Join(" | ", unanswered)}");
    }

    public sealed record Message(JsonElement? Json, FrameHeader? Frame, ReadOnlyMemory<byte> Payload, WebSocketCloseStatus? CloseStatus);
}
