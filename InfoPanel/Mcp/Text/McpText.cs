using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InfoPanel.Hosting;
using InfoPanel.Persistence;

namespace InfoPanel.Mcp.Text;

/// <summary>Tool wording: the server's instructions and each tool's description.</summary>
public sealed record ToolText(string Instructions, IReadOnlyDictionary<string, string> Descriptions);

/// <summary>A guide topic: its id (its file's name without <c>.md</c>) and its title (its first heading).</summary>
public sealed record GuideTopic(string Id, string Title);

/// <summary>An example: its id and its description.</summary>
public sealed record ExampleEntry(string Id, string? Description);

/// <summary>The preview checks' thresholds (the checks read them from P2 on).</summary>
public sealed record CheckThresholds(double CanvasTolerance, double OverlapMin, double ContrastMin);

/// <summary>
/// The text the AI-app tools read, in layers: the built-in text (embedded, <c>Mcp/Text/</c>), then the live folder
/// (<c>--mcp-text</c>), read on every call so the owner tunes it without a rebuild. A file in the live folder replaces the
/// built-in one of the same name; <c>tools.json</c> merges per tool.
/// <code>
/// tools.json        { "instructions": "…", "tools": { "&lt;tool&gt;": { "description": "…" } } }
/// guide/{id}.md     get_design_guide's topics (built-in: scenes.md)
/// palettes.json     colour palettes and type pairings (an object)
/// examples/{id}.json starting points (objects; an optional "description")
/// checks.json       the preview checks' thresholds
/// </code>
/// <b>The live folder's boundary.</b> Topic and example ids are a catalogue (<see cref="IdPattern"/>, found in a bounded
/// listing of at most <see cref="MaxListing"/> entries), never paths given by a caller. Every read goes through
/// <see cref="RegularFile"/> (non-blocking, regular files only, so a FIFO or a device never blocks a call), is capped at
/// <see cref="MaxFileBytes"/>, and refuses a file whose real path leaves the live folder's (a link out of it). Each JSON
/// file's shape and ranges are checked after parsing. A file that is refused, doesn't parse or fails its shape is skipped
/// whole, logged once, and the layer below is used.
/// </summary>
public sealed partial class McpText
{
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxInstructionsBytes = 16 * 1024;
    public const int MaxDescriptionBytes = 4 * 1024;
    public const int MaxListing = 256;

    /// <summary>A topic or example id: lower-case letters, digits and hyphens, at most 64.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    public static partial Regex IdPattern();

    private const string ResourcePrefix = "InfoPanel.Mcp.Text/";

    private static readonly Lazy<Dictionary<string, byte[]>> BuiltInFiles = new(LoadBuiltIn);

    private readonly string? _live;
    private readonly IReadOnlySet<string> _tools;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _warned = new();

    /// <param name="live">The live folder (<c>--mcp-text</c>), or null for the built-in text only.</param>
    /// <param name="tools">The tool names whose descriptions <c>tools.json</c> may set.</param>
    public McpText(string? live, IReadOnlySet<string> tools, ILogger logger)
    {
        _live = live;
        _tools = tools;
        _logger = logger;
    }

    /// <summary>The live folder, or null.</summary>
    public string? Live => _live;

    // ---- tools.json ---------------------------------------------------------------------------------------------------

    /// <summary>The instructions and descriptions: the built-in ones, with the live file's over them per tool.</summary>
    public ToolText Tools()
    {
        var builtIn = ParseTools(BuiltIn("tools.json"), "built-in tools.json")
            ?? throw new InvalidOperationException("The built-in tools.json doesn't read.");
        if (ReadLive("tools.json") is not { } live || ParseTools(live, LivePath("tools.json")) is not { } over)
            return builtIn;

        var descriptions = new Dictionary<string, string>(builtIn.Descriptions, StringComparer.Ordinal);
        foreach (var (tool, description) in over.Descriptions)
            descriptions[tool] = description;
        return new ToolText(over.Instructions.Length > 0 ? over.Instructions : builtIn.Instructions, descriptions);
    }

