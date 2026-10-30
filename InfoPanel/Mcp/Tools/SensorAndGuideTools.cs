using System.Text;
using System.Text.Json.Nodes;
using InfoPanel.Mcp.Text;
using InfoPanel.Security;
using InfoPanel.Sensors;

namespace InfoPanel.Mcp.Tools;

/// <summary>
/// <c>list_sensors</c>: the sensors (as <c>GET /api/sensors</c> and <c>/api/sensors/values</c>), each with its key,
/// name, group, unit, status and value; <c>query</c> keeps those whose key, name or group holds every word of it.
/// </summary>
public sealed class ListSensorsTool(SensorHub hub) : McpTool
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    public override string Name => "list_sensors";
    public override ApiScope Scope => ApiScope.Read;
    public override string Title => "List sensors";
    public override JsonObject InputSchema => Schema(
        ("query", String("Words the sensor's key, name or group must all contain, e.g. \"cpu temp\"."), false),
        ("limit", Integer($"The most sensors to list ({DefaultLimit} when absent).", 1, MaxLimit), false));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var words = (arguments.String("query") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var limit = arguments.Integer("limit") ?? DefaultLimit;
        var matching = hub.GetSensors()
            .Where(s => words.All(w => $"{s.Key} {s.Name} {s.Group}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var lines = new StringBuilder();
        foreach (var sensor in matching.Take(limit))
        {
            var state = hub.ReadState(sensor.Key);
            var entry = new JsonObject
            {
                ["key"] = sensor.Key.ToString(),
                ["name"] = sensor.Name,
                ["group"] = sensor.Group,
                ["unit"] = sensor.Unit,
                ["status"] = System.Text.Json.JsonSerializer.SerializeToNode(state.Status),
            };
            if (state.HasReading)
            {
                var reading = state.Reading;
                if (reading.ValueText != null || reading.ValueTable != null)
                    entry["value"] = reading.ValueText ?? "";
                else if (double.IsFinite(reading.ValueNow))
                    entry["value"] = Math.Round(reading.ValueNow, 3);
            }
            lines.Append(entry.ToJsonString()).Append('\n');
        }

        var head = matching.Count == 0
            ? words.Length == 0 ? "There are no sensors." : $"No sensor matches \"{string.Join(' ', words)}\"."
            : matching.Count > limit
                ? $"{limit} of {matching.Count} sensors{(words.Length > 0 ? " matching" : "")} (ask with a query or a higher limit for the rest):"
                : $"{matching.Count} sensor{(matching.Count == 1 ? "" : "s")}{(words.Length > 0 ? " matching" : "")}:";
        return Task.FromResult(ToolResult.Text($"{head}\n{lines}".TrimEnd()));
    }
}

/// <summary>
/// <c>get_design_guide</c>: with no arguments, the catalogue (topics, palettes, examples); with <c>topic</c> a topic's
/// text (or <c>palettes</c>); with <c>example</c> an example. Ids come from the catalogue only: anything else is an error
/// naming the ids, never a path.
/// </summary>
public sealed class GetDesignGuideTool(McpText text) : McpTool
{
    public const string PalettesTopic = "palettes";

    public override string Name => "get_design_guide";
    public override ApiScope Scope => ApiScope.Read;
    public override string Title => "Design guide";
    public override JsonObject InputSchema => Schema(
        ("topic", String("A topic's id from the list (or palettes).", 64), false),
        ("example", String("An example's id from the list.", 64), false));

    public override Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct)
    {
        var topics = text.Topics();
        if (arguments.String("topic") is { } topic)
        {
            if (topic == PalettesTopic && text.Palettes() is { } palettes)
                return Task.FromResult(ToolResult.Text(palettes));
            if (McpText.IdPattern().IsMatch(topic) && text.Topic(topic) is { } guide)
                return Task.FromResult(ToolResult.Text(guide));
            var ids = topics.Select(t => t.Id).Concat(text.Palettes() != null ? [PalettesTopic] : []);
            throw new ToolFailure("unknownTopic", $"There's no topic {topic} (unknownTopic). The topics are: {string.Join(", ", ids)}.");
        }

        if (arguments.String("example") is { } example)
        {
            if (McpText.IdPattern().IsMatch(example) && text.Example(example) is { } content)
                return Task.FromResult(ToolResult.Text(content));
            var ids = text.Examples().Select(e => e.Id).ToList();
            throw new ToolFailure("unknownExample", ids.Count == 0
                ? $"There's no example {example} (unknownExample): there are no examples."
                : $"There's no example {example} (unknownExample). The examples are: {string.Join(", ", ids)}.");
        }

        var index = new StringBuilder("The design guide. Ask for a topic with get_design_guide topic, an example with example.\n\nTopics:\n");
        foreach (var t in topics)
            index.Append($"- {t.Id}{(t.Title.Length > 0 ? $": {t.Title}" : "")}\n");
        if (text.Palettes() != null)
            index.Append($"- {PalettesTopic}: colour palettes and type pairings\n");
        var examples = text.Examples();
        if (examples.Count > 0)
        {
            index.Append("\nExamples:\n");
            foreach (var e in examples)
                index.Append($"- {e.Id}{(e.Description != null ? $": {e.Description}" : "")}\n");
        }
        return Task.FromResult(ToolResult.Text(index.ToString().TrimEnd()));
    }
}
