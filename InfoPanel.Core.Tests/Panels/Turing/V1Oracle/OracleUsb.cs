namespace InfoPanel.Core.Tests.Panels.Turing.V1Oracle;

/// <summary>
/// What LibUsbDotNet's endpoint reader and writer were to 1.4.x's USB classes, as a recorder: each write's bytes in
/// order (<see cref="Writes"/>), each read answered from <see cref="Replies"/> (none queued: a timeout), each
/// <c>ReadFlush</c> counted where it falls (<see cref="Calls"/>). Test-only; no device.
/// </summary>
internal sealed class OracleUsb
{
    public enum ErrorCode { None, IoTimedOut }

    public List<byte[]> Writes { get; } = [];

    /// <summary>Every call in order: <c>flush</c>, <c>write {hex}</c>, <c>read</c>.</summary>
    public List<string> Calls { get; } = [];

    public Queue<byte[]> Replies { get; } = new();

    public void ReadFlush() => Calls.Add("flush");

    public ErrorCode Write(byte[] data, int timeout, out int transferred)
    {
        Writes.Add(data.ToArray());
        Calls.Add("write " + Convert.ToHexStringLower(data));
        transferred = data.Length;
        return ErrorCode.None;
    }

    public ErrorCode Write(byte[] data, int offset, int count, int timeout, out int transferred)
    {
        var bytes = data.AsSpan(offset, count).ToArray();
        Writes.Add(bytes);
        Calls.Add("write " + Convert.ToHexStringLower(bytes));
        transferred = count;
        return ErrorCode.None;
    }

    public ErrorCode Read(byte[] buffer, int timeout, out int transferred) => Read(buffer, 0, buffer.Length, timeout, out transferred);

    public ErrorCode Read(byte[] buffer, int offset, int count, int timeout, out int transferred)
    {
        Calls.Add("read");
        if (Replies.Count == 0)
        {
            transferred = 0;
            return ErrorCode.IoTimedOut;
        }
        var reply = Replies.Dequeue();
        transferred = Math.Min(count, reply.Length);
        reply.AsSpan(0, transferred).CopyTo(buffer.AsSpan(offset));
        return ErrorCode.None;
    }
}
