using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using InfoPanel.Preview;
using InfoPanel.Spec;

namespace InfoPanel.Tests.Contract;

/// <summary>
/// The preview socket's messages against spec/openapi.json's <c>x-infopanel-socket</c>: text messages a client sends
/// against <c>clientMessage</c> (JSON of at most 4,096 bytes), text messages the engine sends against
/// <c>serverMessage</c>, and a frame message's header against <c>frameHeader</c>. Each check returns null for a message
/// that conforms, else what is wrong. The test clients (<c>PreviewClient</c>, <c>LiveSocket</c>) run them on everything
/// they send and receive.
/// </summary>
internal static class SocketContract
{
    public static string? CheckClientText(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > PreviewSocketHandler.MaxClientMessageBytes)
            return $"A client message of {bytes} bytes; clientMessage is at most {PreviewSocketHandler.MaxClientMessageBytes}.";
        return Check(text, ApiContract.Instance.Socket.ClientMessage, "clientMessage");
    }

    public static string? CheckServerText(string text) => Check(text, ApiContract.Instance.Socket.ServerMessage, "serverMessage");

    /// <summary>A binary frame message: <c>IPF1</c>, the header's length (uint32, little-endian), the header (<c>frameHeader</c>), the frame.</summary>
    public static string? CheckFrame(ReadOnlySpan<byte> message)
    {
        if (message.Length < FrameCodec.PrefixSize || !message[..4].SequenceEqual(FrameCodec.Magic))
            return "A binary message that doesn't start with IPF1 and the header's length.";
        var length = BinaryPrimitives.ReadUInt32LittleEndian(message[4..8]);
        if (length > message.Length - FrameCodec.PrefixSize)
            return $"A frame header of {length} bytes in a message of {message.Length}.";
        var text = Encoding.UTF8.GetString(message.Slice(FrameCodec.PrefixSize, (int)length));
        return Check(text, ApiContract.Instance.Socket.FrameHeader, "frameHeader") ?? CheckFrameVideo(text);
    }

    /// <summary>
    /// <c>frame.video</c> (spec/docs/api.md, "Checks beyond the schema"; the examples' check is the Core tests'
    /// <c>ApiBodyRules.FrameVideo</c>): an <c>h264</c> frame has <c>keyframe</c>; a keyframe has <c>codec</c>,
    /// <c>description</c> and <c>encoder</c>; the video members are only on <c>h264</c> frames, <c>fallback</c> only on <c>png</c> ones.
    /// </summary>
    internal static string? CheckFrameVideo(string header)
    {
        using var json = JsonDocument.Parse(header);
        var root = json.RootElement;
        bool Has(string member) => root.TryGetProperty(member, out _);
        var problem = root.GetProperty("encoding").GetString() switch
        {
            "h264" when !Has("keyframe") => "an h264 frame without keyframe",
            "h264" when root.GetProperty("keyframe").GetBoolean() && !(Has("codec") && Has("description") && Has("encoder")) => "a keyframe without codec, description or encoder",
            "h264" when Has("fallback") => "fallback on an h264 frame",
            not "h264" when Has("keyframe") || Has("codec") || Has("description") || Has("encoder") => "video members on a frame that isn't h264",
            not ("h264" or "png") when Has("fallback") => "fallback on a frame that isn't png",
            _ => null,
        };
        return problem == null ? null : $"frame.video: {problem} {Excerpt(header)}";
    }

    /// <summary>A text message's <c>type</c>, or null when it has none.</summary>
    public static string? TypeOf(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Check(string text, string schema, string name)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(text, SpecJson.DocumentOptions);
        }
        catch (JsonException ex)
        {
            return $"Not JSON, so not a {name}: {ex.Message} {Excerpt(text)}";
        }

        using (json)
        {
            var result = ApiContract.Instance.Schemas.Validate(json.RootElement, schema);
            return result.IsValid
                ? null
                : $"Not a {name}: {string.Join("; ", result.Errors.Take(5).Select(e => $"{(e.InstancePath.Length == 0 ? "(root)" : e.InstancePath)} {e.Keyword}: {e.Message}"))} {Excerpt(text)}";
        }
    }

    private static string Excerpt(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
