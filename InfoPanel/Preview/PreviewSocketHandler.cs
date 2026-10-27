using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Output;
using InfoPanel.Security;
using InfoPanel.Spec;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace InfoPanel.Preview;

/// <summary>How long a <c>/ws</c> connection without an <c>auth.{token}</c> subprotocol has to send its <c>auth</c> message.</summary>
public sealed record PreviewSocketOptions(TimeSpan? AuthTimeout = null)
{
    public static readonly TimeSpan DefaultAuthTimeout = TimeSpan.FromSeconds(5);

    public TimeSpan EffectiveAuthTimeout => AuthTimeout ?? DefaultAuthTimeout;
}

/// <summary>
/// <c>/ws</c> (plan §6.4): the live preview stream and document events. <see cref="SecurityMiddleware"/> has already
/// checked the Origin and the <c>infopanel.v1</c> subprotocol, and the token when it came as <c>auth.{token}</c>; the
/// socket is accepted with <c>infopanel.v1</c> only, so the token is never echoed.
/// <para>
/// <b>First-message authentication.</b> A connection that offered no <c>auth.*</c> subprotocol is accepted and must
/// send <c>{"type":"auth","token":"…"}</c> as its first message within <see cref="PreviewSocketOptions.AuthTimeout"/>
/// (5 s by default). The server sends nothing before that; a valid token starts the session (<c>hello</c> first).
/// Anything else (no message in time, a wrong token, another message, a binary or oversized one) closes the socket
/// with <see cref="AuthFailedStatus"/> (1008, policy violation) and one of the <c>AuthFailed…</c> reasons.
/// </para>
/// <para>
/// <b>Tokens.</b> Either way the token is the session token or an integration token with the <c>read</c> scope. A
/// connection authenticated with an integration token is closed with 1008 and <see cref="TokenRevokedReason"/> as soon
/// as that token is revoked; the others stay open.
/// </para>
/// <list type="bullet">
/// <item>Client → server (JSON text, at most <see cref="MaxClientMessageBytes"/>; the spec's <c>clientMessage</c>):
/// <c>subscribe {profileId, scale?, maxFps?}</c>, <c>view {scale?, maxFps?}</c>, <c>unsubscribe</c>. Members a message
/// doesn't define are ignored.</item>
/// <item>Server → client (the spec's <c>serverMessage</c>, written from its models): <c>hello</c> on connect
/// (<c>protocol</c> 2); <c>doc</c> (<c>docEvent</c>)
/// on subscribe and after every change of the subscribed document's revision or state, whichever client made it;
/// <c>profiles</c> on every <see cref="Workspace.ProfilesChanged"/>; <c>error</c> for a bad message, an unknown profile,
/// one that can't be opened or a deleted one; binary frames (<see cref="FrameCodec"/>).</item>
/// </list>
/// </summary>
public static class PreviewSocketHandler
{
    public const int MaxClientMessageBytes = 4096;

    /// <summary>The close status for a failed first-message authentication.</summary>
    public const WebSocketCloseStatus AuthFailedStatus = WebSocketCloseStatus.PolicyViolation;

    public const string AuthFailedTimeout = "auth timeout";
    public const string AuthFailedToken = "auth rejected";
    public const string AuthFailedMessage = "auth expected";

    /// <summary>The close reason (with <see cref="AuthFailedStatus"/>) when the connection's integration token is revoked.</summary>
    public const string TokenRevokedReason = "token revoked";

    /// <summary>How long the client has to answer a close frame before the connection is dropped.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    public static void MapPreviewSocket(this IEndpointRouteBuilder app) => app.Map("/ws", HandleAsync).RequireScope(ApiScope.Read);

