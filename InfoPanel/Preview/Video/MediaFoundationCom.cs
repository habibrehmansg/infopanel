using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace InfoPanel.Preview.Video;

/// <summary>
/// The values the real port uses beyond <see cref="Mf"/>'s, from the SDK headers (named per value; pinned by
/// MediaFoundationAbiTests).
/// </summary>
internal static class MfCom
{
    public const int ENoInterface = unchecked((int)0x80004002); // E_NOINTERFACE (winerror.h)
    public const int EPointer = unchecked((int)0x80004003); // E_POINTER (winerror.h)
    public const int DispETypeMismatch = unchecked((int)0x80020005); // DISP_E_TYPEMISMATCH (winerror.h)

    public static readonly Guid CategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a"); // MFT_CATEGORY_VIDEO_ENCODER (mfapi.h)
    public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71"); // MFMediaType_Video (mfapi.h)

    // ---- media type attributes (mfapi.h) ----
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"); // MF_MT_MAJOR_TYPE
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"); // MF_MT_SUBTYPE
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"); // MF_MT_FRAME_SIZE
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"); // MF_MT_FRAME_RATE
    public static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"); // MF_MT_PIXEL_ASPECT_RATIO
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"); // MF_MT_INTERLACE_MODE
    public static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"); // MF_MT_AVG_BITRATE
    public static readonly Guid Mpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036"); // MF_MT_MPEG2_PROFILE
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"); // MF_MT_DEFAULT_STRIDE
    public static readonly Guid SampleSize = new("dad3ab78-1990-408b-bce2-eba673dacc10"); // MF_MT_SAMPLE_SIZE
    public static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327"); // MF_MT_YUV_MATRIX
    public static readonly Guid VideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935"); // MF_MT_VIDEO_PRIMARIES
    public static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93"); // MF_MT_TRANSFER_FUNCTION
    public static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11"); // MF_MT_VIDEO_NOMINAL_RANGE

    /// <summary>MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE (mftransform.h), a value in <c>MFT_OUTPUT_DATA_BUFFER.dwStatus</c>.</summary>
    public const uint OutputDataBufferNoSample = 0x300;

    /// <summary>MFSampleExtension_CleanPoint (mfapi.h).</summary>
    public static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
}

/// <summary>
/// Media Foundation's object factories and the two frees, the one seam under the real port and catalog: mfplat's,
/// ole32's and oleaut32's exports on Windows (<see cref="MfPlatObjects"/>), synthetic ones in the tests, so the port's
/// and the catalog's own call sites run on any OS. Each factory answers an HRESULT and, on success, a pointer with one
/// reference the caller owns.
/// </summary>
internal unsafe interface IMfObjects
{
    int CreateMediaType(out nint type);

    int CreateSample(out nint sample);

    /// <summary>A system-memory buffer; <paramref name="alignment"/> 1 is none, otherwise a power of two.</summary>
    int CreateBuffer(int size, int alignment, out nint buffer);

    /// <summary><c>CoTaskMemFree</c>: memory a call handed back for its caller to free (a string, <c>MFTEnumEx</c>'s array); 0 does nothing.</summary>
    void FreeCoTaskMem(nint p);

    /// <summary><c>VariantClear</c>: frees what a VARIANT a call filled in owns, and empties it.</summary>
    int ClearVariant(Variant* value);
}

[SupportedOSPlatform("windows")]
internal sealed unsafe class MfPlatObjects : IMfObjects
{
    public static readonly MfPlatObjects Instance = new();

    public int CreateMediaType(out nint type) => MfNative.MFCreateMediaType(out type);

    public int CreateSample(out nint sample) => MfNative.MFCreateSample(out sample);

    public int CreateBuffer(int size, int alignment, out nint buffer) => alignment > 1
        // MFCreateAlignedMemoryBuffer takes MF_*_BYTE_ALIGNMENT, the alignment less one.
        ? MfNative.MFCreateAlignedMemoryBuffer((uint)size, (uint)(alignment - 1), out buffer)
        : MfNative.MFCreateMemoryBuffer((uint)size, out buffer);

