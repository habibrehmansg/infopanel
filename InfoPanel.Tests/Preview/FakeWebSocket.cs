using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using InfoPanel.Preview;
using InfoPanel.Spec;

namespace InfoPanel.Tests.Preview;

/// <summary>
/// A server-side <see cref="WebSocket"/> driven by the test, for <see cref="PreviewSession"/> interleavings:
/// <list type="bullet">
/// <item><see cref="ReceiveTextAsync"/> hands the session a client message and returns once the session has handled it
/// (its receive loop asked for the next message).</item>
/// <item><see cref="PauseWhen"/> makes the first send that matches block (after it is recorded) until <see cref="Resume"/>,
/// so the test can change state while the send loop is stuck on the wire.</item>
/// <item><see cref="NextAsync"/> reads what the session sent, in order.</item>
/// </list>
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
    private readonly Channel<Sent> _sent = Channel.CreateUnbounded<Sent>();
    private readonly Lock _lock = new();
    private readonly MemoryStream _partial = new();
    private int _receiveCalls;
    private int _written;
    private readonly List<(int Calls, TaskCompletionSource Done)> _waiters = [];
    private Func<Sent, bool>? _pauseWhen;
    private TaskCompletionSource<Sent>? _blocked;
    private TaskCompletionSource? _gate;
    private WebSocketState _state = WebSocketState.Open;

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State
    {
        get { lock (_lock) return _state; }
    }

    public override string? SubProtocol => "infopanel.v1";

    /// <summary>Gives the session a text message and waits until it has handled it.</summary>
    public Task ReceiveTextAsync(string text)
    {
        // The session handled message k once it asks for message k + 1.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _written++;
            if (_receiveCalls >= _written + 1)
                done.TrySetResult();
            else
                _waiters.Add((_written + 1, done));
        }

        _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));
        return done.Task.WaitAsync(Timeout);
    }

    public Task ReceiveAsync(object message) => ReceiveTextAsync(JsonSerializer.Serialize(message));

    /// <summary>The client closes; the session ends.</summary>
    public void ReceiveClose() => _incoming.Writer.TryWrite(([], WebSocketMessageType.Close));

    /// <summary>The first send matching <paramref name="match"/> blocks until <see cref="Resume"/>; the task completes when it does.</summary>
    public Task<Sent> PauseWhen(Func<Sent, bool> match)
    {
        lock (_lock)
        {
            _pauseWhen = match;
            _blocked = new TaskCompletionSource<Sent>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _blocked.Task.WaitAsync(Timeout);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _pauseWhen = null;
            _gate?.TrySetResult();
        }
    }

    /// <summary>The next message the session sent that matches (the others are skipped).</summary>
    public async Task<Sent> NextAsync(Func<Sent, bool>? match = null)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var sent = await _sent.Reader.ReadAsync(timeout.Token);
            if (match == null || match(sent))
                return sent;
        }
    }

    /// <summary>Everything sent so far and not yet read.</summary>
    public List<Sent> Drain()
    {
        var all = new List<Sent>();
        while (_sent.Reader.TryRead(out var sent))
            all.Add(sent);
        return all;
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _receiveCalls++;
            foreach (var waiter in _waiters.Where(w => w.Calls <= _receiveCalls).ToList())
            {
                waiter.Done.TrySetResult();
                _waiters.Remove(waiter);
            }
        }

        var (data, type) = await _incoming.Reader.ReadAsync(cancellationToken);
        if (type == WebSocketMessageType.Close)
        {
            lock (_lock)
                _state = _state == WebSocketState.CloseSent ? WebSocketState.Closed : WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.NormalClosure, "");
        }

        data.CopyTo(buffer.AsSpan());
        return new WebSocketReceiveResult(data.Length, type, true);
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        Sent? sent = null;
        Task? gate = null;
        lock (_lock)
        {
            _partial.Write(buffer);
            if (endOfMessage)
            {
                sent = new Sent(messageType, _partial.ToArray());
                _partial.SetLength(0);
                if (_pauseWhen != null && _pauseWhen(sent))
                {
                    _pauseWhen = null;
                    gate = _gate!.Task;
                    _blocked!.TrySetResult(sent);
                }
            }
        }

        if (sent != null)
            _sent.Writer.TryWrite(sent);
        if (gate != null)
            await gate.WaitAsync(cancellationToken);
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        lock (_lock)
            _state = _state == WebSocketState.CloseReceived ? WebSocketState.Closed : WebSocketState.CloseSent;
        _sent.Writer.TryWrite(new Sent(WebSocketMessageType.Close, []));
        return Task.CompletedTask;
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        lock (_lock)
            _state = WebSocketState.Aborted;
        _incoming.Writer.TryComplete();
    }

    public override void Dispose()
    {
    }

    /// <summary>One message the session sent.</summary>
    public sealed record Sent(WebSocketMessageType Type, byte[] Bytes)
    {
        public JsonElement Json => JsonDocument.Parse(Bytes).RootElement.Clone();

        public bool IsText(string type) =>
            Type == WebSocketMessageType.Text && Json.GetProperty("type").GetString() == type;

        public FrameHeader Frame => FrameCodec.Decode(Bytes).Header;

        public ReadOnlyMemory<byte> Png => FrameCodec.Decode(Bytes).Payload;

        public override string ToString() => Type == WebSocketMessageType.Text ? Encoding.UTF8.GetString(Bytes) : $"{Type} ({Bytes.Length} bytes)";
    }
}
