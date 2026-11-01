using System.Buffers.Binary;
using InfoPanel.SensorHelper;
using Xunit;
using static InfoPanel.Core.Tests.SensorHelper.HelperTestKit;

namespace InfoPanel.Core.Tests.SensorHelper;

/// <summary>
/// The helper's frames, the same code on both sides: a little-endian length, then the bytes; a clean end between
/// frames is the other side closing; a frame ending early, an empty one, or one over 1 MiB is refused, and an oversized
/// frame's bytes are never read.
/// </summary>
public sealed class HelperFramingTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AFrame_IsALittleEndianLength_ThenItsBytes()
    {
        using var stream = new MemoryStream();
        await HelperFraming.WriteAsync(stream, [1, 2, 3], Cancel);

        Assert.Equal(new byte[] { 3, 0, 0, 0, 1, 2, 3 }, stream.ToArray());
    }

    [Fact]
    public async Task Frames_ReadBackAsWritten_AndACleanEndBetweenFramesIsNull()
    {
        using var stream = new MemoryStream();
        await HelperFraming.WriteAsync(stream, [7], Cancel);
        await HelperFraming.WriteAsync(stream, new byte[300], Cancel);
        stream.Position = 0;

        Assert.Equal(new byte[] { 7 }, await HelperFraming.ReadAsync(stream, Cancel));
        Assert.Equal(new byte[300], await HelperFraming.ReadAsync(stream, Cancel));
        Assert.Null(await HelperFraming.ReadAsync(stream, Cancel));
    }

    [Fact]
    public async Task AFrameOfExactlyTheCap_IsRead()
    {
        using var stream = new MemoryStream(Frame(new byte[HelperProtocol.MaxFrameBytes]));

        var frame = await HelperFraming.ReadAsync(stream, Cancel);

        Assert.Equal(HelperProtocol.MaxFrameBytes, frame!.Length);
    }

    [Fact]
    public async Task AFrameOverTheCap_IsRefused_WithoutReadingItsBytes()
    {
        var header = new byte[4 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, HelperProtocol.MaxFrameBytes + 1);
        using var stream = new MemoryStream(header);

        var refused = await Assert.ThrowsAsync<HelperProtocolException>(async () => await HelperFraming.ReadAsync(stream, Cancel));

        Assert.Equal("tooLarge", refused.Code);
        Assert.Equal(4, stream.Position);
    }

    [Fact]
    public async Task AnEmptyFrame_IsRefused()
    {
        using var stream = new MemoryStream(new byte[4]);

        var refused = await Assert.ThrowsAsync<HelperProtocolException>(async () => await HelperFraming.ReadAsync(stream, Cancel));

        Assert.Equal("malformed", refused.Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4 + 5)]
    public async Task AStreamEndingInsideAFrame_IsTruncated(int length)
    {
        var frame = Frame(new byte[10]);
        using var stream = new MemoryStream(frame[..length]);

        var refused = await Assert.ThrowsAsync<HelperProtocolException>(async () => await HelperFraming.ReadAsync(stream, Cancel));

        Assert.Equal("truncated", refused.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(HelperProtocol.MaxFrameBytes + 1)]
    public async Task WritingAnEmptyOrOversizedFrame_IsRefused_AndWritesNothing(int length)
    {
        using var stream = new MemoryStream();

        var refused = await Assert.ThrowsAsync<HelperProtocolException>(async () => await HelperFraming.WriteAsync(stream, new byte[length], Cancel));

        Assert.Equal(length == 0 ? "malformed" : "tooLarge", refused.Code);
        Assert.Equal(0, stream.Length);
    }
}
