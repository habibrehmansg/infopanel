using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InfoPanel.Core.Tests.Images;

/// <summary>A request as <see cref="LocalHttpServer"/> read it: the request line's path and the headers, names in lower case.</summary>
internal sealed record ServedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, IPEndPoint Remote)
{
    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>
/// A minimal HTTP/1.1 server on 127.0.0.1 and a free port, for the media provider's tests: each connection answers one
/// request (<c>Connection: close</c>) with what the handler writes, raw, so a test can answer anything: a stalled body, a
/// wrong length, a redirect chain. Nothing here reaches past loopback.
/// </summary>
internal sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<ServedRequest, Stream, CancellationToken, Task> _handler;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly ConcurrentQueue<ServedRequest> _requests = new();
    private readonly List<Task> _connections = [];
    private int _open;
    private int _peakOpen;

    public LocalHttpServer(Func<ServedRequest, Stream, CancellationToken, Task> handler)
    {
        _handler = handler;
        _listener.Start();
        _accepting = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    /// <summary>The requests read so far, in order.</summary>
    public IReadOnlyList<ServedRequest> Requests => [.. _requests];

    /// <summary>The most requests that were being answered at once.</summary>
    public int PeakOpen => Volatile.Read(ref _peakOpen);

    /// <summary>Raised when a request has been read, before the handler answers it.</summary>
    public event Action<ServedRequest>? Received;

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }
            lock (_connections) _connections.Add(Task.Run(() => ServeAsync(client)));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            try
            {
                var request = await ReadRequestAsync(stream, (IPEndPoint)client.Client.RemoteEndPoint!, _stop.Token);
                if (request is null) return;
                _requests.Enqueue(request);
                var open = Interlocked.Increment(ref _open);
                int peak;
                while ((peak = Volatile.Read(ref _peakOpen)) < open && Interlocked.CompareExchange(ref _peakOpen, open, peak) != peak)
                {
                }
                try
                {
                    Received?.Invoke(request);
                    await _handler(request, stream, _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
                finally
                {
                    Interlocked.Decrement(ref _open);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<ServedRequest?> ReadRequestAsync(Stream stream, IPEndPoint remote, CancellationToken token)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 65536)
        {
            if (await stream.ReadAsync(one, token) == 0) return null;
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n') break;
        }
        var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
        var start = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var earlier) ? earlier + ", " + value : value;
        }
        return new ServedRequest(start[0], start.Length > 1 ? start[1] : "/", headers, remote);
    }

    /// <summary>Writes a whole answer: the status line, the headers, a <c>Content-Length</c> and the body.</summary>
    public static Task Answer(Stream stream, int status, byte[]? body = null, string? contentType = "image/png", params string[] headers)
    {
        body ??= [];
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\n");
        if (contentType is not null) head.Append($"Content-Type: {contentType}\r\n");
        foreach (var h in headers) head.Append(h).Append("\r\n");
        head.Append($"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        return WriteAsync(stream, Encoding.ASCII.GetBytes(head.ToString()), body);
    }

    /// <summary>Writes the status line and headers of an answer with no length (its end is the connection's), and no body yet.</summary>
    public static Task Head(Stream stream, int status, string contentType = "image/png") =>
        WriteAsync(stream, Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nConnection: close\r\n\r\n"), []);

    private static async Task WriteAsync(Stream stream, byte[] head, byte[] body)
    {
        await stream.WriteAsync(head);
        if (body.Length > 0) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        401 => "Unauthorized",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception)
        {
        }
        Task[] connections;
        lock (_connections) connections = [.. _connections];
        await Task.WhenAll(connections.Select(c => c.ContinueWith(_ => { }, TaskScheduler.Default)));
    }
}
