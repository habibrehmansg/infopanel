using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace InfoPanel.Hosting;

/// <summary>
/// What the running engine is: its loopback port, session token and allowed origins. The port is chosen before
/// the server starts (a free one when <c>--port 0</c>) so the allowed origins are known when the pipeline is
/// built; <see cref="EngineLifetime"/> confirms it from the server once it is listening.
/// </summary>
public sealed class EngineRuntime
{
    /// <summary>The WebSocket subprotocol the client asks for; the server answers with this one only.</summary>
    public const string WebSocketProtocol = "infopanel.v1";

    /// <summary>Prefix of the subprotocol that carries the token (<c>auth.{token}</c>); never echoed back.</summary>
    public const string WebSocketAuthPrefix = "auth.";

    /// <summary>Stands in for the token in the log file.</summary>
    public const string RedactedToken = "<token>";

    private readonly byte[] _token;
    private volatile int _port;

    public EngineRuntime(int port, string token, IReadOnlyList<string> devOrigins)
    {
        _port = port;
        Token = token;
        _token = Encoding.UTF8.GetBytes(token);
        DevOrigins = devOrigins;
    }

    public int Port
    {
        get => _port;
        set => _port = value;
    }

    public string Token { get; }

    public IReadOnlyList<string> DevOrigins { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Opening this redirects to the web client with the token in the URL fragment (<c>/#token=…</c>).</summary>
    public string AuthUrl => AuthUrlFor(BaseUrl);

    /// <param name="redacted">Show <see cref="RedactedToken"/> instead of the token (for the log file).</param>
    public string AuthUrlFor(string origin, bool redacted = false) => $"{origin}/auth/{(redacted ? RedactedToken : Token)}";

    /// <summary><c>http://127.0.0.1:{port}</c>, <c>http://localhost:{port}</c> and every dev origin.</summary>
    public IReadOnlyList<string> AllowedOrigins =>
        [$"http://127.0.0.1:{Port}", $"http://localhost:{Port}", .. DevOrigins];

    public bool IsAllowedOrigin(string origin)
    {
        foreach (var allowed in AllowedOrigins)
        {
            if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Compares in constant time for tokens of the right length.</summary>
    public bool IsValidToken(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _token);
    }

    /// <summary>A loopback port that is free right now.</summary>
    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
