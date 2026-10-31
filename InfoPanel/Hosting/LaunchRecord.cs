using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Documents;
using InfoPanel.Persistence;

namespace InfoPanel.Hosting;

/// <summary>
/// The launch record: the flags of the last start of the engine on a data folder, so an engine started for an AI app
/// (<c>--mcp</c>) runs as the one the owner started by hand did, with the same web root, tuning folder and origins.
/// <list type="bullet">
/// <item>Every engine that takes a data folder's lock writes <c>{run folder}/{hash16}.launch.json</c> beside the
/// channel's socket (owner-only, through <see cref="AtomicFile"/>): <c>{"launch":1,"args":[…]}</c>, its own flags from
/// <see cref="Allowed"/>, paths made absolute. Not in the data folder.</item>
/// <item>Only <see cref="Allowed"/> flags are written and only they are read back: a hand-edited record can't add
/// <c>--token</c> (a secret, never stored), <c>--fresh</c> or anything else; such a flag is skipped and said. A record
/// that can't be read (not a regular file, over <see cref="MaxBytes"/>, not JSON) gives no flags.</item>
/// <item>The engine started with them parses them as it parses any: a bad value stops it with exit 2.</item>
/// </list>
/// </summary>
public static class LaunchRecord
{
    public const int Version = 1;
    public const int MaxBytes = 64 * 1024;

    /// <summary>The flags a record carries: those that take a value, the repeatable ones, and the switches.</summary>
    private static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal) { "--port", "--web-root", "--mcp-text", "--mock-seed", "--v1-dir" };
    private static readonly HashSet<string> RepeatedFlags = new(StringComparer.Ordinal) { "--allow-origin", "--dev-origin" };
    private static readonly HashSet<string> Switches = new(StringComparer.Ordinal) { "--mock", "--no-windows", "--no-tray" };

    /// <summary>Every flag a record may hold.</summary>
    public static IReadOnlySet<string> Allowed { get; } = new HashSet<string>([.. ValueFlags, .. RepeatedFlags, .. Switches], StringComparer.Ordinal);

    /// <summary>The record's path for a channel: beside its socket.</summary>
    public static string PathFor(LocalChannelEndpoint endpoint) => Path.Combine(endpoint.RunDirectory, endpoint.Hash + ".launch.json");

    /// <summary>The allowlisted flags of a start, as arguments, paths absolute (they are, once loaded).</summary>
    public static IReadOnlyList<string> ArgumentsOf(EngineOptions options)
    {
        var args = new List<string>();
        if (options.Port != 0)
            args.AddRange(["--port", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.WebRoot != null)
            args.AddRange(["--web-root", options.WebRoot]);
        if (options.McpText != null)
            args.AddRange(["--mcp-text", options.McpText]);
        if (options.V1Dir != null)
            args.AddRange(["--v1-dir", options.V1Dir]);
        if (options.MockSeed is { } seed)
            args.AddRange(["--mock-seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        else if (options.Mock)
            args.Add("--mock");
        if (options.NoWindows)
            args.Add("--no-windows");
        if (options.NoTray)
            args.Add("--no-tray");
        foreach (var origin in options.DevOrigins)
            args.AddRange(["--dev-origin", origin]);
        // The origin a build carries isn't a flag: the engine a record starts takes its own build's.
        if (!options.AllowOriginsBuiltIn)
        {
            foreach (var origin in options.AllowOrigins)
                args.AddRange(["--allow-origin", origin]);
        }
        return args;
    }

    /// <summary>Writes the record of <paramref name="options"/> at <paramref name="path"/>, owner-only.</summary>
    /// <exception cref="IOException">It couldn't be written.</exception>
    public static void Write(string path, EngineOptions options)
    {
        var record = new JsonObject
        {
            ["launch"] = Version,
            ["args"] = new JsonArray([.. ArgumentsOf(options).Select(a => (JsonNode)JsonValue.Create(a))]),
        };
        AtomicFile.WritePrivate(path, JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { WriteIndented = true }),
            Path.GetDirectoryName(path));
    }

    /// <summary>
    /// The record's flags (<see cref="Read"/>) only when it is trusted as the channel is (Codex p2-2 #1): on Unix its run
    /// folder passes <see cref="LocalChannel.CheckRunDirectory"/>, and the record is a regular file, not a link, this
    /// user's, that no one else may write. Otherwise none, the reason said to <paramref name="warn"/>.
    /// </summary>
    public static IReadOnlyList<string> ReadTrusted(LocalChannelEndpoint endpoint, Action<string> warn)
    {
        var path = PathFor(endpoint);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                LocalChannel.CheckRunDirectory(endpoint.RunDirectory);
                if (UnixIdentity.StatOf(path) is not { } stat)
                    return [];
                if ((stat.Mode & UnixIdentity.TypeMask) != UnixIdentity.TypeFile)
                    throw new LocalChannelException($"The launch record {path} is a link or not a file.");
                if (stat.Owner != UnixIdentity.CurrentUser)
                    throw new LocalChannelException($"The launch record {path} belongs to another user (uid {stat.Owner}).");
                if ((stat.Mode & (int)(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                    throw new LocalChannelException($"The launch record {path} is one others can write (mode {Convert.ToString(stat.Mode & 0x1FF, 8)}).");
            }
            catch (IOException ex)
            {
                warn($"{ex.Message} The launch record isn't trusted, so it wasn't read.");
                return [];
            }
        }
        return Read(path, warn);
    }

    /// <summary>
    /// The allowlisted flags of the record at <paramref name="path"/>; none when there is none or it can't be read. Each
    /// flag is checked against <see cref="Allowed"/> again; what isn't on it is skipped and said to <paramref name="warn"/>.
    /// </summary>
    public static IReadOnlyList<string> Read(string path, Action<string> warn)
    {
        byte[] bytes;
        try
        {
            bytes = RegularFile.ReadAtMost(path, MaxBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warn($"The launch record {path} can't be read ({ex.Message}); starting without it.");
            return [];
        }

        List<string> given;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            // Each member's kind is checked before it is read: TryGetInt32 throws on anything but a number.
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("launch", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != Version
                || !root.TryGetProperty("args", out var array) || array.ValueKind != JsonValueKind.Array
                || array.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String))
            {
                warn($"The launch record {path} isn't one this engine reads; starting without it.");
                return [];
            }
            given = [.. array.EnumerateArray().Select(a => a.GetString()!)];
        }
        catch (JsonException ex)
        {
            warn($"The launch record {path} isn't JSON ({ex.Message}); starting without it.");
            return [];
        }

        var args = new List<string>();
        for (var i = 0; i < given.Count; i++)
        {
            var flag = given[i];
            if (Switches.Contains(flag))
            {
                args.Add(flag);
            }
            else if (ValueFlags.Contains(flag) || RepeatedFlags.Contains(flag))
            {
                if (i + 1 >= given.Count || given[i + 1].StartsWith("--", StringComparison.Ordinal) || given[i + 1].Length == 0)
                {
                    warn($"The launch record's {flag} has no value; skipped.");
                    continue;
                }
                args.AddRange([flag, given[++i]]);
            }
            else
            {
                warn($"The launch record's '{flag}' isn't a flag a record may carry; skipped.");
            }
        }

        return args;
    }
}
