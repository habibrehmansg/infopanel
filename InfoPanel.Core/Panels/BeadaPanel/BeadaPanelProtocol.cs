using System;
using System.Buffers.Binary;
using System.Text;

namespace InfoPanel.Panels.BeadaPanel;

/// <summary>STATUS-LINK message types (the control channel, endpoints 0x02 and 0x82).</summary>
public enum StatusLinkType : byte
{
    GetPanelInfo = 1,
    PanelLinkReset = 2,
    SetBacklight = 3,
}

/// <summary>PANEL-LINK message types (the stream channel, endpoint 0x01).</summary>
public enum PanelLinkType : byte
{
    LegacyCommand1 = 1,
    LegacyCommand2 = 2,
    ResetDisplay = 3,
    ClearScreen = 4,
    StartMediaStream = 5,
    EndMediaStream = 6,
}

/// <summary>
/// BeadaPanel's USB protocol as InfoPanel 1.4 speaks it, byte for byte: the ids, endpoints, timeouts and waits, the two
/// message kinds and their checksum, the stream's format string and the brightness byte.
/// </summary>
public static class BeadaPanelProtocol
{
    public const ushort VendorId = 0x4E58;
    public const ushort ProductId = 0x1001;
    public const int Configuration = 1;
    public const int Interface = 0;

    /// <summary>STATUS-LINK out.</summary>
    public const byte StatusOut = 0x02;

    /// <summary>STATUS-LINK in.</summary>
    public const byte StatusIn = 0x82;

    /// <summary>PANEL-LINK messages and pixels out.</summary>
    public const byte PanelOut = 0x01;

    /// <summary>Every transfer's timeout in a session.</summary>
    public const int SessionTimeoutMs = 2000;

    /// <summary>The info query's timeouts when a new panel is identified.</summary>
    public const int IdentifyTimeoutMs = 1000;

    /// <summary>The info reply's length.</summary>
    public const int InfoLength = 100;

    /// <summary>The wait before a session opens the panel.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The wait after the PANEL-LINK reset, before the backlight and the stream's start.</summary>
    public static readonly TimeSpan ResetDelay = TimeSpan.FromMilliseconds(1000);

    public const int StatusLinkHeaderLength = 20;
    public const int PanelLinkLength = 270;
    private const int PanelLinkChecked = 268;
    private const int FormatStringMax = 256;

    /// <summary>A STATUS-LINK message: the 20-byte header (its checksum over bytes 0–17) and the payload.</summary>
    public static byte[] StatusLink(StatusLinkType type, ReadOnlySpan<byte> payload = default)
    {
        var length = StatusLinkHeaderLength + payload.Length;
        var buffer = new byte[length];
        "STATUS-LINK"u8.CopyTo(buffer);
        buffer[11] = 1;           // version
        buffer[12] = (byte)type;
        buffer[13] = 0;           // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), 0); // sequence
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(16), (ushort)length);
        payload.CopyTo(buffer.AsSpan(StatusLinkHeaderLength));
        // Over the header's first 18 bytes only, as 1.4 sums it.
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(18), Checksum(buffer.AsSpan(0, 18)));
        return buffer;
    }

    /// <summary>A PANEL-LINK message: 270 bytes, the format string from byte 12 for types 1 and 5, the checksum at 268.</summary>
    public static byte[] PanelLink(PanelLinkType type, ReadOnlySpan<byte> format = default)
    {
        var buffer = new byte[PanelLinkLength];
        "PANEL-LINK"u8.CopyTo(buffer);
        buffer[10] = 1;           // version
        buffer[11] = (byte)type;
        if (type is PanelLinkType.LegacyCommand1 or PanelLinkType.StartMediaStream)
        {
            if (format.Length > FormatStringMax)
                throw new ArgumentException("A format string is at most 256 bytes.", nameof(format));
            format.CopyTo(buffer.AsSpan(12));
        }
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(PanelLinkChecked), Checksum(buffer.AsSpan(0, PanelLinkChecked)));
        return buffer;
    }

    /// <summary>The 16-bit one's-complement sum of little-endian words (a last odd byte alone), folded twice, inverted.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 2)
            sum += i + 1 < bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]) : bytes[i];
        sum = (sum >> 16) + (sum & 0xFFFF);
        sum += sum >> 16;
        return (ushort)~sum;
    }

    /// <summary>The stream's format string for a panel (write-through: the model's size; otherwise the reported one).</summary>
    public static string FormatString(BeadaPanelInfo info) =>
        info.WriteThrough
            ? FormattableString.Invariant($"image/x-raw, format=BGR16, width={info.Model.Width}, height={info.Model.Height}, framerate=0/1")
            : FormattableString.Invariant($"video/x-raw, format=RGB16, width={info.ResolutionX}, height={info.ResolutionY}, framerate=0/1");

    /// <summary>The backlight byte for a brightness of 0–100: PanelLink version 1 maps it to 25–100.</summary>
    public static byte BrightnessByte(int brightness, byte panelLinkVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(brightness, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(brightness, 100);
        return panelLinkVersion == 1 ? (byte)(brightness / 100.0 * 75 + 25) : (byte)brightness;
    }

    /// <summary>The message that starts the stream (PanelLink version 1: LegacyCommand1).</summary>
    public static byte[] StartStream(BeadaPanelInfo info) =>
        PanelLink(info.PanelLinkVersion == 1 ? PanelLinkType.LegacyCommand1 : PanelLinkType.StartMediaStream,
            Encoding.ASCII.GetBytes(FormatString(info)));

    /// <summary>The message that ends the stream (PanelLink version 1: LegacyCommand2).</summary>
    public static byte[] EndStream(BeadaPanelInfo info) =>
        PanelLink(info.PanelLinkVersion == 1 ? PanelLinkType.LegacyCommand2 : PanelLinkType.EndMediaStream);
}

