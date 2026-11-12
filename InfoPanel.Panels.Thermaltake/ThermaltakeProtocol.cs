using System.Globalization;
using System.Text;

namespace InfoPanel.Panels.Thermaltake;

/// <summary>
/// The panels' HID protocol, as InfoPanel 1.4 spoke it (its <c>ThermaltakeHidDevice</c>): every transfer is a 1024-byte
/// packet sent as a 1025-byte output report (report id 0 first: the device declares none). Commands are text, framed
/// <c>5A 00 [length] [text] [checksum] 5A 00</c>, the length the text's plus 5 and the checksum the low byte of the
/// length plus the text's bytes: a request line (<c>POST conn 1</c>), <c>SeqNumber</c> and <c>Date</c> headers, a
/// content type and length when there is a body, a blank line, the body. A JPEG goes in chunks of 1000 bytes, each after a
/// 24-byte header (<c>5C 03 FD 00</c>, the chunk count and the chunk's index big-endian, <c>01</c>, zeros). A reply is an
/// input report whose printable text says <c>200</c> when the command was taken.
/// </summary>
internal static class ThermaltakeProtocol
{
    /// <summary>A packet's bytes on the wire.</summary>
    public const int PacketSize = 1024;

    /// <summary>A packet as an output report: report id 0, then the packet.</summary>
    public const int ReportSize = PacketSize + 1;

    /// <summary>An image chunk's header.</summary>
    public const int ImageHeaderSize = 24;

    /// <summary>The JPEG bytes a chunk carries.</summary>
    public const int ImagePayload = PacketSize - ImageHeaderSize;

    /// <summary>The most chunks a frame can have: its count is 16 bits.</summary>
    public const int MaxChunks = ushort.MaxValue;

    /// <summary>The longest command text: the length byte holds the text's length plus 5.</summary>
    public const int MaxContent = byte.MaxValue - FrameOverhead;

    /// <summary>A session's first <c>SeqNumber</c>; each command takes the next.</summary>
    public const int FirstSeqNumber = 100;

    /// <summary>The HID collection a panel is driven through: one whose output report holds a whole packet.</summary>
    public const int MinOutputReport = ReportSize;

    /// <summary>1.4's write timeout.</summary>
    public const int WriteTimeoutMs = 5000;

    /// <summary>1.4's read timeout.</summary>
    public const int ReadTimeoutMs = 2000;

    /// <summary>How long after a command its reply is read (1.4's pause).</summary>
    public static readonly TimeSpan ReplyWait = TimeSpan.FromMilliseconds(50);

    private const byte FrameMarker = 0x5A;
    private const int FrameOverhead = 5;

