using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace InfoPanel.Service;

/// <summary>How the connection AI apps elsewhere use stands (the API's <c>serviceState.aiApps</c>).</summary>
public enum RelayState
{
    /// <summary>None is open or being made: the setting is off, or there is no address or no usable credential.</summary>
    Off,

    /// <summary>Connecting, or waiting to connect again after a failure.</summary>
    Connecting,

    /// <summary>The service accepted the connection (<c>welcome</c>).</summary>
    Connected,

    /// <summary>Another engine with the same credential took over (4409): stopped until the setting is turned off and on, or a restart.</summary>
    Replaced,

    /// <summary>The service doesn't serve this engine's protocol (4426): stopped until the engine is updated.</summary>
    Outdated,

    /// <summary>The service asked the engine to wait (429 or 503 with <c>Retry-After</c>, or 4429) before connecting again.</summary>
    Waiting,
}

/// <summary>What a relay call carries into the tool host: its id, the most text its AI app takes, and its cutoff for changes.</summary>
public sealed record RelayCallContext(string Id, int? MaxChars, RelayCutoff Cutoff)
{
    /// <summary>A design the service produced, for the tool to commit (docs/2.0/relay.md, "Designs"), or null.</summary>
    public JsonElement? Design { get; init; }

    /// <summary><c>add_items</c>' designs, one per item in order (null for an item given as a scene), or null.</summary>
    public IReadOnlyList<JsonElement?>? Designs { get; init; }

    /// <summary>The texts the design needs stored, each under its SHA-256, or null.</summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>What <c>update_item</c>'s change without a design was built on, or null.</summary>
    public JsonElement? BasedOn { get; init; }

    /// <summary><c>update_items</c>' <c>basedOn</c>, one per entry in order (null for an entry not based on a read), or null.</summary>
    public IReadOnlyList<JsonElement?>? BasedOnList { get; init; }
}

/// <summary>A relay call's answer: the tool's result, or that its change was stopped before it was made.</summary>
public sealed record RelayToolAnswer(JsonObject Result, bool Cancelled)
{
    /// <summary>A <c>read</c>'s answer, the <c>item</c> frame's members (<c>instance</c>, <c>rev</c>, <c>profile</c>, <c>item</c>, <c>detail</c>), or null for <see cref="Result"/>.</summary>
    public JsonObject? Item { get; init; }

    /// <summary>The design's base no longer held inside the same document: nothing was changed, answered <c>stale</c>.</summary>
    public bool Stale { get; init; }
}

/// <summary>
/// The only thing a relay call reaches: the engine's tool host, by tool name. Nothing else (the API, settings, tokens,
/// logs, folders, plugins) is given to the relay client.
/// </summary>
public interface IRelayToolHost
{
    /// <summary>Runs <paramref name="tool"/> for a call from elsewhere, under the engine's rules for such calls.</summary>
    Task<RelayToolAnswer> CallAsync(string tool, JsonElement arguments, RelayCallContext context, CancellationToken ct);

    /// <summary>
    /// Reads one item of <paramref name="profile"/> for a <c>read</c> frame: the answer's <see cref="RelayToolAnswer.Item"/>,
    /// or an error result (no such profile or item, or a name more than one has).
    /// </summary>
    Task<RelayToolAnswer> ReadAsync(string profile, string item, bool detail, RelayCallContext context, CancellationToken ct) =>
        Task.FromResult(new RelayToolAnswer(new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "This computer doesn't read items for the service (unsupported)." }),
            ["isError"] = true,
        }, Cancelled: false));
}

/// <summary>The relay client's settings: the path on the service, the engine's version, and the limits.</summary>
public sealed class RelayOptions
{
    /// <summary>The relay's path on the configured service.</summary>
    public const string DefaultPath = "/svc/v1/relay";

    public string Path { get; init; } = DefaultPath;

    /// <summary>The engine's version, in its <c>hello</c>.</summary>
    public required string EngineVersion { get; init; }

    /// <summary><c>--offline</c>: only a service on this computer.</summary>
    public bool LoopbackOnly { get; init; }

    /// <summary>Protocol pings this often (20 s).</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A ping unanswered this long ends the connection (10 s).</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>At most this many relay calls run at once; another answers <c>busy</c>.</summary>
    public int MaxCalls { get; init; } = 8;

    /// <summary>
    /// The tests' seam: called, on the thread that gets there, at named points of a call (<c>joined</c>: it joined the
    /// reading in flight; <c>checked</c>: its reading was checked and the clocks read for it). Null in the engine.
    /// </summary>
    public Action<string>? Step { get; init; }
}

