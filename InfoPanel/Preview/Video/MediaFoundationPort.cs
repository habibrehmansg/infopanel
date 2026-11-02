namespace InfoPanel.Preview.Video;

/// <summary>
/// The Media Foundation values <see cref="MediaFoundationH264"/> uses, from the SDK headers (<c>mfapi.h</c>,
/// <c>mftransform.h</c>, <c>mferror.h</c>, <c>codecapi.h</c>, <c>winerror.h</c>). Plain values: nothing here calls into
/// Windows, so the backend and its seams build and run on any OS.
/// </summary>
internal static class Mf
{
    // ---- HRESULTs ----
    public const int SOk = 0;
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int EFail = unchecked((int)0x80004005);
    public const int EInvalidArg = unchecked((int)0x80070057);
    public const int EOutOfMemory = unchecked((int)0x8007000E);
    public const int InvalidMediaType = unchecked((int)0xC00D36B4); // MF_E_INVALIDMEDIATYPE
    public const int NotAccepting = unchecked((int)0xC00D36B5); // MF_E_NOTACCEPTING
    public const int HwMftFailedStartStreaming = unchecked((int)0xC00D3704); // MF_E_HW_MFT_FAILED_START_STREAMING
    public const int Shutdown = unchecked((int)0xC00D3E85); // MF_E_SHUTDOWN
    public const int TransformTypeNotSet = unchecked((int)0xC00D6D60); // MF_E_TRANSFORM_TYPE_NOT_SET
    public const int TransformStreamChange = unchecked((int)0xC00D6D61); // MF_E_TRANSFORM_STREAM_CHANGE
    public const int TransformNeedMoreInput = unchecked((int)0xC00D6D72); // MF_E_TRANSFORM_NEED_MORE_INPUT
    public const int UnsupportedD3DType = unchecked((int)0xC00D6D76); // MF_E_UNSUPPORTED_D3D_TYPE

    // ---- MFTEnumEx flags ----
    public const uint EnumHardware = 0x4; // MFT_ENUM_FLAG_HARDWARE
    public const uint EnumSortAndFilter = 0x40; // MFT_ENUM_FLAG_SORTANDFILTER

    // ---- MFT_MESSAGE_TYPE ----
    public const uint MessageCommandFlush = 0;
    public const uint MessageCommandDrain = 1;
    public const uint MessageSetD3DManager = 2;
    public const uint MessageNotifyBeginStreaming = 0x10000000;
    public const uint MessageNotifyEndStreaming = 0x10000001;
    public const uint MessageNotifyEndOfStream = 0x10000002;
    public const uint MessageNotifyStartOfStream = 0x10000003;

    // ---- media event types ----
    public const uint EventError = 1; // MEError
    public const uint EventNeedInput = 601; // METransformNeedInput
    public const uint EventHaveOutput = 602; // METransformHaveOutput

    // ---- MFT_OUTPUT_STREAM_INFO flags ----
    public const uint OutputStreamProvidesSamples = 0x100;
    public const uint OutputStreamCanProvideSamples = 0x200;

    // ---- an entry's attributes (read from the catalog's IMFActivate) ----
    public static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3"); // MFT_FRIENDLY_NAME_Attribute
    public static readonly Guid HardwareVendorId = new("3aecb0cc-035b-4bcc-8185-2b8d551ef3af"); // MFT_ENUM_HARDWARE_VENDOR_ID_Attribute
    public static readonly Guid HardwareUrl = new("2fb866ac-b078-4942-ab6c-003d05cda674"); // MFT_ENUM_HARDWARE_URL_Attribute
    public static readonly Guid TransformClsid = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c"); // MFT_TRANSFORM_CLSID_Attribute

    /// <summary>A PCI device-instance path on an entry (unnamed in the logs that show it; read by GUID).</summary>
    public static readonly Guid DeviceInstance = new("556155e0-8b27-44ac-8dbf-2547b9bd7112");

    public static readonly Guid AdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1"); // MFT_ENUM_ADAPTER_LUID

    // ---- the activated transform's attributes ----
    public static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a"); // MF_TRANSFORM_ASYNC
    public static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207"); // MF_TRANSFORM_ASYNC_UNLOCK
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // MF_LOW_LATENCY

    /// <summary>The graphics driver's version on the activated transform (likely <c>MFT_GFX_DRIVER_VERSION_ID_Attribute</c>; read by GUID).</summary>
    public static readonly Guid DriverVersion = new("f34b9093-05e0-4b16-993d-3e2a2cde6ad3");