    private static async Task HandleAsync(HttpContext context, Workspace workspace, RenderService render,
        IHostApplicationLifetime lifetime, ILoggerFactory loggers, EngineRuntime runtime, TokenStore tokens, PreviewSocketOptions options)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Problems.Write(context, StatusCodes.Status400BadRequest, "Bad Request", "/ws takes WebSocket connections only.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = EngineRuntime.WebSocketProtocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        var logger = loggers.CreateLogger("InfoPanel.Preview");
        var caller = Caller.Of(context);
        if (context.Items.ContainsKey(SecurityMiddleware.PendingWebSocketAuthItem))
        {
            (caller, var failure) = await AuthenticateFirstMessageAsync(socket, runtime, tokens, options.EffectiveAuthTimeout, context.RequestAborted);
            if (failure != null)
            {
                logger.LogInformation("Closed a preview connection that did not authenticate ({Reason})", failure);
                return;
            }
        }

        var session = new PreviewSession(socket, workspace, render, logger);
        var tokenId = caller?.TokenId;
        void OnRevoked(string id)
        {
            if (id == tokenId)
                session.BeginClose(AuthFailedStatus, TokenRevokedReason);
        }

        if (tokenId != null)
        {
            tokens.Revoked += OnRevoked;
            // Revoked between the check and the subscription: closed at once, like a later revocation.
            if (!tokens.IsActive(tokenId))
                session.BeginClose(AuthFailedStatus, TokenRevokedReason);
        }

        try
        {
            await session.RunAsync(context.RequestAborted, lifetime.ApplicationStopping);
        }
        finally
        {
            if (tokenId != null)
                tokens.Revoked -= OnRevoked;
        }
    }

    /// <summary>
    /// Reads the first message without cancelling the receive (a cancelled receive aborts the socket, and the client
    /// would get no close status). Returns the caller when it is a valid <c>auth</c> message; otherwise closes the socket
    /// with <see cref="AuthFailedStatus"/> and returns the reason.
    /// </summary>
    private static async Task<(Caller? Caller, string? Failure)> AuthenticateFirstMessageAsync(WebSocket socket, EngineRuntime runtime,
        TokenStore tokens, TimeSpan timeout, CancellationToken aborted)
    {
        var buffer = new byte[MaxClientMessageBytes + 1];
        var length = 0;
        var deadline = Task.Delay(timeout, aborted);
        Task<ValueWebSocketReceiveResult>? pending = null;
        string failure;
        try
        {
            while (true)
            {
                // The deadline bounds the whole phase: a client streaming empty fragments completes every receive
                // before the deadline is looked at in WhenAny, so it is also checked on every turn.
                if (deadline.IsCompleted)
                {
                    failure = aborted.IsCancellationRequested ? "" : AuthFailedTimeout;
                    break;
                }

                if (length == buffer.Length)
                {
                    failure = AuthFailedMessage;
                    break;
                }

                pending = socket.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None).AsTask();
                if (await Task.WhenAny(pending, deadline) != pending)
                {
                    failure = aborted.IsCancellationRequested ? "" : AuthFailedTimeout;
                    break;
                }

                var result = await pending;
                pending = null;
                if (result.MessageType == WebSocketMessageType.Close)
                    return (null, await CloseAsync(socket, WebSocketCloseStatus.NormalClosure, "", null, "closed by the client"));

                length += result.Count;
                if (!result.EndOfMessage)
                    continue;

                Caller? caller = null;
                failure = result.MessageType == WebSocketMessageType.Text && length <= MaxClientMessageBytes
                    ? CheckAuthMessage(buffer.AsSpan(0, length), runtime, tokens, out caller)
                    : AuthFailedMessage;
                if (failure.Length == 0)
                    return (caller, null);
                break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            return (null, "connection lost");
        }

        if (aborted.IsCancellationRequested)
        {
            socket.Abort();
            return (null, "connection lost");
        }

        return (null, await CloseAsync(socket, AuthFailedStatus, failure, pending, failure));
    }

