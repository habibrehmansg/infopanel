using System.Collections.Concurrent;
using System.Text.Json;

namespace InfoPanel.Mcp.Text;

/// <summary>The preview checks' thresholds (the checks read them from P2 on).</summary>
public sealed record CheckThresholds(double CanvasTolerance, double OverlapMin, double ContrastMin);

/// <summary>
/// The preview checks' thresholds, <c>checks.json</c>, embedded from <c>Mcp/Text/</c>. (The tools' descriptions, the
/// instructions and the design guide are the configured service's to give, not the engine's.) A file that doesn't parse
/// or fails its shape is logged once.
/// </summary>
public sealed class McpText
{
    private const string ResourcePrefix = "InfoPanel.Mcp.Text/";

    private static readonly Lazy<Dictionary<string, byte[]>> BuiltInFiles = new(LoadBuiltIn);

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _warned = new();

    public McpText(ILogger logger)
    {
        _logger = logger;
    }

    // ---- checks.json --------------------------------------------------------------------------------------------------

    /// <summary>The preview checks' thresholds, each checked against its range.</summary>
    public CheckThresholds Checks() =>
        ParseChecks(BuiltIn("checks.json"), "built-in checks.json", null)
            ?? throw new InvalidOperationException("The built-in checks.json doesn't read.");

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
}
