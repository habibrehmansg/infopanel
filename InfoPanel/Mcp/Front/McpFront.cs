using System.Globalization;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;
using InfoPanel.Persistence;

namespace InfoPanel.Mcp.Front;

/// <summary>
/// <c>InfoPanel --mcp [--data-dir &lt;dir&gt;]</c>: the MCP server an AI app starts. It never opens the data folder's
/// stores, never takes its lock and never starts a web host; it speaks MCP on stdin and stdout (<see cref="McpServer"/>)
/// and relays tool calls to the engine on the folder (<see cref="EngineLink"/>), starting it when it isn't running.
/// stdout carries the protocol only: anything else the process writes to the console goes to stderr. It exits 0 when
/// stdin ends and never stops the engine.
/// </summary>
public static class McpFront
{
    /// <summary>The settings it reads besides <c>--data-dir</c>: host settings, never flags.</summary>
    private static readonly HashSet<string> Accepted = new(StringComparer.OrdinalIgnoreCase)
    {
        $"{EngineOptions.Section}:Mcp", $"{EngineOptions.Section}:DataDir",
    };

    public static async Task<int> RunAsync(EngineCommandLine.Result parsed, Func<JsonObject> builtInList)
    {
        // The protocol's stream first; then everything written to the console goes to stderr.
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();
        Console.SetOut(Console.Error);
        var log = Console.Error;

        if (parsed.Settings.Keys.FirstOrDefault(k => !Accepted.Contains(k)) is { } other)
        {
            await log.WriteLineAsync($"InfoPanel: --mcp takes only --data-dir (not {Flag(other)}); the engine it starts gets the flags of the data folder's last start.").ConfigureAwait(false);
            return 2;
        }

        // Read as the engine reads its settings (environment, then flags and host settings), but only the three the front
        // needs: whatever else of the engine's the AI app's environment holds (Engine__Fresh, a port, a token) is none of
        // its business, and never reaches the engine it starts (EngineLauncher).
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddInMemoryCollection(parsed.Settings)
            .AddCommandLine([.. parsed.HostArgs])
            .Build();
        var section = configuration.GetSection(EngineOptions.Section);
        var errors = new List<string>();
        var dataDir = FullPath(section["DataDir"], "--data-dir", errors);
        var runDir = FullPath(section["RunDir"], $"{EngineOptions.Section}:RunDir", errors);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                await log.WriteLineAsync($"InfoPanel: {error}").ConfigureAwait(false);
            return 2;
        }

        var paths = dataDir != null ? new DataPaths(dataDir) : DataPaths.DefaultRoot();
        LocalChannelEndpoint endpoint;
        try
        {
            endpoint = LocalChannel.EndpointFor(runDir ?? LocalChannel.DefaultRunDirectory(), paths.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await log.WriteLineAsync($"InfoPanel: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var startSeconds = section["McpStartSeconds"];
        var timeout = startSeconds != null && double.TryParse(startSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(30);

        await log.WriteLineAsync($"infopanel-mcp: data folder {paths.Root}, channel {endpoint.Name}").ConfigureAwait(false);
        await using var link = new EngineLink(new EngineLinkOptions
        {
            Endpoint = endpoint,
            DataDir = dataDir,
            Paths = paths,
            RunDir = runDir,
            StartTimeout = timeout,
            BuiltInList = builtInList,
        }, new EngineLauncher(), log);
        await new McpServer(stdin, stdout, link, log).RunAsync(CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>A path setting made absolute; null when it isn't set, and a problem when it isn't a path.</summary>
    private static string? FullPath(string? value, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errors.Add($"{name} '{value}' is not a valid path: {ex.Message}");
            return null;
        }
    }

    private static string Flag(string key)
    {
        var name = key[(EngineOptions.Section.Length + 1)..];
        return name.Contains(':') ? name[..name.IndexOf(':')] : name;
    }
}
