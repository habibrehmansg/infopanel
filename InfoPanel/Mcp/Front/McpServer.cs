using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Hosting;

namespace InfoPanel.Mcp.Front;

/// <summary>What the MCP server relays to: the engine on the data folder, or a test's stand-in.</summary>
public interface IMcpBackend
{
    /// <summary>The engine's <c>{"tools","instructions"}</c> when it is reachable now; null otherwise. Never starts it.</summary>
    Task<JsonObject?> TryListAsync(CancellationToken ct);

    /// <summary>The tools and instructions this executable carries, for when no engine answers.</summary>
    JsonObject BuiltInList();

    /// <summary>A tool call's result, <c>{"content","isError"}</c>: every failure is a result too.</summary>
    Task<JsonObject> CallAsync(string tool, JsonElement arguments, CancellationToken ct);
}

/// <summary>
/// MCP over stdin and stdout, written by hand (tools are the only feature): newline-delimited JSON-RPC 2.0 messages.
/// <list type="bullet">
/// <item><c>initialize</c>: a protocol version in <see cref="ProtocolVersions"/> is echoed, any other answered with the
/// newest; capabilities <c>tools.listChanged</c>; <c>serverInfo</c>; <c>instructions</c> (the engine's when it is
/// reachable, else the built-in).</item>
/// <item><c>ping</c>; <c>tools/list</c> (the engine's when reachable, else the built-in: the tools are always listed);
/// <c>tools/call</c>, answered as each call ends, so a <c>ping</c> is never held up by one; <c>notifications/cancelled</c>
/// drops the answer to the request it names; other notifications are ignored.</item>
/// <item>Errors: unreadable JSON −32700 with a null id, not a request (a member given twice included) −32600, unknown
/// method −32601, <c>initialize</c> or <c>tools/call</c> params of the wrong shape −32602. Every shape is checked before
/// it is read, and a message nothing foresaw is answered −32603 and logged on stderr: no message ends the server. A tool's own failure
/// is a result with <c>isError</c>, never a protocol error.</item>
/// <item>After a call, when the engine's tool list differs from the one last listed (a restart, a live file edited), it
/// sends <c>notifications/tools/list_changed</c>.</item>
/// </list>
/// Nothing but these messages is written to <paramref name="output"/>; diagnostics go to <paramref name="log"/>.
/// </summary>
public sealed class McpServer(Stream input, Stream output, IMcpBackend backend, TextWriter log)
{
    /// <summary>The protocol versions this server speaks, oldest first.</summary>
    public static IReadOnlyList<string> ProtocolVersions { get; } = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    public const string ServerName = "infopanel";

    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _cancelled = new();
    private readonly ConcurrentDictionary<Task, byte> _calls = new();
    private string? _listed;

