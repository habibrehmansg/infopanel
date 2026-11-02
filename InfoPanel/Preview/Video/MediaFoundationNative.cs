using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace InfoPanel.Preview.Video;

// Media Foundation's COM interfaces, structures and exports as MediaFoundationCom uses them: source-generated COM
// (System.Runtime.InteropServices.Marshalling, in the box; no package) and LibraryImport. Each interface is whole up to
// its last slot, in the SDK headers' vtable order (mfobjects.h, mftransform.h, strmif.h), every method PreserveSig with
// an int HRESULT. In-parameters that are interfaces are typed, so the generated marshalling adds and drops its own
// reference; every raw pointer that remains is an out-parameter the caller owns (MediaFoundationCom names each owner).
// IIDs, slot order, parameter types and widths, PreserveSig and the structures' layouts are pinned by
// MediaFoundationAbiTests on any 64-bit OS, against a table copied from the headers.

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
internal unsafe partial interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid key, void* value);
    [PreserveSig] int GetItemType(in Guid key, out int type);
    [PreserveSig] int CompareItem(in Guid key, void* value, out int result);
    [PreserveSig] int Compare(IMFAttributes? theirs, int matchType, out int result);
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    [PreserveSig] int GetDouble(in Guid key, out double value);
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    [PreserveSig] int GetStringLength(in Guid key, out uint length);
    [PreserveSig] int GetString(in Guid key, char* buffer, uint size, out uint length);
    [PreserveSig] int GetAllocatedString(in Guid key, out nint value, out uint length);
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, byte* buffer, uint size, out uint written);
    [PreserveSig] int GetAllocatedBlob(in Guid key, out nint buffer, out uint size);
    [PreserveSig] int GetUnknown(in Guid key, in Guid iid, out nint unknown);
    [PreserveSig] int SetItem(in Guid key, void* value);
    [PreserveSig] int DeleteItem(in Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    [PreserveSig] int SetDouble(in Guid key, double value);
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    [PreserveSig] int SetString(in Guid key, char* value);
    [PreserveSig] int SetBlob(in Guid key, byte* buffer, uint size);
    [PreserveSig] int SetUnknown(in Guid key, nint unknown);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, void* value);
    [PreserveSig] int CopyAllItems(IMFAttributes? destination);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
internal unsafe partial interface IMFMediaType : IMFAttributes
{
    [PreserveSig] int GetMajorType(out Guid type);
    [PreserveSig] int IsCompressedFormat(out int compressed);
    [PreserveSig] int IsEqual(IMFMediaType? other, out uint flags);
    [PreserveSig] int GetRepresentation(Guid representation, out nint data);
    [PreserveSig] int FreeRepresentation(Guid representation, nint data);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
internal unsafe partial interface IMFActivate : IMFAttributes
{
    [PreserveSig] int ActivateObject(in Guid iid, out nint obj);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("df598932-f10c-4e39-bba2-c308f101daa3")]
internal unsafe partial interface IMFMediaEvent : IMFAttributes
{
    // GetType in the header; renamed here only because every C# object has a GetType.
    [PreserveSig] int GetEventType(out uint type);
    [PreserveSig] int GetExtendedType(out Guid type);
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetValue(void* value);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d")]
internal unsafe partial interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint flags, out nint mediaEvent);
    [PreserveSig] int BeginGetEvent(nint callback, nint state);
    [PreserveSig] int EndGetEvent(nint result, out nint mediaEvent);
    [PreserveSig] int QueueEvent(uint type, in Guid extendedType, int status, void* value);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
internal unsafe partial interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out byte* buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
internal unsafe partial interface IMFSample : IMFAttributes
{
    [PreserveSig] int GetSampleFlags(out uint flags);
    [PreserveSig] int SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out nint buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out nint buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(uint index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

/// <summary>MFT_INPUT_STREAM_INFO (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftInputStreamInfo
{
    public long MaxLatency;
    public uint Flags;
    public uint Size;
    public uint MaxLookahead;
    public uint Alignment;
}

/// <summary>MFT_OUTPUT_STREAM_INFO (12 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}

/// <summary>MFT_OUTPUT_DATA_BUFFER (32 bytes on 64-bit: each pointer after a DWORD padded to 8).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint StreamId;
    public nint Sample;
    public uint Status;
    public nint Events;
}

/// <summary>MFT_REGISTER_TYPE_INFO (32 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo
{
    public Guid Major;
    public Guid Sub;
}

/// <summary>A VARIANT (24 bytes on 64-bit; the value at 8), with the two types <see cref="ICodecAPI"/> takes here.</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public uint UI4;
    [FieldOffset(8)] public ulong UI8;
    [FieldOffset(8)] public short Bool;

