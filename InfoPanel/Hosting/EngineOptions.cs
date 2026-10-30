using InfoPanel.Persistence;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace InfoPanel.Hosting;

/// <summary>
/// The engine's settings, from the <c>Engine</c> configuration section (command-line flags map onto it, see
/// <see cref="EngineCommandLine"/>). <see cref="Load"/> validates everything up front so a bad value stops the
/// engine with a clear message before anything starts.
/// </summary>
public sealed partial class EngineOptions
{
    public const string Section = "Engine";

    /// <summary>Loopback port; 0 picks a free one.</summary>
    public int Port { get; init; }

    /// <summary>Data folder; null means <c>DataPaths.DefaultRoot()</c>.</summary>
    public string? DataDir { get; init; }

    /// <summary>
    /// The 1.x data folder the 1.x import reads (<c>--v1-dir</c>), a full path that need not exist; null means 1.x's own,
    /// <c>{LocalApplicationData}/InfoPanel</c> (<c>V1Folder.DefaultRoot</c>).
    /// </summary>
    public string? V1Dir { get; init; }

    /// <summary>The web client's built files; null means <c>{BaseDirectory}/wwwroot</c> when it has an <c>index.html</c>.</summary>
    public string? WebRoot { get; init; }

    public bool NoBrowser { get; init; }

    /// <summary>Don't show active profiles as desktop windows (<c>--no-windows</c>).</summary>
    public bool NoWindows { get; init; }

    /// <summary>The session token (URL-safe characters only); generated when not given.</summary>
    public string Token { get; init; } = "";

    /// <summary>Extra allowed origins (a web client's dev server), normalised to <c>scheme://host[:port]</c>.</summary>
    public IReadOnlyList<string> DevOrigins { get; init; } = [];

    /// <summary>
    /// Origins of web clients served from elsewhere that may call the engine cross-origin (<c>--allow-origin</c>),
    /// exact <c>scheme://host[:port]</c> origins: they get CORS on <c>/api</c> and <c>/healthz</c> and may open
    /// <c>/ws</c>. Empty by default.
    /// </summary>
    public IReadOnlyList<string> AllowOrigins { get; init; } = [];


    /// <summary>Wipe the data folder before starting (only with an explicit <see cref="DataDir"/>).</summary>
    public bool Fresh { get; init; }

    /// <summary>Seed for the mock sensors; null takes one from the clock. Setting it turns on <see cref="Mock"/>.</summary>
    public uint? MockSeed { get; init; }

    /// <summary>
    /// Mock mode (<c>--mock</c>, or any <c>--mock-seed</c>): the mock sensors instead of the platform's real ones, and
    /// simulated values for well-formed keys no source handles. Off, unknown keys are unavailable.
    /// </summary>
    public bool Mock { get; init; }

