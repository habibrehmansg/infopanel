using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Desktop;
using InfoPanel.Documents;
using InfoPanel.Hosting;
using InfoPanel.Mcp.Text;
using InfoPanel.Output;
using InfoPanel.Sensors;
using InfoPanel.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace InfoPanel.Mcp.Tools;

/// <summary>
/// The tools AI apps call, run inside the engine on the same code paths as the API (the workspace, the render service,
/// the sensor hub), each with the scope it declares, never the session's: the list (names and input schemas; their
/// wording is the configured service's), and calls, every failure a result with <c>isError</c>. Each call is logged with
/// its tool, its profile, its outcome and how long it took, never its arguments. It is the only thing a call from elsewhere
/// reaches (<see cref="IRelayToolHost"/>, docs/2.0/relay.md): the same tools, with the call's context (<see cref="McpCall"/>):
/// its cutoff asked under every write's lock, web images refused, its answer held to the AI app's <c>maxChars</c>, and its log
/// line marked "from elsewhere".
/// </summary>
public sealed class McpToolHost : IRelayToolHost
{
    /// <summary>The tools, in the order they are listed.</summary>
    public static IReadOnlyList<string> ToolNames { get; } =
    [
        "list_profiles", "get_profile", "list_sensors", "list_fonts", "render_preview",
        "create_profile", "add_item", "add_items", "update_item", "remove_item", "add_asset", "show_on_desktop",
    ];

    /// <summary>The tools a call from elsewhere may bring a design to (docs/2.0/relay.md, "Designs").</summary>
    public static IReadOnlyList<string> DesignTools { get; } = ["add_item", "update_item", "add_items"];

    private readonly IReadOnlyDictionary<string, McpTool> _tools;
    private readonly McpText _text;
    private readonly ILogger _logger;

    public McpToolHost(Workspace workspace, RenderService render, SensorHub sensors, EngineOptions options, IDesktopPlatform desktop,
        ILogger<McpToolHost> logger)
    {
        _logger = logger;
        _text = new McpText(logger);
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

    /// <summary>The preview checks' thresholds the tools read.</summary>
    public McpText Text => _text;

    /// <summary>
    /// A test seam: called on the calling thread at each step of a change (<c>preflight</c> once a design's files and
    /// deps are checked, before anything is built or stored; <c>read</c> once the document is read,
    /// <c>write</c> and then <c>locking</c> just before a write goes to take its lock, <c>retry</c> after a conflict,
    /// before it reads again, <c>committed</c> once the commit applied, before the save; for a <c>read</c> frame,
    /// <c>reading</c> just before it reads and <c>read</c> once it has), so a test can slip another client's change, or a
    /// cutoff, in between.
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
        new CreateProfileTool(changes!),
        new AddItemTool(changes!),
        new AddItemsTool(changes!),
        new UpdateItemTool(changes!),
        new RemoveItemTool(changes!),
        new AddAssetTool(changes!),
        new ShowOnDesktopTool(changes!, noWindow),
    ];

    /// <summary>The tools as the engine declares them (for tests): names, scopes, schemas.</summary>
    public IReadOnlyCollection<McpTool> All => [.. ToolNames.Select(n => _tools[n])];

    /// <summary>
    /// The list with no engine behind it: the committed snapshot's source. Built from the same tools, so it lists exactly
    /// what an engine would.
    /// </summary>
    public static JsonObject BuiltInList() => new McpToolHost(new McpText(NullLogger.Instance)).List();

    public Task<JsonObject> ListAsync(CancellationToken ct) => Task.FromResult(List());

    private JsonObject List() => new()
    {
        ["tools"] = new JsonArray([.. ToolNames.Select(n => (JsonNode)_tools[n].Listing())]),
    };

    public async Task<JsonObject> CallAsync(string tool, JsonElement arguments, CancellationToken ct) =>
        (await RunAsync(tool, arguments, McpCall.ThisComputer, ct).ConfigureAwait(false)).ToJson();