    /// <summary>Reads requests until stdin ends, then waits briefly for calls still running.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var reader = new LineReader(input, LocalChannel.MaxLineBytes);
        while (true)
        {
            byte[]? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (LineTooLongException)
            {
                await WriteAsync(Error(null, -32700, $"A message is over {LocalChannel.MaxLineBytes} bytes."), ct).ConfigureAwait(false);
                return;
            }

            if (line == null)
                break;
            if (line.Length == 0 || line.All(b => b is (byte)' ' or (byte)'\t'))
                continue;
            try
            {
                await HandleAsync(line, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
            {
                // A message no check foresaw: said on stderr, and the server goes on (one bad message never ends it).
                await log.WriteLineAsync($"infopanel-mcp: a message couldn't be handled: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
                await WriteAsync(Error(null, -32603, "Internal error: the message couldn't be handled."), ct).ConfigureAwait(false);
            }
        }

        try
        {
            await Task.WhenAll(_calls.Keys).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    private async Task HandleAsync(byte[] line, CancellationToken ct)
    {
        JsonNode? message;
        try
        {
            // Strict, so no member read later can be one given twice (a JsonObject throws when it meets one).
            message = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            await WriteAsync(IsJson(line)
                ? Error(null, -32600, "Invalid request: a member is given twice.")
                : Error(null, -32700, "Parse error: the message isn't JSON."), ct).ConfigureAwait(false);
            return;
        }

        if (message is not JsonObject request)
        {
            await WriteAsync(Error(null, -32600, message is JsonArray ? "Batches aren't supported." : "A message is a JSON object."), ct).ConfigureAwait(false);
            return;
        }

        var hasId = request.TryGetPropertyValue("id", out var idNode);
        var id = idNode?.DeepClone();
        if (request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
        {
            // A response (this server sends no requests) or nonsense: answer only what has an id and no result.
            if (hasId && !request.ContainsKey("result") && !request.ContainsKey("error"))
                await WriteAsync(Error(id, -32600, "A request names its method."), ct).ConfigureAwait(false);
            return;
        }

        if (!hasId)
        {
            if (method == "notifications/cancelled" && request["params"] is JsonObject cancel && cancel["requestId"] is { } cancelled)
                _cancelled[Key(cancelled)] = 0;
            return; // notifications are never answered, whatever their params
        }

        switch (method)
        {
            case "initialize":
                // Its params are an object (or absent); a member of the wrong kind is ignored, as an unknown version is.
                if (request["params"] is { } initParams && initParams is not JsonObject)
                {
                    await WriteAsync(Error(id, -32602, "initialize takes a params object."), ct).ConfigureAwait(false);
                    break;
                }
                await WriteAsync(Result(id, await InitializeAsync(request["params"] as JsonObject, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
                break;
            case "ping":
                await WriteAsync(Result(id, new JsonObject()), ct).ConfigureAwait(false);
                break;
            case "tools/list":
                var list = await ListAsync(ct).ConfigureAwait(false);
                await WriteAsync(Result(id, new JsonObject { ["tools"] = list["tools"]?.DeepClone() ?? new JsonArray() }), ct).ConfigureAwait(false);
                break;
            case "tools/call":
                var parameters = request["params"] as JsonObject;
                if (parameters?["name"] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)
                    || parameters["arguments"] is { } given && given is not JsonObject)
                {
                    await WriteAsync(Error(id, -32602, "tools/call takes a tool name and an arguments object."), ct).ConfigureAwait(false);
                    break;
                }

                var arguments = JsonSerializer.SerializeToElement(parameters["arguments"] ?? new JsonObject());
                var call = CallAsync(id, name, arguments, ct);
                _calls[call] = 0;
                _ = call.ContinueWith(t => _calls.TryRemove(t, out _), TaskScheduler.Default);
                break;
            default:
                await WriteAsync(Error(id, -32601, $"Method not found: {method}."), ct).ConfigureAwait(false);
                break;
        }
    }

    private static bool IsJson(byte[] line)
    {
        try
        {
            using var _ = JsonDocument.Parse(line);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<JsonObject> InitializeAsync(JsonObject? parameters, CancellationToken ct)
    {
        var asked = parameters?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var version = asked != null && ProtocolVersions.Contains(asked) ? asked : ProtocolVersions[^1];
        var list = await backend.TryListAsync(ct).ConfigureAwait(false) ?? backend.BuiltInList();
        var result = new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "InfoPanel", ["version"] = Api.ReadEndpoints.EngineVersion() },
        };
        if (list["instructions"] is JsonValue instructions && instructions.TryGetValue<string>(out var text) && text.Length > 0)
            result["instructions"] = text;
        return result;
    }

    private async Task<JsonObject> ListAsync(CancellationToken ct)
    {
        var list = await backend.TryListAsync(ct).ConfigureAwait(false) ?? backend.BuiltInList();
        _listed = Fingerprint(list);
        return list;
    }

    private async Task CallAsync(JsonNode? id, string name, JsonElement arguments, CancellationToken ct)
    {
        JsonObject result;
        try
        {
            result = await backend.CallAsync(name, arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await log.WriteLineAsync($"infopanel-mcp: {name} failed: {ex}").ConfigureAwait(false);
            result = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"InfoPanel's AI connection hit an error ({ex.GetType().Name}). Ask again." }),
                ["isError"] = true,
            };
        }

        if (_cancelled.TryRemove(Key(id), out _))
            return;
        await WriteAsync(Result(id, new JsonObject
        {
            ["content"] = result["content"]?.DeepClone() ?? new JsonArray(),
            ["isError"] = result["isError"] is JsonValue e && e.TryGetValue<bool>(out var isError) && isError,
        }), ct).ConfigureAwait(false);

        // The tools may read differently now (an engine started, a live file edited): say so once it is noticed.
        if (_listed != null && await backend.TryListAsync(ct).ConfigureAwait(false) is { } now)
        {
            var fingerprint = Fingerprint(now);
            if (fingerprint != _listed)
            {
                _listed = fingerprint;
                await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" }, ct).ConfigureAwait(false);
            }
        }
    }

    private static string Fingerprint(JsonObject list) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(list["tools"] ?? new JsonArray())));

    private static string Key(JsonNode? id) => id?.ToJsonString() ?? "null";

    private static JsonObject Result(JsonNode? id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var line = LocalChannel.Line(message);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(line, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }
}
