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