    private ToolText? ParseTools(byte[] bytes, string where)
    {
        return Parse(bytes, where, root =>
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Fail<ToolText>("it isn't a JSON object");
            var instructions = "";
            if (root.TryGetProperty("instructions", out var text))
            {
                if (text.ValueKind != JsonValueKind.String)
                    return Fail<ToolText>("instructions isn't a string");
                instructions = text.GetString()!;
                if (Encoding.UTF8.GetByteCount(instructions) > MaxInstructionsBytes)
                    return Fail<ToolText>($"instructions is over {MaxInstructionsBytes} bytes");
            }

            var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("tools", out var tools))
            {
                if (tools.ValueKind != JsonValueKind.Object)
                    return Fail<ToolText>("tools isn't an object");
                foreach (var tool in tools.EnumerateObject())
                {
                    if (tool.Value.ValueKind != JsonValueKind.Object)
                        return Fail<ToolText>($"tools.{tool.Name} isn't an object");
                    if (!tool.Value.TryGetProperty("description", out var description))
                        continue;
                    if (description.ValueKind != JsonValueKind.String)
                        return Fail<ToolText>($"tools.{tool.Name}.description isn't a string");
                    var value = description.GetString()!;
                    if (Encoding.UTF8.GetByteCount(value) > MaxDescriptionBytes)
                        return Fail<ToolText>($"tools.{tool.Name}.description is over {MaxDescriptionBytes} bytes");
                    if (_tools.Contains(tool.Name))
                        descriptions[tool.Name] = value; // other names: tools this engine doesn't have, ignored
                }
            }

            return (new ToolText(instructions, descriptions), null);
        });
    }

    // ---- guide/ -------------------------------------------------------------------------------------------------------

    /// <summary>The guide's topics, by id: the built-in ones and the live folder's (a live topic replaces a built-in one).</summary>
    public IReadOnlyList<GuideTopic> Topics()
    {
        var topics = new SortedDictionary<string, GuideTopic>(StringComparer.Ordinal);
        foreach (var id in BuiltInIds("guide/", ".md"))
            topics[id] = new GuideTopic(id, TitleOf(BuiltIn($"guide/{id}.md")));
        foreach (var id in LiveIds("guide", ".md"))
        {
            if (ReadLive("guide", id + ".md") is { } bytes)
                topics[id] = new GuideTopic(id, TitleOf(bytes));
        }
        return [.. topics.Values];
    }

    /// <summary>The topic's text, or null when <paramref name="id"/> isn't a catalogued topic.</summary>
    public string? Topic(string id)
    {
        // Only an id the catalogue lists is ever read: a caller's text never becomes a path.
        if (Topics().FirstOrDefault(t => t.Id == id) is not { } topic)
            return null;
        if (LiveIds("guide", ".md").Contains(topic.Id) && ReadLive("guide", topic.Id + ".md") is { } live)
            return Utf8(live);
        return BuiltInFiles.Value.TryGetValue($"guide/{topic.Id}.md", out var builtIn) ? Utf8(builtIn) : null;
    }

    // ---- palettes.json and examples/ ---------------------------------------------------------------------------------

    /// <summary>palettes.json's text (the live one, else the built-in one), or null when there is none.</summary>
    public string? Palettes()
    {
        foreach (var (bytes, where) in Layers("palettes.json"))
        {
            if (Parse(bytes, where, root => root.ValueKind == JsonValueKind.Object ? (true, null) : Fail<bool>("it isn't a JSON object")))
                return Utf8(bytes);
        }
        return null;
    }

    /// <summary>The examples, by id, each with its description.</summary>
    public IReadOnlyList<ExampleEntry> Examples()
    {
        var examples = new SortedDictionary<string, ExampleEntry>(StringComparer.Ordinal);
        foreach (var id in BuiltInIds("examples/", ".json"))
        {
            if (ParseExample(BuiltIn($"examples/{id}.json"), $"built-in examples/{id}.json") is { } description)
                examples[id] = new ExampleEntry(id, description.Length > 0 ? description : null);
        }
        foreach (var id in LiveIds("examples", ".json"))
        {
            if (ReadLive("examples", id + ".json") is { } bytes && ParseExample(bytes, LivePath("examples", id + ".json")) is { } description)
                examples[id] = new ExampleEntry(id, description.Length > 0 ? description : null);
        }
        return [.. examples.Values];
    }

    /// <summary>The example's text, or null when <paramref name="id"/> isn't a catalogued example.</summary>
    public string? Example(string id)
    {
        if (!Examples().Any(e => e.Id == id))
            return null;
        if (LiveIds("examples", ".json").Contains(id) && ReadLive("examples", id + ".json") is { } live
            && ParseExample(live, LivePath("examples", id + ".json")) != null)
        {
            return Utf8(live);
        }
        return BuiltInFiles.Value.TryGetValue($"examples/{id}.json", out var builtIn) ? Utf8(builtIn) : null;
    }

    /// <summary>An example's description ("" when it has none), or null when it isn't a valid example.</summary>
    private string? ParseExample(byte[] bytes, string where) =>
        Parse(bytes, where, root =>
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Fail<string>("it isn't a JSON object");
            if (!root.TryGetProperty("description", out var description))
                return ("", null);
            if (description.ValueKind != JsonValueKind.String)
                return Fail<string>("description isn't a string");
            var text = description.GetString()!;
            return Encoding.UTF8.GetByteCount(text) > MaxDescriptionBytes
                ? Fail<string>($"description is over {MaxDescriptionBytes} bytes")
                : (text, null);
        });

    // ---- checks.json --------------------------------------------------------------------------------------------------

    /// <summary>The preview checks' thresholds: the live file's over the built-in ones, each checked against its range.</summary>
    public CheckThresholds Checks()
    {
        var builtIn = ParseChecks(BuiltIn("checks.json"), "built-in checks.json", null)
            ?? throw new InvalidOperationException("The built-in checks.json doesn't read.");
        return ReadLive("checks.json") is { } live && ParseChecks(live, LivePath("checks.json"), builtIn) is { } over ? over : builtIn;
    }

    private CheckThresholds? ParseChecks(byte[] bytes, string where, CheckThresholds? below) =>
        Parse(bytes, where, root =>
        {
            if (root.ValueKind != JsonValueKind.Object)
                return Fail<CheckThresholds>("it isn't a JSON object");
            double? Number(string name, double min, double max, out string? problem)
            {
                problem = null;
                if (!root.TryGetProperty(name, out var value))
                    return null;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    problem = $"{name} isn't a number";
                    return null;
                }
                if (number < min || number > max)
                {
                    problem = $"{name} is {number}, outside {min}–{max}";
                    return null;
                }
                return number;
            }

            var canvas = Number("canvasTolerance", 0, 50, out var p1);
            var overlap = Number("overlapMin", 0, 1, out var p2);
            var contrast = Number("contrastMin", 1, 21, out var p3);
            if ((p1 ?? p2 ?? p3) is { } problem)
                return Fail<CheckThresholds>(problem);
            if (below == null && (canvas == null || overlap == null || contrast == null))
                return Fail<CheckThresholds>("a threshold is missing");
            return (new CheckThresholds(canvas ?? below!.CanvasTolerance, overlap ?? below!.OverlapMin, contrast ?? below!.ContrastMin), null);
        });

    // ---- reading ------------------------------------------------------------------------------------------------------

    /// <summary>A file's bytes from each layer that has it, the live folder first.</summary>
    private IEnumerable<(byte[] Bytes, string Where)> Layers(string name)
    {
        if (ReadLive(name) is { } live)
            yield return (live, LivePath(name));
        if (BuiltInFiles.Value.TryGetValue(name, out var builtIn))
            yield return (builtIn, $"built-in {name}");
    }

    private string LivePath(params string[] parts) => Path.Combine([_live ?? "", .. parts]);

    /// <summary>
    /// A live file's bytes, or null when there is no live folder or no such file, or it is refused: its real path outside
    /// the live folder's, not a regular file, over <see cref="MaxFileBytes"/>, or unreadable (logged).
    /// </summary>
    private byte[]? ReadLive(params string[] parts)
    {
        if (_live == null)
            return null;
        var shown = LivePath(parts);
        string root;
        string real;
        try
        {
            root = Path.TrimEndingDirectorySeparator(DataFolder.ResolveRealPath(_live));
            real = DataFolder.ResolveRealPath(Path.Combine([root, .. parts]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Warn(shown, $"its path can't be resolved ({ex.Message})");
            return null;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!real.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            if (File.Exists(real) || Directory.Exists(real))
                Warn(shown, "it is a link that leaves the folder");
            return null;
        }

        try
        {
            return RegularFile.ReadAtMost(real, MaxFileBytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            Warn(shown, $"it is over {MaxFileBytes} bytes");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(shown, ex is NotARegularFileException ? "it isn't a regular file" : ex.Message);
            return null;
        }
    }

    /// <summary>The ids of the live folder's <c>{folder}/{id}{extension}</c> files, from a listing of at most <see cref="MaxListing"/> entries.</summary>
    private IReadOnlySet<string> LiveIds(string folder, string extension)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (_live == null)
            return ids;
        var path = Path.Combine(_live, folder);
        try
        {
            foreach (var entry in RegularFile.List(path, new ListingBudget(MaxListing, $"the live text folder's {folder}/")))
            {
                if (entry.Name.EndsWith(extension, StringComparison.Ordinal) && IdPattern().IsMatch(entry.Name[..^extension.Length]))
                    ids.Add(entry.Name[..^extension.Length]);
            }
        }
        catch (ListingBudgetException)
        {
            Warn(path, $"it has over {MaxListing} entries; only the first ones are read");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn(path, ex.Message);
        }
        return ids;
    }

    private static IEnumerable<string> BuiltInIds(string folder, string extension) =>
        BuiltInFiles.Value.Keys
            .Where(k => k.StartsWith(folder, StringComparison.Ordinal) && k.EndsWith(extension, StringComparison.Ordinal))
            .Select(k => k[folder.Length..^extension.Length])
            .Where(id => IdPattern().IsMatch(id));

    private static byte[] BuiltIn(string name) =>
        BuiltInFiles.Value.TryGetValue(name, out var bytes) ? bytes : throw new InvalidOperationException($"No built-in {name}.");

    private static Dictionary<string, byte[]> LoadBuiltIn()
    {
        var assembly = typeof(McpText).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files[name[ResourcePrefix.Length..].Replace('\\', '/')] = copy.ToArray();
        }
        return files;
    }

    /// <summary>Parses and checks a file; a file that fails is logged once and gives the default.</summary>
    private T? Parse<T>(byte[] bytes, string where, Func<JsonElement, (T? Value, string? Problem)> check)
    {
        string? problem;
        T? value = default;
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            (value, problem) = check(json.RootElement);
        }
        catch (JsonException ex)
        {
            problem = $"it isn't JSON ({ex.Message})";
        }

        if (problem == null)
            return value;
        Warn(where, problem);
        return default;
    }

    private static (T? Value, string? Problem) Fail<T>(string problem) => (default, problem);

    private void Warn(string where, string problem)
    {
        // Once per file and problem: the folder is read on every call.
        if (_warned.Count > 1024)
            _warned.Clear();
        if (_warned.TryAdd($"{where}\n{problem}", 0))
            _logger.LogWarning("The AI text file {File} is skipped and the layer below used: {Problem}", where, problem);
    }

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    /// <summary>A topic's title: its first <c># </c> heading, else its id's words.</summary>
    private static string TitleOf(byte[] bytes)
    {
        foreach (var line in Utf8(bytes).Split('\n').Take(50))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                return trimmed[2..].Trim();
        }
        return "";
    }
}
