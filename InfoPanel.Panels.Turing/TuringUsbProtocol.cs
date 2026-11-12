// The Turing variant ported from TuringSmartScreenLib's LcdDriver.TuringSmartScreen/ScreenDevice.cs:
// Copyright (c) 2021 machi_pon (https://github.com/usausa/turing-smart-screen; the full notice is in LICENSES.md). The Lian
// Li variant ported from InfoPanel 1.4's LianLiPanel/LianLiUsbScreenDevice.cs.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace InfoPanel.Panels.Turing;

/// <summary>
/// Which of the two USB variants a panel speaks: <see cref="Turing"/>, as 1.4 drove Turing's USB panels (the
/// TuringSmartScreenLib's <c>ScreenDevice</c>, built into 1.4's <c>Lib/LcdDriver.TuringSmartScreen.dll</c>), or
/// <see cref="LianLi"/>, as 1.4's own <c>LianLiUsbScreenDevice</c> drove Lian Li's.
/// </summary>
internal enum UsbVariant { Turing, LianLi }

/// <summary>
/// The USB protocol Turing's USB panels and Lian Li's share: a 500-byte command (byte 0 its id, bytes 2–3 <c>26 109</c>,
/// bytes 4–7 a timestamp little-endian, then its arguments from byte 8), encrypted with DES-CBC under the key and IV
/// <c>slv3tuzx</c> into a 512-byte packet that ends <c>A1 1A</c>; an image's command carries its length big-endian and
/// its bytes follow. The two variants differ in the cipher's padding (Turing pads the command with zeros to 504 bytes
/// and adds none, Lian Li adds PKCS7), the stop-media command (111, 123), and how a command is sent and answered
/// (<see cref="TuringUsbSession"/>).
/// </summary>
internal static class TuringUsbProtocol
{
    public const int CommandSize = 500;
    public const int PacketSize = 512;

    /// <summary>Lian Li's largest image (<c>LianLiUsbScreenDevice.cs:33</c>); the library's Turing variant has none.</summary>
    public const int MaxImagePayload = 512_000;

    public const byte Sync = 10;
    public const byte Brightness = 14;
    public const byte FrameRate = 15;
    public const byte SyncClock = 51;
    public const byte StopClock = 52;
    public const byte Jpeg = 101;
    public const byte Png = 102;
    public const byte QueryBlock = 122;

    private static readonly byte[] KeyIv = "slv3tuzx"u8.ToArray();

    /// <summary>The stop-media command: the library's 111 for Turing, 123 for Lian Li (<c>LianLiUsbScreenDevice.cs:42</c>).</summary>
    public static byte StopMedia(UsbVariant variant) => variant == UsbVariant.Turing ? (byte)111 : (byte)123;

    /// <summary>A command with no arguments (<c>LianLiUsbScreenDevice.cs:205-213</c>; the library's <c>PrepareCommandHeader</c>).</summary>
    public static byte[] Command(byte id, int timestamp)
    {
        var command = new byte[CommandSize];
        command[0] = id;
        command[2] = 26;
        command[3] = 109;
        BinaryPrimitives.WriteInt32LittleEndian(command.AsSpan(4, 4), timestamp);
        return command;
    }

    /// <summary>A command with one byte at 8: brightness, frame rate, StopClock's 0.</summary>
    public static byte[] WithValue(byte id, byte value, int timestamp)
    {
        var command = Command(id, timestamp);
        command[8] = value;
        return command;
    }

    /// <summary>An image's command: its length big-endian at 8.</summary>
    public static byte[] WithLength(byte id, int length, int timestamp)
    {
        var command = Command(id, timestamp);
        BinaryPrimitives.WriteInt32BigEndian(command.AsSpan(8, 4), length);
        return command;
    }

    /// <summary>Lian Li's clock (<c>LianLiUsbScreenDevice.cs:83-98</c>): the year big-endian, the month, day, hour, minute and second, then 2.</summary>
    public static byte[] Clock(DateTime now, int timestamp)
    {
        var command = Command(SyncClock, timestamp);
        command[8] = (byte)(now.Year >> 8);
        command[9] = (byte)(now.Year & 0xFF);
        command[10] = (byte)now.Month;
        command[11] = (byte)now.Day;
        command[12] = (byte)now.Hour;
        command[13] = (byte)now.Minute;
        command[14] = (byte)now.Second;
        command[15] = 2;
        return command;
    }

    /// <summary>
    /// The 512-byte packet of a 500-byte command: Turing's, the command zero-padded to 504 bytes and encrypted with no
    /// padding (the library's <c>RequestResponse</c>); Lian Li's, encrypted with PKCS7 (<c>LianLiUsbScreenDevice.cs:215-223</c>);
    /// both 504 bytes of cipher text, then zeros, then <c>A1 1A</c>.
    /// </summary>
    public static byte[] Packet(UsbVariant variant, ReadOnlySpan<byte> command)
    {
        if (command.Length != CommandSize)
            throw new ArgumentException($"A command is {CommandSize} bytes.", nameof(command));
        using var des = DES.Create();
        des.Key = KeyIv;
        byte[] cipher;
        if (variant == UsbVariant.Turing)
        {
            var padded = new byte[(CommandSize + 7) & ~7];
            command.CopyTo(padded);
            cipher = des.EncryptCbc(padded, KeyIv, PaddingMode.None);
        }
        else
        {
            cipher = des.EncryptCbc(command, KeyIv, PaddingMode.PKCS7);
        }
        var packet = new byte[PacketSize];
        cipher.AsSpan(0, Math.Min(cipher.Length, PacketSize - 2)).CopyTo(packet);
        packet[PacketSize - 2] = 0xA1;
        packet[PacketSize - 1] = 0x1A;
        return packet;
    }
}
