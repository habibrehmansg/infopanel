using InfoPanel.Output;
using InfoPanel.Spec;
using System.Buffers.Binary;
using System.Text.Json;

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

    /// <summary>Splits a message into its header and payload.</summary>
    /// <exception cref="FormatException">Not a frame message: wrong magic, a length past the end, or a header that isn't the expected JSON.</exception>
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
            using var json = JsonDocument.Parse(message.Slice(PrefixSize, (int)length));
            header = ReadHeader(json.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new FormatException("The frame header is not valid: " + ex.Message, ex);
        }

        return (header, message[(PrefixSize + (int)length)..]);
    }

    private static byte[] HeaderJson(FrameHeader header) => JsonSerializer.SerializeToUtf8Bytes(header, SpecJson.Options);

    private static FrameHeader ReadHeader(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object ? SpecJson.Read<FrameHeader>(root) : throw new JsonException("The header is not an object.");
}