    public void FreeCoTaskMem(nint p)
    {
        if (p != 0)
            Marshal.FreeCoTaskMem(p);
    }

    public int ClearVariant(Variant* value) => MfNative.VariantClear(value);
}

/// <summary>
/// The wrappers the real port makes: each from <see cref="StrategyBasedComWrappers"/> as a unique instance that takes
/// over the caller's reference, and each released deterministically (<see cref="Free"/>, <c>FinalRelease</c>), never
/// left to the collector. <see cref="Live"/> counts them, so a test sees every session give back what it made.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MfWrappers
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static long _live;

    /// <summary>Wrappers made and not yet freed, process-wide (tests).</summary>
    public static long Live => Interlocked.Read(ref _live);

    /// <summary>Wraps <paramref name="p"/>, taking the caller's reference over (released here: the wrapper holds its own).</summary>
    public static T Wrap<T>(nint p) where T : class
    {
        if (p == 0)
            throw new ArgumentNullException(nameof(p), $"a null {typeof(T).Name}");
        object wrapper;
        try
        {
            wrapper = Wrappers.GetOrCreateObjectForComInstance(p, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(p);
        }

        Interlocked.Increment(ref _live);
        if (wrapper is T typed)
            return typed;
        Free(wrapper);
        throw new InvalidCastException($"the object isn't an {typeof(T).Name}");
    }

    /// <summary>Releases a wrapper's references now (<c>FinalRelease</c>); null does nothing.</summary>
    public static void Free(object? wrapper)
    {
        if (wrapper is not ComObject com)
            return;
        com.FinalRelease();
        Interlocked.Decrement(ref _live);
    }

    /// <summary>A raw pointer for <typeparamref name="T"/> on a wrapper, with a reference the caller owns and releases.</summary>
    public static unsafe nint Raw<T>(T wrapper) where T : class => (nint)ComInterfaceMarshaller<T>.ConvertToUnmanaged(wrapper);

    /// <summary>Releases a raw reference the caller owns; 0 does nothing.</summary>
    public static void Release(nint p)
    {
        if (p != 0)
            Marshal.Release(p);
    }
}

