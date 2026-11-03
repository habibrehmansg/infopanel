using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InfoPanel.Preview.Video;

namespace InfoPanel.Tests.Preview.Video;

// The declarations under test are Windows-only for the product; their ABI and the generated stubs are managed code that
// runs on any OS, here over synthetic native objects. This file builds those objects and never calls Windows.
#pragma warning disable CA1416

/// <summary>How a parameter is passed, in the headers' terms.</summary>
internal enum MfPass
{
    /// <summary>By value.</summary>
    Value,

    /// <summary>A const pointer the callee reads (<c>REFGUID</c>, <c>REFIID</c>): C#'s <c>in</c>.</summary>
    In,

    /// <summary>A pointer the callee writes one value through: C#'s <c>out</c>.</summary>
    Out,

    /// <summary>A plain pointer (<c>T*</c>).</summary>
    Pointer,

    /// <summary>A pointer to a pointer (<c>T**</c>).</summary>
    PointerPointer,

    /// <summary>An interface pointer passed in, typed: the generated marshalling adds and drops its reference.</summary>
    Interface,
}

/// <summary>One parameter: its C# element type (what <see cref="Pass"/> points at, or the value) and how it is passed.</summary>
internal readonly record struct MfParam(Type Type, MfPass Pass)
{
    /// <summary>The width the callee reads at the slot: 8 for every pointer kind, else the value's size.</summary>
    public int Width => Pass == MfPass.Value ? Marshal.SizeOf(Type) : IntPtr.Size;

    /// <summary>The width of what a pointer points at (the value's for <see cref="MfPass.Value"/>; 0 for void and interfaces).</summary>
    public int PointeeWidth => Type == typeof(void) || Pass == MfPass.Interface ? 0 : Type.IsPointer ? IntPtr.Size : Marshal.SizeOf(Type);

    public override string ToString() => $"{Pass} {Type.Name}";
}

internal sealed record MfMethod(string Name, MfParam[] Params);

/// <summary>An interface as the SDK header declares it: its IID, its base and its methods in vtable order (after IUnknown's three).</summary>
internal sealed record MfInterface(string Name, string Header, Guid Iid, string? Base, MfMethod[] Methods);

/// <summary>
/// The SDK headers' declarations (mfobjects.h, mftransform.h, strmif.h) of the nine interfaces, copied by hand: every
/// method in vtable order with each parameter's type (DWORD, UINT32, ULONG and the enums as their 32-bit C# types;
/// LONGLONG, MFTIME, UINT64 64-bit; ULONG_PTR, LONG_PTR and every pointer pointer-sized) and how it is passed. Every
/// method returns an HRESULT. MediaFoundationAbiTests compares the C# declarations with it; the synthetic objects lay
/// their vtables out from it, so a slot swapped in C# calls the wrong function.
/// </summary>
internal static class MfHeaders
{
    private static MfParam V<T>() => new(typeof(T), MfPass.Value);
    private static MfParam In<T>() => new(typeof(T), MfPass.In);
    private static MfParam Out<T>() => new(typeof(T), MfPass.Out);
    private static MfParam P<T>() => new(typeof(T), MfPass.Pointer);
    private static MfParam PVoid() => new(typeof(void), MfPass.Pointer);
    private static MfParam PP<T>() => new(typeof(T), MfPass.PointerPointer);
    private static MfParam I<T>() => new(typeof(T), MfPass.Interface);
    private static MfMethod M(string name, params MfParam[] ps) => new(name, ps);

    // Pointers the callee hands back (IUnknown**, LPWSTR*, UINT8**) are `out nint`: one owner, named at the call site.
    public static readonly MfInterface IMFAttributes = new("IMFAttributes", "mfobjects.h", new("2cd2d921-c447-44a7-a13c-4adabfc247e3"), null,
    [
        M("GetItem", In<Guid>(), PVoid()),
        M("GetItemType", In<Guid>(), Out<int>()), // MF_ATTRIBUTE_TYPE*
        M("CompareItem", In<Guid>(), PVoid(), Out<int>()), // REFPROPVARIANT, BOOL*
        M("Compare", I<IMFAttributes>(), V<int>(), Out<int>()), // IMFAttributes*, MF_ATTRIBUTES_MATCH_TYPE, BOOL*
        M("GetUINT32", In<Guid>(), Out<uint>()),
        M("GetUINT64", In<Guid>(), Out<ulong>()),
        M("GetDouble", In<Guid>(), Out<double>()),
        M("GetGUID", In<Guid>(), Out<Guid>()),
        M("GetStringLength", In<Guid>(), Out<uint>()),
        M("GetString", In<Guid>(), P<char>(), V<uint>(), Out<uint>()),
        M("GetAllocatedString", In<Guid>(), Out<nint>(), Out<uint>()),
        M("GetBlobSize", In<Guid>(), Out<uint>()),
        M("GetBlob", In<Guid>(), P<byte>(), V<uint>(), Out<uint>()),
        M("GetAllocatedBlob", In<Guid>(), Out<nint>(), Out<uint>()),
        M("GetUnknown", In<Guid>(), In<Guid>(), Out<nint>()),
        M("SetItem", In<Guid>(), PVoid()),
        M("DeleteItem", In<Guid>()),
        M("DeleteAllItems"),
        M("SetUINT32", In<Guid>(), V<uint>()),
        M("SetUINT64", In<Guid>(), V<ulong>()),
        M("SetDouble", In<Guid>(), V<double>()),
        M("SetGUID", In<Guid>(), In<Guid>()),
        M("SetString", In<Guid>(), P<char>()),
        M("SetBlob", In<Guid>(), P<byte>(), V<uint>()),
        M("SetUnknown", In<Guid>(), V<nint>()),
        M("LockStore"),
        M("UnlockStore"),
        M("GetCount", Out<uint>()),
        M("GetItemByIndex", V<uint>(), Out<Guid>(), PVoid()),
        M("CopyAllItems", I<IMFAttributes>()),
    ]);