    // ---- subtypes ----
    public static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71"); // MFVideoFormat_H264
    public static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71"); // MFVideoFormat_NV12

    // ---- ICodecAPI properties ----
    public static readonly Guid CodecRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423"); // CODECAPI_AVEncCommonRateControlMode
    public static readonly Guid CodecMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52"); // CODECAPI_AVEncCommonMeanBitRate
    public static readonly Guid CodecBFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2"); // CODECAPI_AVEncMPVDefaultBPictureCount
    public static readonly Guid CodecGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1"); // CODECAPI_AVEncMPVGOPSize
    public static readonly Guid CodecLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"); // CODECAPI_AVLowLatencyMode
    public static readonly Guid CodecForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345"); // CODECAPI_AVEncVideoForceKeyFrame

    // ---- media type values ----
    public const uint ProfileBaseline = 66; // eAVEncH264VProfile_Base
    public const uint InterlaceProgressive = 2; // MFVideoInterlace_Progressive
    public const uint MatrixBt709 = 1; // MFVideoTransferMatrix_BT709
    public const uint PrimariesBt709 = 2; // MFVideoPrimaries_BT709
    public const uint TransferBt709 = 5; // MFVideoTransFunc_709
    public const uint NominalRange16To235 = 2; // MFNominalRange_16_235
    public const uint RateControlCbr = 0; // eAVEncCommonRateControlMode_CBR

    public static string Hr(int hr) => $"0x{unchecked((uint)hr):X8}";
}

/// <summary>
/// A video media type as the backend sets and reads it: the subtype, the frame's size and rate, and the attributes the
/// backend uses (null: absent). <see cref="SequenceHeader"/> is what an encoder may offer on its output type
/// (<c>MF_MT_MPEG_SEQUENCE_HEADER</c>); the backend never reads it: a keyframe's parameter sets come from the stream
/// itself, since the header's SPS can differ from the one the slices are coded with.
/// </summary>
internal sealed record MfVideoType(Guid Subtype, int Width, int Height)
{
    public int FrameRate { get; init; }
    public uint? Bitrate { get; init; }
    public uint? Profile { get; init; }
    public int? DefaultStride { get; init; }
    public uint? InterlaceMode { get; init; }
    public uint? Matrix { get; init; }
    public uint? Primaries { get; init; }
    public uint? Transfer { get; init; }
    public uint? NominalRange { get; init; }
    public byte[]? SequenceHeader { get; init; }

    public override string ToString() =>
        $"{(Subtype == Mf.H264 ? "H.264" : Subtype == Mf.Nv12 ? "NV12" : Subtype.ToString())} {Width}x{Height}" +
        (Profile is { } profile ? $" profile {profile}" : "");
}

/// <summary>The current input type's layout: its frame size, <c>MF_MT_DEFAULT_STRIDE</c> and <c>MF_MT_SAMPLE_SIZE</c> (null: absent).</summary>
internal readonly record struct MfInputLayout(int Width, int Height, int? DefaultStride, int? SampleSize);

/// <summary>An input or output stream's info: its flags, its buffer size (a minimum) and its alignment.</summary>
internal readonly record struct MfStreamInfo(uint Flags, int Size, int Alignment);

/// <summary>How <see cref="IMfEncoderPort.ProcessOutput"/> gets its sample: the encoder's own, or one of ours of this size and alignment.</summary>
internal readonly record struct MfOutputAllocation(bool EncoderProvides, int Size, int Alignment)
{
    public static MfOutputAllocation Of(MfStreamInfo info) =>
        (info.Flags & (Mf.OutputStreamProvidesSamples | Mf.OutputStreamCanProvideSamples)) != 0
            ? new MfOutputAllocation(true, 0, 0)
            : new MfOutputAllocation(false, info.Size, Math.Max(info.Alignment, 1));
}

/// <summary>A media event: its type (<see cref="Mf.EventNeedInput"/>, …) and its status.</summary>
internal readonly record struct MfEvent(uint Type, int Status);

/// <summary>One output sample, copied: its bytes (Annex B), its sample time and its clean-point flag (null: absent).</summary>
internal sealed record MfOutput(byte[] Data, long? SampleTime, bool? CleanPoint);

