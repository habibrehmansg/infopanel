using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using InfoPanel.Preview;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Tests.Preview;

public class FrameCodecTests
{
    private static readonly FrameHeader Header = new()
    {
        ProfileId = "0f8fad5b-d9cb-469f-a165-70867728950e",
        Rev = 42,
        Tick = 1820,
        FrameId = 1093,
        Encoding = FrameHeaders.Png,
        Scale = 2,
        PixelWidth = 3840,
        PixelHeight = 960,
        ProfileWidth = 1920,
        ProfileHeight = 480,
        ItemBounds =
        [
            new ItemBounds { Id = "cpu-bar", Frame = Rect(648, 84, 624, 160), Ink = Rect(646, 82, 628, 164), Rotation = 0 },
            new ItemBounds { Id = "group-1", Frame = Rect(10.5, -3.25, 1, 0), Ink = Rect(10.5, -3.25, 1, 0), Rotation = 37.5, Problem = "invalid" },
        ],
    };

    private static BoundsRect Rect(double x, double y, double w, double h) => new() { X = x, Y = y, W = w, H = h };

    private static string Text(FrameHeader header) => JsonSerializer.Serialize(header, SpecJson.Options);

    private static readonly byte[] Payload = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];

    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        var message = FrameCodec.Encode(Header, Payload);
        var (header, payload) = FrameCodec.Decode(message);

        Assert.Equal(Text(Header), Text(header));
        Assert.Equal(Header.ItemBounds, header.ItemBounds);
        Assert.Equal(Payload, payload.ToArray());
    }

    [Fact]
    public void TheLayout_IsMagic_LittleEndianLength_JsonHeader_Payload()
    {
        var message = FrameCodec.Encode(Header, Payload);

        Assert.Equal("IPF1"u8.ToArray(), message[..4]);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(4));
        Assert.Equal(message.Length - 8 - Payload.Length, length);
        Assert.Equal(Payload, message[(8 + length)..]);

        using var json = JsonDocument.Parse(message.AsMemory(8, length));
        var root = json.RootElement;
        // The API's frameHeader (spec/docs/api.md, "Frames"), as its schema describes it.
        Assert.Equal(["profileId", "rev", "tick", "frameId", "encoding", "scale", "pixelWidth", "pixelHeight", "profileWidth", "profileHeight", "itemBounds"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.True(SpecSchemas.Strict.Validate(root, SpecSchemas.Api + "#/$defs/frameHeader").IsValid);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", root.GetProperty("profileId").GetString());
        Assert.Equal((42, 1820), (root.GetProperty("rev").GetInt64(), root.GetProperty("tick").GetInt64()));
        Assert.Equal("png", root.GetProperty("encoding").GetString());
        Assert.Equal(2, root.GetProperty("scale").GetDouble());
        var bound = root.GetProperty("itemBounds")[1];
        Assert.Equal(["id", "frame", "ink", "rotation", "problem"], bound.EnumerateObject().Select(p => p.Name));
        Assert.Equal(-3.25, bound.GetProperty("frame").GetProperty("y").GetDouble());
    }

    [Fact]
    public void EncodePrefix_IsTheMessageWithoutThePayload()
    {
        var message = FrameCodec.Encode(Header, Payload);
        Assert.Equal(message[..^Payload.Length], FrameCodec.EncodePrefix(Header));
    }

    [Fact]
    public void AnEmptyPayloadAndNoBounds_RoundTrip()
    {
        var header = Header with { ItemBounds = [] };
        var (decoded, payload) = FrameCodec.Decode(FrameCodec.Encode(header, []));
        Assert.Empty(decoded.ItemBounds);
        Assert.True(payload.IsEmpty);
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        var good = FrameCodec.Encode(Header, Payload);
        var badMagic = (byte[])good.Clone();
        badMagic[3] = (byte)'2';
        var longLength = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(longLength.AsSpan(4), (uint)good.Length);
        var hugeLength = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hugeLength.AsSpan(4), uint.MaxValue);

        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "short", "IPF1"u8.ToArray() },
            { "bad magic", badMagic },
            { "length past the end", longLength },
            { "huge length", hugeLength },
            { "not json", WithHeader("{not json") },
            { "not an object", WithHeader("[1,2]") },
            { "missing field", WithHeader("""{"profileId":"0f8fad5b-d9cb-469f-a165-70867728950e","rev":1}""") },
            { "wrong type", WithHeader(Json(Header).Replace("\"rev\":42", "\"rev\":\"42\"")) },
            { "unknown member", WithHeader(Json(Header).Replace("\"rev\":42", "\"rev\":42,\"revision\":42")) },
            { "bounds not an array", WithHeader(Json(Header with { ItemBounds = [] }).Replace("\"itemBounds\":[]", "\"itemBounds\":{}")) },
            { "bound missing a field", WithHeader(Json(Header).Replace(",\"rotation\":0", "")) },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void MalformedMessages_AreRejected(string why, byte[] message)
    {
        Assert.False(string.IsNullOrEmpty(why));
        Assert.Throws<FormatException>(() => FrameCodec.Decode(message));
    }

    private static string Json(FrameHeader header)
    {
        var prefix = FrameCodec.EncodePrefix(header);
        return Encoding.UTF8.GetString(prefix, 8, prefix.Length - 8);
    }

    private static byte[] WithHeader(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var message = new byte[8 + bytes.Length + 2];
        "IPF1"u8.CopyTo(message);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), (uint)bytes.Length);
        bytes.CopyTo(message, 8);
        return message;
    }
}
