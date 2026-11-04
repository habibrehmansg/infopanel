using System.Text;
using InfoPanel.Service;
using Xunit;

namespace InfoPanel.Core.Tests.Service;

/// <summary>The relay's frames as the engine admits them, and the clock reading's rules (docs/2.0/relay.md).</summary>
public sealed class RelayFramesTests
{
    private static RelayFrame Read(string text) => RelayFrames.Read(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void TheFourTypes_AreRead_AnythingElseIsRefusedWithItsCode()
    {
        Assert.IsType<RelayFrame.Welcome>(Read("""{"type":"welcome","extra":1}"""));
        Assert.Equal(new RelayFrame.Time("n1", 1762214400000), Read("""{"type":"time","nonce":"n1","now":1762214400000}"""));
        Assert.Equal(new RelayFrame.Cancel("c-1_A"), Read("""{"type":"cancel","id":"c-1_A"}"""));
        var call = Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c1","tool":"add_item","arguments":{"profile":"P"},"sentAt":5,"maxChars":140000}"""));
        Assert.Equal(("c1", "add_item", 5L, 140000), (call.Id, call.Tool, call.SentAt, call.MaxChars!.Value));
        Assert.Equal("""{"profile":"P"}""", call.Arguments.GetRawText());
        Assert.Equal("{}", Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c1","tool":"list_profiles","sentAt":5}""")).Arguments.GetRawText());

        Assert.Equal(new RelayFrame.Refused("unsupported", "x"), Read("""{"type":"open","id":"x"}"""));
        Assert.Equal(new RelayFrame.Refused("unsupported", null), Read("""{"type":"open","id":"a b"}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""[1]"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":3}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":"time","nonce":"n","now":"soon"}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":"cancel"}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"t","sentAt":1.5}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"t","sentAt":1,"arguments":[]}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"","sentAt":1}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"t","sentAt":1,"maxChars":-1}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read(new string('[', RelayFrames.MaxDepth + 1) + new string(']', RelayFrames.MaxDepth + 1)));
    }

    [Fact]
    public void TextThatDoesntDecode_AnywhereInAFrame_IsMalformed_NeverAThrow()
    {
        // Lone surrogates: escapes JSON reads, but no text. In the type, an id, the tool's name, a nonce, the arguments.
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":"\uD800"}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":"open","id":"\uD800"}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":"cancel","id":"\uDC00"}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":"call","id":"\uDC00","tool":"t","sentAt":1}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"\uD800x","sentAt":1}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", null), Read("""{"type":"time","nonce":"\uD800","now":5}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"t","sentAt":1,"arguments":{"profile":"\uD800"}}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"t","sentAt":1,"arguments":{"a":[1,{"b":"x\uDFFF"}]}}"""));
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read("""{"type":"call","id":"c","tool":"t","sentAt":1,"arguments":{"\uD800":1}}"""));

        // A pair is text: read as it is.
        var call = Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"t","sentAt":1,"arguments":{"name":"\uD83D\uDE00"}}"""));
        Assert.Equal("\U0001F600", call.Arguments.GetProperty("name").GetString());
    }

    [Theory]
    // A member no type reads: its value, then its name; in the frame's last member, so a lookup by name meets it first.
    [InlineData("""{"type":"cancel","id":"c","extra":"\uD800"}""", "c")]
    [InlineData("""{"type":"cancel","id":"c","\uD800":1}""", "c")]
    [InlineData("""{"type":"call","id":"c","tool":"t","sentAt":1,"extra":["x\uDC00"]}""", "c")]
    [InlineData("""{"type":"call","id":"c","tool":"t","sentAt":1,"x\uDC00":null}""", "c")]
    [InlineData("""{"type":"welcome","extra":{"a":"\uD800"}}""", null)]
    [InlineData("""{"type":"time","nonce":"n","now":5,"\uDBFF":true}""", null)]
    [InlineData("""{"type":"open","id":"c","extra":"\uDFFF"}""", "c")]
    public void TextThatDoesntDecode_InAMemberNoTypeReads_IsMalformed_WithTheFramesId(string frame, string? id)
    {
        Assert.Equal(new RelayFrame.Refused("malformed", id), Read(frame));
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(0)]
    [InlineData(253_402_300_800_000)] // a millisecond past what a DateTimeOffset holds
    public void ASentAtOutsideWhatATimeHolds_IsMalformed(long sentAt)
    {
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read($$"""{"type":"call","id":"c","tool":"t","sentAt":{{sentAt}}}"""));
    }

    [Fact]
    public void ASentAtAtTheEndOfWhatATimeHolds_IsRead()
    {
        var last = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
        Assert.Equal(last, Assert.IsType<RelayFrame.Call>(Read($$"""{"type":"call","id":"c","tool":"t","sentAt":{{last}}}""")).SentAt);
    }

    [Theory]
    [InlineData(10, RelayFrames.MinMaxChars)]
    [InlineData(140000, 140000)]
    [InlineData(1L << 40, RelayFrames.MaxFrameBytes)]
    public void MaxChars_IsBroughtWithinItsBounds(long given, int taken)
    {
        var call = Assert.IsType<RelayFrame.Call>(Read($$"""{"type":"call","id":"c","tool":"t","sentAt":1,"maxChars":{{given}}}"""));
        Assert.Equal(taken, call.MaxChars);
    }

    [Fact]
    public void TheHello_ListsTheFeatures_Design()
    {
        var hello = RelayFrames.Hello("2.0.1");
        Assert.Equal("""{"type":"hello","protocol":1,"engineVersion":"2.0.1","features":["design"]}""", hello.ToJsonString());
    }

    [Fact]
    public void ACallsDeadline_IsRead_AndOneThatIsntATime_IsMalformed()
    {
        Assert.Null(Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"t","sentAt":5}""")).Deadline);
        Assert.Null(Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"t","sentAt":5,"deadline":null}""")).Deadline);
        Assert.Equal(20_005, Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"t","sentAt":5,"deadline":20005}""")).Deadline);
        // Before sentAt is a time too: the call's change then has none left.
        Assert.Equal(1, Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"t","sentAt":5,"deadline":1}""")).Deadline);
        foreach (var deadline in new[] { "0", "-1", "1.5", "\"soon\"", "[]", "253402300800000", "9223372036854775807" })
            Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read($$"""{"type":"call","id":"c","tool":"t","sentAt":5,"deadline":{{deadline}}}"""));
    }

    [Fact]
    public void ACallsDesignAndFiles_AreRead()
    {
        var call = Assert.IsType<RelayFrame.Call>(Read("""
            {"type":"call","id":"c","tool":"add_item","arguments":{"profile":"P","x":5},"sentAt":5,"deadline":9,
             "files":["one","two"],
             "design":{"layout":{"w":10},"source":{"format":"example.widget/1","data":{},"deps":[]},"scene":{"format":"infopanel.scene/1"},"origin":{"kind":"local"}}}
            """));
        Assert.Equal(["one", "two"], call.Files);
        Assert.Equal("""{"layout":{"w":10},"source":{"format":"example.widget/1","data":{},"deps":[]},"scene":{"format":"infopanel.scene/1"},"origin":{"kind":"local"}}""",
            call.Design!.Value.GetRawText());
        Assert.Equal(9, call.Deadline);

        // Neither: a call as before. Null is absent.
        var plain = Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"t","sentAt":5,"design":null,"files":null}"""));
        Assert.Null(plain.Design);
        Assert.Null(plain.Files);

        // Only layout and origin may be left out.
        var bare = Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"add_item","sentAt":5,"design":{"source":{"ref":"r"},"scene":{}}}"""));
        Assert.Equal("""{"source":{"ref":"r"},"scene":{}}""", bare.Design!.Value.GetRawText());
    }

    [Theory]
    [InlineData("""{"design":[]}""")]
    [InlineData("""{"design":"x"}""")]
    [InlineData("""{"design":{"scene":{}}}""")]
    [InlineData("""{"design":{"source":{}}}""")]
    [InlineData("""{"design":{"source":[],"scene":{}}}""")]
    [InlineData("""{"design":{"source":{},"scene":"x"}}""")]
    [InlineData("""{"design":{"source":{},"scene":{},"layout":5}}""")]
    [InlineData("""{"design":{"source":{},"scene":{},"origin":"local"}}""")]
    [InlineData("""{"files":"x","design":{"source":{},"scene":{}}}""")]
    [InlineData("""{"files":["x",1],"design":{"source":{},"scene":{}}}""")]
    [InlineData("""{"files":{},"design":{"source":{},"scene":{}}}""")]
    // A design is the item's scene and box: the arguments don't give them too.
    [InlineData("""{"arguments":{"scene":{}},"design":{"source":{},"scene":{}}}""")]
    [InlineData("""{"arguments":{"width":10},"design":{"source":{},"scene":{}}}""")]
    [InlineData("""{"arguments":{"height":10},"design":{"source":{},"scene":{}}}""")]
    public void ADesignOrFilesNotAsTheyMustBe_IsMalformed(string members)
    {
        var frame = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"call","id":"c","tool":"add_item","sentAt":5}""")!.AsObject();
        foreach (var (name, value) in System.Text.Json.Nodes.JsonNode.Parse(members)!.AsObject())
            frame[name] = value?.DeepClone();
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read(frame.ToJsonString()));
    }

    [Fact]
    public void ACallsDesigns_AreRead_OnePerItem_NullForAnItemGivenAsAScene()
    {
        var call = Assert.IsType<RelayFrame.Call>(Read("""
            {"type":"call","id":"c","tool":"add_items","arguments":{"profile":"P","items":[{"x":5},{"scene":{}},{"name":"n"}]},"sentAt":5,
             "files":["one"],
             "designs":[{"source":{"format":"example.widget/1"},"scene":{"format":"infopanel.scene/1"}},null,{"layout":{"w":10},"source":{},"scene":{}}]}
            """));
        Assert.Null(call.Design);
        Assert.Equal(["one"], call.Files);
        Assert.Equal(3, call.Designs!.Count);
        Assert.Equal("""{"source":{"format":"example.widget/1"},"scene":{"format":"infopanel.scene/1"}}""", call.Designs[0]!.Value.GetRawText());
        Assert.Null(call.Designs[1]);
        Assert.Equal("""{"layout":{"w":10},"source":{},"scene":{}}""", call.Designs[2]!.Value.GetRawText());

        // Null is absent.
        Assert.Null(Assert.IsType<RelayFrame.Call>(Read("""{"type":"call","id":"c","tool":"t","sentAt":5,"designs":null}""")).Designs);
    }

    [Theory]
    [InlineData("""{"designs":"x"}""")]
    [InlineData("""{"designs":{}}""")]
    [InlineData("""{"designs":[5]}""")]
    [InlineData("""{"designs":[{"scene":{}}]}""")]
    [InlineData("""{"designs":[{"source":{},"scene":{}}],"design":{"source":{},"scene":{}}}""")]
    // One per item: as many as the arguments' items, which must be a list.
    [InlineData("""{"designs":[null,null]}""")]
    [InlineData("""{"designs":[]}""")]
    [InlineData("""{"arguments":{"profile":"P"},"designs":[null]}""")]
    [InlineData("""{"arguments":{"profile":"P","items":{}},"designs":[null]}""")]
    // A design is its item's scene and box: that item doesn't give them too.
    [InlineData("""{"arguments":{"profile":"P","items":[{"scene":{}}]},"designs":[{"source":{},"scene":{}}]}""")]
    [InlineData("""{"arguments":{"profile":"P","items":[{"width":10}]},"designs":[{"source":{},"scene":{}}]}""")]
    [InlineData("""{"arguments":{"profile":"P","items":[{"height":10}]},"designs":[{"source":{},"scene":{}}]}""")]
    [InlineData("""{"arguments":{"profile":"P","items":[5]},"designs":[{"source":{},"scene":{}}]}""")]
    public void DesignsNotAsTheyMustBe_AreMalformed(string members)
    {
        var frame = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"call","id":"c","tool":"add_items","sentAt":5,"arguments":{"profile":"P","items":[{}]}}""")!.AsObject();
        foreach (var (name, value) in System.Text.Json.Nodes.JsonNode.Parse(members)!.AsObject())
            frame[name] = value?.DeepClone();
        Assert.Equal(new RelayFrame.Refused("malformed", "c"), Read(frame.ToJsonString()));
    }

    [Fact]
    public void AReading_CountsOnlyWithin2sOnBothClocks()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        var asked = time.GetUtcNow();
        var start = time.GetTimestamp();
        long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();
        long Ticks(double seconds) => start + (long)(seconds * time.TimestampFrequency);

        // 1 s on both: the offset is the relay's clock less the wall clock's midpoint.
        var reading = RelayClock.Reading(Ms(asked) + 10_500, asked, start, asked.AddSeconds(1), Ticks(1), time);
        Assert.Equal(10_000, reading!.OffsetMs, 3);

        Assert.Null(RelayClock.Reading(Ms(asked), asked, start, asked.AddHours(1), Ticks(1), time)); // held across a sleep
        Assert.Null(RelayClock.Reading(Ms(asked), asked, start, asked.AddSeconds(1), Ticks(3), time)); // Stopwatch ran on
        Assert.Null(RelayClock.Reading(Ms(asked), asked, start, asked.AddSeconds(-1), Ticks(1), time)); // the wall clock went back
    }
}
