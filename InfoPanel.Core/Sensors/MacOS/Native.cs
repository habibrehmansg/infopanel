using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace InfoPanel.Sensors.MacOS;

/// <summary>CoreFoundation through P/Invoke, with the few helpers the macOS sensors need.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe class CF
{
    private const string Lib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    public const uint StringEncodingUtf8 = 0x08000100;
    public const int NumberSInt64Type = 4;
    public const int NumberDoubleType = 13;

    [DllImport(Lib)] public static extern void CFRelease(IntPtr cf);
    [DllImport(Lib)] public static extern IntPtr CFRetain(IntPtr cf);
    [DllImport(Lib)] public static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(Lib)] public static extern nuint CFNumberGetTypeID();
    [DllImport(Lib)] public static extern nuint CFBooleanGetTypeID();
    [DllImport(Lib)] public static extern nuint CFDictionaryGetTypeID();
    [DllImport(Lib)] public static extern nuint CFDataGetTypeID();
    [DllImport(Lib)] public static extern nuint CFStringGetTypeID();
    [DllImport(Lib)] public static extern nuint CFArrayGetTypeID();
    [DllImport(Lib)] public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte* cStr, uint encoding);
    [DllImport(Lib)] public static extern byte CFStringGetCString(IntPtr str, byte* buffer, nint bufferSize, uint encoding);
    [DllImport(Lib)] public static extern byte CFEqual(IntPtr a, IntPtr b);
    [DllImport(Lib)] public static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(Lib)] public static extern byte CFNumberGetValue(IntPtr number, int type, void* value);
    [DllImport(Lib)] public static extern byte CFBooleanGetValue(IntPtr boolean);
    [DllImport(Lib)] public static extern nint CFDataGetLength(IntPtr data);
    [DllImport(Lib)] public static extern byte* CFDataGetBytePtr(IntPtr data);
    [DllImport(Lib)] public static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(Lib)] public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(Lib)] public static extern void CFArrayRemoveValueAtIndex(IntPtr array, nint index);

    private static readonly nuint NumberType = CFNumberGetTypeID();
    private static readonly nuint BooleanType = CFBooleanGetTypeID();
    private static readonly nuint DictionaryType = CFDictionaryGetTypeID();
    private static readonly nuint DataType = CFDataGetTypeID();
    private static readonly nuint StringType = CFStringGetTypeID();
    private static readonly nuint ArrayType = CFArrayGetTypeID();

    /// <summary>A CFString that lives for the process (never released); for property names used on every sample.</summary>
    public static IntPtr Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes)
            return CFStringCreateWithCString(IntPtr.Zero, p, StringEncodingUtf8);
    }

    public static bool IsNumber(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == NumberType;

    public static bool IsDictionary(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == DictionaryType;

    public static bool IsData(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == DataType;

    public static bool IsString(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == StringType;

    public static bool IsArray(IntPtr cf) => cf != IntPtr.Zero && CFGetTypeID(cf) == ArrayType;

    /// <summary>A CFNumber or CFBoolean as a double; false for anything else.</summary>
    public static bool TryGetDouble(IntPtr cf, out double value)
    {
        value = 0;
        if (cf == IntPtr.Zero)
            return false;

        var type = CFGetTypeID(cf);
        if (type == NumberType)
        {
            double d;
            if (CFNumberGetValue(cf, NumberDoubleType, &d) == 0)
            {
                long l;
                if (CFNumberGetValue(cf, NumberSInt64Type, &l) == 0)
                    return false;
                d = l;
            }

            value = d;
            return true;
        }

        if (type == BooleanType)
        {
            value = CFBooleanGetValue(cf) != 0 ? 1 : 0;
            return true;
        }

        return false;
    }

    /// <summary>A CFNumber as a 64-bit integer (exact for counters); false for anything else.</summary>
    public static bool TryGetInt64(IntPtr cf, out long value)
    {
        long l = 0;
        var ok = IsNumber(cf) && CFNumberGetValue(cf, NumberSInt64Type, &l) != 0;
        value = l;
        return ok;
    }

    /// <summary><paramref name="dict"/>[<paramref name="key"/>] as a double.</summary>
    public static bool TryGetDouble(IntPtr dict, IntPtr key, out double value)
    {
        value = 0;
        return IsDictionary(dict) && TryGetDouble(CFDictionaryGetValue(dict, key), out value);
    }

    /// <summary>A CFString as a managed string (allocates; for discovery, not the hot path).</summary>
    public static string? ToManaged(IntPtr cf)
    {
        if (!IsString(cf))
            return null;

        var buffer = stackalloc byte[512];
        return CFStringGetCString(cf, buffer, 512, StringEncodingUtf8) != 0
            ? Marshal.PtrToStringUTF8((IntPtr)buffer)
            : null;
    }
}

/// <summary>IOKit through P/Invoke.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe class IOKit
{
    private const string Lib = "/System/Library/Frameworks/IOKit.framework/IOKit";

    public const int Success = 0;
    public const int NotPrivileged = unchecked((int)0xE00002C1);
    public const int NotPermitted = unchecked((int)0xE00002E2);

    /// <summary>An IOKit or Mach return code that means "access refused".</summary>
    public static bool IsDenied(int kr) => kr is NotPrivileged or NotPermitted;

    [DllImport(Lib)] public static extern IntPtr IOServiceMatching(byte* name);
    [DllImport(Lib)] public static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);
    [DllImport(Lib)] public static extern uint IOServiceGetMatchingService(uint mainPort, IntPtr matching);
    [DllImport(Lib)] public static extern uint IOIteratorNext(uint iterator);
    [DllImport(Lib)] public static extern int IOObjectRelease(uint obj);
    [DllImport(Lib)] public static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
    [DllImport(Lib)] public static extern uint IORegistryEntryFromPath(uint mainPort, byte* path);
    [DllImport(Lib)] public static extern int IORegistryEntryGetChildIterator(uint entry, byte* plane, out uint iterator);
    [DllImport(Lib)] public static extern int IORegistryEntryGetName(uint entry, byte* name);
    [DllImport(Lib)] public static extern int IORegistryEntryGetRegistryEntryID(uint entry, out ulong entryId);
    [DllImport(Lib)] public static extern int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);
    [DllImport(Lib)] public static extern int IOServiceClose(uint connect);
    [DllImport(Lib)] public static extern int IOConnectCallStructMethod(uint connection, uint selector, void* input, nuint inputSize, void* output, nuint* outputSize);

    /// <summary>The first service matching an IOKit class name, or 0. The caller releases it.</summary>
    public static uint FirstService(string className)
    {
        var name = Encoding.ASCII.GetBytes(className + "\0");
        fixed (byte* p = name)
        {
            var matching = IOServiceMatching(p);
            return matching == IntPtr.Zero ? 0 : IOServiceGetMatchingService(0, matching);
        }
    }

    /// <summary>An iterator over services matching an IOKit class name, or 0. The caller releases it.</summary>
    public static uint Services(byte* className)
    {
        var matching = IOServiceMatching(className);
        if (matching == IntPtr.Zero)
            return 0;
        return IOServiceGetMatchingServices(0, matching, out var iterator) == Success ? iterator : 0;
    }

    /// <summary>A registry entry's name (allocates; for discovery).</summary>
    public static string? NameOf(uint entry)
    {
        var buffer = stackalloc byte[128];
        return IORegistryEntryGetName(entry, buffer) == Success ? Marshal.PtrToStringUTF8((IntPtr)buffer) : null;
    }

    /// <summary>An integer property stored as a CFNumber or as 4- or 8-byte little-endian data.</summary>
    public static bool TryIntProperty(uint entry, IntPtr key, out long value)
    {
        value = 0;
        var cf = IORegistryEntryCreateCFProperty(entry, key, IntPtr.Zero, 0);
        if (cf == IntPtr.Zero)
            return false;
        try
        {
            if (CF.TryGetInt64(cf, out value))
                return true;
            if (!CF.IsData(cf))
                return false;
            var length = CF.CFDataGetLength(cf);
            var bytes = CF.CFDataGetBytePtr(cf);
            if (length >= 8)
                value = *(long*)bytes;
            else if (length >= 4)
                value = *(int*)bytes;
            else
                return false;
            return true;
        }
        finally
        {
            CF.CFRelease(cf);
        }
    }

    /// <summary>A property's bytes (allocates; for discovery), or null.</summary>
    public static byte[]? DataProperty(uint entry, IntPtr key)
    {
        var cf = IORegistryEntryCreateCFProperty(entry, key, IntPtr.Zero, 0);
        if (cf == IntPtr.Zero)
            return null;
        try
        {
            if (!CF.IsData(cf))
                return null;
            var length = (int)CF.CFDataGetLength(cf);
            return new ReadOnlySpan<byte>(CF.CFDataGetBytePtr(cf), length).ToArray();
        }
        finally
        {
            CF.CFRelease(cf);
        }
    }
}

/// <summary>libSystem: Mach host calls and sysctl.</summary>
[SupportedOSPlatform("macos")]
internal static unsafe class LibSystem
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";

    public const int ProcessorCpuLoadInfo = 2;
    public const int HostVmInfo64 = 4;
    public const int CpuStateUser = 0;
    public const int CpuStateSystem = 1;
    public const int CpuStateIdle = 2;
    public const int CpuStateNice = 3;
    public const int CpuStateMax = 4;

    [DllImport(Lib)] public static extern uint mach_host_self();
    [DllImport(Lib)] public static extern int mach_port_deallocate(uint task, uint name);
    [DllImport(Lib)] public static extern int host_processor_info(uint host, int flavor, out uint processorCount, out IntPtr info, out uint infoCount);
    [DllImport(Lib)] public static extern int host_statistics64(uint host, int flavor, void* info, ref uint count);
    [DllImport(Lib)] public static extern int vm_deallocate(uint task, nuint address, nuint size);
    [DllImport(Lib)] public static extern int sysctlbyname(byte* name, void* oldp, nuint* oldlenp, void* newp, nuint newlen);
    [DllImport(Lib)] public static extern int sysctl(int* name, uint namelen, void* oldp, nuint* oldlenp, void* newp, nuint newlen);

    private static readonly IntPtr Handle = NativeLibrary.Load(Lib);

    /// <summary><c>mach_task_self()</c>, a macro over the <c>mach_task_self_</c> global.</summary>
    public static readonly uint TaskSelf = *(uint*)NativeLibrary.GetExport(Handle, "mach_task_self_");

    /// <summary>The host port, taken once (each <c>mach_host_self</c> call adds a send right).</summary>
    public static readonly uint HostSelf = mach_host_self();

    /// <summary>A sysctl that is a 32- or 64-bit integer.</summary>
    public static bool TryGetInt64(ReadOnlySpan<byte> nameUtf8Z, out long value)
    {
        long v = 0;
        nuint size = sizeof(long);
        int r;
        fixed (byte* n = nameUtf8Z)
            r = sysctlbyname(n, &v, &size, null, 0);
        value = size == 4 ? (int)v : v;
        return r == 0 && (size == 4 || size == 8);
    }

    /// <summary>A sysctl that is a string (allocates; for discovery).</summary>
    public static string? GetString(string name)
    {
        var n = Encoding.ASCII.GetBytes(name + "\0");
        var buffer = stackalloc byte[256];
        nuint size = 256;
        fixed (byte* p = n)
        {
            if (sysctlbyname(p, buffer, &size, null, 0) != 0 || size == 0)
                return null;
        }

        return Marshal.PtrToStringUTF8((IntPtr)buffer, (int)size).TrimEnd('\0');
    }

    /// <summary>A sysctl into a struct.</summary>
    public static bool TryGet<T>(ReadOnlySpan<byte> nameUtf8Z, out T value) where T : unmanaged
    {
        T v = default;
        nuint size = (nuint)sizeof(T);
        int r;
        fixed (byte* n = nameUtf8Z)
            r = sysctlbyname(n, &v, &size, null, 0);
        value = v;
        return r == 0 && size == (nuint)sizeof(T);
    }
}

/// <summary><c>vm_statistics64</c> (mach/vm_statistics.h).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VmStatistics64
{
    public uint FreeCount;
    public uint ActiveCount;
    public uint InactiveCount;
    public uint WireCount;
    public ulong ZeroFillCount;
    public ulong Reactivations;
    public ulong Pageins;
    public ulong Pageouts;
    public ulong Faults;
    public ulong CowFaults;
    public ulong Lookups;
    public ulong Hits;
    public ulong Purges;
    public uint PurgeableCount;
    public uint SpeculativeCount;
    public ulong Decompressions;
    public ulong Compressions;
    public ulong Swapins;
    public ulong Swapouts;
    public uint CompressorPageCount;
    public uint ThrottledCount;
    public uint ExternalPageCount;
    public uint InternalPageCount;
    public ulong TotalUncompressedPagesInCompressor;
}

/// <summary><c>struct xsw_usage</c> (sys/sysctl.h), from <c>vm.swapusage</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XswUsage
{
    public ulong Total;
    public ulong Avail;
    public ulong Used;
    public uint PageSize;
    public int Encrypted;
}