    async Task<RelayToolAnswer> IRelayToolHost.CallAsync(string tool, JsonElement arguments, RelayCallContext context, CancellationToken ct)
    {
        var result = await RunAsync(tool, arguments, McpCall.Elsewhere(context), ct).ConfigureAwait(false);
        return new RelayToolAnswer(result.ToJson(), result.Outcome == Cancelled) { Stale = result.Outcome == ProfileChanges.Stale };
    }

    Task<RelayToolAnswer> IRelayToolHost.ReadAsync(string profile, string item, bool detail, RelayCallContext context, CancellationToken ct)
    {
        // A read is get_profile's work (a read tool's, on the tool host), answered as the item frame. Its cutoff (cancel,
        // the connection's close, its deadline) is asked before it reads and again before it answers: tripped, the answer
        // is cancelled. Its errors are held to the AI app's maxChars as a call's are.
        var watch = Stopwatch.StartNew();
        RelayToolAnswer answer;
        string outcome;
        try
        {
            if (_tools["get_profile"] is not GetProfileTool reader)
                throw new ToolFailure("unsupported", "This computer doesn't read items for the service (unsupported).");
            StepHook?.Invoke("reading");
            JsonObject? read = null;
            if (!context.Cutoff.IsTripped)
            {
                read = reader.ReadItem(profile, item, detail, context.MaxChars);
                StepHook?.Invoke("read");
            }
            if (read is null || context.Cutoff.IsTripped)
            {
                answer = new RelayToolAnswer(ToolResult.Error(new ToolFailure(Cancelled,
                    $"The read was stopped (cancelled: {context.Cutoff.Reason ?? "cancel"}).")).ToJson(), Cancelled: true);
                outcome = Cancelled;
            }
            else
            {
                answer = new RelayToolAnswer(new JsonObject(), Cancelled: false) { Item = read };
                outcome = "item";
            }
        }
        catch (ToolFailure failure)
        {
            (answer, outcome) = Failed(ToolResult.Error(failure), context.MaxChars);
        }
        catch (IOException ex)
        {
            (answer, outcome) = Failed(ToolResult.Error(new ToolFailure("io", $"InfoPanel couldn't read its data folder (io: {ex.Message}). Try again.")), context.MaxChars);
        }

        _logger.LogInformation("AI app read from elsewhere on {Profile}: {Outcome} in {Milliseconds} ms", profile, outcome, watch.ElapsedMilliseconds);
        return Task.FromResult(answer);
    }

    /// <summary>A read's error result, held to <paramref name="maxChars"/> (<see cref="Fit"/>) as a call's is, and its outcome.</summary>
    private static (RelayToolAnswer, string) Failed(ToolResult result, int? maxChars)
    {
        if (maxChars is { } chars)
            result = Fit(result, chars);
        return (new RelayToolAnswer(result.ToJson(), Cancelled: false), result.Outcome);
    }

    /// <summary>The outcome of a call whose change was stopped before it was made (<see cref="ChangeCutOffException"/>).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Runs <paramref name="tool"/> for <paramref name="call"/>: every failure a result, the call logged.</summary>
    internal async Task<ToolResult> RunAsync(string tool, JsonElement arguments, McpCall call, CancellationToken ct)
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
                // A design comes only from elsewhere (McpCall.Elsewhere), and only to a tool that takes one; it stands for
                // the arguments it gives (the scene and box), which the relay's frame left out.
                if (call.BringsDesign && !DesignTools.Contains(tool))
                {
                    throw new ToolFailure("design",
                        $"{tool} doesn't take a design (design): only {string.Join(", ", DesignTools)} does. Nothing was stored or changed.");
                }
                // add_items takes a design per item (designs), the others one design.
                if (tool == AddItemsTool.ToolName ? call.Design is not null : call.Designs is not null)
                {
                    throw new ToolFailure("design", tool == AddItemsTool.ToolName
                        ? "add_items takes designs, one per item in order, not a design (design). Nothing was stored or changed."
                        : $"{tool} takes a design, not designs (design): only add_items takes one per item. Nothing was stored or changed.");
                }
                // Files are a design's: without one, none are checked or stored, so the call is refused before it runs.
                if (!call.HasDesign && call.Files is { Count: > 0 })
                    throw new ToolFailure("design", "Files come only with a design (design): this call brought files and no design. Nothing was stored or changed.");

