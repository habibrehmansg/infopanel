using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Security;

namespace InfoPanel.Mcp.Tools;

/// <summary>A tool failure the caller can act on: a result with <c>isError</c>, its <see cref="Code"/> named in the text.</summary>
public sealed class ToolFailure(string code, string text) : Exception(text)
{
    public string Code { get; } = code;
}

/// <summary>A tool's result: its content items and whether it is an error.</summary>
public sealed record ToolResult(JsonArray Content, bool IsError, string Outcome)
{
    public static ToolResult Text(string text, string outcome = "ok") =>
        new([new JsonObject { ["type"] = "text", ["text"] = text }], false, outcome);

    public static ToolResult Error(ToolFailure failure) =>
        new([new JsonObject { ["type"] = "text", ["text"] = failure.Message }], true, failure.Code);

    public JsonObject ToJson() => new() { ["content"] = Content.DeepClone(), ["isError"] = IsError };
}

/// <summary>
/// One tool an AI app can call: its name and input schema (the engine's, public), the scope it acts with (<c>read</c> or
/// <c>edit</c>, never the session's), and what it does. Its description comes from the text layers.
/// </summary>
public abstract class McpTool
{
    public abstract string Name { get; }

    /// <summary>What the tool may do, as an integration token's scope: <see cref="ApiScope.Read"/> or <see cref="ApiScope.Edit"/>.</summary>
    public abstract ApiScope Scope { get; }

    /// <summary>A short title for clients that show one.</summary>
    public abstract string Title { get; }

    /// <summary>The input schema: an object schema of string and integer members, closed (<c>additionalProperties</c> false).</summary>
    public abstract JsonObject InputSchema { get; }

    public abstract Task<ToolResult> CallAsync(ToolArguments arguments, CancellationToken ct);

    /// <summary>The tool as <c>tools/list</c> lists it.</summary>
    public JsonObject Listing(string description) => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = description,
        ["inputSchema"] = InputSchema.DeepClone(),
        ["annotations"] = new JsonObject
        {
            ["title"] = Title,
            ["readOnlyHint"] = Scope == ApiScope.Read,
            ["openWorldHint"] = false,
        },
    };

    protected static JsonObject Schema(params (string Name, JsonObject Schema, bool Required)[] members) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(members.Select(m => KeyValuePair.Create(m.Name, (JsonNode?)m.Schema))),
        ["required"] = new JsonArray([.. members.Where(m => m.Required).Select(m => (JsonNode)JsonValue.Create(m.Name))]),
        ["additionalProperties"] = false,
    };

    protected static JsonObject String(string description, int maxLength = 256) =>
        new() { ["type"] = "string", ["description"] = description, ["minLength"] = 1, ["maxLength"] = maxLength };

    protected static JsonObject Integer(string description, int minimum, int maximum) =>
        new() { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum, ["maximum"] = maximum };

    protected static JsonObject Number(string description, double minimum, double maximum) =>
        new() { ["type"] = "number", ["description"] = description, ["minimum"] = minimum, ["maximum"] = maximum };

    protected static JsonObject Boolean(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    /// <summary>A JSON object argument (a scene, bindings): its content is checked by the engine as the API checks it.</summary>
    protected static JsonObject Object(string description) =>
        new() { ["type"] = "object", ["description"] = description };
}

/// <summary>
/// A call's arguments, checked against its tool's input schema before the tool runs: the members it knows, of their
/// types and within their bounds, the required ones present. Anything else is a <see cref="ToolFailure"/>
/// (<c>arguments</c>) naming each problem.
/// </summary>
public sealed class ToolArguments
{
    private readonly JsonElement _arguments;
    private readonly Dictionary<string, JsonNode> _objects;

    private ToolArguments(JsonElement arguments, Dictionary<string, JsonNode> objects)
    {
        _arguments = arguments;
        _objects = objects;
    }

    /// <exception cref="ToolFailure">The arguments don't fit the schema.</exception>
    public static ToolArguments Check(string tool, JsonObject schema, JsonElement arguments)
    {
        var problems = new List<string>();
        var objects = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            problems.Add("the arguments are not an object");
        }
        else
        {
            var properties = schema["properties"]!.AsObject();
            foreach (var member in arguments.EnumerateObject())
            {
                if (properties[member.Name] is not JsonObject property)
                {
                    problems.Add($"'{member.Name}' isn't one of its arguments ({string.Join(", ", properties.Select(p => p.Key))})");
                    continue;
                }

                var type = property["type"]!.GetValue<string>();
                if (type == "string")
                {
                    if (member.Value.ValueKind != JsonValueKind.String)
                        problems.Add($"'{member.Name}' must be text");
                    else if (member.Value.GetString()!.Length is var length
                        && (length < (property["minLength"]?.GetValue<int>() ?? 0) || length > (property["maxLength"]?.GetValue<int>() ?? int.MaxValue)))
                        problems.Add($"'{member.Name}' must be {property["minLength"]}–{property["maxLength"]} characters");
                }
                else if (type == "integer")
                {
                    var min = property["minimum"]!.GetValue<int>();
                    var max = property["maximum"]!.GetValue<int>();
                    if (member.Value.ValueKind != JsonValueKind.Number || !member.Value.TryGetDouble(out var number)
                        || number != Math.Floor(number) || number < min || number > max)
                        problems.Add($"'{member.Name}' must be a whole number from {min} to {max}");
                }
                else if (type == "number")
                {
                    var min = property["minimum"]!.GetValue<double>();
                    var max = property["maximum"]!.GetValue<double>();
                    if (member.Value.ValueKind != JsonValueKind.Number || !member.Value.TryGetDouble(out var number)
                        || !double.IsFinite(number) || number < min || number > max)
                        problems.Add($"'{member.Name}' must be a number from {Number(min)} to {Number(max)}");
                }
                else if (type == "boolean")
                {
                    if (member.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        problems.Add($"'{member.Name}' must be true or false");
                }
                else if (type == "object")
                {
                    if (ObjectOf(member.Value) is { } node)
                        objects[member.Name] = node;
                    else
                        problems.Add($"'{member.Name}' must be a JSON object");
                }
            }

            foreach (var required in schema["required"]!.AsArray().Select(r => r!.GetValue<string>()))
            {
                if (!arguments.TryGetProperty(required, out _))
                    problems.Add($"'{required}' is required");
            }
        }

        if (problems.Count > 0)
            throw new ToolFailure("arguments", $"{tool} can't take these arguments (arguments): {string.Join("; ", problems)}.");
        return new ToolArguments(arguments, objects);
    }

    /// <summary>
    /// An object argument: an object, or text holding one (some AI apps send nested JSON as a string), read at the
    /// format's depth; null for anything else.
    /// </summary>
    private static JsonNode? ObjectOf(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.String))
            return null;
        try
        {
            var text = value.ValueKind == JsonValueKind.Object ? value.GetRawText() : value.GetString()!;
            return JsonNode.Parse(text, documentOptions: InfoPanel.Spec.SpecJson.DocumentOptions) is JsonObject parsed ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool Has(string name) => _arguments.TryGetProperty(name, out _);

    public string? String(string name) =>
        _arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public int? Integer(string name) =>
        _arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (int)value.GetDouble() : null;

    public double? Double(string name) =>
        _arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public bool? Boolean(string name) =>
        _arguments.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>An object argument as a new node (a copy for each call), or null when it wasn't given.</summary>
    public JsonObject? Object(string name) => _objects.TryGetValue(name, out var node) ? node.DeepClone().AsObject() : null;

    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
