using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Sensors.HwInfo.Gadget;

/// <summary>
/// The Windows registry's calls under <see cref="GadgetRegistryReader"/>, read only: the key at <c>path</c> under the
/// hive, in the 64-bit view, opened afresh on each read (HWiNFO may delete and make it again, which leaves a held handle
/// dead). The count and last-write time come from <c>RegQueryInfoKeyW</c>, names from <c>RegEnumValueW</c> into a
/// buffer of the length asked for, a value's type and size from <c>RegQueryValueExW</c> without a buffer and its text
/// with one of that size: nothing larger is ever allocated. A value that is gone (<c>ERROR_FILE_NOT_FOUND</c>) or has
/// grown (<c>ERROR_MORE_DATA</c>) since reads as such, never as an error.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WinGadgetRegistry : IGadgetKeyApi
{
    /// <summary>The registry view every read uses.</summary>
    public const RegistryView View = RegistryView.Registry64;

    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;
    private const int ErrorNoMoreItems = 259;
    private const uint RegSz = 1;

    private readonly string _path;

    /// <param name="path">The key under each hive: HWiNFO's (<see cref="HwInfoGadgetSampler.KeyPath"/>) but in tests.</param>
    public WinGadgetRegistry(string path = HwInfoGadgetSampler.KeyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <summary>The reader over this PC's registry at <paramref name="path"/>.</summary>
    public static GadgetRegistryReader Reader(string path = HwInfoGadgetSampler.KeyPath) => new(new WinGadgetRegistry(path));

    public IGadgetOpenKey? Open(GadgetHive hive)
    {
        using var root = RegistryKey.OpenBaseKey(hive == GadgetHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, View);
        var key = root.OpenSubKey(_path, writable: false);
        return key == null ? null : new OpenKey(key);
    }

    private sealed class OpenKey(RegistryKey key) : IGadgetOpenKey
    {
        public unsafe (int ValueCount, DateTime LastWriteUtc) QueryInfo()
        {
            uint values;
            long lastWrite;
            var error = RegQueryInfoKey(key.Handle, null, null, 0, null, null, null, &values, null, null, null, &lastWrite);
            if (error != ErrorSuccess)
                throw new Win32Exception(error);
            return ((int)Math.Min(values, int.MaxValue), DateTime.FromFileTimeUtc(lastWrite));
        }

        public unsafe GadgetEnumResult EnumName(int index, int maxLength, out string? name)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
            name = null;
            // The buffer holds maxLength characters and the terminator.
            Span<char> buffer = stackalloc char[Math.Min(maxLength, 255) + 1];
            uint length = (uint)buffer.Length;
            int error;
            fixed (char* p = buffer)
                error = RegEnumValue(key.Handle, (uint)index, p, &length, 0, null, null, null);
            switch (error)
            {
                case ErrorSuccess:
                    name = new string(buffer[..(int)length]);
                    return GadgetEnumResult.Found;
                case ErrorMoreData:
                    return GadgetEnumResult.TooLong;
                case ErrorNoMoreItems:
                    return GadgetEnumResult.NoMore;
                default:
                    throw new Win32Exception(error);
            }
        }

        public unsafe (GadgetValueKind Kind, int Bytes)? QueryValue(string name)
        {
            uint type;
            uint size;
            var error = RegQueryValueEx(key.Handle, name, 0, &type, null, &size);
            if (error == ErrorFileNotFound)
                return null;
            if (error != ErrorSuccess)
                throw new Win32Exception(error);
            return (type == RegSz ? GadgetValueKind.String : GadgetValueKind.Other, (int)Math.Min(size, int.MaxValue));
        }

        public unsafe string? ReadText(string name, int bytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, GadgetRegistryReader.MaxTextBytes);
            Span<byte> buffer = stackalloc byte[Math.Max(bytes, 2)];
            uint type;
            uint size = (uint)bytes;
            int error;
            fixed (byte* p = buffer)
                error = RegQueryValueEx(key.Handle, name, 0, &type, p, &size);
            if (error is ErrorFileNotFound or ErrorMoreData)
                return null;
            if (error != ErrorSuccess)
                throw new Win32Exception(error);
            if (type != RegSz)
                return null;

            var chars = MemoryMarshal.Cast<byte, char>(buffer[..((int)size & ~1)]);
            // As .NET reads REG_SZ: one terminator dropped.
            if (chars.Length > 0 && chars[^1] == '\0')
                chars = chars[..^1];
            return new string(chars);
        }

        public void Dispose() => key.Dispose();
    }

    // LSTATUS RegQueryInfoKeyW(HKEY, LPWSTR lpClass, LPDWORD lpcchClass, LPDWORD lpReserved, LPDWORD lpcSubKeys,
    //   LPDWORD lpcbMaxSubKeyLen, LPDWORD lpcbMaxClassLen, LPDWORD lpcValues, LPDWORD lpcbMaxValueNameLen,
    //   LPDWORD lpcbMaxValueLen, LPDWORD lpcbSecurityDescriptor, PFILETIME lpftLastWriteTime)
    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryInfoKeyW")]
    private static unsafe partial int RegQueryInfoKey(
        SafeRegistryHandle key,
        char* className,
        uint* classLength,
        nint reserved,
        uint* subKeys,
        uint* maxSubKeyLength,
        uint* maxClassLength,
        uint* values,
        uint* maxValueNameLength,
        uint* maxValueLength,
        uint* securityDescriptorLength,
        long* lastWriteTime);

    // LSTATUS RegEnumValueW(HKEY, DWORD dwIndex, LPWSTR lpValueName, LPDWORD lpcchValueName, LPDWORD lpReserved,
    //   LPDWORD lpType, LPBYTE lpData, LPDWORD lpcbData)
    [LibraryImport("advapi32.dll", EntryPoint = "RegEnumValueW")]
    private static unsafe partial int RegEnumValue(SafeRegistryHandle key, uint index, char* name, uint* nameLength, nint reserved, uint* type, byte* data, uint* dataLength);

    // LSTATUS RegQueryValueExW(HKEY, LPCWSTR lpValueName, LPDWORD lpReserved, LPDWORD lpType, LPBYTE lpData, LPDWORD lpcbData)
    [LibraryImport("advapi32.dll", EntryPoint = "RegQueryValueExW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved, uint* type, byte* data, uint* dataLength);
}
