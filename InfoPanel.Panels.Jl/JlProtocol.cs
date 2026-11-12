using System.Text;
using System.Text.Json;

namespace InfoPanel.Panels.Jl;

/// <summary>
/// The Hongtai reference firmware's serial protocol, as InfoPanel 1.4 spoke it (<c>JlPanel/JlSerialDevice.cs</c>): every
/// command is framed <c>55 AA [length, 16 bits LE] [command] [payload] [checksum, 16 bits LE]</c>, the length the whole
/// frame's (the payload's plus 7) and the checksum the low 16 bits of the sum of every byte before it; a reply has the same
/// shape. A live frame is the keep-alive (<c>0x11</c>), then the JPEG in its envelope
/// (<c>[length, 32 bits LE] [JPEG] [sum, 16 bits LE]</c>, the sum over the length's bytes and the JPEG's), then
/// <c>FF D9 FF D9</c>. Pure functions only: the session writes what they build.
/// </summary>
internal static class JlProtocol
{
    /// <summary>Restart the panel (1.4 never sent it).</summary>
    public const byte Restart = 0x01;

    /// <summary>The backlight, one byte 0–100.</summary>
    public const byte SetLight = 0x03;

    /// <summary>The device info, answered with JSON.</summary>
    public const byte GetDeviceInfo = 0x06;

    /// <summary>The live pipeline's keep-alive ("live frame ack" / refresh).</summary>
    public const byte KeepAliveCommand = 0x11;

    /// <summary>A firmware preset by name, UTF-8 (1.4 never sent it).</summary>
    public const byte SetRegion = 0x20;

    /// <summary>Releases the live pipeline (firmware 3.1 and later).</summary>
    public const byte CloseCommand = 0x21;

    /// <summary>1.4's rate (2,000,000 baud); a USB CDC port ignores it.</summary>
    public const int Baud = 2_000_000;

    /// <summary>The nominal rate asked for when a platform refuses <see cref="Baud"/>.</summary>
    public const int FallbackBaud = 115_200;

    /// <summary>1.4's read timeout: how long a reply may take.</summary>
    public const int ReadTimeoutMs = 2000;

    /// <summary>1.4's write timeout.</summary>
    public const int WriteTimeoutMs = 5000;

    /// <summary>The longest reply frame 1.4 took (64 KiB).</summary>
    public const int MaxReplyLength = 64 * 1024;

    /// <summary>A device info's <c>status</c> when the panel answered.</summary>
    public const int OkStatus = 200;

    /// <summary>1.4's pause after the port opens, and again after the pipeline reset.</summary>
    public static readonly TimeSpan SettleWait = TimeSpan.FromMilliseconds(200);

