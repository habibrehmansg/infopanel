using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Preview;
using InfoPanel.Tests.Contract;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace InfoPanel.Tests.Editing;

/// <summary>
/// The engine on real Kestrel at <c>127.0.0.1</c> on an ephemeral port, over <paramref name="engine"/>'s temp data
/// folder, with <paramref name="configure"/> layered on (a test middleware, say). Clients talk to it over real sockets.
/// Disposing it stops the host; the data folder stays with <paramref name="engine"/>, so another one can start on it.
/// </summary>
internal sealed class LiveEngine : IDisposable
{
    private readonly WebApplicationFactory<Program> _host;

    private LiveEngine(WebApplicationFactory<Program> host, int port)
    {
        _host = host;
        Port = port;
    }

    /// <summary>
    /// Starts the engine on a free port. The port is free when chosen but released before Kestrel binds it, so another
    /// process can take it in between: start-up is then retried on a fresh port.
    /// </summary>
    public static LiveEngine Start(EngineFactory engine, Action<IWebHostBuilder>? configure = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = EngineRuntime.FindFreePort();
            var host = engine.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Engine:Port", port.ToString());
                configure?.Invoke(builder);
            });
            host.UseKestrel();
            try
            {
                host.StartServer();
                return new LiveEngine(host, port);
            }
            catch (Exception ex) when (attempt < StartAttempts && (IsAddressInUse(ex) || PortTaken(port)))
            {
                // The engine logs a failed start and stops, so the bind error may surface only as its stopped host;
                // the port being held by someone else then is what tells a lost race from any other failure.
                host.Dispose();
            }
        }
    }

    /// <summary>How many fresh ports a start tries before it gives up.</summary>
    public const int StartAttempts = 5;

    /// <summary>Whether <paramref name="error"/> (or an exception inside it) says the port was already taken.</summary>
    private static bool IsAddressInUse(Exception error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (e is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return true;
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsAddressInUse))
                return true;
        }

        return false;
    }

    /// <summary>Whether another socket holds <paramref name="port"/> on the loopback address.</summary>
    private static bool PortTaken(int port)
    {
        var probe = new TcpListener(IPAddress.Loopback, port);
        try
        {
            probe.Start();
            return false;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        finally
        {
            probe.Stop();
        }
    }

    public int Port { get; }

    public Uri BaseAddress => new($"http://127.0.0.1:{Port}");

    public IServiceProvider Services => _host.Services;

    /// <summary>A client over a real connection that sends <paramref name="token"/> as its Bearer token.</summary>
    public HttpClient Client(string token = EngineFactory.Token)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A <c>/ws</c> connection authenticated with <paramref name="token"/>, subscribed to <paramref name="profileId"/>.</summary>
    public Task<LiveSocket> Subscribe(string profileId, CancellationToken ct, string token = EngineFactory.Token, double scale = 0.5, int maxFps = 30) =>
        LiveSocket.Connect(new Uri($"ws://127.0.0.1:{Port}/ws"), token, profileId, scale, maxFps, ct);

    public void Dispose() => _host.Dispose();

    // ---- requests as a client sends them ------------------------------------------------------------------------------

    public static async Task<JsonElement> Body(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    /// <summary>Posts <paramref name="bytes"/> as JSON, byte for byte (a retry sends exactly what the first attempt sent).</summary>
    public static Task<HttpResponseMessage> PostBytes(HttpClient client, string path, byte[] bytes, CancellationToken ct)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return client.PostAsync(path, content, ct);
    }

    public static async Task<string> Instance(HttpClient client, CancellationToken ct) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/session", ct)).GetProperty("instance").GetString()!;

    /// <summary>A commit's bytes: <paramref name="ops"/> based on <paramref name="baseRev"/>, with an optional coalescing key.</summary>
    public static byte[] Commit(string instance, long baseRev, string? coalesceKey, params JsonObject[] ops)
    {
        var body = TestDocs.CommitBody(instance, baseRev, ops);
        if (coalesceKey != null)
            body["coalesceKey"] = coalesceKey;
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    /// <summary>An undo, redo or revert body (<c>historyRequest</c>).</summary>
    public static byte[] History(string instance, long baseRev) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["instance"] = instance, ["baseRev"] = baseRev }.ToJsonString());

    /// <summary>Creates an integration token with <paramref name="scopes"/>; returns its secret.</summary>
    public static async Task<string> CreateToken(HttpClient session, string name, CancellationToken ct, params string[] scopes)
    {
        using var response = await session.PostAsJsonAsync("/api/tokens", new { name, scopes }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("token").GetString()!;
    }

    /// <summary>The repository root (the folder with <c>InfoPanel.slnx</c>).</summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "InfoPanel.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No InfoPanel.slnx above " + AppContext.BaseDirectory);
    }

    /// <summary>p50, p95 and p99 of <paramref name="samples"/> (nearest rank).</summary>
    public static (double P50, double P95, double P99) Percentiles(IReadOnlyCollection<double> samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        return (At(0.50), At(0.95), At(0.99));
    }
}