    public static readonly MfInterface IMFMediaType = new("IMFMediaType", "mfobjects.h", new("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), "IMFAttributes",
    [
        M("GetMajorType", Out<Guid>()),
        M("IsCompressedFormat", Out<int>()),
        M("IsEqual", I<IMFMediaType>(), Out<uint>()),
        M("GetRepresentation", V<Guid>(), Out<nint>()),
        M("FreeRepresentation", V<Guid>(), V<nint>()),
    ]);

    public static readonly MfInterface IMFActivate = new("IMFActivate", "mfobjects.h", new("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), "IMFAttributes",
    [
        M("ActivateObject", In<Guid>(), Out<nint>()),
        M("ShutdownObject"),
        M("DetachObject"),
    ]);

    // The header's GetType is GetEventType in C# (every C# object has a GetType); the slot is what matters.
    public static readonly MfInterface IMFMediaEvent = new("IMFMediaEvent", "mfobjects.h", new("df598932-f10c-4e39-bba2-c308f101daa3"), "IMFAttributes",
    [
        M("GetEventType", Out<uint>()), // MediaEventType*
        M("GetExtendedType", Out<Guid>()),
        M("GetStatus", Out<int>()), // HRESULT*
        M("GetValue", PVoid()), // PROPVARIANT*
    ]);

    public static readonly MfInterface IMFMediaEventGenerator = new("IMFMediaEventGenerator", "mfobjects.h", new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"), null,
    [
        M("GetEvent", V<uint>(), Out<nint>()),
        M("BeginGetEvent", V<nint>(), V<nint>()), // IMFAsyncCallback*, IUnknown*
        M("EndGetEvent", V<nint>(), Out<nint>()), // IMFAsyncResult*, IMFMediaEvent**
        M("QueueEvent", V<uint>(), In<Guid>(), V<int>(), PVoid()),
    ]);

    public static readonly MfInterface IMFMediaBuffer = new("IMFMediaBuffer", "mfobjects.h", new("045fa593-8799-42b8-bc8d-8968c6453507"), null,
    [
        M("Lock", new MfParam(typeof(byte*), MfPass.Out), Out<uint>(), Out<uint>()),
        M("Unlock"),
        M("GetCurrentLength", Out<uint>()),
        M("SetCurrentLength", V<uint>()),
        M("GetMaxLength", Out<uint>()),
    ]);

    public static readonly MfInterface IMFSample = new("IMFSample", "mfobjects.h", new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), "IMFAttributes",
    [
        M("GetSampleFlags", Out<uint>()),
        M("SetSampleFlags", V<uint>()),
        M("GetSampleTime", Out<long>()),
        M("SetSampleTime", V<long>()),
        M("GetSampleDuration", Out<long>()),
        M("SetSampleDuration", V<long>()),
        M("GetBufferCount", Out<uint>()),
        M("GetBufferByIndex", V<uint>(), Out<nint>()),
        M("ConvertToContiguousBuffer", Out<nint>()),
        M("AddBuffer", I<IMFMediaBuffer>()),
        M("RemoveBufferByIndex", V<uint>()),
        M("RemoveAllBuffers"),
        M("GetTotalLength", Out<uint>()),
        M("CopyToBuffer", I<IMFMediaBuffer>()),
    ]);

    public static readonly MfInterface IMFTransform = new("IMFTransform", "mftransform.h", new("bf94c121-5b05-4e6f-8000-ba598961414d"), null,
    [
        M("GetStreamLimits", Out<uint>(), Out<uint>(), Out<uint>(), Out<uint>()),
        M("GetStreamCount", Out<uint>(), Out<uint>()),
        M("GetStreamIDs", V<uint>(), P<uint>(), V<uint>(), P<uint>()),
        M("GetInputStreamInfo", V<uint>(), Out<MftInputStreamInfo>()),
        M("GetOutputStreamInfo", V<uint>(), Out<MftOutputStreamInfo>()),
        M("GetAttributes", Out<nint>()),
        M("GetInputStreamAttributes", V<uint>(), Out<nint>()),
        M("GetOutputStreamAttributes", V<uint>(), Out<nint>()),
        M("DeleteInputStream", V<uint>()),
        M("AddInputStreams", V<uint>(), P<uint>()),
        M("GetInputAvailableType", V<uint>(), V<uint>(), Out<nint>()),
        M("GetOutputAvailableType", V<uint>(), V<uint>(), Out<nint>()),
        M("SetInputType", V<uint>(), I<IMFMediaType>(), V<uint>()),
        M("SetOutputType", V<uint>(), I<IMFMediaType>(), V<uint>()),
        M("GetInputCurrentType", V<uint>(), Out<nint>()),
        M("GetOutputCurrentType", V<uint>(), Out<nint>()),
        M("GetInputStatus", V<uint>(), Out<uint>()),
        M("GetOutputStatus", Out<uint>()),
        M("SetOutputBounds", V<long>(), V<long>()),
        M("ProcessEvent", V<uint>(), I<IMFMediaEvent>()),
        M("ProcessMessage", V<uint>(), V<nuint>()), // MFT_MESSAGE_TYPE, ULONG_PTR
        M("ProcessInput", V<uint>(), I<IMFSample>(), V<uint>()),
        M("ProcessOutput", V<uint>(), V<uint>(), P<MftOutputDataBuffer>(), Out<uint>()),
    ]);

    public static readonly MfInterface ICodecAPI = new("ICodecAPI", "strmif.h", new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), null,
    [
        M("IsSupported", In<Guid>()),
        M("IsModifiable", In<Guid>()),
        M("GetParameterRange", In<Guid>(), P<Variant>(), P<Variant>(), P<Variant>()),
        M("GetParameterValues", In<Guid>(), PP<Variant>(), Out<uint>()),
        M("GetDefaultValue", In<Guid>(), P<Variant>()),
        M("GetValue", In<Guid>(), P<Variant>()),
        M("SetValue", In<Guid>(), P<Variant>()),
        M("RegisterForEvent", In<Guid>(), V<nint>()), // LONG_PTR
        M("UnregisterForEvent", In<Guid>()),
        M("SetAllDefaults"),
        M("SetValueWithNotify", In<Guid>(), P<Variant>(), Out<nint>(), Out<uint>()), // GUID**, ULONG*
        M("SetAllDefaultsWithNotify", Out<nint>(), Out<uint>()),
        M("GetAllSettings", V<nint>()), // IStream*
        M("SetAllSettings", V<nint>()),
        M("SetAllSettingsWithNotify", V<nint>(), Out<nint>(), Out<uint>()),
    ]);

