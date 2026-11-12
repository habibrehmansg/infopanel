using System.Buffers.Binary;
using System.Text;

namespace InfoPanel.Panels.Thermalright;

/// <summary>
/// A ChiZhu init reply's fields: the identifier string (bytes 4–19, trailing NULs dropped; from 12 bytes), the PM (byte
/// 24) and SUB (byte 28) bytes, the serial (bytes 21–36 in hex when byte 17 is 0x10) and the DEV_INFO bytes (20, 32, 36,
/// 40; 0 when the reply is shorter).
/// </summary>
public sealed record ChiZhuReply(string? Identifier, byte? Pm, byte? Sub, string? Serial, byte Flags, byte Display1, byte Display2, byte Display3);

/// <summary>
/// A Trofeo (HID) init reply, its report id gone: whether its magic (<c>DA DB DC DD</c>) and connect ACK (byte 12 = 1)
/// are as expected (true when too short to tell), the PM (byte 5) and SUB (byte 4), the identifier (bytes 20–27, from 28
/// bytes, control characters dropped) and the serial (bytes 19–34 in hex when byte 15 is 0x10).
/// </summary>
public sealed record TrofeoReply(bool MagicOk, bool AckOk, byte? Pm, byte? Sub, string? Identifier, string? Serial);

/// <summary>
/// A TrofeoBulk (LY or LY1) init reply: whether its header (<c>03 FF</c>, byte 8 = 1) is as expected (true when too short
/// to tell), the serial (bytes 16–19 in hex), the size it reports (bytes 24–31, little-endian, each 1 to 4096) and bytes
/// 20 and 22.
/// </summary>
public sealed record TrofeoBulkReply(bool HeaderOk, string? Serial, int? Width, int? Height, byte? Byte20, byte? Byte22);

/// <summary>An ALi init reply: the device type (byte 0), its SUB (byte 1, else 0) and the serial (bytes 10–13 in hex).</summary>
public sealed record AliReply(byte DeviceType, byte Sub, string? Serial);

/// <summary>
/// ChiZhu over USB bulk (InfoPanel 1.4's <c>ThermalrightPanelDeviceTask</c>, after TRCC): a 64-byte init command, a reply
/// of up to 1024 bytes (<c>A1 A2 A3 A4</c> at bytes 4–7 while the panel boots: 1.4 asked again 3 s later, 5 times in
/// all), then each frame as one write of a 64-byte header and the pixels, with a zero-length packet after a write whose
/// length is a multiple of 512, and 15 ms between frames.
/// </summary>
public static class ChiZhuProtocol
{
    public const int HeaderSize = 64;
    public const int ReplySize = 1024;
    public const int BootTries = 5;
    public static readonly TimeSpan BootRetryDelay = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(15);

    private static ReadOnlySpan<byte> Magic => [0x12, 0x34, 0x56, 0x78];