                var given = call.Design is null ? null : RelayFrames.DesignGives;
                result = await found.CallAsync(ToolArguments.Check(tool, found.InputSchema, arguments, given).For(call), ct).ConfigureAwait(false);
            }
            catch (ToolFailure failure)
            {
                result = ToolResult.Error(failure);
            }
            catch (ChangeCutOffException cut)
            {
                result = ToolResult.Error(new ToolFailure(Cancelled,
                    $"The change was stopped before it was made (cancelled: {cut.Reason}): nothing was changed."));
            }
            catch (IOException ex)
            {
                result = ToolResult.Error(new ToolFailure("io", $"InfoPanel couldn't read or write its data folder (io: {ex.Message}). Try again."));
            }
        }

        if (call.MaxChars is { } maxChars)
            result = Fit(result, maxChars);

        // The profile as named (an id or a name the user gave it): never the other arguments.
        if (call.FromElsewhere)
        {
            _logger.LogInformation("AI app tool {Tool} from elsewhere on {Profile}: {Outcome} in {Milliseconds} ms",
                tool, profile ?? "-", result.Outcome, watch.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation("AI app tool {Tool} on {Profile}: {Outcome} in {Milliseconds} ms",
                tool, profile ?? "-", result.Outcome, watch.ElapsedMilliseconds);
        }

        return result;
    }

    /// <summary>
    /// <paramref name="result"/> held to <paramref name="maxChars"/> characters in all, as an AI app that takes no more
    /// needs, a picture counted by its data as sent: an answer within it is left as it is. Otherwise its pictures are kept
    /// whole when they leave room (<see cref="TextRoom"/>) for the text, which is cut to the rest; when they don't, the
    /// answer is an error saying the limit and how to ask for a smaller picture. Each text part is cut at a line where it
    /// can be (whole lines of a listing kept), and the cut says how much was left out and how to ask for less.
    /// </summary>
    internal static ToolResult Fit(ToolResult result, int maxChars)
    {
        if (result.Content.Sum(Characters) <= maxChars)
            return result;

        var pictures = result.Content.Where(p => p?["type"]?.GetValue<string>() != "text").Sum(Characters);
        if (pictures > 0 && pictures > maxChars - TextRoom)
        {
            return ToolResult.Error(new ToolFailure("maxChars",
                $"The picture is {pictures} characters as sent, more than this AI app takes ({maxChars} in all), so none is shown. " +
                "Ask for a smaller one: render_preview with a smaller width."));
        }

        var left = maxChars - pictures;
        var content = new JsonArray();
        foreach (var part in result.Content)
        {
            if (part?["type"]?.GetValue<string>() != "text")
            {
                content.Add(part?.DeepClone());
                continue;
            }

            var text = part["text"]!.GetValue<string>();
            if (left == 0)
                continue;
            if (text.Length <= left)
            {
                left -= text.Length;
                content.Add(part.DeepClone());
                continue;
            }

            var note = $"\n… and {text.Length} characters in all, more than this AI app takes ({maxChars}). Ask for less: a query, a limit or one item.";
            var room = Math.Max(0, left - note.Length);
            var cut = text.LastIndexOf('\n', Math.Max(0, Math.Min(room, text.Length) - 1));
            var kept = cut > room / 2 ? text[..cut] : text[..Math.Min(room, text.Length)];
            content.Add(new JsonObject { ["type"] = "text", ["text"] = kept + note });
            left = 0;
        }

        return result with { Content = content };
    }

    /// <summary>What a part of an answer costs an AI app in characters: its text, or a picture's data as sent.</summary>
    private static int Characters(JsonNode? part) => part?["type"]?.GetValue<string>() switch
    {
        "text" => part!["text"]!.GetValue<string>().Length,
        "image" => part!["data"]?.GetValue<string>().Length ?? 0,
        _ => part?.ToJsonString().Length ?? 0,
    };

    /// <summary>The least room pictures must leave for the text, when it is cut: its note and a few lines.</summary>
    internal const int TextRoom = 300;
}
