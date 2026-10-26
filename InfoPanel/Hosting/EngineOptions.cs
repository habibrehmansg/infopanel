using InfoPanel.Persistence;
using System.Globalization;
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

    /// <summary>The web client's built files; null means <c>{BaseDirectory}/wwwroot</c>, else the nearest <c>client/dist</c> above it.</summary>
    public string? WebRoot { get; init; }

    public bool NoBrowser { get; init; }

    /// <summary>Don't show active profiles as desktop windows (<c>--no-windows</c>).</summary>
    public bool NoWindows { get; init; }

    /// <summary>The session token (URL-safe characters only); generated when not given.</summary>
    public string Token { get; init; } = "";

    /// <summary>Extra allowed origins (the dev server), normalised to <c>scheme://host[:port]</c>.</summary>
    public IReadOnlyList<string> DevOrigins { get; init; } = [];

    /// <summary>A 1.x data folder to import at start-up.</summary>
    public string? ImportV1 { get; init; }

    /// <summary>Wipe the data folder before starting (only with an explicit <see cref="DataDir"/>).</summary>
    public bool Fresh { get; init; }

    /// <summary>Seed for the mock sensors; null takes one from the clock.</summary>
    public uint? MockSeed { get; init; }

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

        var webRoot = Value(section, "WebRoot");
        if (webRoot != null && !Directory.Exists(webRoot))
            problems.Add($"--web-root '{webRoot}' does not exist.");

        var importV1 = Value(section, "ImportV1");
        if (importV1 != null && !Directory.Exists(importV1))
            problems.Add($"--import-v1 '{importV1}' does not exist.");
        else if (importV1 != null && (dataDir == null || fullDataDir != null))
        {
            // The importer's own rule, checked now so nothing (--fresh above all) runs before it fails.
            var dataRoot = fullDataDir ?? DataPaths.DefaultRoot().Root;
            var source = Path.GetFullPath(importV1);
            try
            {
                if ((V1DataImporter.FindOverlap(source, dataRoot)
                        ?? V1DataImporter.FindOverlap(DataFolder.ResolveRealPath(source), DataFolder.ResolveRealPath(dataRoot))) is { } overlap)
                    problems.Add($"--import-v1: {overlap}");
            }
            catch (IOException ex)
            {
                problems.Add($"--import-v1 '{importV1}' can't be checked against the data folder: {ex.Message}");
            }
        }

        errors = problems;
        if (problems.Count > 0)
            return null;

        return new EngineOptions
        {
            Port = port,
            DataDir = fullDataDir,
            WebRoot = webRoot == null ? null : Path.GetFullPath(webRoot),
            NoBrowser = noBrowser,
            NoWindows = noWindows,
            Token = token ?? NewToken(),
            DevOrigins = devOrigins.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ImportV1 = importV1 == null ? null : Path.GetFullPath(importV1),
            Fresh = fresh,
            MockSeed = seed,
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
