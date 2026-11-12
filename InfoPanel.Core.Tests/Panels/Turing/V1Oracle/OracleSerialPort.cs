namespace InfoPanel.Core.Tests.Panels.Turing.V1Oracle;

/// <summary>Stop bits, as <c>System.IO.Ports</c> names them (only <c>One</c> is used).</summary>
internal enum StopBits { One }

/// <summary>Parity, as <c>System.IO.Ports</c> names it (only <c>None</c> is used).</summary>
internal enum Parity { None }

/// <summary>
/// What <c>System.IO.Ports.SerialPort</c> was to the library's screens, as a recorder: its settings kept, each write's
/// bytes in order (<see cref="Writes"/>), each read served from <see cref="Input"/> (filled by <see cref="Respond"/>
/// after each write, or at once), throwing <see cref="TimeoutException"/> when nothing is there, as the port did once
/// its <c>ReadTimeout</c> passed. Test-only; no device.
/// </summary>
internal sealed class OracleSerialPort(string name)
{
    public string Name { get; } = name;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
    public int ReadTimeout { get; set; }
    public int WriteTimeout { get; set; }
    public int BaudRate { get; set; }
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; }
    public Parity Parity { get; set; }
    public bool IsOpen { get; private set; }

    public List<byte[]> Writes { get; } = [];
    public List<string> Calls { get; } = [];
    public List<byte> Input { get; } = [];

    /// <summary>Computes what the port receives after a write (null: nothing).</summary>
    public Func<byte[], byte[]?>? Respond { get; set; }

    public void Open()
    {
        IsOpen = true;
        Calls.Add("open");
    }

    public void Close()
    {
        IsOpen = false;
        Calls.Add("close");
    }

    public void DiscardInBuffer()
    {
        Input.Clear();
        Calls.Add("discard-in");
    }

    public void DiscardOutBuffer() => Calls.Add("discard-out");

    public void Write(byte[] buffer, int offset, int count)
    {
        var bytes = buffer.AsSpan(offset, count).ToArray();
        Writes.Add(bytes);
        Calls.Add("write");
        if (Respond?.Invoke(bytes) is { } answer)
            Input.AddRange(answer);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Calls.Add("read");
        if (Input.Count == 0)
            throw new TimeoutException();
        var n = Math.Min(count, Input.Count);
        Input.CopyTo(0, buffer, offset, n);
        Input.RemoveRange(0, n);
        return n;
    }

    public int ReadByte()
    {
        Calls.Add("read-byte");
        if (Input.Count == 0)
            throw new TimeoutException();
        var value = Input[0];
        Input.RemoveAt(0);
        return value;
    }
}
