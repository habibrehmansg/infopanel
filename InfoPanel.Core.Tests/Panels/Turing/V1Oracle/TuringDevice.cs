using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace InfoPanel.Core.Tests.Panels.Turing.V1Oracle;

/// <summary>
/// InfoPanel 1.4.x's <c>InfoPanel/TuringPanel/TuringDevice.cs</c> (the 1.4.x tip, <c>c71f0f00</c>; a class 1.4.x
/// shipped but never called), its command bytes only: <c>:53-57</c>, <c>BuildCommandPacketHeader</c> <c>:127-157</c>
/// (the timestamp, milliseconds since UTC midnight, given), <c>EncryptWithDES</c> <c>:159-192</c> and
/// <c>EncryptCommandPacket</c> <c>:194-212</c>, and the packets of <c>SendSyncCommand</c> (<c>:214-225</c>, 10),
/// <c>SendBrightnessCommand</c> (<c>:240-252</c>, 14) and <c>SendJpegBytes</c> (<c>:1178-1189</c>, 101). Its cipher is
/// BouncyCastle's <c>BufferedBlockCipher(CbcBlockCipher(DesEngine))</c>, which pads nothing, over the command padded
/// with zeros to a whole block; here .NET's DES-CBC with <see cref="PaddingMode.None"/> over the same bytes, the same
/// transform. Test-only; no device.
/// </summary>
internal sealed class V1TuringDevice
{
    private const int CMD_PACKET_SIZE = 500;
    private const int FULL_PACKET_SIZE = 512;
    private static readonly byte[] DES_KEY_BYTES = Encoding.ASCII.GetBytes("slv3tuzx");
    private static readonly byte[] MAGIC_BYTES = { 161, 26 };

    /// <summary>The header's timestamp (1.4.x: milliseconds since UTC midnight).</summary>
    public uint Timestamp { get; set; }

    public byte[] BuildCommandPacketHeader(byte commandId)
    {
        byte[] packet = ArrayPool<byte>.Shared.Rent(CMD_PACKET_SIZE);
        try
        {
            Array.Clear(packet, 0, CMD_PACKET_SIZE);

            packet[0] = commandId;
            packet[2] = 0x1A;
            packet[3] = 0x6D;

            long timestamp = Timestamp;

            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(4, sizeof(uint)),
                unchecked((uint)timestamp));

            byte[] result = new byte[CMD_PACKET_SIZE];
            Buffer.BlockCopy(packet, 0, result, 0, CMD_PACKET_SIZE);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public byte[] EncryptWithDES(byte[] data)
    {
        int paddedLen = (data.Length + 7) & ~7;    // round up to multiple of 8
        byte[] padded = new byte[paddedLen];
        data.CopyTo(padded, 0);

        using var des = DES.Create();
        des.Key = DES_KEY_BYTES;
        return des.EncryptCbc(padded, DES_KEY_BYTES, PaddingMode.None);
    }

    public byte[] EncryptCommandPacket(byte[] data)
    {
        byte[] encrypted = EncryptWithDES(data);

        byte[] finalPacket = new byte[FULL_PACKET_SIZE];

        Buffer.BlockCopy(encrypted, 0, finalPacket, 0, Math.Min(encrypted.Length, FULL_PACKET_SIZE - 2));

        finalPacket[FULL_PACKET_SIZE - 2] = MAGIC_BYTES[0];  // 161
        finalPacket[FULL_PACKET_SIZE - 1] = MAGIC_BYTES[1];  // 26

        return finalPacket;
    }

    /// <summary><c>SendSyncCommand</c>'s packet (<c>:218-219</c>).</summary>
    public byte[] Sync() => EncryptCommandPacket(BuildCommandPacketHeader(10));

    /// <summary><c>SendBrightnessCommand</c>'s packet (<c>:244-246</c>).</summary>
    public byte[] Brightness(byte brightness)
    {
        byte[] cmdPacket = BuildCommandPacketHeader(14);
        cmdPacket[8] = brightness;
        return EncryptCommandPacket(cmdPacket);
    }

    /// <summary><c>SendJpegBytes</c>'s command packet (<c>:1180-1186</c>), written before the JPEG's own bytes.</summary>
    public byte[] Jpeg(int imgSize)
    {
        byte[] cmdPacket = BuildCommandPacketHeader(101);
        BinaryPrimitives.WriteInt32BigEndian(cmdPacket.AsSpan(8, 4), imgSize);
        return EncryptCommandPacket(cmdPacket);
    }
}