    /// <summary>The init command: the magic, then 1 (little-endian) at byte 56.</summary>
    public static byte[] InitCommand()
    {
        var command = new byte[HeaderSize];
        Magic.CopyTo(command);
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(56), 1);
        return command;
    }

    /// <summary>Whether a reply says the panel is still booting (<c>A1 A2 A3 A4</c> at bytes 4–7).</summary>
    public static bool IsBooting(ReadOnlySpan<byte> reply) =>
        reply.Length >= 8 && reply[4] == 0xA1 && reply[5] == 0xA2 && reply[6] == 0xA3 && reply[7] == 0xA4;

    public static ChiZhuReply ParseReply(ReadOnlySpan<byte> reply)
    {
        var length = reply.Length;
        return new ChiZhuReply(
            Identifier: length >= 12 ? Encoding.ASCII.GetString(reply.Slice(4, Math.Min(16, length - 4))).TrimEnd('\0') : null,
            Pm: length >= 25 ? reply[24] : null,
            Sub: length >= 29 ? reply[28] : null,
            Serial: length >= 37 && reply[17] == 0x10 ? Convert.ToHexString(reply.Slice(21, 16)) : null,
            Flags: length >= 21 ? reply[20] : (byte)0,
            Display1: length >= 33 ? reply[32] : (byte)0,
            Display2: length >= 37 ? reply[36] : (byte)0,
            Display3: length >= 41 ? reply[40] : (byte)0);
    }

    /// <summary>
    /// A frame's header: the magic; the format (3 for RGB565 of either order, 2 for JPEG), the width and the height at
    /// bytes 4, 8 and 12; the frame type 2 at byte 56, whatever the format; the data's length at byte 60. Little-endian.
    /// </summary>
    public static byte[] DisplayHeader(int width, int height, int dataSize, ThermalrightPixels pixels)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), pixels == ThermalrightPixels.Jpeg ? 0x02 : 0x03);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(56), 0x02);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60), dataSize);
        return header;
    }

    /// <summary>A frame's one write: its header, then its bytes.</summary>
    public static byte[] FramePacket(ReadOnlySpan<byte> frame, int width, int height, ThermalrightPixels pixels)
    {
        var packet = new byte[HeaderSize + frame.Length];
        DisplayHeader(width, height, frame.Length, pixels).CopyTo(packet, 0);
        frame.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    /// <summary>Whether a write of this length is followed by a zero-length packet (a multiple of 512 bytes).</summary>
    public static bool NeedsZeroLengthPacket(int packetLength) => packetLength % 512 == 0;
}

/// <summary>
/// Trofeo (InfoPanel 1.4's <c>HidPanelDevice</c>, after Thermal Engine): 512-byte packets, each sent over HID as a
/// 513-byte output report with report id 0 (or, on a panel whose bulk interface has WinUSB, over bulk as is). The init
/// packet is the magic <c>DA DB DC DD</c> with 1 at byte 12; a frame is a header packet (the magic, command 2 at byte 4,
/// 1 at byte 6 for RGB565, the width and height as 16-bit values at bytes 8 and 10, type 2 at byte 12, the length at
/// byte 16, the first 492 bytes at byte 20), then the rest in 512-byte packets, the last one padded with zeros.
/// </summary>
public static class TrofeoProtocol
{
    public const int PacketSize = 512;
    public const int HeaderDataOffset = 20;

    private static ReadOnlySpan<byte> Magic => [0xDA, 0xDB, 0xDC, 0xDD];

    public static byte[] InitPacket()
    {
        var packet = new byte[PacketSize];
        Magic.CopyTo(packet);
        packet[12] = 0x01;
        return packet;
    }

    /// <summary>A packet as a HID output report: report id 0, then the packet.</summary>
    public static byte[] HidReport(ReadOnlySpan<byte> packet)
    {
        var report = new byte[packet.Length + 1];
        packet.CopyTo(report.AsSpan(1));
        return report;
    }

    /// <summary>An input report without its report id (the first byte); null for an empty read.</summary>
    public static byte[]? FromHidReport(ReadOnlySpan<byte> report) => report.Length > 0 ? report[1..].ToArray() : null;

    public static TrofeoReply ParseReply(ReadOnlySpan<byte> reply)
    {
        var length = reply.Length;
        var magicOk = length < 4 || reply[..4].SequenceEqual(Magic);
        var ackOk = length < 13 || reply[12] == 0x01;
        if (length < 6)
            return new TrofeoReply(magicOk, ackOk, null, null, null, null);

        string? identifier = null;
        if (length >= 28)
        {
            var text = Encoding.ASCII.GetString(reply.Slice(20, Math.Min(8, length - 20)));
            identifier = new string(text.Where(c => c >= ' ').ToArray());
        }
        var serial = length >= 35 && reply[15] == 0x10 ? Convert.ToHexString(reply.Slice(19, 16)) : null;
        return new TrofeoReply(magicOk, ackOk, reply[5], reply[4], identifier, serial);
    }

