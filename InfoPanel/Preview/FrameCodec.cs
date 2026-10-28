using InfoPanel.Output;
using InfoPanel.Spec;
using InfoPanel.Spec.Schema;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfoPanel.Preview;

/// <summary>The <c>frameHeader</c> of a rendered frame (spec/docs/api.md, "Frames").</summary>
public static class FrameHeaders
{
    public const string Png = "png";

    /// <summary>The frame's header; its <c>itemBounds</c> are empty when the subscriber asked for none.</summary>
    public static FrameHeader From(RenderedFrame frame, bool includeBounds) => new()
    {
        ProfileId = frame.ProfileId,
        Rev = frame.Rev,
        Tick = frame.Tick,
        FrameId = frame.FrameId,
        Encoding = Png,
        Scale = frame.Scale,
        PixelWidth = frame.PixelWidth,
        PixelHeight = frame.PixelHeight,
        ProfileWidth = frame.ProfileWidth,
        ProfileHeight = frame.ProfileHeight,
        ItemBounds = includeBounds ? frame.Bounds : [],
    };
}

/// <summary>
/// Binary frame messages on <c>/ws</c> (little-endian): the magic <c>IPF1</c>, a <c>uint32</c> header length N,
/// N bytes of UTF-8 JSON (<see cref="FrameHeader"/>, the API's <c>frameHeader</c>), then the payload (the PNG).
/// </summary>
public static class FrameCodec
{
    public const int PrefixSize = 8;

    /// <summary>The longest header <see cref="Decode"/> accepts (a profile with tens of thousands of items fits).</summary>
    public const int MaxHeaderBytes = 8 * 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "IPF1"u8;

    /// <summary>Everything before the payload: magic, header length and header. The socket sends the payload after it in the same message.</summary>
    public static byte[] EncodePrefix(FrameHeader header)
    {
        var json = HeaderJson(header);
        var prefix = new byte[PrefixSize + json.Length];
        Magic.CopyTo(prefix);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), (uint)json.Length);
        json.CopyTo(prefix.AsSpan(PrefixSize));
        return prefix;
    }

    /// <summary>The whole message: <see cref="EncodePrefix"/> followed by <paramref name="payload"/>.</summary>
    public static byte[] Encode(FrameHeader header, ReadOnlySpan<byte> payload)
    {
        var prefix = EncodePrefix(header);
        var message = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(message, 0);
        payload.CopyTo(message.AsSpan(prefix.Length));
        return message;
    }

    /// <summary>
    /// Splits a message into its header and payload. The header is read as a client reads what the engine sends
    /// (spec/docs/versioning.md): first the whole header as the formats read JSON (spec/docs/validation.md#json), members
    /// it doesn't know included; then members it doesn't know are skipped, at any depth, and every member it knows is
    /// read as strictly as the formats read it.
    /// </summary>
    /// <exception cref="FormatException">
    /// Not a frame message: wrong magic, a length past the end, a header the formats' JSON-level pass refuses (nested
    /// deeper than <c>jsonDepth</c>, a number that rounds to ±∞, a member name repeated in one object, a string or name
    /// that isn't Unicode text), or one that isn't the expected JSON.
    /// </exception>
    public static (FrameHeader Header, ReadOnlyMemory<byte> Payload) Decode(ReadOnlyMemory<byte> message)
    {
        var span = message.Span;
        if (span.Length < PrefixSize || !span[..4].SequenceEqual(Magic))
            throw new FormatException("Not a frame message (no IPF1 magic).");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (length > MaxHeaderBytes || length > span.Length - PrefixSize)
            throw new FormatException($"The header length {length} runs past the message.");

        FrameHeader header;
        try
        {
            // Parsed at any depth, so that nesting is the JSON-level pass's jsonDepth, not the parser's own default (64).
            using var json = JsonDocument.Parse(message.Slice(PrefixSize, (int)length), SpecJson.DocumentOptions);
            var check = SchemaSet.CheckJson(json.RootElement, SpecLimits.Default.JsonDepth);
            if (!check.IsValid)
            {
                var first = check.Errors[0];
                throw new FormatException(
                    $"The frame header is not valid: at {(first.InstancePath.Length == 0 ? "the header" : first.InstancePath)}, {first.Message}");
            }

            header = ReadHeader(json.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new FormatException("The frame header is not valid: " + ex.Message, ex);
        }

        return (header, message[(PrefixSize + (int)length)..]);
    }

    private static byte[] HeaderJson(FrameHeader header) => JsonSerializer.SerializeToUtf8Bytes(header, SpecJson.Options);

    /// <summary>The formats' options, except that a member the model doesn't have is skipped; a repeated one is still refused.</summary>
    private static readonly JsonSerializerOptions Tolerant = CreateTolerant();

    private static JsonSerializerOptions CreateTolerant()
    {
        var options = new JsonSerializerOptions(SpecJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
        options.MakeReadOnly();
        return options;
    }

    private static FrameHeader ReadHeader(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
            ? root.Deserialize<FrameHeader>(Tolerant) ?? throw new JsonException("The header is null.")
            : throw new JsonException("The header is not an object.");
}
