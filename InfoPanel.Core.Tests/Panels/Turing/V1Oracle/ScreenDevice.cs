// TuringSmartScreenLib, LcdDriver.TuringSmartScreen/ScreenDevice.cs: Copyright (c) 2021 machi_pon
// (https://github.com/usausa/turing-smart-screen; the full notice is in LICENSES.md). This is the source of 1.4.x's
// InfoPanel/Lib/LcdDriver.TuringSmartScreen.dll (0.9.2, built from the library's commit df363d80), the "closed DLL"
// 1.4.x drove Turing's USB panels with (Services/TuringPanelUsbDeviceTask.cs:4,119).

using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace InfoPanel.Core.Tests.Panels.Turing.V1Oracle;

/// <summary>
/// The library's <c>ScreenDevice</c> at <c>df363d80</c>, copied verbatim but for its I/O: the LibUsbDotNet reader and
/// writer are <see cref="OracleUsb"/>, a recorder, and the header's timestamp
/// (<c>(int)DateTime.Now.TimeOfDay.TotalMilliseconds</c>) is given. Test-only; no device.
/// </summary>
internal sealed class V1ScreenDevice : IDisposable
{
    private const int CommandSize = 500;
    private const int PaddingCommandSize = (CommandSize + 7) & ~7;
    private const int PacketSize = 512;

    private const int WriteTimeout = 1500;
    private const int ReadTimeout = 1500;

    private static readonly byte[] KeyIv = "slv3tuzx"u8.ToArray();

    private readonly OracleUsb reader;

    private readonly OracleUsb writer;

#pragma warning disable CA5351
    private readonly DES des = DES.Create();
#pragma warning restore CA5351

    private byte[] commandBuffer;

    private byte[] encryptedBuffer;

    private byte[] readBuffer;

    /// <summary>The header's timestamp (the library: the local time of day in milliseconds).</summary>
    public int Timestamp { get; set; }

    public V1ScreenDevice(OracleUsb usb)
    {
        reader = usb;
        writer = usb;

        des.Key = KeyIv;
        des.IV = KeyIv;

        commandBuffer = ArrayPool<byte>.Shared.Rent(PaddingCommandSize);
        encryptedBuffer = ArrayPool<byte>.Shared.Rent(PacketSize);
        readBuffer = ArrayPool<byte>.Shared.Rent(PacketSize);
    }

    public void Dispose()
    {
        if (commandBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(commandBuffer);
            commandBuffer = [];
        }
        if (encryptedBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(encryptedBuffer);
            encryptedBuffer = [];
        }
        if (readBuffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
            readBuffer = [];
        }

        des.Dispose();
    }

    private void PrepareCommandHeader(byte commandId)
    {
        commandBuffer.AsSpan().Clear();

        commandBuffer[0] = commandId;

        commandBuffer[2] = 0x1A;
        commandBuffer[3] = 0x6D;

        var timestamp = Timestamp;
        BinaryPrimitives.WriteInt32LittleEndian(commandBuffer.AsSpan(4, 4), timestamp);
    }

    /// <summary>The 500 command bytes as they stand (for the vectors).</summary>
    public byte[] CommandFor(byte commandId, byte? value8 = null, int? length = null)
    {
        PrepareCommandHeader(commandId);
        if (value8 is { } v)
            commandBuffer[8] = v;
        if (length is { } l)
            BinaryPrimitives.WriteInt32BigEndian(commandBuffer.AsSpan(8, 4), l);
        return commandBuffer.AsSpan(0, CommandSize).ToArray();
    }

    /// <summary>The 512-byte packet the command as it stands encrypts to (RequestResponse's first lines; for the vectors).</summary>
    public byte[] Packet()
    {
        encryptedBuffer.AsSpan().Clear();
        des.EncryptCbc(commandBuffer.AsSpan(0, PaddingCommandSize), KeyIv, encryptedBuffer, PaddingMode.None);

        // End marker
        encryptedBuffer[PacketSize - 2] = 0xA1;
        encryptedBuffer[PacketSize - 1] = 0x1A;
        return encryptedBuffer.AsSpan(0, PacketSize).ToArray();
    }

    private bool RequestResponse(byte[]? data = null)
    {
        encryptedBuffer.AsSpan().Clear();
        des.EncryptCbc(commandBuffer.AsSpan(0, PaddingCommandSize), KeyIv, encryptedBuffer, PaddingMode.None);

        // End marker
        encryptedBuffer[PacketSize - 2] = 0xA1;
        encryptedBuffer[PacketSize - 1] = 0x1A;

        // [MEMO] Flush pending data caused by ZLP handling ?
        reader.ReadFlush();

        var errorCode = writer.Write(encryptedBuffer, 0, PacketSize, WriteTimeout, out var transferLength);
        if ((errorCode != OracleUsb.ErrorCode.None) || (transferLength != PacketSize))
        {
            return false;
        }

        if (data is not null)
        {
            errorCode = writer.Write(data, 0, data.Length, WriteTimeout, out transferLength);
            if ((errorCode != OracleUsb.ErrorCode.None) || (transferLength != data.Length))
            {
                return false;
            }
        }

        errorCode = reader.Read(readBuffer, 0, PacketSize, ReadTimeout, out transferLength);
        var result = (errorCode == OracleUsb.ErrorCode.None) && (transferLength == PacketSize);

        return result;
    }

    public bool Sync()
    {
        PrepareCommandHeader(10);
        return RequestResponse();
    }

    public bool SetBrightness(byte value)
    {
        PrepareCommandHeader(14);
        commandBuffer[8] = value;
        return RequestResponse();
    }

    public bool DrawJpeg(byte[] imageBytes)
    {
        PrepareCommandHeader(101);
        BinaryPrimitives.WriteInt32BigEndian(commandBuffer.AsSpan(8, 4), imageBytes.Length);
        return RequestResponse(imageBytes);
    }

    public bool StopMedia()
    {
        PrepareCommandHeader(111);
        return RequestResponse();
    }
}