/// <summary>
/// The engine's side of AI apps elsewhere (docs/2.0/relay.md): while the <c>service.aiApps</c> setting is on and the
/// engine has a service address and a present credential, it keeps one WebSocket open to the configured service's relay,
/// opened by the engine with <see cref="ServiceClient"/>'s rules (the address's origin only, <c>wss</c> or loopback
/// <c>ws</c>, no proxy, no redirect followed, the credential only in the upgrade's <c>Authorization</c>), and answers the
/// tool calls that arrive over it through <see cref="IRelayToolHost"/> alone. A 401 or close 4401 marks the credential
/// rejected (by its generation); 429 or 503 waits for <c>Retry-After</c>, close 4429 ten minutes; 4409 stops it until the
/// setting is turned off and on, 4426 until an update; anything else backs off from 1 s, doubling to 60 s with jitter,
/// cut short by a network change. A call is refused unrun (<c>late</c>) when it was sent over 10 s ago on the relay's
/// clock, read by a <c>time</c> challenge (<see cref="RelayClock"/>), and its change stops unmade (<c>cancelled</c>) once
/// its cutoff trips (<see cref="RelayCutoff"/>): <c>cancel</c>, the connection's close, or 60 s after it was sent on
/// both of the engine's clocks.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CloseWait = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    /// <summary>A connection that lasted this long resets the backoff.</summary>
    public static readonly TimeSpan SteadyAfter = TimeSpan.FromSeconds(60);

    /// <summary>Close codes the relay uses.</summary>
    public const int ClosedRemoved = 4401, ClosedReplaced = 4409, ClosedOutdated = 4426, ClosedWait = 4429;

    private readonly ServiceCredentials _credentials;
    private readonly IRelayToolHost _tools;
    private readonly RelayOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<double> _random;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _calls = [];

    private bool _enabled;

    /// <summary>The setting's generation: one more each time it is turned off. A connection's outcome counts only in the generation it began in.</summary>
    private long _epoch;

    private bool _replaced;
    private bool _outdated;
    private RelayState _state = RelayState.Off;
    private CancellationTokenSource _wake = new();
    private CancellationTokenSource _network = new();
    private Session? _session;
    private int _running;
    private int _disposed;
    private Task? _loop;

    /// <param name="random">The backoff's jitter, from 0 to 1 (a test fixes it).</param>
    public RelayClient(ServiceCredentials credentials, IRelayToolHost tools, RelayOptions options, TimeProvider? time = null, ILogger? logger = null,
        Func<double>? random = null)
    {
        _credentials = credentials;
        _tools = tools;
        _options = options;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? Log.ForContext<RelayClient>();
        _random = random ?? Random.Shared.NextDouble;
        _credentials.Changed += ConfigChanged;
    }

    /// <summary>How the connection stands.</summary>
    public RelayState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>Raised, outside the client's lock, when <see cref="State"/> changes.</summary>
    public event Action<RelayState>? StateChanged;

    /// <summary>
    /// Raised each time the client settles into a wait (for a change, for a retry, or for a wait the service asked for),
    /// with its state then: it has acted on everything it was told before.
    /// </summary>
    public event Action<RelayState>? Settled;

    /// <summary>How many relay calls are running.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Starts the client's loop (once).</summary>
    public void Start()
    {
        lock (_lock)
            _loop ??= Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>
    /// The <c>service.aiApps</c> setting: on, the client connects when it can; off, it sends <c>bye</c> and closes. Turning
    /// it off also clears <see cref="RelayState.Replaced"/>.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_enabled == enabled)
                return;
            _enabled = enabled;
            if (!enabled)
            {
                _replaced = false;
                _epoch++;
            }
        }

        ConfigChanged();
    }

    /// <summary>
    /// The settings or the credential may have changed: a wait is woken, and a connection made with an address or
    /// credential no longer in effect, or while the setting is now off, is closed.
    /// </summary>
    public void ConfigChanged()
    {
        Session? session;
        lock (_lock)
        {
            _wake.Cancel();
            session = _session;
        }

        if (session is not null && !Holds(session))
            session.End(Enabled ? EndReason.Changed : EndReason.TurnedOff);
    }

    /// <summary>The computer's network changed: a wait after a failure ends at once, and the backoff starts again.</summary>
    public void NetworkChanged()
    {
        lock (_lock)
            _network.Cancel();
    }

    private bool Enabled
    {
        get
        {
            lock (_lock)
                return _enabled;
        }
    }

    /// <summary>Whether <paramref name="session"/> is still the connection to keep: the setting on, its address and credential in effect.</summary>
    private bool Holds(Session session) =>
        Enabled && _credentials.TryTake(out var address, out _, out _, out var generation) && address == session.Address && generation == session.Generation;

    private void Set(RelayState state)
    {
        lock (_lock)
        {
            if (_state == state)
                return;
            _state = state;
        }

        StateChanged?.Invoke(state);
    }

    /// <summary>The relay's address on <paramref name="address"/>: <c>wss</c>, or <c>ws</c> on a loopback host; null when the rules refuse it.</summary>
    internal Uri? Target(string? address)
    {
        if (!ServiceAddress.IsNormal(address))
            return null;
        var origin = new Uri(address!);
        if (_options.LoopbackOnly && !ServiceAddress.IsLoopbackHost(origin))
            return null;
        if (ServiceClient.Target(origin, _options.Path) is not { } target)
            return null;
        return new UriBuilder(target) { Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri;
    }

    /// <summary>A backoff wait after <paramref name="failures"/> failures in a row: 1 s doubling to 60 s, from 80 % to 120 % of it, never over 60 s.</summary>
    internal TimeSpan Backoff(int failures)
    {
        var seconds = Math.Min(FirstBackoff.TotalSeconds * Math.Pow(2, Math.Max(0, failures - 1)), MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Min(seconds * (0.8 + 0.4 * _random()), MaxBackoff.TotalSeconds));
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        var failures = 0;
        while (!stop.IsCancellationRequested)
        {
            CancellationToken wake;
            bool enabled, replaced, outdated;
            long epoch;
            lock (_lock)
            {
                if (_wake.IsCancellationRequested)
                {
                    _wake.Dispose();
                    _wake = new CancellationTokenSource();
                }

                wake = _wake.Token;
                (enabled, replaced, outdated, epoch) = (_enabled, _replaced, _outdated, _epoch);
            }

            Outcome outcome;
            try
            {
                if (!enabled || outdated || replaced
                    || !_credentials.TryTake(out var address, out var credential, out _, out var generation) || Target(address) is not { } uri)
                {
                    Set(!enabled ? RelayState.Off : outdated ? RelayState.Outdated : replaced ? RelayState.Replaced : RelayState.Off);
                    failures = 0;
                    Settled?.Invoke(State);
                    await WaitAsync(Timeout.InfiniteTimeSpan, wake, CancellationToken.None, stop).ConfigureAwait(false);
                    continue;
                }

                Set(RelayState.Connecting);
                outcome = await ConnectAsync(uri, address!, credential!, generation, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }

            CancellationToken network;
            lock (_lock)
            {
                // A connection that began before the setting was turned off (and maybe on again) is over, whatever it
                // ended with: what it says (4409 included) was about it, not about the connection to make now. A refused
                // credential still counts, by the credential's own generation. What the outcome stops is kept here, under
                // the same lock as that check, so a turn off and on after it (even from the log line below) clears it.
                if (_epoch != epoch && outcome.Kind != OutcomeKind.Rejected)
                    outcome = new Outcome(OutcomeKind.Stopped);
                if (outcome.Kind == OutcomeKind.Replaced)
                    _replaced = true;
                else if (outcome.Kind == OutcomeKind.Outdated)
                    _outdated = true;
                if (_network.IsCancellationRequested)
                {
                    _network.Dispose();
                    _network = new CancellationTokenSource();
                }

                network = _network.Token;
            }

            TimeSpan wait;
            var cutShort = CancellationToken.None;
            switch (outcome.Kind)
            {
                case OutcomeKind.Rejected:
                    _logger.Warning("AI apps elsewhere: the configured service refused the credential; not connecting again until a client provides another");
                    _credentials.Reject(outcome.Generation);
                    failures = 0;
                    continue;
                case OutcomeKind.Replaced:
                    _logger.Information("AI apps elsewhere: another engine took over the connection; stopped until the setting is turned off and on");
                    continue;
                case OutcomeKind.Outdated:
                    _logger.Warning("AI apps elsewhere: the configured service no longer serves this engine's version; stopped until an update");
                    continue;
                case OutcomeKind.Stopped:
                    failures = 0;
                    continue;
                case OutcomeKind.WaitFor:
                    Set(RelayState.Waiting);
                    _logger.Information("AI apps elsewhere: the configured service asked the engine to wait {Seconds} s", (int)outcome.Wait.TotalSeconds);
                    wait = outcome.Wait;
                    failures = 0;
                    break;
                case OutcomeKind.Ended:
                    Set(RelayState.Connecting);
                    failures = outcome.Steady ? 1 : failures + 1;
                    wait = Backoff(failures);
                    cutShort = network;
                    break;
                default:
                    Set(RelayState.Connecting);
                    failures++;
                    wait = Backoff(failures);
                    cutShort = network;
                    _logger.Information("AI apps elsewhere: couldn't connect ({Reason}); trying again in {Seconds:0.#} s", outcome.Reason, wait.TotalSeconds);
                    break;
            }

            try
            {
                Settled?.Invoke(State);
                if (await WaitAsync(wait, wake, cutShort, stop).ConfigureAwait(false) == Woken.Network)
                    failures = 0;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
        }

        Set(RelayState.Off);
    }

    private enum Woken
    {
        Elapsed,
        Config,
        Network,
    }

    /// <summary>Waits <paramref name="wait"/> on the client's clock, or until a config change or (when given) a network change.</summary>
    private async Task<Woken> WaitAsync(TimeSpan wait, CancellationToken wake, CancellationToken network, CancellationToken stop)
    {
        using var any = CancellationTokenSource.CreateLinkedTokenSource(wake, network, stop);
        try
        {
            await Task.Delay(wait, _time, any.Token).ConfigureAwait(false);
            return Woken.Elapsed;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return wake.IsCancellationRequested ? Woken.Config : Woken.Network;
        }
    }

    private enum OutcomeKind
    {
        Failed,
        Rejected,
        Replaced,
        Outdated,
        WaitFor,
        Stopped,
        Ended,
    }

    private readonly record struct Outcome(OutcomeKind Kind, long Generation = 0, TimeSpan Wait = default, bool Steady = false, string Reason = "")
    {
        public static Outcome Failed(string reason) => new(OutcomeKind.Failed, Reason: reason);
    }

    private enum EndReason
    {
        None,
        TurnedOff,
        Changed,
        Shutdown,
    }

    private async Task<Outcome> ConnectAsync(Uri uri, string address, string credential, long generation, CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = _options.KeepAliveInterval;
        socket.Options.KeepAliveTimeout = _options.KeepAliveTimeout;
        socket.Options.CollectHttpResponseDetails = true;
        // The credential only here, in the upgrade request; the handler follows no redirect and uses no proxy.
        socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
        using var invoker = new HttpMessageInvoker(ServiceClient.NewHandler(), disposeHandler: true);

        var session = new Session(this, socket, address, generation);
        lock (_lock)
            _session = session;
        try
        {
            // A change between taking the credential and here ends the attempt at once.
            if (!Holds(session))
                return new Outcome(OutcomeKind.Stopped);
            try
            {
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(session.Ending, stop);
                await socket.ConnectAsync(uri, invoker, connecting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.Ending.IsCancellationRequested || stop.IsCancellationRequested)
            {
                return new Outcome(OutcomeKind.Stopped);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or InvalidOperationException)
            {
                return Refused(socket, generation, ex);
            }

            return await session.RunAsync(stop).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
            {
                if (_session == session)
                    _session = null;
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>What an upgrade the service didn't accept means: 401 the credential refused, 429 or 503 a wait, anything else a failure.</summary>
    private Outcome Refused(ClientWebSocket socket, long generation, Exception ex)
    {
        var status = socket.HttpStatusCode;
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return new Outcome(OutcomeKind.Rejected, generation);
            case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable when RetryAfter(socket.HttpResponseHeaders) is { } wait:
                return new Outcome(OutcomeKind.WaitFor, Wait: wait);
            case 0:
                return Outcome.Failed(ex.GetType().Name);
            default:
                return Outcome.Failed($"{(int)status}");
        }
    }

    /// <summary>A <c>Retry-After</c> header's wait (seconds or a date), at least 1 s and at most <see cref="MaxRetryAfter"/>; null without one.</summary>
    internal TimeSpan? RetryAfter(IReadOnlyDictionary<string, IEnumerable<string>>? headers)
    {
        if (headers is null)
            return null;
        var value = headers.FirstOrDefault(h => string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        TimeSpan wait;
        if (long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            wait = TimeSpan.FromSeconds(Math.Min(seconds, (long)MaxRetryAfter.TotalSeconds));
        else if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
            wait = date - _time.GetUtcNow();
        else
            return null;
        return wait < FirstBackoff ? FirstBackoff : wait > MaxRetryAfter ? MaxRetryAfter : wait;
    }

    private void Track(Task call)
    {
        lock (_calls)
            _calls.Add(call);
        _ = call.ContinueWith(t =>
        {
            lock (_calls)
                _calls.Remove(t);
        }, TaskScheduler.Default);
    }

    /// <summary>Stops the client (once): a connection closes as "going away", without <c>bye</c>; running calls are cut off and waited for.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _credentials.Changed -= ConfigChanged;
        Session? session;
        Task? loop;
        lock (_lock)
        {
            session = _session;
            loop = _loop;
        }

        session?.End(EndReason.Shutdown);
        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] calls;
        lock (_calls)
            calls = [.. _calls];
        await Task.WhenAll(calls).ConfigureAwait(false);
    }

    /// <summary>One connection: its frames, the relay's clock readings, and the calls that arrived over it.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly RelayClient _client;
        private readonly ClientWebSocket _socket;
        private readonly SemaphoreSlim _send = new(1, 1);

        /// <summary>Cancelled as the session is disposed: a send waiting its turn, or stalled, stops.</summary>
        private readonly CancellationTokenSource _sending = new();

        /// <summary>Completes once the session is disposed and no send is under way (<see cref="_senders"/> at 0).</summary>
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Sends under way, and one for the session itself until it is disposed.</summary>
        private int _senders = 1;

        private readonly CancellationTokenSource _ending = new();
        private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly Dictionary<string, RelayCutoff> _calls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<(long Now, DateTimeOffset Wall, long Monotonic)>> _readings = new(StringComparer.Ordinal);
        private RelayReading? _reading;
        private Task<RelayReading?>? _readingInFlight;
        private EndReason _endReason;
        private bool _welcomed;
        private long _welcomedAt;
        private ClockSnapshot _opened, _lastMessage;
        private int _ended;

        public Session(RelayClient client, ClientWebSocket socket, string address, long generation)
        {
            _client = client;
            _socket = socket;
            Address = address;
            Generation = generation;
        }

        public string Address { get; }

        public long Generation { get; }

        /// <summary>Cancelled once the engine ends the connection.</summary>
        public CancellationToken Ending => _ending.Token;

        private TimeProvider Time => _client._time;

        /// <summary>
        /// Ends the connection from the engine's side: turned off, <c>bye</c> and a normal close; a change of address or
        /// credential, a normal close; a shutdown, "going away". The service's close answer, or an abort 5 s later, ends
        /// the receive loop.
        /// </summary>
        public void End(EndReason reason)
        {
            lock (_lock)
            {
                if (_endReason != EndReason.None)
                    return;
                _endReason = reason;
            }

            _ending.Cancel();
            // Every call's change stops now, not once the service has answered the close.
            TripCalls("closed");
            _ = CloseAsync(reason);
        }

        private async Task CloseAsync(EndReason reason)
        {
            // Five seconds for all of it (bye, the close, the service's answer), even behind a send that can't finish;
            // then the connection is cut off.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    if (reason == EndReason.TurnedOff)
                        await SendAsync(RelayFrames.Bye(), limit.Token).ConfigureAwait(false);
                    await SendingAsync(() => _socket.CloseOutputAsync(reason == EndReason.Shutdown ? WebSocketCloseStatus.EndpointUnavailable : WebSocketCloseStatus.NormalClosure,
                        reason == EndReason.TurnedOff ? "bye" : null, limit.Token), limit.Token).ConfigureAwait(false);
                }

                // The service answers the close; one that doesn't is cut off.
                await _received.Task.WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
            }

            try
            {
                if (!_received.Task.IsCompleted)
                    _socket.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async Task<Outcome> RunAsync(CancellationToken stop)
        {
            using var shutdown = stop.Register(() => End(EndReason.Shutdown));
            _opened = _lastMessage = ClockSnapshot.Take(Time);
            var started = _opened.Monotonic;
            try
            {
                await SendAsync(RelayFrames.Hello(_client._options.EngineVersion)).ConfigureAwait(false);
                return await ReceiveAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                LogEnded(Failure(ex));
                return Ended(started, ex.GetType().Name);
            }
            finally
            {
                _received.TrySetResult();
                TripAll("closed");
            }
        }

        /// <summary>
        /// One line per connection that ended: why, how old it was, and the time since the last message it received, on the
        /// wall clock (the running clock stops while macOS sleeps), or the running clock's when the wall clock was set back;
        /// with how long the computer slept when the wall clock ran over 5 s ahead of the running clock. Only the first reason
        /// given is logged, so a close that then fails or stalls adds no second line.
        /// </summary>
        private void LogEnded(string reason)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
                return;
            bool cancelled;
            lock (_lock)
                cancelled = _endReason != EndReason.None;
            var now = ClockSnapshot.Take(Time);
            TimeSpan Since(ClockSnapshot then) => TimeSpan.FromTicks(Math.Max((now.Wall - then.Wall).Ticks, Time.GetElapsedTime(then.Monotonic, now.Monotonic).Ticks));
            var age = Since(_opened).TotalSeconds;
            var idle = Since(_lastMessage).TotalSeconds;
            var asleep = (now.Wall - _opened.Wall - Time.GetElapsedTime(_opened.Monotonic, now.Monotonic)).TotalSeconds;
            if (asleep > 5)
                _client._logger.Information("AI apps elsewhere: the connection ended ({Reason}) after {Age:0.#} s, asleep about {Asleep:0} s; the last message came {Idle:0.#} s before",
                    cancelled ? "cancelled" : reason, age, Math.Round(asleep), idle);
            else
                _client._logger.Information("AI apps elsewhere: the connection ended ({Reason}) after {Age:0.#} s; the last message came {Idle:0.#} s before",
                    cancelled ? "cancelled" : reason, age, idle);
        }

        /// <summary>A failure as its type and codes; never its message, which can carry an address or what came from elsewhere.</summary>
        private static string Failure(Exception ex)
        {
            var codes = new List<string>();
            if (ex is WebSocketException webSocket)
                codes.Add(webSocket.WebSocketErrorCode.ToString());
            for (var inner = ex; inner is not null; inner = inner.InnerException)
                if (inner is SocketException socket)
                {
                    codes.Add(socket.SocketErrorCode.ToString());
                    break;
                }

            codes.Add($"0x{ex.HResult:X8}");
            return $"{ex.GetType().Name} ({string.Join(", ", codes)})";
        }

        /// <summary>A close's description, when it is one the relay is known to send; any other text from elsewhere isn't logged.</summary>
        private static string Closing(int? code, string? description) => description switch
        {
            null or "" => $"closed {code}",
            "bye" or "replaced" => $"closed {code} ({description})",
            _ => $"closed {code} (description withheld)",
        };

        private Outcome Ended(long started, string reason)
        {
            EndReason ended;
            lock (_lock)
                ended = _endReason;
            if (ended != EndReason.None)
                return new Outcome(OutcomeKind.Stopped);
            var steady = _welcomed && Time.GetElapsedTime(_welcomedAt) >= SteadyAfter;
            return new Outcome(OutcomeKind.Ended, Steady: steady, Reason: reason);
        }

        private async Task<Outcome> ReceiveAsync()
        {
            var started = Time.GetTimestamp();
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (true)
            {
                var received = await _socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    return Closed(started);
                if (message.Length + received.Count > RelayFrames.MaxFrameBytes)
                {
                    // Over 9 MiB as it arrives: the rest isn't read.
                    _client._logger.Warning("AI apps elsewhere: the relay sent a frame over {Limit} bytes; closing", RelayFrames.MaxFrameBytes);
                    LogEnded("too big");
                    _client._options.Step?.Invoke("tooBig");
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None).ConfigureAwait(false);
                    return Outcome.Failed("tooBig");
                }

                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;

                var wall = Time.GetUtcNow();
                var monotonic = Time.GetTimestamp();
                _lastMessage = new ClockSnapshot(wall, monotonic);
                var bytes = message.GetBuffer().AsMemory(0, (int)message.Length);
                if (received.MessageType == WebSocketMessageType.Binary)
                    await SendAsync(RelayFrames.Error("unsupported", null)).ConfigureAwait(false);
                else
                    await DispatchAsync(RelayFrames.Read(bytes), wall, monotonic).ConfigureAwait(false);
                message.SetLength(0);
            }
        }

        private Outcome Closed(long started)
        {
            var code = (int?)_socket.CloseStatus;
            LogEnded(Closing(code, _socket.CloseStatusDescription));
            switch (code)
            {
                case ClosedRemoved:
                    return new Outcome(OutcomeKind.Rejected, Generation);
                case ClosedReplaced:
                    return new Outcome(OutcomeKind.Replaced);
                case ClosedOutdated:
                    return new Outcome(OutcomeKind.Outdated);
                case ClosedWait:
                    return new Outcome(OutcomeKind.WaitFor, Wait: CloseWait);
                default:
                    return Ended(started, $"closed {code}");
            }
        }

        private async Task DispatchAsync(RelayFrame frame, DateTimeOffset wall, long monotonic)
        {
            switch (frame)
            {
                case RelayFrame.Welcome:
                    lock (_lock)
                    {
                        if (_welcomed)
                            break;
                        _welcomed = true;
                        _welcomedAt = monotonic;
                    }

                    if (_client.Holds(this))
                    {
                        _client.Set(RelayState.Connected);
                        _client._logger.Information("AI apps elsewhere: connected to the configured service");
                    }

                    _ = ReadingAsync();
                    break;
                case RelayFrame.Time time:
                    TaskCompletionSource<(long, DateTimeOffset, long)>? waiting;
                    lock (_lock)
                        _readings.Remove(time.Nonce, out waiting);
                    waiting?.TrySetResult((time.Now, wall, monotonic));
                    break;
                case RelayFrame.Cancel cancel:
                    RelayCutoff? cutoff;
                    lock (_lock)
                        _calls.TryGetValue(cancel.Id, out cutoff);
                    cutoff?.Trip("cancel");
                    break;
                case RelayFrame.Call call:
                    await AdmitAsync(new Work(call.Id, call.Tool, call.SentAt, call.Deadline, call.MaxChars, call), wall, monotonic).ConfigureAwait(false);
                    break;
                case RelayFrame.ReadItem read:
                    // A read arrives as a call does: counted among the calls at once, late by sentAt + 10 s, cancellable.
                    await AdmitAsync(new Work(read.Id, "read", read.SentAt, read.Deadline, read.MaxChars, read), wall, monotonic).ConfigureAwait(false);
                    break;
                case RelayFrame.Refused refused:
                    await SendAsync(RelayFrames.Error(refused.Code, refused.Id)).ConfigureAwait(false);
                    break;
            }
        }

        private async Task AdmitAsync(Work call, DateTimeOffset wall, long monotonic)
        {
            var cutoff = new RelayCutoff(Time);
            bool duplicate;
            lock (_lock)
            {
                duplicate = !_calls.TryAdd(call.Id, cutoff);
            }

            if (duplicate)
            {
                cutoff.Dispose();
                await SendAsync(RelayFrames.Error("duplicate", call.Id)).ConfigureAwait(false);
                return;
            }

            if (Interlocked.Increment(ref _client._running) > _client._options.MaxCalls)
            {
                Interlocked.Decrement(ref _client._running);
                Forget(call.Id);
                cutoff.Dispose();
                await SendAsync(RelayFrames.Error("busy", call.Id)).ConfigureAwait(false);
                return;
            }

            if (_ending.IsCancellationRequested)
                cutoff.Trip("closed");
            // The reading the call starts from is taken here, as the call is admitted: a usable one, or the one in flight.
            var reading = ReadingAsync();
            _client.Track(Task.Run(() => RunCallAsync(call, cutoff, reading, wall, monotonic)));
        }

        /// <summary>A call or a read, as admitted: its id, its tool (<c>read</c> for a read, in the logs), its times and limit, and the frame.</summary>
        private sealed record Work(string Id, string Tool, long SentAt, long? Deadline, int? MaxChars, RelayFrame Frame);

        private void Forget(string id)
        {
            lock (_lock)
                _calls.Remove(id);
        }

        private async Task RunCallAsync(Work call, RelayCutoff cutoff, Task<RelayReading?> admitted, DateTimeOffset arrivedWall, long arrivedMonotonic)
        {
            var logger = _client._logger;
            try
            {
                // Checked as it comes, not only as it was asked for: a reading joined in flight may have been voided
                // meanwhile (a step of the wall clock, or its age), and one the call's sentAt contradicts (the relay's clock
                // stepped ahead since it was read) would give the call time it doesn't have; either is asked for once more,
                // and a call whose sentAt still contradicts the fresh one gets no more than its own span from its arrival
                // (below). None at all (two questions unanswered) is final. The clocks are read once for what follows (the
                // reading checked, how long the call waited, how late it is, its deadline): a step of the wall clock after
                // that reaches none of it.
                var reading = await admitted.ConfigureAwait(false);
                var now = ClockSnapshot.Take(Time);
                if (reading is not null && (!RelayClock.IsUsable(reading, now, Time) || RelayClock.Contradicts(reading, now, call.SentAt)))
                {
                    reading = await ReadingAsync(reading).ConfigureAwait(false);
                    now = ClockSnapshot.Take(Time);
                }

                if (!RelayClock.IsUsable(reading, now, Time))
                    reading = null;
                _client._options.Step?.Invoke("checked");
                var waited = now.Wall - arrivedWall;
                var waitedMonotonic = Time.GetElapsedTime(arrivedMonotonic, now.Monotonic);
                if (cutoff.IsTripped)
                {
                    logger.Information("AI app tool {Tool} from elsewhere: cancelled before it ran", call.Tool);
                    await SendAsync(RelayFrames.Cancelled(call.Id)).ConfigureAwait(false);
                    return;
                }

                // Without a reading, or waiting for one past 10 s on either clock, a call is refused unrun.
                if (reading is null || waited > RelayClock.LateAfter || waitedMonotonic > RelayClock.LateAfter
                    || RelayClock.RelayNowMs(reading, now) - call.SentAt > RelayClock.LateAfter.TotalMilliseconds)
                {
                    logger.Information("AI app tool {Tool} from elsewhere: late, not run", call.Tool);
                    await SendAsync(RelayFrames.Late(call.Id)).ConfigureAwait(false);
                    return;
                }

                // The absolute cutoff: the time left until the earlier of the call's deadline and sentAt + 60 s, on the relay's
                // clock as read (a reading the call's sentAt contradicted was read again above, so a delivery delay counts
                // against the call), from 0 to 60 s: a relay can shorten a call's time, never extend it. In doubles, from a
                // sentAt and a deadline within what a time holds (RelayFrames.MaxSentAt): nothing overflows.
                // The call's own span, deadline − sentAt (60 s at most), also runs from its arrival on the engine's clocks: a
                // call whose sentAt is ahead of the relay's clock even as read again gets no more than that. The earlier wins.
                var within = RelayClock.CommitWithin.TotalMilliseconds;
                var until = Math.Min(call.SentAt + within, call.Deadline ?? double.MaxValue);
                var span = Math.Min(within, (call.Deadline ?? double.MaxValue) - call.SentAt) - waitedMonotonic.TotalMilliseconds;
                var left = Math.Min(until - RelayClock.RelayNowMs(reading, now), span);
                cutoff.SetDeadline(TimeSpan.FromMilliseconds(Math.Clamp(left, 0, within)), now);
                var context = new RelayCallContext(call.Id, call.MaxChars, cutoff)
                {
                    Design = (call.Frame as RelayFrame.Call)?.Design,
                    Designs = (call.Frame as RelayFrame.Call)?.Designs,
                    Files = (call.Frame as RelayFrame.Call)?.Files,
                    BasedOn = (call.Frame as RelayFrame.Call)?.BasedOn,
                    BasedOnList = (call.Frame as RelayFrame.Call)?.BasedOnList,
                };
                var answer = call.Frame switch
                {
                    RelayFrame.ReadItem read => await _client._tools.ReadAsync(read.Profile, read.Item, read.Detail, context, _client._stop.Token).ConfigureAwait(false),
                    RelayFrame.Call tool => await _client._tools.CallAsync(tool.Tool, tool.Arguments, context, _client._stop.Token).ConfigureAwait(false),
                    _ => throw new InvalidOperationException("Only a call or a read is admitted."),
                };
                if (answer.Cancelled)
                {
                    await SendAsync(RelayFrames.Cancelled(call.Id)).ConfigureAwait(false);
                    return;
                }

                if (answer.Stale)
                {
                    await SendAsync(RelayFrames.Stale(call.Id)).ConfigureAwait(false);
                    return;
                }

                var result = answer.Item is { } item ? RelayFrames.Item(call.Id, item) : RelayFrames.Result(call.Id, answer.Result);
                if (RelayFrames.Write(result).Length > RelayFrames.MaxFrameBytes)
                {
                    result = RelayFrames.Result(call.Id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"The answer is too large to send to an AI app elsewhere (over {RelayFrames.MaxFrameBytes / (1024 * 1024)} MiB). Ask for less: one item, or a smaller preview.",
                        }),
                        ["isError"] = true,
                    });
                }

                await SendAsync(result).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // The connection closed: the answer has nowhere to go, and the change, if any, stopped at its cutoff.
            }
            finally
            {
                Forget(call.Id);
                cutoff.Dispose();
                Interlocked.Decrement(ref _client._running);
            }
        }

        /// <summary>
        /// A usable reading of the relay's clock (<see cref="RelayClock.IsUsable"/>) other than <paramref name="stale"/>, or a
        /// fresh one (joining one in flight), asked once more when the first fails; null when neither counts.
        /// </summary>
        private Task<RelayReading?> ReadingAsync(RelayReading? stale = null)
        {
            Task<RelayReading?> reading;
            bool joined;
            lock (_lock)
            {
                if (RelayClock.IsUsable(_reading, Time) && !ReferenceEquals(_reading, stale))
                    return Task.FromResult(_reading);
                joined = _readingInFlight is not null;
                reading = _readingInFlight ??= ReadAsync();
            }

            if (joined)
                _client._options.Step?.Invoke("joined");
            return reading;
        }

        private async Task<RelayReading?> ReadAsync()
        {
            await Task.Yield();
            try
            {
                for (var attempt = 0; attempt < 2 && !_ending.IsCancellationRequested; attempt++)
                {
                    var nonce = Base64Url(RandomNumberGenerator.GetBytes(16));
                    var answered = new TaskCompletionSource<(long Now, DateTimeOffset Wall, long Monotonic)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_lock)
                        _readings[nonce] = answered;
                    try
                    {
                        var wall = Time.GetUtcNow();
                        var monotonic = Time.GetTimestamp();
                        await SendAsync(RelayFrames.TimeRequest(nonce)).ConfigureAwait(false);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_ending.Token);
                        var timer = Task.Delay(RelayClock.ReadingRoundTrip, Time, timeout.Token);
                        if (await Task.WhenAny(answered.Task, timer).ConfigureAwait(false) == answered.Task)
                        {
                            await timeout.CancelAsync().ConfigureAwait(false);
                            var (now, wallAnswered, monotonicAnswered) = answered.Task.Result;
                            if (RelayClock.Reading(now, wall, monotonic, wallAnswered, monotonicAnswered, Time) is { } reading)
                            {
                                lock (_lock)
                                    _reading = reading;
                                return reading;
                            }
                        }
                    }
                    finally
                    {
                        lock (_lock)
                            _readings.Remove(nonce);
                    }
                }

                return null;
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                return null;
            }
            finally
            {
                lock (_lock)
                    _readingInFlight = null;
            }
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public Task SendAsync(JsonObject frame, CancellationToken ct = default)
        {
            var bytes = RelayFrames.Write(frame);
            return SendingAsync(() => _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct), ct);
        }

        /// <summary>
        /// Runs <paramref name="send"/> in its turn (one at a time). The session's disposal stops it, waiting its turn or
        /// stalled, and disposes the turn's semaphore only once no send is under way.
        /// </summary>
        /// <exception cref="OperationCanceledException">The session was disposed, or <paramref name="ct"/> cancelled.</exception>
        /// <exception cref="ObjectDisposedException">The session was disposed before this send began.</exception>
        private async Task SendingAsync(Func<Task> send, CancellationToken ct)
        {
            Interlocked.Increment(ref _senders);
            try
            {
                using var sending = CancellationTokenSource.CreateLinkedTokenSource(_sending.Token, ct);
                await _send.WaitAsync(sending.Token).ConfigureAwait(false);
                try
                {
                    // A send stalled (the service reads nothing) ends when the session does: cancelling it aborts the socket.
                    using (sending.Token.Register(static socket => ((ClientWebSocket)socket!).Abort(), _socket))
                        await send().ConfigureAwait(false);
                }
                finally
                {
                    _send.Release();
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref _senders) == 0)
                    _drained.TrySetResult();
            }
        }

        /// <summary>Every call's change stops unmade.</summary>
        private void TripCalls(string reason)
        {
            RelayCutoff[] calls;
            lock (_lock)
                calls = [.. _calls.Values];
            foreach (var call in calls)
                call.Trip(reason);
        }

        /// <summary>The connection closed: every call's change stops unmade, and readings waiting get no answer.</summary>
        private void TripAll(string reason)
        {
            TripCalls(reason);
            TaskCompletionSource<(long, DateTimeOffset, long)>[] readings;
            lock (_lock)
                readings = [.. _readings.Values];
            foreach (var reading in readings)
                reading.TrySetCanceled();
            if (!_ending.IsCancellationRequested)
            {
                try
                {
                    _ending.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>
        /// Ends the session: every call's change stops, sends waiting their turn or stalled stop, and once none is under
        /// way the semaphore is disposed (a send after that finds it disposed and fails at once).
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _received.TrySetResult();
            TripAll("closed");
            await _sending.CancelAsync().ConfigureAwait(false);
            if (Interlocked.Decrement(ref _senders) == 0)
                _drained.TrySetResult();
            await _drained.Task.ConfigureAwait(false);
            _send.Dispose();
        }
    }
}