/// <summary>
/// The real catalog: COM per thread, <c>MFStartup</c> once per process, and the hardware encoders <c>MFTEnumEx</c>
/// lists (<see cref="Enumerate"/>), turned into entries by <see cref="MfCatalogAdapter"/> in the array's order.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfComCatalog(IMfObjects? objects = null) : IMfCatalog
{
    private const int RpcEChangedMode = unchecked((int)0x80010106); // RPC_E_CHANGED_MODE (winerror.h)
    private static readonly Lock StartupLock = new();
    private static bool _started;

    private readonly IMfObjects _objects = objects ?? MfPlatObjects.Instance;

    public int EnterThread(string role)
    {
        var hr = MfNative.CoInitializeEx(0, MfNative.CoInitMultithreaded);
        // S_FALSE: already multithreaded here, which still needs its CoUninitialize. A thread of another apartment is a
        // failure: the backend's threads are its own, so it never happens.
        return hr == RpcEChangedMode ? hr : Math.Min(hr, 0);
    }

    public void LeaveThread() => MfNative.CoUninitialize();

    public int Startup()
    {
        lock (StartupLock)
        {
            if (_started)
                return Mf.SOk;
            var hr = MfNative.MFStartup(MfNative.MfVersion, MfNative.StartupFull);
            // Never MFShutdown: a quarantined session may still be inside Media Foundation.
            _started = hr >= 0;
            return hr;
        }
    }

    public unsafe int Enumerate(uint flags, out IReadOnlyList<IMfEntry> entries)
    {
        entries = [];
        var output = new MftRegisterTypeInfo { Major = MfCom.MediaTypeVideo, Sub = Mf.H264 };
        var hr = MfNative.MFTEnumEx(MfCom.CategoryVideoEncoder, flags, null, &output, out var array, out var count);
        if (hr < 0)
        {
            _objects.FreeCoTaskMem(array);
            return hr;
        }

        entries = TakeOver(array, count, _objects);
        return hr;
    }

    /// <summary>
    /// <c>MFTEnumEx</c>'s answer turned into entries, from the moment it returns: <paramref name="array"/> and every
    /// pointer in it are this call's. Each pointer is taken out of the array before its wrapper takes its reference over
    /// (<see cref="MfWrappers.Wrap{T}"/> consumes it even when it throws); on any exception every pointer not taken yet is
    /// released and every wrapper made is freed; the array is freed whatever happens. <paramref name="wrap"/> and
    /// <paramref name="adapt"/> are the tests' (a wrap or an adapter that throws).
    /// </summary>
    internal static unsafe IReadOnlyList<IMfEntry> TakeOver(nint array, uint count, IMfObjects objects, Func<nint, IMFActivate>? wrap = null,
        Func<IReadOnlyList<IMFActivate>, IMfObjects, IReadOnlyList<IMfEntry>>? adapt = null)
    {
        wrap ??= MfWrappers.Wrap<IMFActivate>;
        adapt ??= MfCatalogAdapter.Entries;
        var pointers = (nint*)array;
        var n = array == 0 ? 0 : (int)count;
        List<IMFActivate>? activates = null;
        try
        {
            // Its capacity made first, so adding a wrapper never allocates: a wrapper made is always in the list.
            activates = new List<IMFActivate>(n);
            for (var i = 0; i < n; i++)
            {
                var p = pointers[i];
                pointers[i] = 0;
                if (p == 0)
                    continue;
                try
                {
                    activates.Add(wrap(p));
                }
                catch (Exception ex) when (ex is InvalidCastException or COMException)
                {
                    // Wrap released the pointer; an object that isn't an IMFActivate isn't an entry.
                }
            }

            var entries = adapt(activates, objects);
            activates = null; // the entries own the wrappers now
            return entries;
        }
        finally
        {
            if (activates != null)
            {
                foreach (var activate in activates)
                    MfWrappers.Free(activate);
            }

            // Every pointer still in the array was never taken (an exception mid-walk): released here.
            for (var i = 0; i < n; i++)
                MfWrappers.Release(pointers[i]);
            objects.FreeCoTaskMem(array);
        }
    }
}

/// <summary>
/// Turns the activation objects <c>MFTEnumEx</c> returned into catalog entries: every one, in the given order, each
/// answering its own attributes. No entry is dropped, moved or tested by vendor: the order is Windows', and the choice
/// is <see cref="MediaFoundationH264"/>'s walk.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class MfCatalogAdapter
{
    /// <summary>The entries take the activates over: disposing an entry frees its wrapper.</summary>
    public static IReadOnlyList<IMfEntry> Entries(IReadOnlyList<IMFActivate> activates, IMfObjects objects)
    {
        var entries = new IMfEntry[activates.Count];
        for (var i = 0; i < activates.Count; i++)
            entries[i] = new MfComEntry(activates[i], objects);
        return entries;
    }
}

/// <summary>One catalog entry over its <see cref="IMFActivate"/>, whose attributes are read by GUID when asked.</summary>
[SupportedOSPlatform("windows")]
internal sealed class MfComEntry(IMFActivate activate, IMfObjects objects) : IMfEntry
{
    private IMFActivate? _activate = activate;

    private IMFActivate Activate => _activate ?? throw new ObjectDisposedException(nameof(MfComEntry));

    public string? GetString(Guid key) => MfAttributes.String(Activate, key, objects);

    public Guid? GetGuid(Guid key) => Activate.GetGUID(key, out var value) >= 0 ? value : null;

    public ulong? GetUInt64(Guid key) => Activate.GetUINT64(key, out var value) >= 0 ? value : null;

    /// <summary>A port with its own reference to the activation (a wrapper of its own), so disposing the entry leaves it working.</summary>
    public IMfEncoderPort CreatePort() => new MfComPort(MfWrappers.Wrap<IMFActivate>(MfWrappers.Raw(Activate)), objects);

    public void Dispose() => MfWrappers.Free(Interlocked.Exchange(ref _activate, null));
}

[SupportedOSPlatform("windows")]
internal static class MfAttributes
{
    /// <summary>A string attribute, or null (absent, or another type).</summary>
    public static string? String(IMFAttributes attributes, Guid key, IMfObjects objects) => String(attributes, key, objects, out var value) >= 0 ? value : null;