    /// <summary>
    /// The folder of the data folders' local channels (<see cref="LocalChannel"/>), from the <c>Engine:RunDir</c> setting
    /// (a host setting, not a flag: tests give their engines their own); null means
    /// <see cref="LocalChannel.DefaultRunDirectory"/>.
    /// </summary>
    public string? RunDir { get; init; }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,256}$")]
    private static partial Regex TokenPattern();

    /// <summary>32 random bytes, base64url without padding.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads and validates the <c>Engine</c> section. Returns null and the problems when anything is invalid.</summary>
    public static EngineOptions? Load(IConfiguration configuration, out IReadOnlyList<string> errors)
    {
        var section = configuration.GetSection(Section);
        var problems = new List<string>();

        var port = 0;
        if (Value(section, "Port") is { } portText
            && (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port > 65535))
        {
            problems.Add($"--port must be a number from 0 to 65535 (got '{portText}').");
        }

        uint? seed = null;
        if (Value(section, "MockSeed") is { } seedText)
        {
            if (uint.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out var s))
                seed = s;
            else
                problems.Add($"--mock-seed must be a number from 0 to {uint.MaxValue} (got '{seedText}').");
        }

        var mock = Flag(section, "Mock", "--mock", problems) || seed != null;
        var noBrowser = Flag(section, "NoBrowser", "--no-browser", problems);
        var noWindows = Flag(section, "NoWindows", "--no-windows", problems);
        var fresh = Flag(section, "Fresh", "--fresh", problems);

        var token = Value(section, "Token");
        if (token != null && !TokenPattern().IsMatch(token))
            problems.Add("--token may only contain letters, digits, '-' and '_' (at most 256).");

        var devOrigins = new List<string>();
        foreach (var origin in section.GetSection("DevOrigins").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (TryNormalizeOrigin(origin!, out var normalized))
                devOrigins.Add(normalized);
            else
                problems.Add($"--dev-origin must be an http(s) origin such as http://localhost:5180 (got '{origin}').");
        }

        var allowOrigins = new List<string>();
        foreach (var origin in section.GetSection("AllowOrigins").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (!TryNormalizeExactOrigin(origin!, out var normalized))
                problems.Add($"--allow-origin must be an exact http or https origin, scheme://host[:port] with no path, wildcard, percent sign or IPv6 zone, such as https://client.example (got '{origin}').");
            else if (!IsSecureOrLoopbackOrigin(normalized))
                problems.Add($"--allow-origin must use https unless the host is loopback (localhost, 127.0.0.0/8 or [::1]): the pairing link hands the origin the token (got '{origin}').");
            else
                allowOrigins.Add(normalized);
        }

        var dataDir = Value(section, "DataDir");
        string? fullDataDir = null;
        if (dataDir != null)
        {
            try
            {
                fullDataDir = Path.GetFullPath(dataDir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"--data-dir '{dataDir}' is not a valid path: {ex.Message}");
            }
        }

        if (fresh)
        {
            if (dataDir == null)
                problems.Add("--fresh wipes the data folder, so it needs an explicit --data-dir.");
            else if (fullDataDir != null && DataFolder.CheckWipeable(fullDataDir) is { } wipeError)
                problems.Add(wipeError);
        }

        var v1Dir = Value(section, "V1Dir");
        string? fullV1Dir = null;
        if (v1Dir != null)
        {
            try
            {
                fullV1Dir = Path.GetFullPath(v1Dir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"--v1-dir '{v1Dir}' is not a valid path: {ex.Message}");
            }
        }

        var webRoot = Value(section, "WebRoot");
        if (webRoot != null && !Directory.Exists(webRoot))
            problems.Add($"--web-root '{webRoot}' does not exist.");

        var runDir = Value(section, "RunDir");
        string? fullRunDir = null;
        if (runDir != null)
        {
            try
            {
                fullRunDir = Path.GetFullPath(runDir);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"Engine:RunDir '{runDir}' is not a valid path: {ex.Message}");
            }
        }

        errors = problems;
        if (problems.Count > 0)
            return null;

        return new EngineOptions
        {
            Port = port,
            DataDir = fullDataDir,
            V1Dir = fullV1Dir,
            WebRoot = webRoot == null ? null : Path.GetFullPath(webRoot),
            NoBrowser = noBrowser,
            NoWindows = noWindows,
            Token = token ?? NewToken(),
            DevOrigins = devOrigins.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AllowOrigins = allowOrigins.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Fresh = fresh,
            MockSeed = seed,
            Mock = mock,
            RunDir = fullRunDir,
        };
    }

    /// <summary><c>scheme://host[:port]</c> of an absolute http(s) URL with no path, query or fragment.</summary>
    public static bool TryNormalizeOrigin(string value, out string origin)
    {
        origin = "";
        if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
        {
            return false;
        }

        origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// <see cref="TryNormalizeOrigin"/>, stricter, for origins that get cross-origin access: the value must already be
    /// an origin as a browser sends it in <c>Origin</c> (ASCII, no wildcard, no path other than an optional trailing
    /// <c>/</c>, no user info, query or fragment).
    /// </summary>
    public static bool TryNormalizeExactOrigin(string value, out string origin)
    {
        origin = "";
        var text = value.Trim();
        var authority = text.IndexOf("://", StringComparison.Ordinal);
        if (authority < 0 || text.Contains('*') || text.Contains('\\') || text.Any(c => c <= 0x20 || c > 0x7E))
            return false;

        // Uri would quietly drop dot segments (http://host/./ is "http://host/"), so a path is refused before parsing.
        var slash = text.IndexOf('/', authority + 3);
        if (slash >= 0 && slash != text.Length - 1)
            return false;

        // A percent sign is either an escape a browser would decode or an IPv6 zone id, which browsers refuse.
        if (text.Contains('%'))
            return false;

        if (!TryNormalizeOrigin(text, out var normalized) || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return false;
        }

        // Uri keeps an embedded IPv4 part dotted ([::ffff:127.0.0.1]); browsers send the WHATWG form ([::ffff:7f00:1]).
        if (uri.HostNameType == UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || address.ScopeId != 0)
                return false;
            normalized = $"{uri.Scheme}://[{SerializeIPv6(address)}]" + (uri.IsDefaultPort ? "" : $":{uri.Port}");
        }

        origin = normalized;
        return true;
    }

    /// <summary>
    /// Whether an origin from <see cref="TryNormalizeExactOrigin"/> may get the token: https, or http on a loopback
    /// host (<c>localhost</c>, 127.0.0.0/8, <c>[::1]</c> and its IPv4-mapped forms), where no network sits in between.
    /// </summary>
    public static bool IsSecureOrLoopbackOrigin(string origin)
    {
        var uri = new Uri(origin);
        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;
        if (uri.HostNameType == UriHostNameType.Dns)
            return uri.Host == "localhost";
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
            return false;
        return IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    /// <summary>An IPv6 address as the WHATWG URL serializer writes it: lower-case hex, the first longest run of two or more zero pieces as <c>::</c>.</summary>
    private static string SerializeIPv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var pieces = new int[8];
        for (var i = 0; i < 8; i++)
            pieces[i] = (bytes[2 * i] << 8) | bytes[2 * i + 1];

        int bestStart = -1, bestLength = 1;
        for (var i = 0; i < 8;)
        {
            var end = i;
            while (end < 8 && pieces[end] == 0)
                end++;
            if (end - i > bestLength)
                (bestStart, bestLength) = (i, end - i);
            i = end == i ? i + 1 : end;
        }

        if (bestStart < 0)
            return string.Join(':', pieces.Select(p => p.ToString("x", CultureInfo.InvariantCulture)));
        string Join(int from, int to) => string.Join(':', pieces[from..to].Select(p => p.ToString("x", CultureInfo.InvariantCulture)));
        return Join(0, bestStart) + "::" + Join(bestStart + bestLength, 8);
    }

    /// <summary>
    /// A filesystem root, the user's home folder, or a folder that contains it. Compared lexically; callers pass the
    /// real path too (<see cref="DataFolder.ResolveRealPath"/>), and the home folder is compared both as given and
    /// by its real path.
    /// </summary>
    internal static bool IsProtectedDirectory(string fullPath)
    {
        var path = Path.TrimEndingDirectorySeparator(fullPath);
        var root = Path.GetPathRoot(fullPath);
        if (root != null && string.Equals(path, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            return true;
        if (path.Length == 0)
            return true;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            return false;

        string[] homes = [Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)), DataFolder.ResolveRealPath(home)];
        return homes.Any(h => string.Equals(path, h, StringComparison.OrdinalIgnoreCase)
            || h.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Value(IConfigurationSection section, string key)
    {
        var value = section[key];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool Flag(IConfigurationSection section, string key, string flag, List<string> problems)
    {
        var value = Value(section, key);
        if (value == null)
            return false;
        if (bool.TryParse(value, out var result))
            return result;
        problems.Add($"{flag} takes no value, or true/false (got '{value}').");
        return false;
    }
}
