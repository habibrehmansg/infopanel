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
        EncodeMs = 12.5,
        Skipped = 3,
    };

    /// <summary>A video keyframe's header: the PNG header's with the video members.</summary>
    private static readonly FrameHeader VideoKeyframe = Header with
    {
        Encoding = "h264",
        Keyframe = true,
        Codec = "avc1.42C028",
        Description = "AUIAKP/hAA4nQgAoq0AyA801AQEBAgEABCjOPIA=",
        Encoder = "hardware",
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
        Assert.Equal(["profileId", "rev", "tick", "frameId", "encoding", "scale", "pixelWidth", "pixelHeight", "profileWidth", "profileHeight", "itemBounds",
                "encodeMs", "skipped"],
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
    public void AVideoFramesMembers_RoundTrip_AndAPngFramesFallback()
    {
        foreach (var sent in new[] { VideoKeyframe, VideoKeyframe with { Keyframe = false, Codec = null, Description = null, Encoder = null }, Header with { Fallback = "encoder.size" } })
        {
            var (header, payload) = FrameCodec.Decode(FrameCodec.Encode(sent, Payload));
            Assert.Equal(Text(sent), Text(header));
            Assert.Equal((sent.Keyframe, sent.Codec, sent.Description, sent.Encoder, sent.Fallback), (header.Keyframe, header.Codec, header.Description, header.Encoder, header.Fallback));
            Assert.Equal((12.5, 3L), (header.EncodeMs, header.Skipped));
            Assert.Equal(Payload, payload.ToArray());
        }

        using var json = JsonDocument.Parse(Text(VideoKeyframe));
        Assert.Equal(["profileId", "rev", "tick", "frameId", "encoding", "scale", "pixelWidth", "pixelHeight", "profileWidth", "profileHeight", "itemBounds",
                "keyframe", "codec", "description", "encoder", "encodeMs", "skipped"],
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.True(SpecSchemas.Strict.Validate(json.RootElement, SpecSchemas.Api + "#/$defs/frameHeader").IsValid);
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
            // A member it doesn't know is skipped (below); one it knows, repeated, is still refused, never resolved to one.
            { "repeated member", WithHeader(Json(Header).Replace("\"rev\":42", "\"rev\":42,\"rev\":42")) },
            { "bounds not an array", WithHeader(Json(Header with { ItemBounds = [] }).Replace("\"itemBounds\":[]", "\"itemBounds\":{}")) },
            { "bound missing a field", WithHeader(Json(Header).Replace(",\"rotation\":0", "")) },
            // The whole header is JSON as the formats read it (spec/docs/validation.md#json) before any member is read or
            // skipped: a member it doesn't know is no exception.
            { "repeated name in an unknown member", WithAdded("\"future\":{\"x\":1,\"x\":2}") },
            { "unknown member repeated", WithAdded("\"future\":1,\"future\":2") },
            { "+infinity in an unknown member", WithAdded("\"future\":1e400") },
            { "-infinity in an unknown member", WithAdded("\"future\":[-1e400]") },
            { "lone surrogate in an unknown member", WithAdded("\"future\":\"\\ud800\"") },
            { "lone surrogate in an unknown member's name", WithAdded("\"\\udc00\":1") },
        };
    }

    /// <summary>
    /// The frame header is what the engine sends, read tolerantly (spec/docs/versioning.md): a reader skips members it
    /// doesn't know, at any depth, so a later engine can add one without breaking an earlier reader. Every member it
    /// knows is read as strictly as before (<see cref="Malformed"/>).
    /// </summary>
    [Fact]
    public void AHeaderWithMembersItDoesntKnow_Decodes_AsWithoutThem()
    {
        var json = Json(Header)
            .Replace("\"rev\":42,", "\"rev\":42,\"revision\":{\"of\":[42,{\"at\":null}]},")
            .Replace("\"rotation\":0", "\"rotation\":0,\"opacity\":0.5")
            .Replace("\"x\":648,", "\"x\":648,\"z\":3,");
        foreach (var added in new[] { "\"revision\"", "\"opacity\"", "\"z\"" })
            Assert.Contains(added, json);

        var (header, payload) = FrameCodec.Decode(WithHeader(json));

        Assert.Equal(Text(Header), Text(header));
        Assert.Equal(Header.ItemBounds, header.ItemBounds);
        Assert.Equal(new byte[2], payload.ToArray());
    }

    /// <summary>
    /// An unknown member nests as deeply as the formats allow (<c>jsonDepth</c>, 256, the header object counting as 1):
    /// 100 levels and the whole limit decode; one level more is refused for the limit.
    /// </summary>
    [Theory]
    [InlineData(100, true)]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public void AnUnknownMemberNested_UpToJsonDepth_IsSkipped_AndDeeperIsRefused(int arrays, bool decodes)
    {
        var message = WithAdded("\"future\":" + new string('[', arrays) + new string(']', arrays));

        if (decodes)
            Assert.Equal(Text(Header), Text(FrameCodec.Decode(message).Header));
        else
            Assert.Contains("nested deeper than 256 levels", Assert.Throws<FormatException>(() => FrameCodec.Decode(message)).Message);
    }

    /// <summary>An enumeration's value it doesn't know is read as it is, for the reader's own fallback (spec/docs/versioning.md).</summary>
    [Fact]
    public void ValuesItDoesntKnow_DecodeAsTheyAre()
    {
        var json = Json(Header).Replace("\"problem\":\"invalid\"", "\"problem\":\"overheated\"").Replace("\"encoding\":\"png\"", "\"encoding\":\"tiles\"");

        var (header, _) = FrameCodec.Decode(WithHeader(json));

        Assert.Equal("overheated", header.ItemBounds[1].Problem);
        Assert.Equal("tiles", header.Encoding);
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

    /// <summary>The header with <paramref name="members"/> (JSON text) added after <c>rev</c>.</summary>
    private static byte[] WithAdded(string members) => WithHeader(Json(Header).Replace("\"rev\":42", "\"rev\":42," + members));

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
