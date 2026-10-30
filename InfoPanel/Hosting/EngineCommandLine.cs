namespace InfoPanel.Hosting;

/// <summary>
/// Parses the engine's command line into <c>Engine:*</c> settings. The stock command-line configuration provider
/// can't take value-less flags (<c>--no-browser --port 1</c> would read "--port" as the flag's value) or repeated
/// options, and accepts anything, so the engine parses its own flags and fails on anything it doesn't know.
/// <c>--key=value</c> arguments whose key is a configuration path (contains ':') or a host setting
/// (applicationName, environment, contentRoot) or the <c>Engine</c> section itself pass through to the host; that is how
/// <c>WebApplicationFactory</c> and <c>UseSetting</c> hand settings to the entry point. Kestrel and URL settings
/// (<c>--Kestrel:*</c>, <c>--urls</c>, <c>--http_ports</c>, …) are refused: the engine listens on loopback only.
/// </summary>
public static class EngineCommandLine
{
    public sealed record Result(
        IReadOnlyDictionary<string, string?> Settings,
        IReadOnlyList<string> HostArgs,
        string? Error,
        bool ShowHelp);

    private static readonly Dictionary<string, string> ValueOptions = new(StringComparer.Ordinal)
    {
        ["--port"] = "Port",
        ["--data-dir"] = "DataDir",
        ["--v1-dir"] = "V1Dir",
        ["--web-root"] = "WebRoot",
        ["--token"] = "Token",
        ["--mock-seed"] = "MockSeed",
        ["--mcp-text"] = "McpText",
    };

    private static readonly Dictionary<string, string> Flags = new(StringComparer.Ordinal)
    {
        ["--no-browser"] = "NoBrowser",
        ["--no-windows"] = "NoWindows",
        ["--fresh"] = "Fresh",
        ["--mock"] = "Mock",
        ["--offline"] = "Offline",
        ["--mcp"] = "Mcp",
    };

    private const string DevOrigin = "--dev-origin";
    private const string AllowOrigin = "--allow-origin";

    private static readonly HashSet<string> HostKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationName", "environment", "contentRoot",
    };

    /// <summary>Kestrel and server-address settings, which could add listeners beyond loopback.</summary>
    private static bool IsListenSetting(string key) =>
        key.StartsWith("Kestrel:", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Kestrel__", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Kestrel", StringComparison.OrdinalIgnoreCase)
        || key.Equals("urls", StringComparison.OrdinalIgnoreCase)
        || key.Equals("http_ports", StringComparison.OrdinalIgnoreCase)
        || key.Equals("https_ports", StringComparison.OrdinalIgnoreCase)
        || key.Equals("preferHostingUrls", StringComparison.OrdinalIgnoreCase);

    public const string Usage = """
        Usage: InfoPanel [options]

          --port <n>            Loopback port to listen on (default 0: a free port)
          --data-dir <dir>      Data folder (default: the per-user InfoPanel/v2 folder)
          --v1-dir <dir>        The InfoPanel 1.x data folder the 1.x import reads, never writes
                                (default: 1.x's own per-user InfoPanel folder)
          --web-root <dir>      The web client's built files (default: wwwroot next to the engine)
          --no-browser          Don't open the browser
          --no-windows          Don't show active profiles as desktop windows
          --token <token>       Session token (default: random)
          --dev-origin <url>    Extra allowed origin, e.g. http://localhost:5180 (repeatable)
          --allow-origin <url>  Exact origin of a web client served elsewhere that may call the
                                engine cross-origin (CORS, /ws), e.g. https://client.example; http
                                only on a loopback host (repeatable)
          --fresh               Wipe the data folder first (needs --data-dir)
          --mock                Mock sensors, and simulated values for sensors no source has
          --mock-seed <n>       Seed for the mock sensors (implies --mock)
          --offline             Media providers connect only to this computer (tests)
          --mcp-text <dir>      A folder of tool wording, guide topics and other text for AI apps,
                                read on every call over the built-in text
          --mcp                 Be the MCP server an AI app starts (stdin and stdout): it talks to
                                the engine on the data folder, starting it when it isn't running.
                                Takes only --data-dir; the engine it starts gets the flags of the
                                folder's last start (its launch record) and --no-browser
          --help                Show this help
        """;

    public static Result Parse(IReadOnlyList<string> args)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var hostArgs = new List<string>();
        var devOrigins = 0;
        var allowOrigins = 0;

        Result Fail(string error) => new(settings, hostArgs, error, false);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h" or "-?")
                return new Result(settings, hostArgs, null, true);

            string name = arg;
            string? inlineValue = null;
            var equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            if (Flags.TryGetValue(name, out var flagKey))
            {
                settings[$"{EngineOptions.Section}:{flagKey}"] = inlineValue ?? "true";
                continue;
            }

            if (ValueOptions.TryGetValue(name, out var key) || name is DevOrigin or AllowOrigin)
            {
                string value;
                if (inlineValue != null)
                {
                    value = inlineValue;
                }
                else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }
                else
                {
                    return Fail($"{name} needs a value.");
                }

                if (value.Length == 0)
                    return Fail($"{name} needs a value.");

                if (name == DevOrigin)
                    settings[$"{EngineOptions.Section}:DevOrigins:{devOrigins++}"] = value;
                else if (name == AllowOrigin)
                    settings[$"{EngineOptions.Section}:AllowOrigins:{allowOrigins++}"] = value;
                else if (settings.ContainsKey($"{EngineOptions.Section}:{key}"))
                    return Fail($"{name} is given more than once.");
                else
                    settings[$"{EngineOptions.Section}:{key}"] = value;
                continue;
            }

            if (inlineValue != null)
            {
                var hostKey = name[2..];
                if (IsListenSetting(hostKey))
                    return Fail($"'{name}' is not accepted: the engine listens on 127.0.0.1 only (choose the port with --port).");

                // WebApplicationFactory also passes section keys with empty values (--Engine=).
                if (hostKey.Contains(':') || HostKeys.Contains(hostKey) || string.Equals(hostKey, EngineOptions.Section, StringComparison.OrdinalIgnoreCase))
                {
                    hostArgs.Add(arg);
                    continue;
                }
            }

            return Fail($"Unknown argument '{arg}'.");
        }

        return new Result(settings, hostArgs, null, false);
    }
}
