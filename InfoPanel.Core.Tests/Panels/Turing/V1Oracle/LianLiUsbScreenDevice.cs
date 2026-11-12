using System.Buffers.Binary;
using System.Security.Cryptography;

namespace InfoPanel.Core.Tests.Panels.Turing.V1Oracle;

/// <summary>
/// InfoPanel 1.4.x's <c>InfoPanel/LianLiPanel/LianLiUsbScreenDevice.cs</c> (the 1.4.x tip, <c>c71f0f00</c>), copied
/// verbatim but for its I/O: the LibUsbDotNet reader and writer are <see cref="OracleUsb"/>, a recorder; the logger is
/// gone; the header's timestamp (<c>:211</c>, milliseconds since the day before the app started) and the clock
/// command's local time (<c>:85</c>) are given, not read from the clock; the drain's 2 ms sleep (<c>:168</c>) is
/// skipped. Lines cited per member. Test-only; no device.
/// </summary>
internal sealed class V1LianLiUsbScreenDevice : IDisposable
{
    // :26-42
    private static readonly byte[] KeyIv = "slv3tuzx"u8.ToArray();

    private const int CommandSize = 500;
    private const int PacketSize = 512;
    private const int TimeoutMs = 2000;
    private const int ImageAckTimeoutMs = 200;
    private const int MaxImagePayload = 512_000;
    private const byte GetVersionCommand = 10;
    private const byte SetBrightnessCommand = 14;
    private const byte SetFrameRateCommand = 15;
    private const byte SyncClockCommand = 51;
    private const byte StopClockCommand = 52;
    private const byte PushJpegCommand = 101;
    private const byte PushPngCommand = 102;
    private const byte QueryBlockCommand = 122;
    private const byte StopMediaCommand = 123;

    private readonly OracleUsb _reader;
    private readonly OracleUsb _writer;
    private readonly DES _des;
    private readonly byte[] _commandBuffer = new byte[CommandSize];
    private readonly byte[] _readBuffer = new byte[PacketSize];

    /// <summary>The header's timestamp (1.4.x: <c>(DateTime.UtcNow - AppStartTime).TotalMilliseconds</c>).</summary>
    public int Timestamp { get; set; }

    /// <summary>The clock command's local time (1.4.x: <c>DateTime.Now</c>).</summary>
    public DateTime Now { get; set; }

    /// <summary>The timeouts each read was given, in order.</summary>
    public List<int> ReadTimeouts { get; } = [];

    // :52-70, the device's configuration and claim left to the caller (the session's open)
    public V1LianLiUsbScreenDevice(OracleUsb usb)
    {
        _reader = usb;
        _writer = usb;

        _des = DES.Create();
        _des.Mode = CipherMode.CBC;
        _des.Padding = PaddingMode.PKCS7;
        _des.Key = KeyIv;
        _des.IV = KeyIv;
    }

    // :72
    public bool Sync() => RequestResponse(GetVersionCommand);

    // :74-79
    public bool SetBrightness(byte value)
    {
        PrepareCommandHeader(SetBrightnessCommand);
        _commandBuffer[8] = value;
        return RequestResponsePrepared();
    }

    // :81
    public bool StopMedia() => RequestResponse(StopMediaCommand);

    // :83-98
    public bool SyncClockOnly()
    {
        var now = Now;

        PrepareCommandHeader(SyncClockCommand);
        _commandBuffer[8] = (byte)(now.Year >> 8);
        _commandBuffer[9] = (byte)(now.Year & 0xFF);
        _commandBuffer[10] = (byte)now.Month;
        _commandBuffer[11] = (byte)now.Day;
        _commandBuffer[12] = (byte)now.Hour;
        _commandBuffer[13] = (byte)now.Minute;
        _commandBuffer[14] = (byte)now.Second;
        _commandBuffer[15] = 2;

        return RequestResponsePrepared();
    }

    // :100-105
    public bool StopClock()
    {
        PrepareCommandHeader(StopClockCommand);
        _commandBuffer[8] = 0;
        return RequestResponsePrepared();
    }