    public const ushort VtEmpty = 0, VtI4 = 3, VtBool = 11, VtUI4 = 19, VtUI8 = 21;

    public static Variant FromUInt(uint v) => new() { Vt = VtUI4, UI4 = v };

    public static Variant FromBool(bool v) => new() { Vt = VtBool, Bool = (short)(v ? -1 : 0) };
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
internal unsafe partial interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out uint inMin, out uint inMax, out uint outMin, out uint outMax);
    [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int GetStreamIDs(uint inSize, uint* inIds, uint outSize, uint* outIds);
    [PreserveSig] int GetInputStreamInfo(uint id, out MftInputStreamInfo info);
    [PreserveSig] int GetOutputStreamInfo(uint id, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out nint attributes);
    [PreserveSig] int GetInputStreamAttributes(uint id, out nint attributes);
    [PreserveSig] int GetOutputStreamAttributes(uint id, out nint attributes);
    [PreserveSig] int DeleteInputStream(uint id);
    [PreserveSig] int AddInputStreams(uint count, uint* ids);
    [PreserveSig] int GetInputAvailableType(uint id, uint index, out nint type);
    [PreserveSig] int GetOutputAvailableType(uint id, uint index, out nint type);
    [PreserveSig] int SetInputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int SetOutputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint id, out nint type);
    [PreserveSig] int GetOutputCurrentType(uint id, out nint type);
    [PreserveSig] int GetInputStatus(uint id, out uint flags);
    [PreserveSig] int GetOutputStatus(out uint flags);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(uint id, IMFMediaEvent? mediaEvent);
    [PreserveSig] int ProcessMessage(uint message, nuint param);
    [PreserveSig] int ProcessInput(uint id, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count, MftOutputDataBuffer* buffers, out uint status);
}

[SupportedOSPlatform("windows")]
[GeneratedComInterface, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
internal unsafe partial interface ICodecAPI
{
    [PreserveSig] int IsSupported(in Guid api);
    [PreserveSig] int IsModifiable(in Guid api);
    [PreserveSig] int GetParameterRange(in Guid api, Variant* min, Variant* max, Variant* step);
    [PreserveSig] int GetParameterValues(in Guid api, Variant** values, out uint count);
    [PreserveSig] int GetDefaultValue(in Guid api, Variant* value);
    [PreserveSig] int GetValue(in Guid api, Variant* value);
    [PreserveSig] int SetValue(in Guid api, Variant* value);
    [PreserveSig] int RegisterForEvent(in Guid api, nint userData);
    [PreserveSig] int UnregisterForEvent(in Guid api);
    [PreserveSig] int SetAllDefaults();
    [PreserveSig] int SetValueWithNotify(in Guid api, Variant* value, out nint changed, out uint count);
    [PreserveSig] int SetAllDefaultsWithNotify(out nint changed, out uint count);
    [PreserveSig] int GetAllSettings(nint stream);
    [PreserveSig] int SetAllSettings(nint stream);
    [PreserveSig] int SetAllSettingsWithNotify(nint stream, out nint changed, out uint count);
}

/// <summary>The ole32, oleaut32 and mfplat exports MediaFoundationCom calls; each answers an HRESULT unless void.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class MfNative
{
    /// <summary><c>MF_VERSION</c> (mfapi.h): <c>MF_SDK_VERSION</c> 0x0002 &lt;&lt; 16 | <c>MF_API_VERSION</c> 0x0070.</summary>
    public const uint MfVersion = 0x00020070;

    /// <summary><c>MFSTARTUP_FULL</c> (mfapi.h).</summary>
    public const uint StartupFull = 0;

    /// <summary><c>COINIT_MULTITHREADED</c> (objbase.h).</summary>
    public const uint CoInitMultithreaded = 0;

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    [LibraryImport("oleaut32.dll")]
    public static partial int VariantClear(Variant* value);

    [LibraryImport("mfplat.dll")]
    public static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    public static partial int MFTEnumEx(Guid category, uint flags, MftRegisterTypeInfo* input, MftRegisterTypeInfo* output, out nint activates, out uint count);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMediaType(out nint type);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateSample(out nint sample);

    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateMemoryBuffer(uint maxLength, out nint buffer);

    /// <summary><paramref name="alignment"/> is one of mfapi.h's <c>MF_*_BYTE_ALIGNMENT</c> values: the alignment less one.</summary>
    [LibraryImport("mfplat.dll")]
    public static partial int MFCreateAlignedMemoryBuffer(uint maxLength, uint alignment, out nint buffer);
}