    public static readonly MfInterface[] All =
        [IMFAttributes, IMFMediaType, IMFActivate, IMFMediaEvent, IMFMediaEventGenerator, IMFMediaBuffer, IMFSample, IMFTransform, ICodecAPI];

    public static MfInterface Named(string name) => All.Single(i => i.Name == name);

    /// <summary>Every method of <paramref name="iface"/> in vtable order, its bases' first (IUnknown's three not included).</summary>
    public static IReadOnlyList<(MfInterface Owner, MfMethod Method)> Flattened(MfInterface iface) =>
        [.. (iface.Base is { } b ? Flattened(Named(b)) : []), .. iface.Methods.Select(m => (iface, m))];

    /// <summary>A method's slot: IUnknown's three, then the flattened methods.</summary>
    public static int Slot(MfInterface iface, string method) => 3 + Flattened(iface).ToList().FindIndex(m => m.Method.Name == method);
}

/// <summary>
/// A native COM object built in the test: one face per interface it answers (a vtable pointer and a handle back to this
/// object), each vtable laid out from <see cref="MfHeaders"/> with the functions it implements and <c>E_NOTIMPL</c>
/// elsewhere. <see cref="Refs"/> is its reference count, which starts at 1 (the creator's); every <c>AddRef</c> and
/// <c>Release</c> is counted.
/// </summary>
internal abstract unsafe class SyntheticObject
{
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int ENoInterface = unchecked((int)0x80004002);
    private static readonly Guid IUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly ConcurrentDictionary<string, nint> Vtables = new();
    private readonly Dictionary<Guid, nint> _faces = [];
    private readonly nint _identity;
    private int _refs = 1;
    private int _addRefs;
    private int _releases;

    protected SyntheticObject(params MfInterface[] faces)
    {
        var handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        foreach (var iface in faces)
        {
            var face = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
            face[0] = VtableFor(iface);
            face[1] = handle;
            for (var i = iface; i != null; i = i.Base is { } b ? MfHeaders.Named(b) : null)
                _faces.TryAdd(i.Iid, (nint)face);
            if (_identity == 0)
                _identity = (nint)face;
        }
    }

    public int Refs => Volatile.Read(ref _refs);

    public int AddRefs => Volatile.Read(ref _addRefs);

    public int Releases => Volatile.Read(ref _releases);

    /// <summary>The face for <paramref name="iface"/>, with no reference added (the creator's reference is the object's).</summary>
    public nint Face(MfInterface iface) => _faces[iface.Iid];

    /// <summary>The face for <paramref name="iface"/> with a reference added for the caller.</summary>
    public nint Give(MfInterface iface)
    {
        AddRef();
        return Face(iface);
    }

    public void AddRef()
    {
        Interlocked.Increment(ref _addRefs);
        Interlocked.Increment(ref _refs);
    }

    public void Release()
    {
        Interlocked.Increment(ref _releases);
        var left = Interlocked.Decrement(ref _refs);
        if (left == 0)
            LastReleased();
        // Below zero: a release too many. Counted (Refs goes negative), never thrown: a Release through the vtable runs
        // in an unmanaged callback, where an exception would end the process instead of failing the test.
    }

    /// <summary>The last reference went: whatever this object holds is let go (the native memory stays, so a stray call is counted, not a crash).</summary>
    protected virtual void LastReleased()
    {
    }

    protected static T From<T>(nint self) where T : SyntheticObject => (T)GCHandle.FromIntPtr(((nint*)self)[1]).Target!;

    /// <summary>The synthetic object behind a face pointer (null: none).</summary>
    public static SyntheticObject? Behind(nint face) => face == 0 ? null : From<SyntheticObject>(face);

    // ---- vtables ----