    // :107-112
    public bool DrawPngLayer(byte[] imageBytes)
    {
        if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));

        return DrawImageLayer(PushPngCommand, imageBytes);
    }

    // :114-119
    public bool DrawJpegLayer(byte[] imageBytes)
    {
        if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));

        return DrawImageLayer(PushJpegCommand, imageBytes);
    }

    // :121-128
    public bool DrawJpeg(byte[] imageBytes)
    {
        if (imageBytes == null) throw new ArgumentNullException(nameof(imageBytes));

        return DrawJpegLayer(imageBytes);
    }

    // :131-136
    public bool SetFrameRate(byte fps)
    {
        PrepareCommandHeader(SetFrameRateCommand);
        _commandBuffer[8] = fps;
        return RequestResponsePrepared();
    }

    // :142-151
    public byte? QueryBlock()
    {
        PrepareCommandHeader(QueryBlockCommand);
        if (!RequestResponsePrepared(null, out var hasResponse) || !hasResponse)
        {
            return null;
        }

        return _readBuffer[8];
    }

    // :158-172, the 2 ms sleep skipped
    private void WaitForBufferDrain(byte threshold)
    {
        for (var i = 0; i < 1000; i++)
        {
            var level = QueryBlock();
            if (level == null || level <= threshold)
            {
                return;
            }
        }
    }

    // :174-197
    private bool DrawImageLayer(byte commandId, byte[] imageBytes)
    {
        if (imageBytes.Length > MaxImagePayload)
        {
            return false;
        }

        PrepareCommandHeader(commandId);
        BinaryPrimitives.WriteInt32BigEndian(_commandBuffer.AsSpan(8, 4), imageBytes.Length);
        var ok = RequestResponsePrepared(imageBytes, out var hasResponse);

        if (ok && hasResponse && _readBuffer[8] > 3)
        {
            WaitForBufferDrain(2);
        }

        return ok;
    }

    // :199-203
    private bool RequestResponse(byte commandId)
    {
        PrepareCommandHeader(commandId);
        return RequestResponsePrepared();
    }

    // :205-213, the timestamp given
    private void PrepareCommandHeader(byte commandId)
    {
        Array.Clear(_commandBuffer, 0, _commandBuffer.Length);
        _commandBuffer[0] = commandId;
        _commandBuffer[2] = 26;
        _commandBuffer[3] = 109;
        var timestamp = Timestamp;
        BinaryPrimitives.WriteInt32LittleEndian(_commandBuffer.AsSpan(4, 4), timestamp);
    }

    /// <summary>The 500-byte command as it stands (for the vectors).</summary>
    public byte[] CommandFor(byte commandId, byte? value8 = null, int? length = null)
    {
        PrepareCommandHeader(commandId);
        if (value8 is { } v)
            _commandBuffer[8] = v;
        if (length is { } l)
            BinaryPrimitives.WriteInt32BigEndian(_commandBuffer.AsSpan(8, 4), l);
        return _commandBuffer.ToArray();
    }

    /// <summary>The 512-byte packet the command as it stands encrypts to (for the vectors).</summary>
    public byte[] Packet() => EncryptCommandPacket();

    // :215-223
    private byte[] EncryptCommandPacket()
    {
        var encryptedCommand = _des.EncryptCbc(_commandBuffer, KeyIv, PaddingMode.PKCS7);
        var packet = new byte[PacketSize];
        Buffer.BlockCopy(encryptedCommand, 0, packet, 0, Math.Min(encryptedCommand.Length, PacketSize - 2));
        packet[510] = 161;
        packet[511] = 26;
        return packet;
    }

    // :225-228
    private bool RequestResponsePrepared(byte[]? payload = null)
    {
        return RequestResponsePrepared(payload, out _);
    }

    // :230-290, the logging gone
    private bool RequestResponsePrepared(byte[]? payload, out bool hasResponse)
    {
        hasResponse = false;
        var commandPacket = EncryptCommandPacket();
        byte[] writeBuffer;

        if (payload == null || payload.Length == 0)
        {
            writeBuffer = commandPacket;
        }
        else
        {
            writeBuffer = new byte[PacketSize + payload.Length];
            Buffer.BlockCopy(commandPacket, 0, writeBuffer, 0, PacketSize);
            Buffer.BlockCopy(payload, 0, writeBuffer, PacketSize, payload.Length);
        }

        try
        {
            _reader.ReadFlush();

            var errorCode = _writer.Write(writeBuffer, TimeoutMs, out var transferred);
            if (errorCode != OracleUsb.ErrorCode.None || transferred != writeBuffer.Length)
            {
                return false;
            }

            Array.Clear(_readBuffer, 0, _readBuffer.Length);
            ReadTimeouts.Add(payload == null ? TimeoutMs : ImageAckTimeoutMs);
            errorCode = _reader.Read(_readBuffer, payload == null ? TimeoutMs : ImageAckTimeoutMs, out transferred);
            if (payload != null && errorCode == OracleUsb.ErrorCode.IoTimedOut)
            {
                return true;
            }

            if (errorCode != OracleUsb.ErrorCode.None || transferred != PacketSize)
            {
                return false;
            }

            hasResponse = true;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _des.Dispose();
}