    /// <summary>Written once the port opens: flushes the firmware's parser.</summary>
    public static ReadOnlySpan<byte> PipelineReset => [0xFF, 0xD9, 0xFF, 0xD9, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Written after each frame's envelope: two JPEG end-of-image markers.</summary>
    public static ReadOnlySpan<byte> FrameTerminator => [0xFF, 0xD9, 0xFF, 0xD9];

    /// <summary>The keep-alive's frame, <c>55 AA 07 00 11 17 01</c>.</summary>
    public static byte[] KeepAliveFrame { get; } = Frame(KeepAliveCommand, []);

    /// <summary>The device info query's frame.</summary>
    public static byte[] DeviceInfoQuery { get; } = Frame(GetDeviceInfo, []);

    /// <summary>The close's frame.</summary>
    public static byte[] CloseFrame { get; } = Frame(CloseCommand, []);

    /// <summary>The restart's frame.</summary>
    public static byte[] RestartFrame { get; } = Frame(Restart, []);

    /// <summary>A command's frame: <c>55 AA</c>, the frame's length, the command, the payload, the checksum.</summary>
    public static byte[] Frame(byte command, ReadOnlySpan<byte> payload)
    {
        var length = 7 + payload.Length;
        var frame = new byte[length];
        frame[0] = 0x55;
        frame[1] = 0xAA;
        frame[2] = (byte)length;
        frame[3] = (byte)(length >> 8);
        frame[4] = command;
        payload.CopyTo(frame.AsSpan(5));
        ushort sum = 0;
        for (var i = 0; i < length - 2; i++)
            sum += frame[i];
        frame[length - 2] = (byte)sum;
        frame[length - 1] = (byte)(sum >> 8);
        return frame;
    }

    /// <summary>The backlight at <paramref name="percent"/>, clamped to 0–100.</summary>
    public static byte[] Light(int percent) => Frame(SetLight, [(byte)Math.Clamp(percent, 0, 100)]);

    /// <summary>The preset <paramref name="region"/>, its name in UTF-8.</summary>
    public static byte[] Region(string region) => Frame(SetRegion, Encoding.UTF8.GetBytes(region));

    /// <summary>The envelope's length for a JPEG of <paramref name="jpegLength"/> bytes.</summary>
    public static int EnvelopeLength(int jpegLength) => 4 + jpegLength + 2;

    /// <summary>
    /// Writes <paramref name="jpeg"/>'s envelope into the start of <paramref name="envelope"/> (at least
    /// <see cref="EnvelopeLength"/> bytes) and answers its length: the JPEG's length (32 bits LE), the JPEG, and the low
    /// 16 bits of the sum of every byte before it (LE).
    /// </summary>
    public static int Envelope(ReadOnlySpan<byte> jpeg, Span<byte> envelope)
    {
        var length = EnvelopeLength(jpeg.Length);
        envelope[0] = (byte)jpeg.Length;
        envelope[1] = (byte)(jpeg.Length >> 8);
        envelope[2] = (byte)(jpeg.Length >> 16);
        envelope[3] = (byte)(jpeg.Length >> 24);
        jpeg.CopyTo(envelope[4..]);
        ushort sum = 0;
        for (var i = 0; i < 4 + jpeg.Length; i++)
            sum += envelope[i];
        envelope[length - 2] = (byte)sum;
        envelope[length - 1] = (byte)(sum >> 8);
        return length;
    }

    /// <summary>
    /// The reply in <paramref name="received"/>, the bytes read so far, read as 1.4 read one: anything before a <c>55</c>
    /// is passed over; <c>AA</c> must follow it, and the frame's length must be 7 to <see cref="MaxReplyLength"/>, else
    /// <see cref="JlReply.Invalid"/>; the command and the checksum are not looked at. <see cref="JlReply.Incomplete"/>
    /// while bytes it needs haven't come; <see cref="JlReply.Complete"/> with the <paramref name="payload"/>.
    /// </summary>
    public static JlReply ParseReply(ReadOnlySpan<byte> received, out byte[]? payload)
    {
        payload = null;
        var start = received.IndexOf((byte)0x55);
        if (start < 0)
            return JlReply.Incomplete;
        var frame = received[start..];
        if (frame.Length < 2)
            return JlReply.Incomplete;
        if (frame[1] != 0xAA)
            return JlReply.Invalid;
        if (frame.Length < 4)
            return JlReply.Incomplete;
        var length = frame[2] | frame[3] << 8;
        if (length is < 7 or > MaxReplyLength)
            return JlReply.Invalid;
        if (frame.Length < length) // the command, the payload and both checksum bytes
            return JlReply.Incomplete;
        payload = frame.Slice(5, length - 7).ToArray();
        return JlReply.Complete;
    }

    private static readonly JsonSerializerOptions InfoJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The device info in a reply's <paramref name="payload"/>, as 1.4 read it: UTF-8 JSON, NULs trimmed from both ends,
    /// names in any case; null when the payload is shorter than 5 bytes or isn't such JSON.
    /// </summary>
    public static JlDeviceInfo? ParseDeviceInfo(byte[]? payload)
    {
        if (payload == null || payload.Length < 5)
            return null;
        try
        {
            return JsonSerializer.Deserialize<JlDeviceInfo>(Encoding.UTF8.GetString(payload).Trim('\0'), InfoJson);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>What the bytes read so far come to: a reply still coming, one 1.4 would refuse, or a whole one.</summary>
internal enum JlReply { Incomplete, Invalid, Complete }

/// <summary>The device info's JSON, as 1.4 read it: the status (200 when answered), the panel's id, model, firmware, size and angle, and its preset.</summary>
internal sealed class JlDeviceInfo
{
    public int Status { get; set; }
    public string? Uid { get; set; }
    public string? Model { get; set; }
    public string? Version { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Angle { get; set; }
    public string? Region { get; set; }
}
