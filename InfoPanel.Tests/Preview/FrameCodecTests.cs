using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using InfoPanel.Drawing;
using InfoPanel.Preview;
using Xunit;

namespace InfoPanel.Tests.Preview;

public class FrameCodecTests
{
    private static readonly FrameHeader Header = new(
        Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), 42, 1093, FrameHeader.Png, 2, 3840, 960, 1920, 480,
        [
            new ItemBounds(Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"), 648, 84, 624, 160, 0, false),
            new ItemBounds(Guid.Parse("16fd2706-8baf-433b-82eb-8c7fada847da"), 10.5f, -3.25f, 1, 0, 37.5f, true),
        ]);

    private static readonly byte[] Payload = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];

    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        var message = FrameCodec.Encode(Header, Payload);
        var (header, payload) = FrameCodec.Decode(message);

        Assert.Equal(Header with { Bounds = [] }, header with { Bounds = [] });
        Assert.Equal(Header.Bounds, header.Bounds);
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
        Assert.Equal(["profileId", "revision", "frameId", "encoding", "scale", "pixelWidth", "pixelHeight", "profileWidth", "profileHeight", "bounds"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", root.GetProperty("profileId").GetString());
        Assert.Equal("png", root.GetProperty("encoding").GetString());
        Assert.Equal(2, root.GetProperty("scale").GetDouble());
        var bound = root.GetProperty("bounds")[1];
        Assert.Equal(["id", "x", "y", "w", "h", "r", "g"], bound.EnumerateObject().Select(p => p.Name));
        Assert.Equal(-3.25, bound.GetProperty("y").GetDouble());
        Assert.True(bound.GetProperty("g").GetBoolean());
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
        var header = Header with { Bounds = [] };
        var (decoded, payload) = FrameCodec.Decode(FrameCodec.Encode(header, []));
        Assert.Empty(decoded.Bounds);
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
            { "missing field", WithHeader("""{"profileId":"0f8fad5b-d9cb-469f-a165-70867728950e","revision":1}""") },
            { "wrong type", WithHeader(Json(Header).Replace("\"revision\":42", "\"revision\":\"42\"")) },
            { "bad guid", WithHeader(Json(Header).Replace("0f8fad5b-d9cb-469f-a165-70867728950e", "nope")) },
            { "bounds not an array", WithHeader(Json(Header with { Bounds = [] }).Replace("\"bounds\":[]", "\"bounds\":{}")) },
            { "bound missing a field", WithHeader(Json(Header).Replace(",\"g\":false", "")) },
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
