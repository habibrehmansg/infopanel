using System.Text;
using System.Text.Json.Nodes;
using InfoPanel.Security;
using InfoPanel.Sensors;
using InfoPanel.Spec;

namespace InfoPanel.Mcp.Tools;

/// <summary>
/// <c>list_sensors</c>: the sensors (as <c>GET /api/sensors</c> and <c>/api/sensors/values</c>), each with its key,
/// name, group, unit, type, status and value; a table's value is its text (its first row), with its columns and format
/// but none of its rows; <c>query</c> keeps those whose key, name or group holds every word of it.
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
                ["type"] = SensorEntry.TypeName(state.Type),
                ["status"] = System.Text.Json.JsonSerializer.SerializeToNode(state.Status),
            };
            if (state.HasReading)
            {
                var reading = state.Reading;
                if (reading.ValueText != null || reading.ValueTable != null)
                    entry["value"] = reading.ValueText ?? "";
                else if (double.IsFinite(reading.ValueNow))
                    entry["value"] = Math.Round(reading.ValueNow, 3);
                // A table's columns and the layout its source suggests, so its columns can be set; no row: a table's
                // rows (a process list, say) stay out of an AI app beyond the first row's text above.
                if (reading.ValueTable is { } table)
                {
                    entry["columns"] = System.Text.Json.JsonSerializer.SerializeToNode(SensorTable.ColumnsOf(table), SpecJson.Options);
                    entry["format"] = reading.ValueTableFormat ?? "";
                }
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