/// <summary>
/// The hardware H.264 encoders Windows lists (<c>MFTEnumEx</c>) and the process's Media Foundation: one seam, so the
/// backend's choice runs over a fake on any OS. Every call answers an HRESULT; a missing Media Foundation (Windows N)
/// throws <see cref="DllNotFoundException"/>.
/// </summary>
internal interface IMfCatalog
{
    /// <summary>A thread's COM init (<c>CoInitializeEx</c>, multithreaded), first on every thread the backend runs; <paramref name="role"/> names it for the log.</summary>
    int EnterThread(string role);

    /// <summary>A thread's COM uninit, last, after a successful <see cref="EnterThread"/>.</summary>
    void LeaveThread();

    /// <summary><c>MFStartup</c>, once per process however often it is called (never <c>MFShutdown</c>).</summary>
    int Startup();

    /// <summary>
    /// Every video encoder entry whose output is H.264 that <paramref name="flags"/> asks for, in the order Windows
    /// returns them, unfiltered and unsorted. The caller disposes each entry; a port made from one keeps its own reference.
    /// </summary>
    int Enumerate(uint flags, out IReadOnlyList<IMfEntry> entries);
}

/// <summary>One catalog entry: an encoder's activation object, whose attributes are read by GUID (null: absent).</summary>
internal interface IMfEntry : IDisposable
{
    string? GetString(Guid key);

    Guid? GetGuid(Guid key);

    ulong? GetUInt64(Guid key);

    /// <summary>A port over this entry's activation, not activated yet.</summary>
    IMfEncoderPort CreatePort();
}

/// <summary>
/// One encoder MFT, as the backend drives it. Every call answers an HRESULT and none throws for a failing one; the
/// owner of each native object behind it is the port, released by <see cref="Release"/> (failing calls included).
/// </summary>
internal interface IMfEncoderPort
{
    /// <summary><c>IMFActivate::ActivateObject</c> for <c>IMFTransform</c>.</summary>
    int Activate();

    /// <summary>The activated transform's attributes.</summary>
    int GetUInt32(Guid key, out uint value);

    int GetUInt64(Guid key, out ulong value);

    int SetUInt32(Guid key, uint value);

    /// <summary><c>ICodecAPI::SetValue</c> and <c>GetValue</c>.</summary>
    int SetCodecValue(Guid api, uint value);

    int GetCodecValue(Guid api, out uint value);

    int SetOutputType(MfVideoType type);

    int SetInputType(MfVideoType type);

    int GetOutputCurrentType(out MfVideoType? type);

    int GetOutputAvailableType(int index, out MfVideoType? type);

    /// <summary>The current input type's frame size, default stride and sample size.</summary>
    int GetInputCurrentLayout(out MfInputLayout layout);

    int GetInputStreamInfo(out MfStreamInfo info);

    int GetOutputStreamInfo(out MfStreamInfo info);

    /// <summary><c>IMFTransform::ProcessMessage</c> with no parameter (an <c>MFT_MESSAGE_TYPE</c> value).</summary>
    int ProcessMessage(uint message);

    /// <summary><c>IMFMediaEventGenerator::GetEvent(0)</c>: blocks for the next event; <see cref="Mf.Shutdown"/> once shut down.</summary>
    int NextEvent(out MfEvent e);

    /// <summary>A system-memory buffer of at least <paramref name="size"/> bytes aligned to <paramref name="alignment"/> (1: none).</summary>
    int CreateInputBuffer(int size, int alignment, out IMfInputBuffer? buffer);

    /// <summary>A sample of <paramref name="buffer"/> at <paramref name="sampleTime"/> (100 ns units) into the transform.</summary>
    int ProcessInput(IMfInputBuffer buffer, long sampleTime, long duration);

    /// <summary>
    /// <c>ProcessOutput</c> into the encoder's sample, or one of ours as <paramref name="allocation"/> says (made and
    /// released within the call); the output is copied out and its sample released before this returns.
    /// </summary>
    int ProcessOutput(MfOutputAllocation allocation, out MfOutput? output);

    /// <summary><c>IMFActivate::ShutdownObject</c>: ends a blocked <see cref="NextEvent"/> with <see cref="Mf.Shutdown"/>.</summary>
    int Shutdown();

    /// <summary>Releases the transform and the activation; nothing is called after it.</summary>
    void Release();
}

/// <summary>An input buffer: the bytes written at its start and its current length set to theirs; disposing releases it.</summary>
internal interface IMfInputBuffer : IDisposable
{
    int Write(ReadOnlySpan<byte> data);
}
