using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using InfoPanel.Spec;

namespace InfoPanel.Service;

/// <summary>A frame the service's relay sent, as the engine admits it (docs/2.0/relay.md).</summary>
public abstract record RelayFrame
{
    /// <summary><c>welcome</c>: the relay accepted the connection.</summary>
    public sealed record Welcome : RelayFrame;

    /// <summary><c>time</c>: the relay's clock (<paramref name="Now"/>, ms since 1970 UTC) in answer to the reading <paramref name="Nonce"/>.</summary>
    public sealed record Time(string Nonce, long Now) : RelayFrame;

    /// <summary>
    /// <c>call</c>: a tool call, <paramref name="SentAt"/> on the relay's clock (ms since 1970 UTC); <paramref name="MaxChars"/>
    /// the most text the AI app takes, or null.
    /// </summary>
    public sealed record Call(string Id, string Tool, JsonElement Arguments, long SentAt, int? MaxChars) : RelayFrame
    {
        /// <summary><c>deadline</c>: the latest its change may be made, on the relay's clock (ms since 1970 UTC), or null.</summary>
        public long? Deadline { get; init; }

        /// <summary><c>design</c>: a design the service produced (<c>layout</c>, <c>source</c>, <c>scene</c>, <c>origin</c>), or null.</summary>
        public JsonElement? Design { get; init; }

        /// <summary><c>files</c>: the texts the design needs stored, or null.</summary>
        public IReadOnlyList<string>? Files { get; init; }
    }

    /// <summary><c>cancel</c>: the relay withdrew the call <paramref name="Id"/>.</summary>
    public sealed record Cancel(string Id) : RelayFrame;

    /// <summary>A frame the engine doesn't dispatch: answered <c>error</c> with <paramref name="Code"/>, and with <paramref name="Id"/> when it had a usable one.</summary>
    public sealed record Refused(string Code, string? Id) : RelayFrame;
}

/// <summary>
/// The frames of the connection AI apps elsewhere use (docs/2.0/relay.md): text frames of JSON, each an object with a
/// <c>type</c>, at most <see cref="MaxFrameBytes"/> as received and <see cref="MaxDepth"/> levels deep. The engine
/// dispatches only <c>welcome</c>, <c>time</c>, <c>call</c> and <c>cancel</c>; any other type is answered
/// <c>{"type":"error","code":"unsupported"}</c> (with the frame's <c>id</c> when it has one) and nothing closes, so the
/// relay can add types without a protocol change.
/// </summary>
public static class RelayFrames
{
    /// <summary>The protocol the engine speaks, in its <c>hello</c>.</summary>
    public const int Protocol = 1;

    /// <summary>The most a frame takes, either way, counted as it arrives (9 MiB).</summary>
    public const int MaxFrameBytes = 9 * 1024 * 1024;

    /// <summary>How deeply a frame is read and written: the formats' depth and an envelope, as on the local channel (272).</summary>
    public const int MaxDepth = SpecJson.SchemaDepth + 16;

    /// <summary>The least and most <c>maxChars</c> the engine takes; a value outside is brought within.</summary>
    public const int MinMaxChars = 1_000;