    /// <summary>A frame's packets, header first.</summary>
    public static IReadOnlyList<byte[]> FramePackets(ReadOnlySpan<byte> frame, int width, int height, ThermalrightPixels pixels)
    {
        var packets = new List<byte[]>();
        var header = new byte[PacketSize];
        Magic.CopyTo(header);
        header[4] = 0x02;
        if (pixels != ThermalrightPixels.Jpeg)
            header[6] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)height);
        header[12] = 0x02;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), frame.Length);
        var first = Math.Min(frame.Length, PacketSize - HeaderDataOffset);
        frame[..first].CopyTo(header.AsSpan(HeaderDataOffset));
        packets.Add(header);

        for (var offset = first; offset < frame.Length; offset += PacketSize)
        {
            var chunk = new byte[PacketSize];
            frame.Slice(offset, Math.Min(frame.Length - offset, PacketSize)).CopyTo(chunk);
            packets.Add(chunk);
        }
        return packets;
    }
}

/// <summary>
/// TrofeoBulk over USB bulk (InfoPanel 1.4, after TRCC): LY (the 9.16" and 11.3", 2048-byte init) and LY1 (512-byte
/// init). The init packet is <c>02 FF</c> with 1 at byte 8; the reply (512 bytes, LY1 511) starts <c>03 FF</c>. A JPEG
/// frame is cut into 496-byte pieces, each in a 512-byte sub-packet behind a 16-byte header (<c>01 FF</c>, the JPEG's
/// length, the piece's length, the command 1 for LY or 2 for LY1, the piece count and the piece's index). LY pads the
/// sub-packets to a multiple of four and writes 4096 bytes at a time; LY1 writes them all at once. Each frame is
/// acknowledged by a read (500 ms; five failures in a row end the session). JPEGs are capped at 230,000 bytes.
/// </summary>
public static class TrofeoBulkProtocol
{
    public const int LyInitSize = 2048;
    public const int Ly1InitSize = 512;
    public const int ReplySize = 512;
    public const int Ly1ReplySize = 511;
    public const int TransferSize = 4096;
    public const int SubPacketSize = 512;
    public const int SubHeaderSize = 16;
    public const int SubDataSize = SubPacketSize - SubHeaderSize;
    public const int MaxAckFailures = 5;

    public static byte[] InitPacket(ThermalrightProtocol protocol)
    {
        var packet = new byte[protocol switch
        {
            ThermalrightProtocol.TrofeoBulk => LyInitSize,
            ThermalrightProtocol.TrofeoBulkLy1 => Ly1InitSize,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        }];
        packet[0] = 0x02;
        packet[1] = 0xFF;
        packet[8] = 0x01;
        return packet;
    }

    public static TrofeoBulkReply ParseReply(ReadOnlySpan<byte> reply)
    {
        var length = reply.Length;
        var headerOk = length < 9 || (reply[0] == 0x03 && reply[1] == 0xFF && reply[8] == 0x01);
        int? width = null, height = null;
        if (length >= 32)
        {
            var w = BinaryPrimitives.ReadInt32LittleEndian(reply[24..]);
            var h = BinaryPrimitives.ReadInt32LittleEndian(reply[28..]);
            if (w is > 0 and <= 4096 && h is > 0 and <= 4096)
                (width, height) = (w, h);
        }
        return new TrofeoBulkReply(
            headerOk,
            length >= 20 ? Convert.ToHexString(reply.Slice(16, 4)) : null,
            width,
            height,
            length >= 21 ? reply[20] : null,
            length >= 23 ? reply[22] : null);
    }

    /// <summary>The longest JPEG a frame's 16-bit piece count can carry: 65,535 pieces of 496 bytes.</summary>
    public const int MaxFrameJpegBytes = ushort.MaxValue * SubDataSize;

    /// <summary>An LY frame's sub-packets, padded with empty ones to a multiple of four.</summary>
    public static byte[] LyFrame(ReadOnlySpan<byte> jpeg)
    {
        var pieces = Pieces(jpeg.Length);
        var padded = (pieces + 3) / 4 * 4;
        return Frame(jpeg, pieces, padded, 0x01);
    }