    private static nint VtableFor(MfInterface iface) => Vtables.GetOrAdd(iface.Name, _ =>
    {
        var methods = MfHeaders.Flattened(iface);
        var vtable = (nint*)NativeMemory.AllocZeroed((nuint)(3 + methods.Count), (nuint)sizeof(nint));
        vtable[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        vtable[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefSlot;
        vtable[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseSlot;
        for (var i = 0; i < methods.Count; i++)
            vtable[3 + i] = Implementations.TryGetValue($"{methods[i].Owner.Name}.{methods[i].Method.Name}", out var f) ? f : (nint)(delegate* unmanaged<nint, int>)&NotImpl;
        return (nint)vtable;
    });

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint self, Guid* iid, nint* ppv)
    {
        var o = From<SyntheticObject>(self);
        if (*iid == IUnknown)
        {
            o.AddRef();
            *ppv = o._identity;
            return 0;
        }

        if (o._faces.TryGetValue(*iid, out var face))
        {
            o.AddRef();
            *ppv = face;
            return 0;
        }

        *ppv = 0;
        return ENoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRefSlot(nint self)
    {
        var o = From<SyntheticObject>(self);
        o.AddRef();
        return (uint)o.Refs;
    }

    [UnmanagedCallersOnly]
    private static uint ReleaseSlot(nint self)
    {
        var o = From<SyntheticObject>(self);
        o.Release();
        return (uint)Math.Max(o.Refs, 0);
    }

    [UnmanagedCallersOnly]
    private static int NotImpl(nint self) => ENotImpl;

    /// <summary>Implemented slots by "Interface.Method" of the interface that declares the method.</summary>
    private static readonly Dictionary<string, nint> Implementations = new()
    {
        ["IMFAttributes.GetUINT32"] = (nint)(delegate* unmanaged<nint, Guid*, uint*, int>)&SyntheticAttributes.GetUInt32,
        ["IMFAttributes.GetUINT64"] = (nint)(delegate* unmanaged<nint, Guid*, ulong*, int>)&SyntheticAttributes.GetUInt64,
        ["IMFAttributes.GetGUID"] = (nint)(delegate* unmanaged<nint, Guid*, Guid*, int>)&SyntheticAttributes.GetGuid,
        ["IMFAttributes.GetAllocatedString"] = (nint)(delegate* unmanaged<nint, Guid*, nint*, uint*, int>)&SyntheticAttributes.GetAllocatedString,
        ["IMFAttributes.SetUINT32"] = (nint)(delegate* unmanaged<nint, Guid*, uint, int>)&SyntheticAttributes.SetUInt32,
        ["IMFAttributes.SetUINT64"] = (nint)(delegate* unmanaged<nint, Guid*, ulong, int>)&SyntheticAttributes.SetUInt64,
        ["IMFAttributes.SetGUID"] = (nint)(delegate* unmanaged<nint, Guid*, Guid*, int>)&SyntheticAttributes.SetGuid,
        ["IMFActivate.ActivateObject"] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&SyntheticActivate.ActivateObject,
        ["IMFActivate.ShutdownObject"] = (nint)(delegate* unmanaged<nint, int>)&SyntheticActivate.ShutdownObject,
        ["IMFMediaEvent.GetEventType"] = (nint)(delegate* unmanaged<nint, uint*, int>)&SyntheticEvent.GetEventType,
        ["IMFMediaEvent.GetStatus"] = (nint)(delegate* unmanaged<nint, int*, int>)&SyntheticEvent.GetStatus,
        ["IMFMediaBuffer.Lock"] = (nint)(delegate* unmanaged<nint, byte**, uint*, uint*, int>)&SyntheticBuffer.Lock,
        ["IMFMediaBuffer.Unlock"] = (nint)(delegate* unmanaged<nint, int>)&SyntheticBuffer.Unlock,
        ["IMFMediaBuffer.GetCurrentLength"] = (nint)(delegate* unmanaged<nint, uint*, int>)&SyntheticBuffer.GetCurrentLength,
        ["IMFMediaBuffer.SetCurrentLength"] = (nint)(delegate* unmanaged<nint, uint, int>)&SyntheticBuffer.SetCurrentLength,
        ["IMFMediaBuffer.GetMaxLength"] = (nint)(delegate* unmanaged<nint, uint*, int>)&SyntheticBuffer.GetMaxLength,
        ["IMFSample.GetSampleTime"] = (nint)(delegate* unmanaged<nint, long*, int>)&SyntheticSample.GetSampleTime,
        ["IMFSample.SetSampleTime"] = (nint)(delegate* unmanaged<nint, long, int>)&SyntheticSample.SetSampleTime,
        ["IMFSample.SetSampleDuration"] = (nint)(delegate* unmanaged<nint, long, int>)&SyntheticSample.SetSampleDuration,
        ["IMFSample.GetBufferCount"] = (nint)(delegate* unmanaged<nint, uint*, int>)&SyntheticSample.GetBufferCount,
        ["IMFSample.AddBuffer"] = (nint)(delegate* unmanaged<nint, nint, int>)&SyntheticSample.AddBuffer,
        ["IMFSample.ConvertToContiguousBuffer"] = (nint)(delegate* unmanaged<nint, nint*, int>)&SyntheticSample.ConvertToContiguousBuffer,
        ["IMFMediaEventGenerator.GetEvent"] = (nint)(delegate* unmanaged<nint, uint, nint*, int>)&SyntheticTransform.GetEvent,
        ["IMFTransform.GetInputStreamInfo"] = (nint)(delegate* unmanaged<nint, uint, MftInputStreamInfo*, int>)&SyntheticTransform.GetInputStreamInfo,
        ["IMFTransform.GetOutputStreamInfo"] = (nint)(delegate* unmanaged<nint, uint, MftOutputStreamInfo*, int>)&SyntheticTransform.GetOutputStreamInfo,
        ["IMFTransform.GetAttributes"] = (nint)(delegate* unmanaged<nint, nint*, int>)&SyntheticTransform.GetAttributes,
        ["IMFTransform.GetOutputAvailableType"] = (nint)(delegate* unmanaged<nint, uint, uint, nint*, int>)&SyntheticTransform.GetOutputAvailableType,
        ["IMFTransform.SetInputType"] = (nint)(delegate* unmanaged<nint, uint, nint, uint, int>)&SyntheticTransform.SetInputType,
        ["IMFTransform.SetOutputType"] = (nint)(delegate* unmanaged<nint, uint, nint, uint, int>)&SyntheticTransform.SetOutputType,
        ["IMFTransform.GetInputCurrentType"] = (nint)(delegate* unmanaged<nint, uint, nint*, int>)&SyntheticTransform.GetInputCurrentType,
        ["IMFTransform.GetOutputCurrentType"] = (nint)(delegate* unmanaged<nint, uint, nint*, int>)&SyntheticTransform.GetOutputCurrentType,
        ["IMFTransform.ProcessMessage"] = (nint)(delegate* unmanaged<nint, uint, nuint, int>)&SyntheticTransform.ProcessMessage,
        ["IMFTransform.ProcessInput"] = (nint)(delegate* unmanaged<nint, uint, nint, uint, int>)&SyntheticTransform.ProcessInput,
        ["IMFTransform.ProcessOutput"] = (nint)(delegate* unmanaged<nint, uint, uint, MftOutputDataBuffer*, uint*, int>)&SyntheticTransform.ProcessOutput,
        ["ICodecAPI.GetValue"] = (nint)(delegate* unmanaged<nint, Guid*, Variant*, int>)&SyntheticTransform.GetValue,
        ["ICodecAPI.SetValue"] = (nint)(delegate* unmanaged<nint, Guid*, Variant*, int>)&SyntheticTransform.SetValue,
    };
}

/// <summary>An attribute store (UINT32, UINT64, GUID, string), each read and write recorded; any read can be made to fail.</summary>
internal unsafe class SyntheticAttributes(params MfInterface[] faces) : SyntheticObject(faces.Length == 0 ? [MfHeaders.IMFAttributes] : faces)
{
    public const int AttributeNotFound = unchecked((int)0xC00D36E6); // MF_E_ATTRIBUTENOTFOUND

    public ConcurrentDictionary<Guid, object> Values { get; } = new();

    /// <summary>"GetUINT32 {key} {value}", … in order.</summary>
    public ConcurrentQueue<string> Log { get; } = new();

    /// <summary>A read of this key fails with this HRESULT, and an out pointer is still written (GetAllocatedString).</summary>
    public Dictionary<Guid, int> FailReads { get; } = [];

    private static SyntheticAttributes Self(nint self) => From<SyntheticAttributes>(self);

    private bool Read<T>(Guid key, out T value, out int hr)
    {
        value = default!;
        if (FailReads.TryGetValue(key, out hr))
            return false;
        if (Values.TryGetValue(key, out var v) && v is T t)
        {
            value = t;
            hr = 0;
            return true;
        }

        hr = AttributeNotFound;
        return false;
    }

    [UnmanagedCallersOnly]
    public static int GetUInt32(nint self, Guid* key, uint* value)
    {
        var o = Self(self);
        o.Log.Enqueue($"GetUINT32 {*key}");
        if (!o.Read<uint>(*key, out var v, out var hr))
            return hr;
        *value = v;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetUInt64(nint self, Guid* key, ulong* value)
    {
        var o = Self(self);
        o.Log.Enqueue($"GetUINT64 {*key}");
        if (!o.Read<ulong>(*key, out var v, out var hr))
            return hr;
        *value = v;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetGuid(nint self, Guid* key, Guid* value)
    {
        var o = Self(self);
        o.Log.Enqueue($"GetGUID {*key}");
        if (!o.Read<Guid>(*key, out var v, out var hr))
            return hr;
        *value = v;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetAllocatedString(nint self, Guid* key, nint* value, uint* length)
    {
        var o = Self(self);
        o.Log.Enqueue($"GetAllocatedString {*key}");
        if (!o.Read<string>(*key, out var v, out var hr))
        {
            // A failing call that writes its out pointer anyway: the caller must still free it.
            *value = o.FailReads.ContainsKey(*key) ? o.Allocate("stale") : 0;
            return hr;
        }

        *value = o.Allocate(v);
        *length = (uint)v.Length;
        return 0;
    }

    /// <summary>Every string handed out (CoTaskMem, the caller's to free), failing reads' included.</summary>
    public ConcurrentQueue<nint> StringsGiven { get; } = new();

    private nint Allocate(string s)
    {
        var p = Marshal.StringToCoTaskMemUni(s);
        StringsGiven.Enqueue(p);
        return p;
    }

    [UnmanagedCallersOnly]
    public static int SetUInt32(nint self, Guid* key, uint value)
    {
        var o = Self(self);
        o.Log.Enqueue($"SetUINT32 {*key} {value}");
        o.Values[*key] = value;
        return o.SetHr;
    }

    [UnmanagedCallersOnly]
    public static int SetUInt64(nint self, Guid* key, ulong value)
    {
        var o = Self(self);
        o.Log.Enqueue($"SetUINT64 {*key} {value}");
        o.Values[*key] = value;
        return o.SetHr;
    }

    [UnmanagedCallersOnly]
    public static int SetGuid(nint self, Guid* key, Guid* value)
    {
        var o = Self(self);
        o.Log.Enqueue($"SetGUID {*key} {*value}");
        o.Values[*key] = *value;
        return o.SetHr;
    }

    /// <summary>What every Set answers.</summary>
    public int SetHr { get; set; }
}

internal sealed class SyntheticMediaType() : SyntheticAttributes(MfHeaders.IMFMediaType);

internal sealed unsafe class SyntheticEvent(uint type, int status) : SyntheticAttributes(MfHeaders.IMFMediaEvent)
{
    [UnmanagedCallersOnly]
    public static int GetEventType(nint self, uint* type)
    {
        *type = From<SyntheticEvent>(self).Type;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetStatus(nint self, int* status)
    {
        *status = From<SyntheticEvent>(self).Status;
        return 0;
    }

    public uint Type => type;

    public int Status => status;
}

/// <summary>A memory buffer of native memory at the alignment it was made with.</summary>
internal sealed unsafe class SyntheticBuffer : SyntheticObject
{
    private readonly byte* _data;

    public SyntheticBuffer(int maxLength, int alignment = 1) : base(MfHeaders.IMFMediaBuffer)
    {
        MaxLength = maxLength;
        Alignment = alignment;
        _data = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(maxLength, 1), (nuint)Math.Max(alignment, 1));
    }

    public int MaxLength { get; }

    public int Alignment { get; }

    public int CurrentLength { get; set; }

    public int Locks;

    public nint Address => (nint)_data;

    public byte[] Bytes => new ReadOnlySpan<byte>(_data, CurrentLength).ToArray();

    public void Fill(byte[] bytes)
    {
        bytes.CopyTo(new Span<byte>(_data, MaxLength));
        CurrentLength = bytes.Length;
    }

    [UnmanagedCallersOnly]
    public static int Lock(nint self, byte** p, uint* max, uint* current)
    {
        var o = From<SyntheticBuffer>(self);
        Interlocked.Increment(ref o.Locks);
        *p = o._data;
        if (max != null)
            *max = (uint)o.MaxLength;
        if (current != null)
            *current = (uint)o.CurrentLength;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int Unlock(nint self)
    {
        Interlocked.Decrement(ref From<SyntheticBuffer>(self).Locks);
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetCurrentLength(nint self, uint* length)
    {
        *length = (uint)From<SyntheticBuffer>(self).CurrentLength;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int SetCurrentLength(nint self, uint length)
    {
        var o = From<SyntheticBuffer>(self);
        if (length > o.MaxLength)
            return unchecked((int)0x80070057);
        o.CurrentLength = (int)length;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetMaxLength(nint self, uint* length)
    {
        *length = (uint)From<SyntheticBuffer>(self).MaxLength;
        return 0;
    }
}

/// <summary>A sample: its time, duration and buffers, each buffer held (a reference) until the sample's last reference goes.</summary>
internal sealed unsafe class SyntheticSample() : SyntheticAttributes(MfHeaders.IMFSample)
{
    private readonly List<SyntheticBuffer> _buffers = [];

    public long? Time { get; set; }

    public long? Duration { get; set; }

    public IReadOnlyList<SyntheticBuffer> Buffers
    {
        get
        {
            lock (_buffers)
                return [.. _buffers];
        }
    }

    public void Hold(SyntheticBuffer buffer)
    {
        buffer.AddRef();
        lock (_buffers)
            _buffers.Add(buffer);
    }

    protected override void LastReleased()
    {
        SyntheticBuffer[] held;
        lock (_buffers)
        {
            held = [.. _buffers];
            _buffers.Clear();
        }

        foreach (var buffer in held)
            buffer.Release();
    }

    [UnmanagedCallersOnly]
    public static int GetSampleTime(nint self, long* time)
    {
        if (From<SyntheticSample>(self).Time is not { } t)
            return unchecked((int)0xC00D36C8); // MF_E_NO_SAMPLE_TIMESTAMP
        *time = t;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int SetSampleTime(nint self, long time)
    {
        From<SyntheticSample>(self).Time = time;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int SetSampleDuration(nint self, long duration)
    {
        From<SyntheticSample>(self).Duration = duration;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetBufferCount(nint self, uint* count)
    {
        *count = (uint)From<SyntheticSample>(self).Buffers.Count;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int AddBuffer(nint self, nint buffer)
    {
        if (Behind(buffer) is not SyntheticBuffer b)
            return unchecked((int)0x80070057);
        From<SyntheticSample>(self).Hold(b);
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int ConvertToContiguousBuffer(nint self, nint* buffer)
    {
        var buffers = From<SyntheticSample>(self).Buffers;
        if (buffers.Count == 0)
        {
            *buffer = 0;
            return unchecked((int)0x80004005);
        }

        *buffer = buffers[0].Give(MfHeaders.IMFMediaBuffer);
        return 0;
    }
}

/// <summary>
/// The synthetic objects the real port's object factories make (<see cref="IMfObjects"/>): each made with one more
/// reference the test holds, so after the port is done with one its count is back to the test's one.
/// </summary>
internal sealed class SyntheticObjects : IMfObjects
{
    public List<SyntheticMediaType> Types { get; } = [];

    public List<SyntheticSample> Samples { get; } = [];

    public List<SyntheticBuffer> Buffers { get; } = [];

    public int CreateMediaType(out nint type)
    {
        var made = new SyntheticMediaType();
        made.AddRef(); // the test's
        lock (Types)
            Types.Add(made);
        type = made.Face(MfHeaders.IMFMediaType);
        return 0;
    }

    /// <summary>What every attribute Set on the samples made from now on answers.</summary>
    public int SampleSetHr { get; set; }

    public int CreateSample(out nint sample)
    {
        var made = new SyntheticSample { SetHr = SampleSetHr };
        made.AddRef();
        lock (Samples)
            Samples.Add(made);
        sample = made.Face(MfHeaders.IMFSample);
        return 0;
    }

    public int CreateBuffer(int size, int alignment, out nint buffer)
    {
        var made = new SyntheticBuffer(size, alignment);
        made.AddRef();
        lock (Buffers)
            Buffers.Add(made);
        buffer = made.Face(MfHeaders.IMFMediaBuffer);
        return 0;
    }

    /// <summary>The memory the code under test freed (CoTaskMemFree), in order.</summary>
    public ConcurrentQueue<nint> Freed { get; } = new();

    /// <summary>The VARIANT types the code under test cleared (VariantClear), in order.</summary>
    public ConcurrentQueue<ushort> Cleared { get; } = new();

    public void FreeCoTaskMem(nint p)
    {
        if (p == 0)
            return;
        Freed.Enqueue(p);
        Marshal.FreeCoTaskMem(p);
    }

    /// <summary>VariantClear for the types the synthetic objects hand out: an interface (VT_UNKNOWN) is released.</summary>
    public unsafe int ClearVariant(Variant* value)
    {
        Cleared.Enqueue(value->Vt);
        if (value->Vt == SyntheticTransform.VtUnknown && value->UI8 != 0)
            Marshal.Release((nint)value->UI8);
        *value = default;
        return 0;
    }

    /// <summary>Lets go of the test's references (the objects whose count the test has read).</summary>
    public void DropTestReferences()
    {
        foreach (var o in Types.Cast<SyntheticObject>().Concat(Samples).Concat(Buffers))
            o.Release();
    }
}

/// <summary>An activation object: entry attributes, and the transform it activates (a reference per activation).</summary>
internal sealed unsafe class SyntheticActivate(SyntheticTransform? transform = null) : SyntheticAttributes(MfHeaders.IMFActivate)
{
    public SyntheticTransform? Transform { get; } = transform;

    public int Activations;

    public int Shutdowns;

    [UnmanagedCallersOnly]
    public static int ActivateObject(nint self, Guid* iid, nint* obj)
    {
        var o = From<SyntheticActivate>(self);
        Interlocked.Increment(ref o.Activations);
        if (o.Transform == null)
        {
            *obj = 0;
            return unchecked((int)0x80004005);
        }

        var t = o.Transform;
        if (*iid != MfHeaders.IMFTransform.Iid)
        {
            *obj = 0;
            return ENoInterface;
        }

        *obj = t.Give(MfHeaders.IMFTransform);
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int ShutdownObject(nint self)
    {
        var o = From<SyntheticActivate>(self);
        Interlocked.Increment(ref o.Shutdowns);
        o.Transform?.Shutdown();
        return 0;
    }
}

/// <summary>
/// A transform: IMFTransform, ICodecAPI and IMFMediaEventGenerator faces of one object. With <see cref="Keeps"/> it
/// holds the types it accepts (until cleared, shut down or finally released) and the samples it accepts (until their
/// output, or shutdown), as an MFT may; without it it holds nothing. Its answers are set per call; every call and its
/// arguments are recorded.
/// </summary>
internal sealed unsafe class SyntheticTransform() : SyntheticObject(MfHeaders.IMFTransform, MfHeaders.ICodecAPI, MfHeaders.IMFMediaEventGenerator)
{
    private readonly Lock _lock = new();
    private SyntheticObject? _outputType;
    private SyntheticObject? _inputType;
    private readonly Queue<SyntheticSample> _heldSamples = [];

    public bool Keeps { get; init; }

    public SyntheticAttributes Attributes { get; } = new();

    public ConcurrentQueue<string> Calls { get; } = new();

    public int SetOutputTypeHr { get; set; }

    public int SetInputTypeHr { get; set; }

    public int ProcessInputHr { get; set; }

    public int ProcessMessageHr { get; set; }

    /// <summary>What the last accepted or refused SetXType saw: the type's attributes at the call.</summary>
    public Dictionary<Guid, object>? LastOutputType { get; private set; }

    public Dictionary<Guid, object>? LastInputType { get; private set; }

    public MftInputStreamInfo InputInfo { get; set; }

    public MftOutputStreamInfo OutputInfo { get; set; }

    /// <summary>The types the Get*Type calls hand out (a fresh object each call, recorded in <see cref="Made"/>).</summary>
    public Dictionary<Guid, object> CurrentOutput { get; } = [];

    public Dictionary<Guid, object> CurrentInput { get; } = [];

    public Dictionary<Guid, object> Available { get; } = [];

    /// <summary>A Get*Type answer: its HRESULT, and whether a failing call still writes its out pointer.</summary>
    public int GetTypeHr { get; set; }

    public bool PointerOnFailure { get; set; }

    /// <summary>Objects this transform made and handed out (types, events, output samples and buffers, event collections).</summary>
    public ConcurrentQueue<SyntheticObject> Made { get; } = new();

    // ---- output ----

    public int ProcessOutputHr { get; set; }

    /// <summary>The encoder's own samples (pSample filled in); otherwise it writes into the caller's.</summary>
    public bool ProvidesSamples { get; set; }

    public byte[] OutputBytes { get; set; } = [0, 0, 0, 1, 0x65, 0x88];

    public long? OutputTime { get; set; } = 1234;

    public bool? OutputCleanPoint { get; set; } = true;

    /// <summary>pEvents filled in (an object the caller must release), whatever the answer.</summary>
    public bool EventsOnOutput { get; set; }

    /// <summary>No sample this call: the caller's sample left untouched and none of its own (events, if any, still given).</summary>
    public bool NoSample { get; set; }

    /// <summary>MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE (mftransform.h) in the data buffer's status.</summary>
    public bool FlagNoSample { get; set; }

    /// <summary>pSample filled in with an object that isn't a sample (its wrapping fails).</summary>
    public bool HandsBackNotASample { get; set; }

    /// <summary>The caller's sample as ProcessOutput saw it (null: none given).</summary>
    public SyntheticSample? CallersSample { get; private set; }

    // ---- events ----

    public ConcurrentQueue<(uint Type, int Status, int Hr, bool PointerOnFailure)> Events { get; } = new();

    // ---- codec API ----

    public ConcurrentDictionary<Guid, Variant> CodecValues { get; } = new();

    public int SetValueHr { get; set; }

    public int GetValueHr { get; set; }

    /// <summary>GetValue fills the VARIANT in with this object (VT_UNKNOWN, a reference the caller owns), whatever it answers.</summary>
    public SyntheticObject? OwningValue { get; set; }

    public const ushort VtUnknown = 13; // VT_UNKNOWN (wtypes.h)

    public bool IsShutDown { get; private set; }

    public void Shutdown()
    {
        IsShutDown = true;
        LetGo();
    }

    protected override void LastReleased()
    {
        LetGo();
        Attributes.Release();
    }

    private void LetGo()
    {
        SyntheticObject? output, input;
        SyntheticSample[] samples;
        lock (_lock)
        {
            (output, input) = (_outputType, _inputType);
            (_outputType, _inputType) = (null, null);
            samples = [.. _heldSamples];
            _heldSamples.Clear();
        }

        output?.Release();
        input?.Release();
        foreach (var sample in samples)
            sample.Release();
    }

    private static SyntheticTransform Self(nint self) => From<SyntheticTransform>(self);

    [UnmanagedCallersOnly]
    public static int GetInputStreamInfo(nint self, uint id, MftInputStreamInfo* info)
    {
        var o = Self(self);
        o.Calls.Enqueue($"GetInputStreamInfo {id}");
        *info = o.InputInfo;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetOutputStreamInfo(nint self, uint id, MftOutputStreamInfo* info)
    {
        var o = Self(self);
        o.Calls.Enqueue($"GetOutputStreamInfo {id}");
        *info = o.OutputInfo;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetAttributes(nint self, nint* attributes)
    {
        *attributes = Self(self).Attributes.Give(MfHeaders.IMFAttributes);
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int SetOutputType(nint self, uint id, nint type, uint flags) => Self(self).SetType(id, type, flags, output: true);

    [UnmanagedCallersOnly]
    public static int SetInputType(nint self, uint id, nint type, uint flags) => Self(self).SetType(id, type, flags, output: false);

    private int SetType(uint id, nint type, uint flags, bool output)
    {
        var name = output ? "SetOutputType" : "SetInputType";
        Calls.Enqueue($"{name} {id} {(type == 0 ? "null" : "type")} {flags}");
        var o = Behind(type) as SyntheticAttributes;
        if (output)
            LastOutputType = o?.Values.ToDictionary();
        else
            LastInputType = o?.Values.ToDictionary();
        var hr = output ? SetOutputTypeHr : SetInputTypeHr;
        if (type != 0 && hr < 0)
            return hr;
        if (!Keeps && type != 0)
            return hr;
        // Keeping: the accepted type replaces the held one; a null type clears it.
        o?.AddRef();
        SyntheticObject? old;
        lock (_lock)
        {
            old = output ? _outputType : _inputType;
            if (output)
                _outputType = o;
            else
                _inputType = o;
        }

        old?.Release();
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetOutputCurrentType(nint self, uint id, nint* type) => Self(self).HandOut(id, type, Self(self).CurrentOutput, "GetOutputCurrentType");

    [UnmanagedCallersOnly]
    public static int GetInputCurrentType(nint self, uint id, nint* type) => Self(self).HandOut(id, type, Self(self).CurrentInput, "GetInputCurrentType");

    [UnmanagedCallersOnly]
    public static int GetOutputAvailableType(nint self, uint id, uint index, nint* type) => Self(self).HandOut(id, type, Self(self).Available, $"GetOutputAvailableType {index}");

    private int HandOut(uint id, nint* type, Dictionary<Guid, object> values, string name)
    {
        Calls.Enqueue($"{name} {id}");
        *type = 0;
        if (GetTypeHr < 0 && !PointerOnFailure)
            return GetTypeHr;
        var made = new SyntheticMediaType();
        foreach (var (key, value) in values)
            made.Values[key] = value;
        Made.Enqueue(made);
        *type = made.Face(MfHeaders.IMFMediaType);
        return GetTypeHr;
    }

    [UnmanagedCallersOnly]
    public static int ProcessMessage(nint self, uint message, nuint param)
    {
        var o = Self(self);
        o.Calls.Enqueue($"ProcessMessage 0x{message:X} {param}");
        return o.ProcessMessageHr;
    }

    [UnmanagedCallersOnly]
    public static int ProcessInput(nint self, uint id, nint sample, uint flags)
    {
        var o = Self(self);
        var s = Behind(sample) as SyntheticSample;
        o.Calls.Enqueue($"ProcessInput {id} {s?.Time} {s?.Duration} {flags} {s?.Buffers.Count}");
        if (o.ProcessInputHr < 0)
            return o.ProcessInputHr;
        if (o.Keeps && s != null)
        {
            s.AddRef();
            lock (o._lock)
                o._heldSamples.Enqueue(s);
        }

        return 0;
    }

    [UnmanagedCallersOnly]
    public static int ProcessOutput(nint self, uint flags, uint count, MftOutputDataBuffer* buffers, uint* status)
    {
        var o = Self(self);
        o.CallersSample = Behind(buffers->Sample) as SyntheticSample;
        o.Calls.Enqueue($"ProcessOutput {flags} {count} {buffers->StreamId} {(buffers->Sample == 0 ? "none" : "caller's")}");
        *status = 0;
        if (o.EventsOnOutput)
        {
            var events = new SyntheticAttributes();
            o.Made.Enqueue(events);
            buffers->Events = events.Face(MfHeaders.IMFAttributes);
        }

        if (o.FlagNoSample)
            buffers->Status = 0x300; // MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE
        if (o.NoSample)
            return o.ProcessOutputHr;
        if (o.HandsBackNotASample)
        {
            var other = new SyntheticAttributes();
            o.Made.Enqueue(other);
            buffers->Sample = other.Face(MfHeaders.IMFAttributes);
            return o.ProcessOutputHr;
        }

        SyntheticSample? out1 = null;
        lock (o._lock)
        {
            if (o._heldSamples.Count > 0)
                out1 = o._heldSamples.Dequeue();
        }

        out1?.Release();
        if (o.ProvidesSamples)
        {
            var sample = new SyntheticSample { Time = o.OutputTime };
            if (o.OutputCleanPoint is { } clean)
                sample.Values[MfCom.CleanPoint] = clean ? 1u : 0u;
            var buffer = new SyntheticBuffer(Math.Max(o.OutputBytes.Length, 1));
            buffer.Fill(o.OutputBytes);
            sample.Hold(buffer);
            buffer.Release(); // the sample's reference alone
            o.Made.Enqueue(sample);
            o.Made.Enqueue(buffer);
            buffers->Sample = sample.Face(MfHeaders.IMFSample);
            return o.ProcessOutputHr;
        }

        if (o.ProcessOutputHr >= 0 && o.CallersSample is { } callers && callers.Buffers is [var first, ..])
        {
            first.Fill(o.OutputBytes);
            callers.Time = o.OutputTime;
            if (o.OutputCleanPoint is { } clean)
                callers.Values[MfCom.CleanPoint] = clean ? 1u : 0u;
        }

        return o.ProcessOutputHr;
    }

    [UnmanagedCallersOnly]
    public static int GetEvent(nint self, uint flags, nint* mediaEvent)
    {
        var o = Self(self);
        o.Calls.Enqueue($"GetEvent {flags}");
        *mediaEvent = 0;
        if (!o.Events.TryDequeue(out var e))
            return unchecked((int)0xC00D3E80); // MF_E_NO_EVENTS_AVAILABLE
        if (e.Hr < 0 && !e.PointerOnFailure)
            return e.Hr;
        var made = new SyntheticEvent(e.Type, e.Status);
        o.Made.Enqueue(made);
        *mediaEvent = made.Face(MfHeaders.IMFMediaEvent);
        return e.Hr;
    }

    [UnmanagedCallersOnly]
    public static int SetValue(nint self, Guid* api, Variant* value)
    {
        var o = Self(self);
        o.Calls.Enqueue($"SetValue {*api} vt{value->Vt} {(value->Vt == Variant.VtBool ? value->Bool : value->UI4)}");
        if (o.SetValueHr < 0)
            return o.SetValueHr;
        o.CodecValues[*api] = *value;
        return 0;
    }

    [UnmanagedCallersOnly]
    public static int GetValue(nint self, Guid* api, Variant* value)
    {
        var o = Self(self);
        o.Calls.Enqueue($"GetValue {*api}");
        if (o.OwningValue is { } owned)
        {
            *value = default;
            value->Vt = VtUnknown;
            value->UI8 = (ulong)owned.Give(MfHeaders.IMFAttributes);
            return o.GetValueHr;
        }

        if (o.GetValueHr < 0)
            return o.GetValueHr;
        if (!o.CodecValues.TryGetValue(*api, out var v))
            return ENotImpl;
        *value = v;
        return 0;
    }
}