    /// <summary>A string attribute and the read's HRESULT (<paramref name="value"/> null unless it succeeded).</summary>
    public static int String(IMFAttributes attributes, Guid key, IMfObjects objects, out string? value)
    {
        // Owner of p: this call; freed whatever the answer (a failing call that set it included).
        var hr = attributes.GetAllocatedString(key, out var p, out var length);
        try
        {
            value = hr >= 0 && p != 0 ? Marshal.PtrToStringUni(p, (int)Math.Min(length, int.MaxValue)) : null;
            return hr >= 0 && p == 0 ? MfCom.EPointer : hr;
        }
        finally
        {
            objects.FreeCoTaskMem(p);
        }
    }

    public static uint? UInt32(IMFAttributes attributes, Guid key) => attributes.GetUINT32(key, out var v) >= 0 ? v : null;

    public static ulong? UInt64(IMFAttributes attributes, Guid key) => attributes.GetUINT64(key, out var v) >= 0 ? v : null;
}

/// <summary>
/// The real <see cref="IMfEncoderPort"/>: one encoder MFT through the interfaces of <c>MediaFoundationNative.cs</c>. It
/// owns its activation's wrapper, the transform's (whose <see cref="ICodecAPI"/> and
/// <see cref="IMFMediaEventGenerator"/> are the same wrapper) and the transform's attributes, all freed by
/// <see cref="Release"/>. Every other object lives within one call: media types, samples and buffers are wrapped and
/// freed there, and each raw pointer a call hands back is released in a <c>finally</c>, failing calls included.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MfComPort(IMFActivate activate, IMfObjects objects) : IMfEncoderPort
{
    private IMFActivate? _activate = activate;
    private IMFTransform? _mft;
    private IMFAttributes? _attributes;
    private ICodecAPI? _codec;
    private IMFMediaEventGenerator? _events;

    public int Activate()
    {
        if (_activate == null)
            return MfCom.EPointer;
        // Owner of p: this call until a wrapper takes it over; a failing activation's pointer is released.
        var hr = _activate.ActivateObject(typeof(IMFTransform).GUID, out var p);
        if (hr < 0)
        {
            MfWrappers.Release(p);
            return hr;
        }

        if (p == 0)
            return MfCom.EPointer;
        _mft = MfWrappers.Wrap<IMFTransform>(p);
        _codec = _mft as ICodecAPI;
        _events = _mft as IMFMediaEventGenerator;

        // Owner of attributes: this call until wrapped.
        var ahr = _mft.GetAttributes(out var attributes);
        if (ahr >= 0 && attributes != 0)
            _attributes = MfWrappers.Wrap<IMFAttributes>(attributes);
        else
            MfWrappers.Release(attributes);
        return hr;
    }

    public int GetUInt32(Guid key, out uint value)
    {
        value = 0;
        return _attributes == null ? MfCom.ENoInterface : _attributes.GetUINT32(key, out value);
    }

    public int GetUInt64(Guid key, out ulong value)
    {
        value = 0;
        return _attributes == null ? MfCom.ENoInterface : _attributes.GetUINT64(key, out value);
    }

    public int GetString(Guid key, out string? value)
    {
        value = null;
        return _attributes == null ? MfCom.ENoInterface : MfAttributes.String(_attributes, key, objects, out value);
    }

    public int SetUInt32(Guid key, uint value) => _attributes == null ? MfCom.ENoInterface : _attributes.SetUINT32(key, value);

    /// <summary><c>CODECAPI_AVLowLatencyMode</c> is a <c>VT_BOOL</c> (codecapi.h); every other property here a <c>VT_UI4</c>.</summary>
    public int SetCodecValue(Guid api, uint value)
    {
        if (_codec == null)
            return MfCom.ENoInterface;
        var v = api == Mf.CodecLowLatencyMode ? Variant.FromBool(value != 0) : Variant.FromUInt(value);
        return _codec.SetValue(api, &v);
    }

    public int GetCodecValue(Guid api, out uint value)
    {
        value = 0;
        if (_codec == null)
            return MfCom.ENoInterface;
        // Owner of what v holds: this call; cleared whatever the answer (a failing call that filled it in included).
        var v = default(Variant);
        try
        {
            var hr = _codec.GetValue(api, &v);
            if (hr < 0)
                return hr;
            switch (v.Vt)
            {
                case Variant.VtBool:
                    value = v.Bool != 0 ? 1u : 0u;
                    return hr;
                case Variant.VtUI4:
                case Variant.VtI4:
                    value = v.UI4;
                    return hr;
                case Variant.VtUI8:
                    value = (uint)Math.Min(v.UI8, uint.MaxValue);
                    return hr;
                default:
                    // A value of a type this doesn't read: refused (and cleared, as every value is).
                    return MfCom.DispETypeMismatch;
            }
        }
        finally
        {
            objects.ClearVariant(&v);
        }
    }

    public int SetOutputType(MfVideoType type) => SetType(type, output: true);

    public int SetInputType(MfVideoType type) => SetType(type, output: false);

    /// <summary>
    /// A media type built from <paramref name="type"/> and set: the type is an in-parameter, typed, so the generated
    /// marshalling adds and drops the call's reference, and this call frees its own wrapper, refused or not.
    /// </summary>
    private int SetType(MfVideoType type, bool output)
    {
        if (_mft == null)
            return MfCom.EPointer;
        var hr = objects.CreateMediaType(out var p);
        if (hr < 0)
        {
            MfWrappers.Release(p);
            return hr;
        }

        var media = MfWrappers.Wrap<IMFMediaType>(p);
        try
        {
            hr = Build(media, type);
            if (hr < 0)
                return hr;
            return output ? _mft.SetOutputType(0, media, 0) : _mft.SetInputType(0, media, 0);
        }
        finally
        {
            MfWrappers.Free(media);
        }
    }

    private static int Build(IMFMediaType media, MfVideoType type)
    {
        int hr;
        if ((hr = media.SetGUID(MfCom.MajorType, MfCom.MediaTypeVideo)) < 0 ||
            (hr = media.SetGUID(MfCom.Subtype, type.Subtype)) < 0 ||
            (hr = media.SetUINT64(MfCom.FrameSize, Pair((uint)type.Width, (uint)type.Height))) < 0 ||
            (hr = media.SetUINT64(MfCom.PixelAspectRatio, Pair(1, 1))) < 0)
            return hr;
        if (type.FrameRate > 0 && (hr = media.SetUINT64(MfCom.FrameRate, Pair((uint)type.FrameRate, 1))) < 0)
            return hr;
        foreach (var (key, value) in new (Guid, uint?)[]
                 {
                     (MfCom.AvgBitrate, type.Bitrate), (MfCom.Mpeg2Profile, type.Profile), (MfCom.InterlaceMode, type.InterlaceMode),
                     (MfCom.DefaultStride, type.DefaultStride is { } stride ? unchecked((uint)stride) : null),
                     (MfCom.YuvMatrix, type.Matrix), (MfCom.VideoPrimaries, type.Primaries), (MfCom.TransferFunction, type.Transfer),
                     (MfCom.NominalRange, type.NominalRange),
                 })
        {
            if (value is { } v && (hr = media.SetUINT32(key, v)) < 0)
                return hr;
        }

        // Never MF_MT_MPEG_SEQUENCE_HEADER: a keyframe's parameter sets come from the stream itself.
        return Mf.SOk;
    }

    private static ulong Pair(uint high, uint low) => ((ulong)high << 32) | low;

    public int GetOutputCurrentType(out MfVideoType? type)
    {
        type = null;
        if (_mft == null)
            return MfCom.EPointer;
        // Owner of p: this call (ReadType frees its wrapper; a failing call's pointer is released).
        var hr = _mft.GetOutputCurrentType(0, out var p);
        return ReadType(hr, p, out type);
    }

    public int GetOutputAvailableType(int index, out MfVideoType? type)
    {
        type = null;
        if (_mft == null)
            return MfCom.EPointer;
        var hr = _mft.GetOutputAvailableType(0, (uint)index, out var p);
        return ReadType(hr, p, out type);
    }

    private static int ReadType(int hr, nint p, out MfVideoType? type)
    {
        type = null;
        if (hr < 0 || p == 0)
        {
            MfWrappers.Release(p);
            return hr < 0 ? hr : MfCom.EPointer;
        }

        var media = MfWrappers.Wrap<IMFMediaType>(p);
        try
        {
            if (media.GetGUID(MfCom.Subtype, out var subtype) < 0)
                subtype = Guid.Empty;
            var (width, height) = Split(MfAttributes.UInt64(media, MfCom.FrameSize));
            var (rateNumerator, rateDenominator) = Split(MfAttributes.UInt64(media, MfCom.FrameRate));
            type = new MfVideoType(subtype, (int)width, (int)height)
            {
                FrameRate = rateDenominator > 0 ? (int)(rateNumerator / rateDenominator) : 0,
                Bitrate = MfAttributes.UInt32(media, MfCom.AvgBitrate),
                Profile = MfAttributes.UInt32(media, MfCom.Mpeg2Profile),
                DefaultStride = MfAttributes.UInt32(media, MfCom.DefaultStride) is { } stride ? unchecked((int)stride) : null,
                InterlaceMode = MfAttributes.UInt32(media, MfCom.InterlaceMode),
                Matrix = MfAttributes.UInt32(media, MfCom.YuvMatrix),
                Primaries = MfAttributes.UInt32(media, MfCom.VideoPrimaries),
                Transfer = MfAttributes.UInt32(media, MfCom.TransferFunction),
                NominalRange = MfAttributes.UInt32(media, MfCom.NominalRange),
                // The type's sequence header is never read (MfVideoType.SequenceHeader).
            };
            return hr;
        }
        finally
        {
            MfWrappers.Free(media);
        }
    }

    private static (uint High, uint Low) Split(ulong? pair) => pair is { } v ? ((uint)(v >> 32), (uint)v) : (0, 0);

    public int GetInputCurrentLayout(out MfInputLayout layout)
    {
        layout = default;
        if (_mft == null)
            return MfCom.EPointer;
        // Owner of p: this call.
        var hr = _mft.GetInputCurrentType(0, out var p);
        if (hr < 0 || p == 0)
        {
            MfWrappers.Release(p);
            return hr < 0 ? hr : MfCom.EPointer;
        }

        var media = MfWrappers.Wrap<IMFMediaType>(p);
        try
        {
            var (width, height) = Split(MfAttributes.UInt64(media, MfCom.FrameSize));
            layout = new MfInputLayout((int)width, (int)height,
                MfAttributes.UInt32(media, MfCom.DefaultStride) is { } stride ? unchecked((int)stride) : null,
                MfAttributes.UInt32(media, MfCom.SampleSize) is { } size ? (int)Math.Min(size, int.MaxValue) : null);
            return hr;
        }
        finally
        {
            MfWrappers.Free(media);
        }
    }

    public int GetInputStreamInfo(out MfStreamInfo info)
    {
        info = default;
        if (_mft == null)
            return MfCom.EPointer;
        var hr = _mft.GetInputStreamInfo(0, out var raw);
        if (hr >= 0)
            info = new MfStreamInfo(raw.Flags, Int(raw.Size), Int(raw.Alignment));
        return hr;
    }

    public int GetOutputStreamInfo(out MfStreamInfo info)
    {
        info = default;
        if (_mft == null)
            return MfCom.EPointer;
        var hr = _mft.GetOutputStreamInfo(0, out var raw);
        if (hr >= 0)
            info = new MfStreamInfo(raw.Flags, Int(raw.Size), Int(raw.Alignment));
        return hr;
    }

    private static int Int(uint v) => (int)Math.Min(v, int.MaxValue);

    public int ProcessMessage(uint message) => _mft == null ? MfCom.EPointer : _mft.ProcessMessage(message, 0);

    public int NextEvent(out MfEvent e)
    {
        e = default;
        if (_events == null)
            return MfCom.ENoInterface;
        // Owner of p: this call; a failing GetEvent's pointer is released too, never trusted to be null.
        var hr = _events.GetEvent(0, out var p);
        if (hr < 0 || p == 0)
        {
            MfWrappers.Release(p);
            return hr < 0 ? hr : MfCom.EPointer;
        }

        var mediaEvent = MfWrappers.Wrap<IMFMediaEvent>(p);
        try
        {
            if ((hr = mediaEvent.GetEventType(out var type)) < 0 || (hr = mediaEvent.GetStatus(out var status)) < 0)
                return hr;
            e = new MfEvent(type, status);
            return hr;
        }
        finally
        {
            MfWrappers.Free(mediaEvent);
        }
    }

    public int CreateInputBuffer(int size, int alignment, out IMfInputBuffer? buffer)
    {
        buffer = null;
        var hr = objects.CreateBuffer(size, alignment, out var p);
        if (hr < 0 || p == 0)
        {
            MfWrappers.Release(p);
            return hr < 0 ? hr : MfCom.EPointer;
        }

        buffer = new MfComInputBuffer(MfWrappers.Wrap<IMFMediaBuffer>(p));
        return hr;
    }

    /// <summary>A sample of the buffer, made and freed within the call (the transform keeps its own reference if it holds it).</summary>
    public int ProcessInput(IMfInputBuffer buffer, long sampleTime, long duration)
    {
        if (_mft == null)
            return MfCom.EPointer;
        if (buffer is not MfComInputBuffer { Buffer: { } media })
            return Mf.EInvalidArg;
        var hr = objects.CreateSample(out var p);
        if (hr < 0 || p == 0)
        {
            MfWrappers.Release(p);
            return hr < 0 ? hr : MfCom.EPointer;
        }

        var sample = MfWrappers.Wrap<IMFSample>(p);
        try
        {
            if ((hr = sample.AddBuffer(media)) < 0 || (hr = sample.SetSampleTime(sampleTime)) < 0 || (hr = sample.SetSampleDuration(duration)) < 0)
                return hr;
            return _mft.ProcessInput(0, sample, 0);
        }
        finally
        {
            MfWrappers.Free(sample);
        }
    }

    public int ProcessOutput(MfOutputAllocation allocation, out MfOutput? output)
    {
        output = null;
        if (_mft == null)
            return MfCom.EPointer;
        IMFSample? ours = null;
        // Owner of oursRaw: this call (the reference MFT_OUTPUT_DATA_BUFFER.pSample carries in); released in the finally.
        nint oursRaw = 0;
        // Owner of theirs: this call (a sample the encoder put in pSample, its reference ours to release).
        nint theirs = 0;
        try
        {
            int hr;
            if (!allocation.EncoderProvides)
            {
                hr = OurSample(allocation, out ours);
                if (hr < 0)
                    return hr;
                oursRaw = MfWrappers.Raw(ours!);
            }

            var data = new MftOutputDataBuffer { StreamId = 0, Sample = oursRaw };
            hr = _mft.ProcessOutput(0, 1, &data, out _);
            // Owner of data.Events: this call, whatever the answer.
            MfWrappers.Release(data.Events);
            if (data.Sample != 0 && data.Sample != oursRaw)
                theirs = data.Sample;
            if (hr < 0)
                return hr;
            // No sample this time (events only, flagged so, or none given back): nothing failed, and nothing to read.
            if ((data.Status & MfCom.OutputDataBufferNoSample) == MfCom.OutputDataBufferNoSample)
                return Mf.SFalse;
            if (theirs != 0)
            {
                var raw = theirs;
                theirs = 0; // Wrap takes the reference over, even when it throws: never released here again
                var sample = MfWrappers.Wrap<IMFSample>(raw);
                try
                {
                    return ReadSome(sample, out output);
                }
                finally
                {
                    MfWrappers.Free(sample);
                }
            }

            return ours != null && data.Sample == oursRaw ? ReadSome(ours, out output) : Mf.SFalse;
        }
        finally
        {
            MfWrappers.Release(theirs);
            MfWrappers.Release(oursRaw);
            MfWrappers.Free(ours);
        }
    }

    /// <summary>One of ours of the allocation's size and alignment, its buffer held by the sample alone.</summary>
    private int OurSample(MfOutputAllocation allocation, out IMFSample? sample)
    {
        sample = null;
        var hr = objects.CreateBuffer(Math.Max(allocation.Size, 1), Math.Max(allocation.Alignment, 1), out var bp);
        if (hr < 0 || bp == 0)
        {
            MfWrappers.Release(bp);
            return hr < 0 ? hr : MfCom.EPointer;
        }

        var buffer = MfWrappers.Wrap<IMFMediaBuffer>(bp);
        try
        {
            hr = objects.CreateSample(out var sp);
            if (hr < 0 || sp == 0)
            {
                MfWrappers.Release(sp);
                return hr < 0 ? hr : MfCom.EPointer;
            }

            sample = MfWrappers.Wrap<IMFSample>(sp);
            hr = sample.AddBuffer(buffer);
            if (hr < 0)
            {
                MfWrappers.Free(sample);
                sample = null;
            }

            return hr;
        }
        finally
        {
            MfWrappers.Free(buffer);
        }
    }

    /// <summary><see cref="Read"/>, where an empty sample (ours, which the encoder left unwritten) is no output: <see cref="Mf.SFalse"/>.</summary>
    private static int ReadSome(IMFSample sample, out MfOutput? output)
    {
        var hr = Read(sample, out output);
        if (hr < 0 || output is not { Data.Length: 0 })
            return hr;
        output = null;
        return Mf.SFalse;
    }

    /// <summary>An output sample's bytes (one contiguous buffer), its time and its clean-point flag, copied out.</summary>
    private static int Read(IMFSample sample, out MfOutput? output)
    {
        output = null;
        long? time = sample.GetSampleTime(out var t) >= 0 ? t : null;
        bool? clean = MfAttributes.UInt32(sample, MfCom.CleanPoint) is { } c ? c != 0 : null;
        // Owner of p: this call.
        var hr = sample.ConvertToContiguousBuffer(out var p);
        if (hr < 0 || p == 0)
        {
            MfWrappers.Release(p);
            return hr < 0 ? hr : MfCom.EPointer;
        }

        var buffer = MfWrappers.Wrap<IMFMediaBuffer>(p);
        try
        {
            if ((hr = buffer.Lock(out var bytes, out _, out var length)) < 0)
                return hr;
            try
            {
                output = new MfOutput(new ReadOnlySpan<byte>(bytes, (int)Math.Min(length, int.MaxValue)).ToArray(), time, clean);
            }
            finally
            {
                buffer.Unlock();
            }

            return hr;
        }
        finally
        {
            MfWrappers.Free(buffer);
        }
    }

    /// <summary><c>IMFActivate::ShutdownObject</c>: shuts the transform down, which ends a blocked <c>GetEvent</c> with <c>MF_E_SHUTDOWN</c>.</summary>
    public int Shutdown() => _activate == null ? MfCom.EPointer : _activate.ShutdownObject();

    public void Release()
    {
        // The codec API and the event generator are the transform's wrapper, cast: freed with it.
        _codec = null;
        _events = null;
        MfWrappers.Free(Interlocked.Exchange(ref _attributes, null));
        MfWrappers.Free(Interlocked.Exchange(ref _mft, null));
        MfWrappers.Free(Interlocked.Exchange(ref _activate, null));
    }
}

/// <summary>An input buffer: written at its start, its current length set to what was written; disposing frees it.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MfComInputBuffer(IMFMediaBuffer buffer) : IMfInputBuffer
{
    public IMFMediaBuffer? Buffer { get; private set; } = buffer;

    public int Write(ReadOnlySpan<byte> data)
    {
        if (Buffer is not { } media)
            return MfCom.EPointer;
        var hr = media.Lock(out var p, out var max, out _);
        if (hr < 0)
            return hr;
        try
        {
            if (max < (uint)data.Length)
                return Mf.EInvalidArg;
            data.CopyTo(new Span<byte>(p, data.Length));
        }
        finally
        {
            media.Unlock();
        }

        return media.SetCurrentLength((uint)data.Length);
    }

    public void Dispose()
    {
        var media = Buffer;
        Buffer = null;
        MfWrappers.Free(media);
    }
}
