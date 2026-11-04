using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InfoPanel.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InfoPanel.Tests.Relay;

/// <summary>
/// A stand-in for the configured service's relay on a loopback port of its own (never a fixed one): it answers the
/// upgrade as a test says (<see cref="Upgrade"/>), records each upgrade request's <c>Authorization</c>, and hands each
/// accepted connection to the test as a <see cref="RelayPeer"/>.
/// </summary>
public sealed class FakeRelay : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Channel<RelayPeer> _peers = Channel.CreateUnbounded<RelayPeer>();
    private readonly ConcurrentQueue<RelayPeer> _accepted = new();
    private int _requests;

    private FakeRelay(WebApplication app) => _app = app;

    /// <summary>The relay's origin, <c>http://127.0.0.1:{port}</c>.</summary>
    public string Address { get; private set; } = "";

    /// <summary>
    /// Answers an upgrade request before it is accepted: true when it answered (a refusal: a status and headers), false to
    /// accept it. Null accepts every one.
    /// </summary>
    public Func<HttpContext, bool>? Upgrade { get; set; }

    /// <summary>Accepted connections don't read what the engine sends until <see cref="RelayPeer.StartReading"/> (pings go unanswered).</summary>
    public bool Silent { get; set; }

    /// <summary>Every request that reached the relay's path or any other, upgrade or not.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>Each request's <c>Authorization</c> header (null without one), in order.</summary>
    public ConcurrentQueue<string?> Authorizations { get; } = new();

    /// <summary>Each request's path, in order.</summary>
    public ConcurrentQueue<string> Paths { get; } = new();

    public static async Task<FakeRelay> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var relay = new FakeRelay(app);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        app.Run(relay.HandleAsync);
        await app.StartAsync();
        relay.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return relay;
    }

    /// <summary>The next connection the relay accepted.</summary>
    public async Task<RelayPeer> NextPeerAsync(CancellationToken ct) => await _peers.Reader.ReadAsync(ct).AsTask().WaitAsync(RelayRig.Limit, ct);

    /// <summary>Whether a connection is waiting to be taken by <see cref="NextPeerAsync"/>.</summary>
    public bool HasPeer => _peers.Reader.TryPeek(out _);

    private async Task HandleAsync(HttpContext context)
    {
        Interlocked.Increment(ref _requests);
        Authorizations.Enqueue(context.Request.Headers.Authorization.Count > 0 ? context.Request.Headers.Authorization.ToString() : null);
        Paths.Enqueue(context.Request.Path.Value ?? "");
        if (Upgrade?.Invoke(context) == true)
            return;
        if (!context.WebSockets.IsWebSocketRequest || context.Request.Path != RelayOptions.DefaultPath)
        {
            context.Response.StatusCode = 404;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var peer = new RelayPeer(socket, reading: !Silent);
        _accepted.Enqueue(peer);
        await _peers.Writer.WriteAsync(peer);
        await peer.RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // A connection a test left paused reads again, so it ends with the server.
        foreach (var peer in _accepted)
            peer.ResumeReading();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>One connection the fake relay accepted: the frames the engine sent, and the relay's side of it.</summary>
public sealed class RelayPeer
{
    private readonly WebSocket _socket;
    private readonly Channel<JsonObject> _frames = Channel.CreateUnbounded<JsonObject>();
    private readonly TaskCompletionSource _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WebSocketCloseStatus?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _send = new(1, 1);
    private TaskCompletionSource? _paused;

    public RelayPeer(WebSocket socket, bool reading)
    {
        _socket = socket;
        if (reading)
            _read.SetResult();
    }

    /// <summary>The close the engine sent (its status), or null when the connection ended without one.</summary>
    public Task<WebSocketCloseStatus?> Closed => _closed.Task;

    /// <summary>The close's description the engine sent.</summary>
    public string? CloseDescription { get; private set; }

    /// <summary>Starts reading a silent connection's frames.</summary>
    public void StartReading() => _read.TrySetResult();

    /// <summary>
    /// Stops reading what the engine sends (after at most the read under way): its frames and its close wait unread, so
    /// its sends meet backpressure and its close is not answered, until <see cref="ResumeReading"/>.
    /// </summary>
    public void PauseReading()
    {
        lock (_frames)
            _paused ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Reads again after <see cref="PauseReading"/>.</summary>
    public void ResumeReading()
    {
        TaskCompletionSource? paused;
        lock (_frames)
            (paused, _paused) = (_paused, null);
        paused?.TrySetResult();
    }

    private Task Unpaused()
    {
        lock (_frames)
            return _paused?.Task ?? Task.CompletedTask;
    }

    internal async Task RunAsync()
    {
        await _read.Task;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                await Unpaused();
                var received = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    CloseDescription = _socket.CloseStatusDescription;
                    _closed.TrySetResult(_socket.CloseStatus);
                    if (_socket.State == WebSocketState.CloseReceived)
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }

                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;
                _frames.Writer.TryWrite(JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray()))!.AsObject());
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            _closed.TrySetResult(null);
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    /// <summary>The next frame the engine sent.</summary>
    public async Task<JsonObject> NextAsync(CancellationToken ct) => await _frames.Reader.ReadAsync(ct).AsTask().WaitAsync(RelayRig.Limit, ct);

    /// <summary>The next frame the engine sent, which must be of <paramref name="type"/>.</summary>
    public async Task<JsonObject> ExpectAsync(string type, CancellationToken ct)
    {
        var frame = await NextAsync(ct);
        Assert.True(frame["type"]?.GetValue<string>() == type, $"Expected a {type} frame, got {frame.ToJsonString()}");
        return frame;
    }

    /// <summary>Whether a frame is waiting to be read.</summary>
    public bool HasFrame => _frames.Reader.TryPeek(out _);

    /// <summary>Answers the engine's next frame, a <c>time</c> question, with <paramref name="now"/> (ms since 1970 UTC).</summary>
    public async Task AnswerTimeAsync(long now, CancellationToken ct)
    {
        var question = await ExpectAsync("time", ct);
        await SendAsync(new JsonObject { ["type"] = "time", ["nonce"] = question["nonce"]!.GetValue<string>(), ["now"] = now });
    }

    public Task SendAsync(JsonObject frame) => SendTextAsync(frame.ToJsonString());

    public async Task SendTextAsync(string text)
    {
        await _send.WaitAsync();
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    public async Task SendBinaryAsync(byte[] bytes)
    {
        await _send.WaitAsync();
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>The relay closes the connection with <paramref name="code"/>.</summary>
    public async Task CloseAsync(int code)
    {
        await _send.WaitAsync();
        try
        {
            await _socket.CloseOutputAsync((WebSocketCloseStatus)code, null, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>The connection drops, with no close.</summary>
    public void Abort() => _socket.Abort();
}

/// <summary>
/// The engine's two clocks, apart: the wall clock and the monotonic clock (Stopwatch's) move together on
/// <see cref="Advance"/>; <see cref="Sleep"/> moves the wall clock alone (macOS stops Stopwatch in sleep) and the relay's
/// true time with it; <see cref="StepWall"/> moves the wall clock alone and not the true time (a user or the network setting
/// it). Timers run on the monotonic clock and fire as it passes their due times; a test waits for one to be armed with
/// <see cref="TimerArmedAsync"/>.
/// </summary>
public sealed class TwoClocks : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<Timer> _timers = [];
    private readonly List<(TimeSpan Due, TaskCompletionSource Armed)> _waiters = [];
    private readonly DateTimeOffset _start;
    private DisposeHold? _disposeHold;
    private TimeSpan _wall;
    private TimeSpan _true;
    private long _monotonic;

    public TwoClocks(DateTimeOffset start) => _start = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
            return _start + _wall;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
            return _monotonic;
    }

    /// <summary>The relay's clock: the true time, in ms since 1970 UTC.</summary>
    public long RelayNow
    {
        get
        {
            lock (_lock)
                return (long)(_start + _true - DateTimeOffset.UnixEpoch).TotalMilliseconds;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Completes once a timer is armed to fire <paramref name="due"/> from the moment it was armed.</summary>
    public Task TimerArmedAsync(TimeSpan due)
    {
        lock (_lock)
        {
            if (_timers.Any(t => t.Length == due && t.DueAt is not null))
                return Task.CompletedTask;
            var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((due, armed));
            return armed.Task;
        }
    }

    /// <summary>
    /// Holds the next disposal of a timer armed for <paramref name="length"/> (a <c>Task.Delay</c> on these clocks cancelled
    /// once what it waited beside arrived): the thread disposing it waits there until the hold is released, so a test
    /// holds that thread's continuation at that point.
    /// </summary>
    public DisposeHold HoldDispose(TimeSpan length)
    {
        var hold = new DisposeHold(length);
        lock (_lock)
            _disposeHold = hold;
        return hold;
    }

    /// <summary>A held timer disposal (<see cref="HoldDispose"/>): <see cref="Disposing"/> once a thread waits in it; released on dispose too.</summary>
    public sealed class DisposeHold(TimeSpan length) : IDisposable
    {
        private readonly TaskCompletionSource _disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _released = new();

        public TimeSpan Length => length;

        /// <summary>Completes once a thread is held in the timer's disposal.</summary>
        public Task Disposing => _disposing.Task;

        public void Release() => _released.Set();

        internal void Wait()
        {
            _disposing.TrySetResult();
            _released.Wait(RelayRig.Limit);
        }

        public void Dispose() => Release();
    }

    private void Disposing(Timer timer)
    {
        DisposeHold? hold;
        lock (_lock)
        {
            hold = _disposeHold is { } held && held.Length == timer.Length && timer.DueAt is not null ? held : null;
            if (hold is not null)
                _disposeHold = null;
        }

        hold?.Wait();
    }

    /// <summary>How many timers are armed.</summary>
    public int Armed
    {
        get
        {
            lock (_lock)
                return _timers.Count(t => t.DueAt is not null);
        }
    }

    /// <summary>Both clocks and the true time move on by <paramref name="by"/>; timers due meanwhile fire, in order.</summary>
    public void Advance(TimeSpan by)
    {
        long end;
        lock (_lock)
        {
            _wall += by;
            _true += by;
            end = _monotonic + by.Ticks;
        }

        RunTimers(end);
    }

    /// <summary>Only the monotonic clock moves on (a backward step of the wall clock is <see cref="StepWall"/> with this).</summary>
    public void AdvanceMonotonic(TimeSpan by)
    {
        long end;
        lock (_lock)
        {
            _true += by;
            end = _monotonic + by.Ticks;
        }

        RunTimers(end);
    }

    /// <summary>A sleep on macOS: the wall clock and the true time move on, the monotonic clock doesn't, so no timer fires.</summary>
    public void Sleep(TimeSpan by)
    {
        lock (_lock)
        {
            _wall += by;
            _true += by;
        }
    }

    /// <summary>The wall clock is set <paramref name="by"/> (back when negative); the true time and the monotonic clock don't move.</summary>
    public void StepWall(TimeSpan by)
    {
        lock (_lock)
            _wall += by;
    }

    private void RunTimers(long end)
    {
        while (true)
        {
            Timer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.DueAt is { } due && due <= end).MinBy(t => t.DueAt!.Value);
                if (next is null)
                {
                    _monotonic = end;
                    return;
                }

                _monotonic = Math.Max(_monotonic, next.DueAt!.Value);
                next.DueAt = next.Period is { } period ? _monotonic + period : null;
                if (next.DueAt is null)
                    _timers.Remove(next);
            }

            next.Fire();
        }
    }

    private sealed class Timer(TwoClocks owner, TimerCallback callback, object? state) : ITimer
    {
        public long? DueAt;
        public long? Period;
        public TimeSpan Length;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            List<TaskCompletionSource> armed = [];
            lock (owner._lock)
            {
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    DueAt = null;
                    owner._timers.Remove(this);
                }
                else
                {
                    Length = dueTime;
                    DueAt = owner._monotonic + dueTime.Ticks;
                    if (!owner._timers.Contains(this))
                        owner._timers.Add(this);
                    foreach (var waiter in owner._waiters.Where(w => w.Due == dueTime).ToList())
                    {
                        owner._waiters.Remove(waiter);
                        armed.Add(waiter.Armed);
                    }
                }
            }

            foreach (var waiter in armed)
                waiter.TrySetResult();
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            owner.Disposing(this);
            Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A credential store in memory.</summary>
public sealed class MemoryCredentialStore : IServiceCredentialStore
{
    private StoredServiceCredential? _held;

    public string Name => "private";

    public StoredServiceCredential? Read() => _held;

    public void Write(StoredServiceCredential record) => _held = record;

    public void Delete() => _held = null;
}

/// <summary>
/// A tool host that records each call and answers it with a text result, or holds it until the test releases it
/// (<see cref="Hold"/>), so a test sees what reaches the tools and nothing else.
/// </summary>
public sealed class FakeToolHost : IRelayToolHost
{
    public ConcurrentQueue<(string Tool, string Arguments, RelayCallContext Context)> Calls { get; } = new();

    /// <summary>When set, each call waits for it before answering.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Signalled each time a call starts.</summary>
    public Channel<string> Started { get; } = Channel.CreateUnbounded<string>();

    public async Task<RelayToolAnswer> CallAsync(string tool, JsonElement arguments, RelayCallContext context, CancellationToken ct)
    {
        Calls.Enqueue((tool, arguments.GetRawText(), context));
        Started.Writer.TryWrite(context.Id);
        if (Hold is { } hold)
            await hold.Task.WaitAsync(ct);
        return new RelayToolAnswer(new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"ran {tool}" }),
            ["isError"] = false,
        }, Cancelled: false);
    }
}