/// <summary>A document's state as a <c>doc</c> message or a <c>documentResponse</c> carries it.</summary>
internal readonly record struct DocState(long Rev, bool Dirty, bool CanUndo, bool CanRedo)
{
    public static DocState From(JsonElement json) => new(json.GetProperty("rev").GetInt64(), json.GetProperty("dirty").GetBoolean(),
        json.GetProperty("canUndo").GetBoolean(), json.GetProperty("canRedo").GetBoolean());
}

/// <summary>
/// A client's <c>/ws</c> connection over a real socket, subscribed to one document, read continuously in the background:
/// every <c>doc</c> state and every frame's <c>rev</c>, with the time each revision's first frame arrived. What it sends
/// is a <c>clientMessage</c>, and a message it receives outside the spec (a text message that isn't a
/// <c>serverMessage</c>, a frame whose header isn't a <c>frameHeader</c>) fails the connection like a lost socket.
/// </summary>
internal sealed class LiveSocket : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly List<DocState> _docs = [];
    private readonly List<long> _frameRevs = [];
    private readonly Dictionary<long, long> _firstFrameAt = [];
    private readonly List<string> _errors = [];
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _reader = Task.CompletedTask;
    private Exception? _failure;
    private SocketContractViolation? _violation;

    /// <summary>A message the engine sent outside the spec: it fails the connection, and its disposal too.</summary>
    private sealed class SocketContractViolation(string message) : InvalidOperationException(message);

    private LiveSocket(ClientWebSocket socket) => _socket = socket;

    public static async Task<LiveSocket> Connect(Uri uri, string token, string profileId, double scale, int maxFps, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(EngineRuntime.WebSocketProtocol);
        socket.Options.AddSubProtocol(EngineRuntime.WebSocketAuthPrefix + token);
        await socket.ConnectAsync(uri, ct);
        var live = new LiveSocket(socket);
        live._reader = Task.Run(live.Read);
        var subscribe = JsonSerializer.Serialize(new { type = "subscribe", profileId, scale, maxFps });
        if (SocketContract.CheckClientText(subscribe) is { } violation)
            throw new InvalidOperationException(violation);
        await socket.SendAsync(Encoding.UTF8.GetBytes(subscribe), WebSocketMessageType.Text, true, ct);
        return live;
    }

    /// <summary>Every <c>doc</c> state received, in order.</summary>
    public IReadOnlyList<DocState> Docs
    {
        get { lock (_gate) return [.. _docs]; }
    }

    /// <summary>Every frame's <c>rev</c>, in order of arrival.</summary>
    public IReadOnlyList<long> FrameRevs
    {
        get { lock (_gate) return [.. _frameRevs]; }
    }

    public IReadOnlyList<string> Errors
    {
        get { lock (_gate) return [.. _errors]; }
    }

    /// <summary>The latest revision this connection has been told about (<c>doc</c>), or 0.</summary>
    public long LastDocRev
    {
        get { lock (_gate) return _docs.Count == 0 ? 0 : _docs[^1].Rev; }
    }

    /// <summary>
    /// When (<see cref="Stopwatch.GetTimestamp"/>) the first frame labelled exactly <paramref name="rev"/> arrived, which
    /// must be after <paramref name="sentAt"/> (when the request that made the revision was sent): a frame of a revision
    /// the document didn't have yet, or one from before the change was asked for, fails.
    /// </summary>
    public async Task<long> FirstFrameAt(long rev, long sentAt, TimeSpan timeout, CancellationToken ct)
    {
        long at = 0;
        await Until(() => _firstFrameAt.TryGetValue(rev, out at), timeout, $"a frame of rev {rev}", ct);
        Assert.True(at > sentAt, $"The first frame of rev {rev} arrived {Stopwatch.GetElapsedTime(at, sentAt).TotalMilliseconds:0.000} ms before its request was sent.");
        return at;
    }

    /// <summary>Waits until <paramref name="condition"/> (evaluated under the connection's lock) holds.</summary>
    public async Task Until(Func<bool> condition, TimeSpan timeout, string what, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_failure != null)
                    throw new InvalidOperationException($"The socket failed while waiting for {what}.", _failure);
                if (condition())
                    return;
                changed = _changed.Task;
            }

            var left = TimeSpan.FromSeconds((double)(deadline - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
            if (left <= TimeSpan.Zero)
            {
                lock (_gate)
                    Assert.Fail($"Timed out waiting for {what}; docs {string.Join(",", _docs.Select(d => d.Rev))}, frames {string.Join(",", _frameRevs.Distinct())}.");
            }

            try
            {
                await changed.WaitAsync(left, ct);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private async Task Read()
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(chunk, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException($"The engine closed the socket: {result.CloseStatus} {result.CloseStatusDescription}");
                buffer.Write(chunk, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var bytes = buffer.ToArray();
                buffer.SetLength(0);
                var now = Stopwatch.GetTimestamp();
                var outside = result.MessageType == WebSocketMessageType.Binary
                    ? SocketContract.CheckFrame(bytes)
                    : SocketContract.CheckServerText(Encoding.UTF8.GetString(bytes));
                if (outside != null)
                    throw new SocketContractViolation($"The engine sent a message outside the spec. {outside}");
                lock (_gate)
                {
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        var rev = FrameCodec.Decode(bytes).Header.Rev;
                        _frameRevs.Add(rev);
                        _firstFrameAt.TryAdd(rev, now);
                    }
                    else
                    {
                        var json = JsonDocument.Parse(bytes).RootElement;
                        switch (json.GetProperty("type").GetString())
                        {
                            case "doc":
                                _docs.Add(DocState.From(json));
                                break;
                            case "error":
                                _errors.Add(json.GetRawText());
                                break;
                        }
                    }
                    Signal();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _failure = ex;
                _violation = ex as SocketContractViolation;
                Signal();
            }
        }
    }

    private void Signal()
    {
        var changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult();
    }

    /// <summary>
    /// Closes the connection, reading what the engine sent until its close (for at most 2 s), and throws if any of it was
    /// outside the spec, however late it came. A close or a lost connection is quiet.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        // The messages already on their way are read and checked before the reader is stopped.
        await Task.WhenAny(_reader, Task.Delay(TimeSpan.FromSeconds(2)));
        _stop.Cancel();
        try
        {
            await _reader;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        _socket.Dispose();
        _stop.Dispose();

        SocketContractViolation? violation;
        lock (_gate)
            violation = _violation;
        if (violation != null)
            throw new InvalidOperationException("The engine sent a message outside the spec on this connection.", violation);
    }
}
