using InfoPanel.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace InfoPanel.Tests;

/// <summary>
/// A request body is held to its size limit as it is read (spec/docs/limits.md, "Request limits"): with or without a
/// <c>Content-Length</c>, at most the limit plus one byte is read, and a body over it is refused without keeping it.
/// </summary>
public class BodyReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A body of <paramref name="length"/> bytes that can't seek and counts what is read of it.</summary>
    internal sealed class CountingBody(long length) : Stream
    {
        public long Consumed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, length - Consumed);
            buffer[..n].Fill((byte)' ');
            Consumed += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpRequest Request(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = body;
        return context.Request;
    }

    [Theory]
    [InlineData(4_096)]
    [InlineData(65_536)]
    [InlineData(100_000)] // more than one read's buffer
    public async Task A_body_without_a_length_is_read_to_the_limit_plus_one_byte_and_not_kept(int limit)
    {
        // 1 MiB on the wire, no Content-Length: the read stops one byte past the limit, and nothing over it is returned.
        var over = new CountingBody(1_048_576);
        var (bytes, isOver) = await ApiResults.ReadBodyAsync(Request(over), limit, Ct);
        Assert.True(isOver);
        Assert.Empty(bytes);
        Assert.Equal(limit + 1, over.Consumed);

        var at = new CountingBody(limit);
        (bytes, isOver) = await ApiResults.ReadBodyAsync(Request(at), limit, Ct);
        Assert.False(isOver);
        Assert.Equal(limit, bytes.Length);
        Assert.All(bytes, b => Assert.Equal((byte)' ', b));
    }

    [Fact]
    public async Task A_body_whose_length_is_over_the_limit_is_not_read()
    {
        var body = new CountingBody(4_097);
        var request = Request(body);
        request.ContentLength = 4_097;
        var (bytes, isOver) = await ApiResults.ReadBodyAsync(request, 4_096, Ct);
        Assert.Equal((true, 0, 0L), (isOver, bytes.Length, body.Consumed));
    }

    [Theory]
    [InlineData(10, 5_000, true)]   // header smaller than the body, body over the limit: refused by the body's real size
    [InlineData(10, 4_096, false)]  // header smaller than the body, body at the limit: accepted
    [InlineData(10, 100, false)]    // header smaller than the body, body within the limit: accepted whole
    [InlineData(4_000, 100, false)] // header larger than the body (under the limit): accepted as the 100 bytes that arrive
    [InlineData(4_096, 4_097, true)] // header at the limit, body one byte over: refused
    public async Task A_lying_content_length_does_not_decide_a_body_that_is_read(long declared, int actual, bool refused)
    {
        var body = new CountingBody(actual);
        var request = Request(body);
        request.ContentLength = declared;
        var (bytes, isOver) = await ApiResults.ReadBodyAsync(request, 4_096, Ct);
        Assert.Equal(refused, isOver);
        Assert.Equal(refused ? 0 : actual, bytes.Length);
        Assert.True(body.Consumed <= 4_097, $"read {body.Consumed} bytes");
    }
}