    /// <summary>The latest <c>sentAt</c> the engine takes: the last millisecond a time holds (9999-12-31, UTC).</summary>
    public static readonly long MaxSentAt = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    /// <summary>The longest call id and tool name the engine takes.</summary>
    public const int MaxIdLength = 64;

    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = MaxDepth };

    private static readonly JsonSerializerOptions WriteOptions = new() { MaxDepth = MaxDepth };

    /// <summary>What a text frame holds, as the engine admits it: never throws.</summary>
    public static RelayFrame Read(ReadOnlyMemory<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, ReadOptions);
        }
        catch (JsonException)
        {
            return new RelayFrame.Refused("malformed", null);
        }

        using (document)
        {
            // JSON can hold text that isn't text: an escaped lone surrogate reads as JSON, then fails to decode (an
            // InvalidOperationException) wherever a name or string is read. Anywhere in the frame, a member no type reads
            // included, that is malformed, with the frame's id when the id itself decoded: every name and string is
            // decoded before the frame is dispatched.
            var root = document.RootElement;
            string? id;
            try
            {
                id = root.ValueKind == JsonValueKind.Object ? IdOf(root) : null;
            }
            catch (InvalidOperationException)
            {
                return new RelayFrame.Refused("malformed", null);
            }

            try
            {
                Decode(root);
                return Admit(root, id);
            }
            catch (InvalidOperationException)
            {
                return new RelayFrame.Refused("malformed", id);
            }
        }
    }

    private static RelayFrame Admit(JsonElement root, string? id)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return new RelayFrame.Refused("malformed", null);
        switch (type.GetString())
        {
            case "welcome":
                return new RelayFrame.Welcome();
            case "time":
                if (root.TryGetProperty("nonce", out var nonce) && nonce.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("now", out var now) && now.ValueKind == JsonValueKind.Number && now.TryGetInt64(out var ms) && ms > 0)
                {
                    return new RelayFrame.Time(nonce.GetString()!, ms);
                }

                return new RelayFrame.Refused("malformed", null);
            case "cancel":
                return id is null ? new RelayFrame.Refused("malformed", null) : new RelayFrame.Cancel(id);
            case "call":
                return CallOf(root, id);
            default:
                return new RelayFrame.Refused("unsupported", id);
        }
    }

    private static RelayFrame CallOf(JsonElement root, string? id)
    {
        if (id is null)
            return new RelayFrame.Refused("malformed", null);
        if (!root.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.String || tool.GetString()!.Length is 0 or > MaxIdLength)
            return new RelayFrame.Refused("malformed", id);
        if (!root.TryGetProperty("sentAt", out var sent) || sent.ValueKind != JsonValueKind.Number || !sent.TryGetInt64(out var sentAt) || sentAt <= 0 || sentAt > MaxSentAt)
            return new RelayFrame.Refused("malformed", id);

        JsonElement arguments;
        if (!root.TryGetProperty("arguments", out var given) || given.ValueKind == JsonValueKind.Null)
            arguments = EmptyObject();
        else if (given.ValueKind == JsonValueKind.Object)
            arguments = given.Clone(); // Every name and string in them decodes (Read), so no tool meets one that doesn't.
        else
            return new RelayFrame.Refused("malformed", id);

        int? maxChars = null;
        if (root.TryGetProperty("maxChars", out var max) && max.ValueKind != JsonValueKind.Null)
        {
            if (max.ValueKind != JsonValueKind.Number || !max.TryGetInt64(out var chars) || chars <= 0)
                return new RelayFrame.Refused("malformed", id);
            maxChars = (int)Math.Clamp(chars, MinMaxChars, MaxFrameBytes);
        }

        // The latest its change may be made, on the relay's clock: a time, as sentAt is (the client takes the earlier of it
        // and sentAt + 60 s).
        long? deadline = null;
        if (root.TryGetProperty("deadline", out var until) && until.ValueKind != JsonValueKind.Null)
        {
            if (until.ValueKind != JsonValueKind.Number || !until.TryGetInt64(out var at) || at <= 0 || at > MaxSentAt)
                return new RelayFrame.Refused("malformed", id);
            deadline = at;
        }

        // A design the service produced, and the texts it needs stored: their shape only (the tool host checks the rest).
        JsonElement? design = null;
        if (root.TryGetProperty("design", out var shape) && shape.ValueKind != JsonValueKind.Null)
        {
            if (!IsDesign(shape))
                return new RelayFrame.Refused("malformed", id);
            // The design is the item's scene and box: arguments that give them too are a relay's mistake.
            foreach (var member in DesignGives)
            {
                if (arguments.TryGetProperty(member, out _))
                    return new RelayFrame.Refused("malformed", id);
            }

            design = shape.Clone();
        }

        List<string>? files = null;
        if (root.TryGetProperty("files", out var texts) && texts.ValueKind != JsonValueKind.Null)
        {
            if (texts.ValueKind != JsonValueKind.Array)
                return new RelayFrame.Refused("malformed", id);
            files = [];
            foreach (var text in texts.EnumerateArray())
            {
                if (text.ValueKind != JsonValueKind.String)
                    return new RelayFrame.Refused("malformed", id);
                files.Add(text.GetString()!);
            }
        }

        return new RelayFrame.Call(id, tool.GetString()!, arguments, sentAt, maxChars) { Deadline = deadline, Design = design, Files = files };
    }

    /// <summary>The members of a call's arguments a design stands for: with a design, the arguments don't give them.</summary>
    public static IReadOnlyList<string> DesignGives { get; } = ["scene", "width", "height"];

    /// <summary>
    /// A design's shape: an object with a <c>source</c> and a <c>scene</c>, each an object, and an optional <c>layout</c> and
    /// <c>origin</c>, each an object when given.
    /// </summary>
    private static bool IsDesign(JsonElement design) =>
        design.ValueKind == JsonValueKind.Object
        && design.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
        && design.TryGetProperty("scene", out var scene) && scene.ValueKind == JsonValueKind.Object
        && (!design.TryGetProperty("layout", out var layout) || layout.ValueKind == JsonValueKind.Object)
        && (!design.TryGetProperty("origin", out var origin) || origin.ValueKind == JsonValueKind.Object);

    /// <summary>Reads every name and string in <paramref name="element"/> (at most <see cref="MaxDepth"/> deep, as parsed).</summary>
    /// <exception cref="InvalidOperationException">One doesn't decode (a lone surrogate).</exception>
    private static void Decode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    _ = property.Name;
                    Decode(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Decode(item);
                break;
            case JsonValueKind.String:
                _ = element.GetString();
                break;
        }
    }

    /// <summary>
    /// The frame's <c>id</c> when it is one the engine echoes: 1 to 64 letters, digits, <c>-</c> and <c>_</c> (base64url);
    /// the last <c>id</c> member counts. Another member's name that doesn't decode is passed over here (<see cref="Decode"/>
    /// refuses the frame for it).
    /// </summary>
    /// <exception cref="InvalidOperationException">The id's own text doesn't decode.</exception>
    private static string? IdOf(JsonElement root)
    {
        JsonElement? found = null;
        foreach (var property in root.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (name == "id")
                found = property.Value;
        }

        if (found is not { ValueKind: JsonValueKind.String } id)
            return null;
        var text = id.GetString()!;
        if (text.Length is 0 or > MaxIdLength)
            return null;
        foreach (var c in text)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
                return null;
        }

        return text;
    }

    private static JsonElement EmptyObject()
    {
        using var empty = JsonDocument.Parse("{}");
        return empty.RootElement.Clone();
    }

    /// <summary>
    /// What the engine takes beyond the protocol, in its <c>hello</c>: <c>design</c>, a call's design the service produced
    /// (docs/2.0/relay.md, "Designs"). A relay sends a design only to an engine that lists it.
    /// </summary>
    public static IReadOnlyList<string> Features { get; } = ["design"];

    /// <summary><c>{"type":"hello","protocol":1,"engineVersion":…,"features":[…]}</c>, the engine's first frame.</summary>
    public static JsonObject Hello(string engineVersion) => new()
    {
        ["type"] = "hello",
        ["protocol"] = Protocol,
        ["engineVersion"] = engineVersion,
        ["features"] = new JsonArray([.. Features.Select(f => (JsonNode)JsonValue.Create(f))]),
    };

    /// <summary><c>{"type":"time","nonce":…}</c>: the engine asks for the relay's clock.</summary>
    public static JsonObject TimeRequest(string nonce) => new() { ["type"] = "time", ["nonce"] = nonce };

    /// <summary><c>{"type":"result","id":…,"result":{"content":[…],"isError":…}}</c>.</summary>
    public static JsonObject Result(string id, JsonObject result) => new() { ["type"] = "result", ["id"] = id, ["result"] = result };

    /// <summary><c>{"type":"late","id":…}</c>: the call arrived too late, and didn't run.</summary>
    public static JsonObject Late(string id) => new() { ["type"] = "late", ["id"] = id };

    /// <summary><c>{"type":"cancelled","id":…}</c>: the call's change was stopped before it was made.</summary>
    public static JsonObject Cancelled(string id) => new() { ["type"] = "cancelled", ["id"] = id };

    /// <summary><c>{"type":"error","code":…}</c>, with the frame's <c>id</c> when it had one.</summary>
    public static JsonObject Error(string code, string? id)
    {
        var error = new JsonObject { ["type"] = "error", ["code"] = code };
        if (id is not null)
            error["id"] = id;
        return error;
    }

    /// <summary><c>{"type":"bye"}</c>: the engine turned AI apps elsewhere off; the relay forgets this computer.</summary>
    public static JsonObject Bye() => new() { ["type"] = "bye" };

    /// <summary>A frame as sent: UTF-8 JSON, written at <see cref="MaxDepth"/>.</summary>
    public static byte[] Write(JsonObject frame) => JsonSerializer.SerializeToUtf8Bytes(frame, WriteOptions);
}