    /// <summary>
    /// "" for <c>{"type":"auth","token":"…"}</c> with the session token or an integration token with the <c>read</c>
    /// scope (<paramref name="caller"/> set), else the failure reason.
    /// </summary>
    private static string CheckAuthMessage(ReadOnlySpan<byte> utf8, EngineRuntime runtime, TokenStore tokens, out Caller? caller)
    {
        caller = null;
        try
        {
            using var json = JsonDocument.Parse(utf8.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "auth"
                || !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
            {
                return AuthFailedMessage;
            }

            caller = Caller.Authenticate(token.GetString(), runtime, tokens);
            if (caller?.Allows(ApiScope.Read) == true)
                return "";
            caller = null;
            return AuthFailedToken;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // InvalidOperationException: a string that isn't valid UTF-16 once unescaped, such as a lone "\uD800".
            return AuthFailedMessage;
        }
    }

    /// <summary>Sends a close frame and waits (at most <see cref="CloseTimeout"/>) for the client's, then drops the connection.</summary>
    private static async Task<string> CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description,
        Task<ValueWebSocketReceiveResult>? pending, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, description, CancellationToken.None).WaitAsync(CloseTimeout);

            var scratch = new byte[1024];
            var until = Task.Delay(CloseTimeout);
            while (socket.State == WebSocketState.CloseSent && !until.IsCompleted)
            {
                pending ??= socket.ReceiveAsync(scratch.AsMemory(), CancellationToken.None).AsTask();
                if (await Task.WhenAny(pending, until) != pending)
                    break;
                await pending;
                pending = null;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or TimeoutException)
        {
        }

        if (socket.State != WebSocketState.Closed)
            socket.Abort();
        return reason;
    }
}

/// <summary>
/// One <c>/ws</c> connection. A receive loop parses client messages; one send loop does every send, so the render
/// thread and event handlers only ever set state and wake it. What it sends, in this order on every wake:
/// <list type="bullet">
/// <item><c>hello</c>, once, first.</item>
/// <item><c>error</c> messages, the only expendable ones: a bounded queue that drops the oldest when full.</item>
/// <item><c>doc</c>: a slot, never evicted. Document events only mark it; the send loop reads the subscribed
/// document's state when it sends, so the last <c>doc</c> a client gets is always the current state (a save's
/// <c>dirty:false</c> can't be overtaken by an older state of the same revision). Unchanged states aren't resent,
/// except right after a subscribe.</item>
/// <item><c>profiles</c>: a slot holding the newest workspace version seen; sent when it is newer than the last one sent.</item>
/// <item>The newest frame of the current subscription (each subscription has its own sink and one-slot channel).</item>
/// </list>
/// </summary>
internal sealed class PreviewSession
{
    /// <summary>How long the client has to answer our close frame before the connection is aborted.</summary>
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly WebSocket _socket;
    private readonly Workspace _workspace;
    private readonly RenderService _render;
    private readonly ILogger _logger;
    /// <summary>How many <c>error</c> messages wait at most; the oldest is dropped beyond that.</summary>
    internal const int MaxQueuedErrors = 64;

