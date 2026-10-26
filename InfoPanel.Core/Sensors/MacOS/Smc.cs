using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.MacOS;

/// <summary>The AppleSMC user client's 80-byte call structure (<c>SMCKeyData_t</c>).</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal unsafe struct SmcKeyData
{
    [FieldOffset(0)] public uint Key;
    [FieldOffset(28)] public uint DataSize;
    [FieldOffset(32)] public uint DataType;
    [FieldOffset(36)] public byte DataAttributes;
    [FieldOffset(40)] public byte Result;
    [FieldOffset(41)] public byte Status;
    [FieldOffset(42)] public byte Data8;
    [FieldOffset(44)] public uint Data32;
    [FieldOffset(48)] public fixed byte Bytes[32];
}

/// <summary>A key's type and size, from <c>kSMCGetKeyInfo</c>.</summary>
internal readonly record struct SmcKeyInfo(uint Key, uint Type, uint Size);

/// <summary>
/// Reads the System Management Controller (private, undocumented) through the <c>AppleSMC</c> user client:
/// <c>IOServiceOpen</c>, then <c>IOConnectCallStructMethod</c> selector 2 with an <see cref="SmcKeyData"/>. Reads need no
/// root. Values decode by type: <c>flt </c> little-endian float (Apple Silicon), <c>sp78</c>/<c>fpe2</c> big-endian fixed
/// point (Intel), <c>ui8 </c>/<c>ui16</c>/<c>ui32</c>/<c>si16</c> big-endian integers.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class Smc : IDisposable
{
    private const uint HandleYpcEvent = 2;
    private const byte CmdReadKey = 5;
    private const byte CmdGetKeyFromIndex = 8;
    private const byte CmdGetKeyInfo = 9;

    public static readonly uint TypeFlt = FourCC("flt ");
    public static readonly uint TypeSp78 = FourCC("sp78");
    public static readonly uint TypeFpe2 = FourCC("fpe2");
    public static readonly uint TypeUi8 = FourCC("ui8 ");
    public static readonly uint TypeUi16 = FourCC("ui16");
    public static readonly uint TypeUi32 = FourCC("ui32");
    public static readonly uint TypeSi16 = FourCC("si16");

    private uint _connection;

    private Smc(uint connection) => _connection = connection;

    /// <summary>Opens the SMC. Returns null with the IOKit code when there is no AppleSMC or the open fails.</summary>
    public static Smc? Open(out int result)
    {
        var service = IOKit.FirstService("AppleSMC");
        if (service == 0)
        {
            result = -1;
            return null;
        }

        try
        {
            result = IOKit.IOServiceOpen(service, LibSystem.TaskSelf, 0, out var connection);
            return result == IOKit.Success ? new Smc(connection) : null;
        }
        finally
        {
            IOKit.IOObjectRelease(service);
        }
    }

    public static uint FourCC(string s) =>
        (uint)s[0] << 24 | (uint)s[1] << 16 | (uint)s[2] << 8 | s[3];

    public static string KeyName(uint key) =>
        string.Create(4, key, (span, k) =>
        {
            for (var i = 0; i < 4; i++)
                span[i] = (char)(byte)(k >> (24 - 8 * i));
        });

    private int Call(ref SmcKeyData input, out SmcKeyData output)
    {
        output = default;
        nuint size = (nuint)sizeof(SmcKeyData);
        fixed (SmcKeyData* i = &input)
        fixed (SmcKeyData* o = &output)
            return IOKit.IOConnectCallStructMethod(_connection, HandleYpcEvent, i, (nuint)sizeof(SmcKeyData), o, &size);
    }

    /// <summary>A key's type and size; false when the key doesn't exist or the call fails (<paramref name="kr"/>).</summary>
    public bool TryGetInfo(uint key, out SmcKeyInfo info, out int kr)
    {
        var input = new SmcKeyData { Key = key, Data8 = CmdGetKeyInfo };
        kr = Call(ref input, out var output);
        info = new SmcKeyInfo(key, output.DataType, output.DataSize);
        return kr == IOKit.Success && output.Result == 0 && output.DataSize is > 0 and <= 32;
    }

    /// <summary>Reads a key's value as a number (<see cref="Decode"/>); false when the read fails.</summary>
    public bool TryRead(in SmcKeyInfo info, out double value, out int kr)
    {
        value = 0;
        var input = new SmcKeyData { Key = info.Key, DataSize = info.Size, Data8 = CmdReadKey };
        kr = Call(ref input, out var output);
        if (kr != IOKit.Success || output.Result != 0)
            return false;

        return Decode(info.Type, new ReadOnlySpan<byte>(output.Bytes, (int)Math.Min(info.Size, 32u)), out value);
    }

    /// <summary>Decodes an SMC value of a numeric type; false for other types or a short buffer.</summary>
    public static bool Decode(uint type, ReadOnlySpan<byte> bytes, out double value)
    {
        value = 0;
        if (type == TypeFlt && bytes.Length >= 4)
            value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        else if (type == TypeSp78 && bytes.Length >= 2)
            value = BinaryPrimitives.ReadInt16BigEndian(bytes) / 256.0;
        else if (type == TypeFpe2 && bytes.Length >= 2)
            value = BinaryPrimitives.ReadUInt16BigEndian(bytes) / 4.0;
        else if (type == TypeUi8 && bytes.Length >= 1)
            value = bytes[0];
        else if (type == TypeUi16 && bytes.Length >= 2)
            value = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        else if (type == TypeUi32 && bytes.Length >= 4)
            value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        else if (type == TypeSi16 && bytes.Length >= 2)
            value = BinaryPrimitives.ReadInt16BigEndian(bytes);
        else
            return false;
        return double.IsFinite(value);
    }

    /// <summary>Every key the SMC reports (<c>#KEY</c>, then one index read per key; about 3.5k on an M4 Max).</summary>
    public List<uint> EnumerateKeys()
    {
        var keys = new List<uint>();
        if (!TryGetInfo(FourCC("#KEY"), out var countInfo, out _) || !TryRead(countInfo, out var count, out _))
            return keys;

        for (uint i = 0; i < (uint)Math.Min(count, 20000); i++)
        {
            var input = new SmcKeyData { Data8 = CmdGetKeyFromIndex, Data32 = i };
            if (Call(ref input, out var output) == IOKit.Success && output.Result == 0 && output.Key != 0)
                keys.Add(output.Key);
        }

        return keys;
    }

    public void Dispose()
    {
        if (_connection != 0)
        {
            IOKit.IOServiceClose(_connection);
            _connection = 0;
        }
    }
}