    /// <summary>An LY frame's writes: 4096 bytes each, the last one what is left.</summary>
    public static IReadOnlyList<(int Offset, int Length)> LyTransfers(int frameLength)
    {
        var transfers = new List<(int, int)>();
        for (var offset = 0; offset < frameLength; offset += TransferSize)
            transfers.Add((offset, Math.Min(TransferSize, frameLength - offset)));
        return transfers;
    }

    /// <summary>An LY1 frame's sub-packets, unpadded, written at once.</summary>
    public static byte[] Ly1Frame(ReadOnlySpan<byte> jpeg)
    {
        var pieces = Pieces(jpeg.Length);
        return Frame(jpeg, pieces, pieces, 0x02);
    }

    // The frame's piece count; a JPEG longer than its 16-bit count holds is refused before anything is built (1.4 wrote
    // the count through a ushort cast, which would wrap).
    private static int Pieces(int jpegLength)
    {
        if (jpegLength > MaxFrameJpegBytes)
            throw new ArgumentOutOfRangeException(nameof(jpegLength), jpegLength, $"A frame carries at most {MaxFrameJpegBytes} bytes of JPEG.");
        return (jpegLength + SubDataSize - 1) / SubDataSize;
    }

    private static byte[] Frame(ReadOnlySpan<byte> jpeg, int pieces, int subPackets, byte command)
    {
        var frame = new byte[subPackets * SubPacketSize];
        var offset = 0;
        for (var i = 0; i < pieces; i++)
        {
            var at = frame.AsSpan(i * SubPacketSize);
            var size = Math.Min(SubDataSize, jpeg.Length - offset);
            at[0] = 0x01;
            at[1] = 0xFF;
            BinaryPrimitives.WriteInt32LittleEndian(at[2..], jpeg.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(at[6..], (ushort)size);
            at[8] = command;
            BinaryPrimitives.WriteUInt16LittleEndian(at[9..], (ushort)pieces);
            BinaryPrimitives.WriteUInt16LittleEndian(at[11..], (ushort)i);
            jpeg.Slice(offset, size).CopyTo(at[SubHeaderSize..]);
            offset += size;
        }
        return frame;
    }
}

/// <summary>
/// ALi over USB bulk (InfoPanel 1.4): a 1040-byte init (a 16-byte <c>F5</c> header and 1024 zeros), a reply of up to 1024
/// bytes (device type 54: 320 × 240, else 320 × 320), then each frame as one write of a 16-byte <c>F5 01</c> header with
/// the frame's size at byte 12 and the RGB565 pixels (little-endian), and a 16-byte acknowledgement read (100 ms).
/// </summary>
public static class AliProtocol
{
    public const int HeaderSize = 16;
    public const int AckSize = 16;

    /// <summary>The frame size a session keeps when its init isn't answered: 320 × 320 in RGB565 (1.4's default).</summary>
    public const int DefaultFrameBytes = 320 * 320 * 2;

    private static ReadOnlySpan<byte> InitHeader => [0xF5, 0x00, 0x01, 0x00, 0xBC, 0xFF, 0xB6, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00];

    private static ReadOnlySpan<byte> FrameHeader => [0xF5, 0x01, 0x01, 0x00, 0xBC, 0xFF, 0xB6, 0xC8];

    public static byte[] InitPacket()
    {
        var packet = new byte[HeaderSize + 1024];
        InitHeader.CopyTo(packet);
        return packet;
    }

    public static AliReply ParseReply(ReadOnlySpan<byte> reply) =>
        new(reply.Length > 0 ? reply[0] : (byte)0, reply.Length >= 2 ? reply[1] : (byte)0,
            reply.Length >= 14 ? Convert.ToHexString(reply.Slice(10, 4)) : null);

    /// <summary>A frame's one write: the header, then <paramref name="frameSize"/> bytes of pixels (cut, or padded with zeros).</summary>
    public static byte[] FramePacket(ReadOnlySpan<byte> pixels, int frameSize)
    {
        var packet = new byte[HeaderSize + frameSize];
        FrameHeader.CopyTo(packet);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12), frameSize);
        pixels[..Math.Min(pixels.Length, frameSize)].CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }
}