    private readonly Channel<string> _errors = Channel.CreateBounded<string>(new BoundedChannelOptions(MaxQueuedErrors) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Lock _lock = new();
    private WebPreviewSink? _sink;
    private ProfileDocument? _doc;
    private int _docChanged;
    private int _docSubscribed;
    private long _profilesVersion;
    private volatile bool _closing;
    private WebSocketCloseStatus _closeStatus = WebSocketCloseStatus.NormalClosure;
    private string _closeDescription = "";
    private CancellationTokenSource? _abort;

    public PreviewSession(WebSocket socket, Workspace workspace, RenderService render, ILogger logger)
    {
        _socket = socket;
        _workspace = workspace;
        _render = render;
        _logger = logger;
    }

    /// <summary>Tests: called with the profile's id after a subscribe found its document, before frames are attached.</summary>
    internal Action<string>? Found;

    /// <summary>The current subscription's sink (tests use it to hand an earlier subscription a late frame).</summary>
    internal WebPreviewSink? CurrentSink
    {
        get { lock (_lock) return _sink; }
    }

    public async Task RunAsync(CancellationToken aborted, CancellationToken stopping)
    {
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        _abort = abort;
        // Closed before it ran (its token revoked meanwhile): the close is bounded like any other.
        if (_closing)
            abort.CancelAfter(CloseTimeout);
        _workspace.ProfilesChanged += OnProfilesChanged;
        using var onStopping = stopping.Register(() => BeginClose(WebSocketCloseStatus.EndpointUnavailable, "The engine is stopping."));

        var sending = SendLoopAsync(abort.Token);
        try
        {
            await ReceiveLoopAsync(abort);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The client went away, or didn't answer our close in time.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A preview connection failed");
        }
        finally
        {
            _workspace.ProfilesChanged -= OnProfilesChanged;
            Unsubscribe();
            BeginClose(_closeStatus, _closeDescription);

            try
            {
                await sending.WaitAsync(CloseTimeout, CancellationToken.None);
            }
            catch (Exception)
            {
                // Aborted, timed out or failed: nothing more to send either way.
            }

            abort.Cancel();
            _abort = null;
        }
    }

    // ---- receiving ----

    private async Task ReceiveLoopAsync(CancellationTokenSource abort)
    {
        var buffer = new byte[MaxClientMessageBytesPlusOne];
        while (_socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length)
                {
                    BeginClose(WebSocketCloseStatus.MessageTooBig, $"Messages are at most {PreviewSocketHandler.MaxClientMessageBytes} bytes.");
                    await DrainUntilCloseAsync(abort.Token);
                    return;
                }

                result = await _socket.ReceiveAsync(buffer.AsMemory(length), abort.Token);
                length += result.Count;
            }
            while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                BeginClose(WebSocketCloseStatus.NormalClosure, "");
                return;
            }

            if (length > PreviewSocketHandler.MaxClientMessageBytes)
            {
                BeginClose(WebSocketCloseStatus.MessageTooBig, $"Messages are at most {PreviewSocketHandler.MaxClientMessageBytes} bytes.");
                await DrainUntilCloseAsync(abort.Token);
                return;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                SendError("Send JSON text messages; binary messages are not accepted.");
                continue;
            }

