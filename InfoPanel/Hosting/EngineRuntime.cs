using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace InfoPanel.Hosting;

/// <summary>
/// The link the engine opens when asked (<see cref="EngineRuntime.EditorLinkFor"/>): a web client's code link
/// (<paramref name="WebClient"/>, opened in Chrome when installed), which carries a one-time pairing code and never the
/// session token, or the engine's sign-in link, which carries the token to the web client served here.
/// </summary>
public sealed record EditorLink(string Url, bool WebClient)
{
    /// <summary>What the link is, without it: never print or log <see cref="Url"/> but on the console.</summary>
    public override string ToString() => WebClient ? "a web client's code link" : "the sign-in link";
}

/// <summary>
/// The client's pages a link the engine opens may land on (its <c>then</c>, docs/2.0/desktop-shell.md, "Opening the
/// editor"): fixed here, never taken from a caller. A client that doesn't know the member, or the page, ignores it.
/// </summary>
public static class EditorRoutes
{
    /// <summary>The client's engine settings, where another computer is set up to edit this one (the menu's Edit from Another Computer…).</summary>
    public const string EngineSettings = "#/settings/engine";

    /// <summary>Every page a link may land on.</summary>
    public static IReadOnlyList<string> Known { get; } = [EngineSettings];

    /// <summary>The known page <paramref name="value"/> is (compared exactly), or null.</summary>
    public static string? Find(string? value)
    {
        foreach (var route in Known)
        {
            if (string.Equals(value, route, StringComparison.Ordinal))
                return route;
        }

        return null;
    }

    /// <summary>The <c>then</c> member or query for a known page: <c>then={the page, URL-encoded}</c>.</summary>
    internal static string Member(string route) => $"then={Uri.EscapeDataString(route)}";
}

/// <summary>
/// What the running engine is: its loopback port, session token, allowed origins and when it started. The port is
/// chosen before the server starts (a free one when <c>--port 0</c>) so the allowed origins are known when the
/// pipeline is built; <see cref="EngineLifetime"/> confirms it from the server once it is listening.
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

    public EngineRuntime(int port, string token, IReadOnlyList<string> devOrigins, IReadOnlyList<string>? crossOrigins = null)
    {
        _port = port;
        Token = token;
        _token = Encoding.UTF8.GetBytes(token);
        DevOrigins = devOrigins;
        CrossOrigins = crossOrigins ?? [];
    }

    public int Port
    {
        get => _port;
        set => _port = value;
    }

    public string Token { get; }

    /// <summary>
    /// When this run of the engine started (the session's <c>started</c>): <c>Program</c> takes it before it opens the
    /// data folder. The same for the whole run.
    /// </summary>
    public DateTimeOffset Started { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<string> DevOrigins { get; }

    /// <summary>Whether this engine serves a web client (<c>--web-root</c>, or <c>wwwroot</c> beside it).</summary>
    public bool WebRootServed { get; init; }

    /// <summary>
    /// What the engine opens when the user asks for it (the menu's Open Editor, a second launch): with no web client served
    /// here and exactly one cross origin allowed, that origin's code link (a release build's configured web client) with a
    /// new one-time pairing code from <paramref name="newCode"/>, asked for only then (<see cref="CodeUrlFor"/>: the session
    /// token is never in it); otherwise the sign-in link (<see cref="AuthUrl"/>), to the web client served here.
    /// <paramref name="then"/>, one of <see cref="EditorRoutes.Known"/>, is the page the client opens at: the code link's
    /// last member (<c>&amp;then=…</c>), or the sign-in link's query (<c>?then=…</c>), which its redirect hands on after
    /// the token. Any other page is refused before a code is minted.
    /// </summary>
    public EditorLink EditorLinkFor(Func<string> newCode, string? then = null)
    {
        string? member = null;
        if (then is not null)
            member = EditorRoutes.Member(EditorRoutes.Find(then) ?? throw new ArgumentException("Not a page a link may land on.", nameof(then)));

        return !WebRootServed && CrossOrigins.Count == 1
            ? new EditorLink(CodeUrlFor(CrossOrigins[0], newCode()) + (member is null ? "" : $"&{member}"), WebClient: true)
            : new EditorLink(AuthUrl + (member is null ? "" : $"?{member}"), WebClient: false);
    }

    /// <summary>
    /// The code link for a cross-origin client: its own origin with the engine's URL and a one-time pairing code in the
    /// fragment (<c>{origin}/#engine={BaseUrl}&amp;code={code}</c>), which the client exchanges once for the run's token
    /// and a lasting pairing (spec/docs/api.md, "Pairing").
    /// </summary>
    public string CodeUrlFor(string origin, string code) => $"{origin}/#engine={BaseUrl}&code={code}";

    /// <summary>
    /// Origins of web clients served from elsewhere (<c>--allow-origin</c>): allowed like the others, and the only
    /// ones that get CORS headers (<see cref="Security.CrossOrigin"/>).
    /// </summary>
    public IReadOnlyList<string> CrossOrigins { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Opening this redirects to the web client with the token in the URL fragment (<c>/#token=…</c>).</summary>
    public string AuthUrl => AuthUrlFor(BaseUrl);

    /// <param name="redacted">Show <see cref="RedactedToken"/> instead of the token (for the log file).</param>
    public string AuthUrlFor(string origin, bool redacted = false) => $"{origin}/auth/{(redacted ? RedactedToken : Token)}";

    /// <summary>
    /// The pairing link for a cross-origin client: its own origin with the engine's URL and the token in the fragment
    /// (<c>{origin}/#engine={BaseUrl}&amp;token={token}</c>), which the browser never sends to that origin's server.
    /// </summary>
    /// <param name="redacted">Show <see cref="RedactedToken"/> instead of the token (for the log file).</param>
    public string PairingUrlFor(string origin, bool redacted = false) =>
        $"{origin}/#engine={BaseUrl}&token={(redacted ? RedactedToken : Token)}";

    /// <summary><c>http://127.0.0.1:{port}</c>, <c>http://localhost:{port}</c>, every dev origin and every cross origin.</summary>
    public IReadOnlyList<string> AllowedOrigins =>
        [$"http://127.0.0.1:{Port}", $"http://localhost:{Port}", .. DevOrigins, .. CrossOrigins];

    /// <summary>An origin given with <c>--allow-origin</c>, which gets CORS headers.</summary>
    public bool IsCrossOrigin(string origin)
    {
        foreach (var allowed in CrossOrigins)
        {
            if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public bool IsAllowedOrigin(string origin)
    {
        foreach (var allowed in AllowedOrigins)
        {
            if (string.Equals(origin, allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is this run's session token (full access); compares in constant time for
    /// tokens of the right length. Integration tokens are checked by <see cref="Security.TokenStore"/>.
    /// </summary>
    public bool IsSessionToken(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _token);
    }

    /// <summary><paramref name="preferred"/> when it is a loopback port that is free right now, else one that is.</summary>
    public static int FindFreePort(int? preferred)
    {
        if (preferred is >= 1 and <= 65535)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, preferred.Value);
                listener.Start();
                listener.Stop();
                return preferred.Value;
            }
            catch (SocketException)
            {
            }
        }

        return FindFreePort();
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
