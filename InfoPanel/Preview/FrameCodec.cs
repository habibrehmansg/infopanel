using InfoPanel.Drawing;
using InfoPanel.Output;
using System.Buffers.Binary;
using System.Text.Json;

namespace InfoPanel.Preview;

/// <summary>
/// The JSON header of a binary frame message (plan §6.4). <see cref="Bounds"/> are in profile coordinates and draw
/// order, visible items only; empty when the subscriber asked for none.
/// </summary>
public sealed record FrameHeader(
    Guid ProfileId,
    long Revision,
    long FrameId,
    string Encoding,
    float Scale,
    int PixelWidth,
    int PixelHeight,
    int ProfileWidth,
    int ProfileHeight,
    IReadOnlyList<ItemBounds> Bounds)
{
    public const string Png = "png";

    public static FrameHeader From(RenderedFrame frame, bool includeBounds) => new(
        frame.ProfileId, frame.Revision, frame.FrameId, Png, frame.Scale, frame.PixelWidth, frame.PixelHeight,
        frame.ProfileWidth, frame.ProfileHeight, includeBounds ? frame.Bounds : []);
}

/// <summary>
/// Binary frame messages on <c>/ws</c> (little-endian): the magic <c>IPF1</c>, a <c>uint32</c> header length N,
/// N bytes of UTF-8 JSON (<see cref="FrameHeader"/>, camelCase, bounds as <c>{id,x,y,w,h,r,g}</c>), then the payload
/// (the PNG).
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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new FormatException("The frame header is not valid: " + ex.Message, ex);
        }

        return (header, message[(PrefixSize + (int)length)..]);
    }

    private static byte[] HeaderJson(FrameHeader header)
    {
        using var stream = new MemoryStream(256 + header.Bounds.Count * 96);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("profileId", header.ProfileId);
            json.WriteNumber("revision", header.Revision);
            json.WriteNumber("frameId", header.FrameId);
            json.WriteString("encoding", header.Encoding);
            json.WriteNumber("scale", header.Scale);
            json.WriteNumber("pixelWidth", header.PixelWidth);
            json.WriteNumber("pixelHeight", header.PixelHeight);
            json.WriteNumber("profileWidth", header.ProfileWidth);
            json.WriteNumber("profileHeight", header.ProfileHeight);
            json.WriteStartArray("bounds");
            foreach (var b in header.Bounds)
            {
                json.WriteStartObject();
                json.WriteString("id", b.Id);
                json.WriteNumber("x", b.X);
                json.WriteNumber("y", b.Y);
                json.WriteNumber("w", b.W);
                json.WriteNumber("h", b.H);
                json.WriteNumber("r", b.Rotation);
                json.WriteBoolean("g", b.IsGroup);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static FrameHeader ReadHeader(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The header is not an object.");

        var bounds = new List<ItemBounds>();
        var array = root.GetProperty("bounds");
        if (array.ValueKind != JsonValueKind.Array)
            throw new JsonException("bounds is not an array.");

        foreach (var b in array.EnumerateArray())
        {
            bounds.Add(new ItemBounds(b.GetProperty("id").GetGuid(), b.GetProperty("x").GetSingle(), b.GetProperty("y").GetSingle(),
                b.GetProperty("w").GetSingle(), b.GetProperty("h").GetSingle(), b.GetProperty("r").GetSingle(), b.GetProperty("g").GetBoolean()));
        }

        return new FrameHeader(
            root.GetProperty("profileId").GetGuid(),
            root.GetProperty("revision").GetInt64(),
            root.GetProperty("frameId").GetInt64(),
            root.GetProperty("encoding").GetString() ?? throw new JsonException("encoding is null."),
            root.GetProperty("scale").GetSingle(),
            root.GetProperty("pixelWidth").GetInt32(),
            root.GetProperty("pixelHeight").GetInt32(),
            root.GetProperty("profileWidth").GetInt32(),
            root.GetProperty("profileHeight").GetInt32(),
            bounds);
    }
}