            Handle(buffer.AsSpan(0, length));
        }
    }

    private const int MaxClientMessageBytesPlusOne = PreviewSocketHandler.MaxClientMessageBytes + 1;

    /// <summary>After we have sent a close frame: read and ignore until the client's close arrives (or the timeout aborts).</summary>
    private async Task DrainUntilCloseAsync(CancellationToken ct)
    {
        var scratch = new byte[1024];
        while (_socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await _socket.ReceiveAsync(scratch.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return;
        }
    }

    private void Handle(ReadOnlySpan<byte> utf8)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException)
        {
            SendError("Messages must be JSON objects with a \"type\".");
            return;
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                SendError("Messages must be JSON objects with a \"type\".");
                return;
            }

            var type = typeElement.GetString()!;
            switch (type)
            {
                case "subscribe":
                    Subscribe(root);
                    break;
                case "view":
                    View(root);
                    break;
                case "unsubscribe":
                    Unsubscribe();
                    break;
                default:
                    SendError($"Unknown message type '{Truncate(type)}'.");
                    break;
            }
        }
    }

    private void Subscribe(JsonElement message)
    {
        if (!message.TryGetProperty("profileId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
        {
            SendError("subscribe needs a \"profileId\".");
            return;
        }

        var profileId = idElement.GetString()!;

        if (!TryReadRequest(message, new FrameRequest(1, RenderLoop.DefaultFps), out var request))
            return;

        Unsubscribe();

        var found = _workspace.Find(profileId);
        if (found.Document is not { } doc)
        {
            SendError(found.Problem is { } problem
                ? $"Profile '{Truncate(profileId)}' can't be opened: it is {problem.Code}."
                : $"No profile '{Truncate(profileId)}'.");
            return;
        }

        WebPreviewSink sink;
        lock (_lock)
        {
            sink = new WebPreviewSink(profileId, request, Wake, OnSinkClosed);
            _sink = sink;
            _doc = doc;
            doc.Changed += OnDocumentChanged;
        }

        // The current doc state goes out before the subscription's first frame (the send loop sends the slot first).
        Volatile.Write(ref _docSubscribed, 1);
        Wake();
        Found?.Invoke(profileId);
        string error;
        try
        {
            // Frames and doc events come from the one document found: deleted since (its id perhaps created again), the
            // subscription ends as a delete ends it.
            if (_render.Attach(sink, doc))
                return;
            error = $"Profile '{Truncate(profileId)}' was deleted.";
        }
        catch (ObjectDisposedException)
        {
            // The engine is stopping (the close follows).
            error = $"Profile '{profileId}' can't be previewed.";
        }

        Unsubscribe();
        SendError(error);
    }

    private void View(JsonElement message)
    {
        WebPreviewSink? sink;
        lock (_lock)
            sink = _sink;

        if (sink == null)
        {
            SendError("view needs a subscription; send subscribe first.");
            return;
        }

        if (!TryReadRequest(message, sink.Request, out var request))
            return;

        sink.Request = request;
        _render.RequestChanged(sink);
    }

    /// <summary>Reads <c>scale</c> (a positive number) and <c>maxFps</c> (a whole number, 1 or more), keeping <paramref name="current"/>'s for absent ones.</summary>
    private bool TryReadRequest(JsonElement message, FrameRequest current, out FrameRequest request)
    {
        request = current;
        var scale = current.Scale;
        var maxFps = current.MaxFps;

        if (message.TryGetProperty("scale", out var scaleElement))
        {
            if (scaleElement.ValueKind != JsonValueKind.Number || !scaleElement.TryGetSingle(out scale) || !float.IsFinite(scale) || scale <= 0)
            {
                SendError("\"scale\" must be a positive number.");
                return false;
            }
        }

        if (message.TryGetProperty("maxFps", out var fpsElement))
        {
            if (fpsElement.ValueKind != JsonValueKind.Number || !fpsElement.TryGetInt32(out maxFps) || maxFps < 1)
            {
                SendError("\"maxFps\" must be a whole number of at least 1.");
                return false;
            }
        }

        request = current with { Scale = scale, MaxFps = maxFps };
        return true;
    }

    private void Unsubscribe()
    {
        WebPreviewSink? sink;
        ProfileDocument? doc;
        lock (_lock)
        {
            sink = _sink;
            doc = _doc;
            _sink = null;
            _doc = null;
            if (doc != null)
                doc.Changed -= OnDocumentChanged;
        }

        if (sink != null)
        {
            sink.Retire();
            _render.Detach(sink);
        }
    }

    // ---- events (any thread) ----

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (sender is ProfileDocument doc)
        {
            lock (_lock)
            {
                if (doc != _doc)
                    return;
            }

            Volatile.Write(ref _docChanged, 1);
            Wake();
        }
    }

    private void OnProfilesChanged(object? sender, ProfilesChangedEventArgs e)
    {
        var seen = Volatile.Read(ref _profilesVersion);
        while (e.Version > seen)
        {
            var previous = Interlocked.CompareExchange(ref _profilesVersion, e.Version, seen);
            if (previous == seen)
                break;
            seen = previous;
        }

        Wake();
    }

    private void OnSinkClosed(WebPreviewSink sink, FrameSinkClosedReason reason)
    {
        lock (_lock)
        {
            if (sink != _sink)
                return;

            if (_doc != null)
                _doc.Changed -= OnDocumentChanged;
            _sink = null;
            _doc = null;
        }

        sink.Retire();
        switch (reason)
        {
            case FrameSinkClosedReason.ProfileDeleted:
                SendError($"Profile '{sink.ProfileId}' was deleted.");
                break;
            case FrameSinkClosedReason.Unrenderable:
                SendError($"Profile '{sink.ProfileId}' is too large to preview.");
                break;
            default:
                BeginClose(WebSocketCloseStatus.EndpointUnavailable, "The engine is stopping.");
                break;
        }
    }

    /// <summary>The subscribed document's state now, as a <c>doc</c> message; null without a subscription.</summary>
    private string? CurrentDocMessage()
    {
        ProfileDocument? doc;
        lock (_lock)
            doc = _doc;

        if (doc == null)
            return null;

        var status = doc.Status;
        return JsonSerializer.Serialize(new DocEvent
        {
            Type = "doc",
            ProfileId = doc.Id,
            Rev = status.Rev,
            Dirty = status.Dirty,
            CanUndo = status.CanUndo,
            CanRedo = status.CanRedo,
        }, SpecJson.Options);
    }

    private void SendError(string message)
    {
        if (_errors.Writer.TryWrite(JsonSerializer.Serialize(new ErrorMessage { Type = "error", Message = message }, SpecJson.Options)))
            Wake();
    }

    private void Wake()
    {
        try
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Asks the send loop to send a close frame (once) and stop; the connection is aborted if the client hasn't
    /// answered within <see cref="CloseTimeout"/>. Safe from any thread (a token's revocation calls it).
    /// </summary>
    internal void BeginClose(WebSocketCloseStatus status, string description)
    {
        lock (_lock)
        {
            if (_closing)
                return;

            _closing = true;
            _closeStatus = status;
            _closeDescription = description;
        }

        Wake();
        try
        {
            _abort?.CancelAfter(CloseTimeout);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // ---- sending ----

    private async Task SendLoopAsync(CancellationToken ct)
    {
        string? lastDoc = null;
        long profilesSent = 0;
        await SendTextAsync(JsonSerializer.Serialize(new HelloMessage { Type = "hello", Protocol = Api.ReadEndpoints.Protocol }, SpecJson.Options), ct);

        while (true)
        {
            await _signal.WaitAsync(ct);

            while (_errors.Reader.TryRead(out var text))
            {
                if (!CanSend)
                    return;
                await SendTextAsync(text, ct);
            }

            if (_closing)
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await _socket.CloseOutputAsync(_closeStatus, _closeDescription, ct);
                return;
            }

            // Clear the marks before reading the state: a change after the read marks the slot again and wakes us.
            var subscribed = Interlocked.Exchange(ref _docSubscribed, 0) == 1;
            if ((Interlocked.Exchange(ref _docChanged, 0) == 1 || subscribed) && CurrentDocMessage() is { } doc
                && (subscribed || doc != lastDoc))
            {
                if (!CanSend)
                    return;
                await SendTextAsync(doc, ct);
                lastDoc = doc;
            }

            var profiles = Volatile.Read(ref _profilesVersion);
            if (profiles > profilesSent)
            {
                if (!CanSend)
                    return;
                await SendTextAsync(JsonSerializer.Serialize(new ProfilesMessage { Type = "profiles", Version = profiles }, SpecJson.Options), ct);
                profilesSent = profiles;
            }

            WebPreviewSink? sink;
            lock (_lock)
                sink = _sink;

            // A subscribe whose doc hasn't gone out yet (it came after the check above): its frame waits a turn.
            if (sink != null && Volatile.Read(ref _docSubscribed) == 0 && sink.TryTakeFrame(out var frame) && CanSend)
            {
                await _socket.SendAsync(frame.Prefix, WebSocketMessageType.Binary, false, ct);
                await _socket.SendAsync(frame.Png, WebSocketMessageType.Binary, true, ct);
            }
        }
    }

    private Task SendTextAsync(string text, CancellationToken ct) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private bool CanSend => _socket.State is WebSocketState.Open or WebSocketState.CloseReceived;

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64] + "…";
}