    /// <summary>
    /// <paramref name="content"/> framed as a packet: <c>5A 00 [length] [content] [checksum] 5A 00</c>, zeros after.
    /// <see cref="ArgumentOutOfRangeException"/> for a content longer than <see cref="MaxContent"/>, whose length the
    /// byte can't hold.
    /// </summary>
    public static byte[] Frame(ReadOnlySpan<byte> content)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(content.Length, MaxContent, nameof(content));
        var length = (byte)(content.Length + FrameOverhead);
        var sum = (int)length;
        foreach (var b in content)
            sum += b;
        var packet = new byte[PacketSize];
        packet[0] = FrameMarker;
        packet[1] = 0x00;
        packet[2] = length;
        content.CopyTo(packet.AsSpan(3));
        packet[3 + content.Length] = (byte)(sum & 0xFF);
        packet[4 + content.Length] = FrameMarker;
        packet[5 + content.Length] = 0x00;
        return packet;
    }

    /// <summary>
    /// A command's packet: <paramref name="command"/>, <c>SeqNumber</c> and <c>Date</c> (Unix milliseconds), and with
    /// <paramref name="hasBody"/> a content type and length when <paramref name="contentType"/> is given, then the blank
    /// line and the body.
    /// </summary>
    public static byte[] Command(string command, int seqNumber, long dateMs, string? body = null, string? contentType = null, bool hasBody = true)
    {
        var text = new StringBuilder();
        text.Append(command).Append("\r\n");
        text.Append(CultureInfo.InvariantCulture, $"SeqNumber={seqNumber}\r\n");
        text.Append(CultureInfo.InvariantCulture, $"Date={dateMs}\r\n");
        if (hasBody && contentType != null)
        {
            text.Append(CultureInfo.InvariantCulture, $"ContentType={contentType}\r\n");
            text.Append(CultureInfo.InvariantCulture, $"ContentLength={body?.Length ?? 0}\r\n");
        }
        text.Append("\r\n");
        if (hasBody && body != null)
            text.Append(body);
        return Frame(Encoding.ASCII.GetBytes(text.ToString()));
    }

    /// <summary>The handshake's first command, <c>POST conn 1</c>, with no body.</summary>
    public static byte[] Conn(int seqNumber, long dateMs) => Command("POST conn 1", seqNumber, dateMs, hasBody: false);

    /// <summary><c>POST realtimeDisplay 1</c>: on, the handshake's second command; off, the screen-off (the panel back to its own screen).</summary>
    public static byte[] RealtimeDisplay(bool enable, int seqNumber, long dateMs) =>
        Command("POST realtimeDisplay 1", seqNumber, dateMs, body: enable ? "{\"enable\":true}" : "{\"enable\":false}", contentType: "json");

    /// <summary>The chunks a JPEG of <paramref name="length"/> bytes takes: its thousands, rounded up.</summary>
    public static int ChunkCount(int length) => (int)(((long)length + ImagePayload - 1) / ImagePayload);

    /// <summary>
    /// Writes chunk <paramref name="chunk"/> of <paramref name="totalChunks"/> of <paramref name="jpeg"/> into
    /// <paramref name="report"/> (<see cref="ReportSize"/> bytes, every one written): report id 0, the header, the
    /// chunk's bytes, zeros after.
    /// </summary>
    public static void ImageReport(ReadOnlySpan<byte> jpeg, int chunk, int totalChunks, Span<byte> report)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalChunks, MaxChunks);
        ArgumentOutOfRangeException.ThrowIfNegative(chunk);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chunk, totalChunks);
        ArgumentOutOfRangeException.ThrowIfLessThan(report.Length, ReportSize);
        var offset = chunk * ImagePayload;
        var payload = jpeg.Slice(offset, Math.Min(ImagePayload, jpeg.Length - offset));
        report[..ReportSize].Clear();
        var packet = report[1..];
        packet[0] = 0x5C; // image data
        packet[1] = 0x03;
        packet[2] = 0xFD;
        packet[3] = 0x00; // area
        packet[4] = (byte)(totalChunks >> 8);
        packet[5] = (byte)totalChunks;
        packet[6] = (byte)(chunk >> 8);
        packet[7] = (byte)chunk;
        packet[8] = 0x01;
        payload.CopyTo(packet[ImageHeaderSize..]);
    }

    /// <summary>A packet as its output report: report id 0, then the packet.</summary>
    public static byte[] Report(ReadOnlySpan<byte> packet)
    {
        var report = new byte[ReportSize];
        packet[..Math.Min(packet.Length, PacketSize)].CopyTo(report.AsSpan(1));
        return report;
    }

    /// <summary>
    /// A reply's text, as 1.4 read it: of a report of more than 4 bytes, its printable ASCII and line ends after the
    /// report id, other bytes skipped; null for a shorter report or one with no such byte.
    /// </summary>
    public static string? ResponseText(ReadOnlySpan<byte> report, int read)
    {
        if (read <= 4)
            return null;
        var text = new StringBuilder();
        foreach (var b in report[1..read])
        {
            if (b is >= 32 and <= 126 or 13 or 10)
                text.Append((char)b);
        }
        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>Whether a reply's text says the command was taken: it holds <c>200</c>.</summary>
    public static bool Accepted(string? response) => response != null && response.Contains("200", StringComparison.Ordinal);
}
