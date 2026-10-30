using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Mcp.Text;
using InfoPanel.Output;
using InfoPanel.Sensors;
using Microsoft.Extensions.Logging.Abstractions;

namespace InfoPanel.Mcp.Tools;

/// <summary>
/// The tools AI apps call, run inside the engine on the same code paths as the API (the workspace, the render service,
/// the sensor hub), each with the scope it declares, never the session's. It answers the local channel
/// (<see cref="ILocalChannelHandler"/>): the list with each tool's wording from the text layers (<see cref="McpText"/>),
/// and calls, every failure a result with <c>isError</c>. Each call is logged with its tool, its profile, its outcome and
/// how long it took, never its arguments.
/// </summary>
public sealed class McpToolHost : ILocalChannelHandler
{
    /// <summary>The tools, in the order they are listed.</summary>
    public static IReadOnlyList<string> ToolNames { get; } =
    [
        "list_profiles", "get_profile", "list_sensors", "list_fonts", "render_preview", "get_design_guide",
        "create_profile", "add_item", "update_item", "remove_item", "add_asset", "show_on_desktop",
    ];

    private readonly IReadOnlyDictionary<string, McpTool> _tools;
    private readonly McpText _text;
    private readonly ILogger _logger;

    public McpToolHost(Workspace workspace, RenderService render, SensorHub sensors, EngineOptions options, IDesktopPlatform desktop,
        ILogger<McpToolHost> logger)
    {
        _logger = logger;
        _text = new McpText(options.McpText, ToolNames.ToHashSet(), logger);
        var changes = new ProfileChanges(workspace, () => StepHook);
        // Why no desktop window opens on this engine, or null when windows open (the window manager's Enabled).
        var noWindow = options.NoWindows ? ShowOnDesktopTool.WindowsOff : desktop.IsSupported ? null : ShowOnDesktopTool.Unsupported;
        _tools = Tools(workspace, render, sensors, _text, changes, noWindow).ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>The tools with no engine behind them: only listed, never called (<see cref="BuiltInList"/>).</summary>
    private McpToolHost(McpText text)
    {
        _logger = NullLogger.Instance;
        _text = text;
        _tools = Tools(null, null, null, text, null, null).ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    /// <summary>The text layers the tools read.</summary>
    public McpText Text => _text;

    /// <summary>
    /// A test seam: called on the calling thread at each step of a change (<c>read</c> once the document is read,
    /// <c>retry</c> after a conflict, before it reads again, <c>committed</c> once the commit applied, before the save),
    /// so a test can slip another client's change in between.
    /// </summary>
    internal Action<string>? StepHook { get; set; }

    private static IReadOnlyList<McpTool> Tools(Workspace? workspace, RenderService? render, SensorHub? sensors, McpText text,
        ProfileChanges? changes, string? noWindow) =>
    [
        new ListProfilesTool(workspace!),
        new GetProfileTool(workspace!),
        new ListSensorsTool(sensors!),
        new ListFontsTool(),
        new RenderPreviewTool(workspace!, render!, text),
        new GetDesignGuideTool(text),
        new CreateProfileTool(changes!),
        new AddItemTool(changes!),
        new UpdateItemTool(changes!),
        new RemoveItemTool(changes!),
        new AddAssetTool(changes!),
        new ShowOnDesktopTool(changes!, noWindow),
    ];

    /// <summary>The tools as the engine declares them (for tests): names, scopes, schemas.</summary>
    public IReadOnlyCollection<McpTool> All => [.. ToolNames.Select(n => _tools[n])];

    /// <summary>
    /// The list with the built-in wording only: what the front answers while no engine runs. Built from the same tools,
    /// so it lists exactly what an engine would.
    /// </summary>
    public static JsonObject BuiltInList() => BuiltInList(null, NullLogger.Instance);

    /// <summary>
    /// <see cref="BuiltInList()"/> with the text of the live folder <paramref name="live"/> over the built-in wording, read
    /// through the same layers as an engine reads it (<see cref="McpText"/>: containment, caps, a bad file skipped and
    /// logged): what the front answers before an engine runs, from the folder the engine would start with.
    /// </summary>
    public static JsonObject BuiltInList(string? live, ILogger logger) =>
        new McpToolHost(new McpText(live, ToolNames.ToHashSet(), logger)).List();

    public Task<JsonObject> ListAsync(CancellationToken ct) => Task.FromResult(List());

    private JsonObject List()
    {
        var wording = _text.Tools();
        return new JsonObject
        {
            ["tools"] = new JsonArray([.. ToolNames.Select(n => (JsonNode)_tools[n].Listing(wording.Descriptions.GetValueOrDefault(n, "")))]),
            ["instructions"] = wording.Instructions,
        };
    }

    public async Task<JsonObject> CallAsync(string tool, JsonElement arguments, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        ToolResult result;
        var profile = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
        if (!_tools.TryGetValue(tool, out var found))
        {
            result = ToolResult.Error(new ToolFailure("unknownTool", $"There's no tool {tool} (unknownTool). The tools are: {string.Join(", ", ToolNames)}."));
        }
        else
        {
            try
            {
                result = await found.CallAsync(ToolArguments.Check(tool, found.InputSchema, arguments), ct).ConfigureAwait(false);
            }
            catch (ToolFailure failure)
            {
                result = ToolResult.Error(failure);
            }
            catch (IOException ex)
            {
                result = ToolResult.Error(new ToolFailure("io", $"InfoPanel couldn't read or write its data folder (io: {ex.Message}). Try again."));
            }
        }

        // The profile as named (an id or a name the user gave it): never the other arguments.
        _logger.LogInformation("AI app tool {Tool} on {Profile}: {Outcome} in {Milliseconds} ms",
            tool, profile ?? "-", result.Outcome, watch.ElapsedMilliseconds);
        return result.ToJson();
    }
}
