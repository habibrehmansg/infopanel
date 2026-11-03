using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using InfoPanel.Preview.Video;
using Xunit;

namespace InfoPanel.Tests.Preview.Video;

// The declarations under test are Windows-only for the product; their ABI and the generated stubs are managed code that
// runs on any OS, here over synthetic native objects (SyntheticMediaFoundation.cs). Nothing here calls Windows.
#pragma warning disable CA1416

/// <summary>
/// Media Foundation's ABI as the engine declares it, on any 64-bit OS: the structures' layouts, every interface against
/// the SDK headers' table (<see cref="MfHeaders"/>: IID, base, slot order, parameter types, widths and passing,
/// PreserveSig and an int return), the HRESULT and GUID values against the headers, calls through the generated stubs
/// into synthetic native objects (arguments at their slots, HRESULTs back unchanged, references balanced), the real
/// port's own call sites against a transform that keeps nothing and one that keeps what it accepts, and the real
/// catalog's adapter over synthetic activation objects in mixed-vendor orders.
/// </summary>
[Collection(MediaFoundationAbiCollection.Name)]
public sealed unsafe class MediaFoundationAbiTests
{
    // ---- layouts ----

    [Fact]
    public void TheStructures_HaveTheir64BitWindowsLayouts()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(32, Unsafe.SizeOf<MftOutputDataBuffer>());
        Assert.Equal(0, Offset<MftOutputDataBuffer>(nameof(MftOutputDataBuffer.StreamId)));
        Assert.Equal(8, Offset<MftOutputDataBuffer>(nameof(MftOutputDataBuffer.Sample)));
        Assert.Equal(16, Offset<MftOutputDataBuffer>(nameof(MftOutputDataBuffer.Status)));
        Assert.Equal(24, Offset<MftOutputDataBuffer>(nameof(MftOutputDataBuffer.Events)));

        Assert.Equal(24, Unsafe.SizeOf<MftInputStreamInfo>());
        Assert.Equal(0, Offset<MftInputStreamInfo>(nameof(MftInputStreamInfo.MaxLatency)));
        Assert.Equal(8, Offset<MftInputStreamInfo>(nameof(MftInputStreamInfo.Flags)));
        Assert.Equal(12, Offset<MftInputStreamInfo>(nameof(MftInputStreamInfo.Size)));
        Assert.Equal(16, Offset<MftInputStreamInfo>(nameof(MftInputStreamInfo.MaxLookahead)));
        Assert.Equal(20, Offset<MftInputStreamInfo>(nameof(MftInputStreamInfo.Alignment)));

        Assert.Equal(12, Unsafe.SizeOf<MftOutputStreamInfo>());
        Assert.Equal(4, Offset<MftOutputStreamInfo>(nameof(MftOutputStreamInfo.Size)));
        Assert.Equal(8, Offset<MftOutputStreamInfo>(nameof(MftOutputStreamInfo.Alignment)));

        Assert.Equal(32, Unsafe.SizeOf<MftRegisterTypeInfo>());
        Assert.Equal(16, Offset<MftRegisterTypeInfo>(nameof(MftRegisterTypeInfo.Sub)));

        Assert.Equal(24, Unsafe.SizeOf<Variant>());
        Assert.Equal(0, Offset<Variant>(nameof(Variant.Vt)));
        Assert.Equal(8, Offset<Variant>(nameof(Variant.UI4)));
        Assert.Equal(8, Offset<Variant>(nameof(Variant.UI8)));
        Assert.Equal(8, Offset<Variant>(nameof(Variant.Bool)));
        // The VARTYPEs (wtypes.h).
        Assert.Equal((0, 3, 11, 19, 21), (Variant.VtEmpty, Variant.VtI4, Variant.VtBool, Variant.VtUI4, Variant.VtUI8));
    }

    private static int Offset<T>(string field) where T : struct => (int)Marshal.OffsetOf<T>(field);

    // ---- the headers' table ----

    public static TheoryData<string> Interfaces => [.. MfHeaders.All.Select(i => i.Name)];

    [Theory]
    [MemberData(nameof(Interfaces))]
    public void EachInterface_MatchesTheHeaders(string name)
    {
        var header = MfHeaders.Named(name);
        var type = typeof(MediaFoundationH264).Assembly.GetType($"InfoPanel.Preview.Video.{name}", throwOnError: true)!;
        Assert.True(type.IsInterface);
        Assert.Equal(header.Iid, type.GUID);

        // The base: the one interface it extends directly (none for IUnknown's direct heirs).
        var bases = type.GetInterfaces();
        var direct = bases.Where(b => !bases.Any(o => o != b && o.GetInterfaces().Contains(b))).Select(b => b.Name).ToArray();
        Assert.Equal(header.Base is { } b ? [b] : [], direct);

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            // The generator redeclares a base's methods in a derived interface (shadows that forward to the base's slots):
            // those aren't the derived interface's own.
            .Where(m => !m.IsSpecialName && !bases.Any(b => b.GetMethods().Any(bm => bm.Name == m.Name)))
            .OrderBy(m => m.MetadataToken).ToArray();
        Assert.Equal(header.Methods.Select(m => m.Name), methods.Select(m => m.Name));
        foreach (var (expected, method) in header.Methods.Zip(methods))
        {
            Assert.True(method.ReturnType == typeof(int), $"{name}.{method.Name} returns {method.ReturnType.Name}, not an int HRESULT");
            Assert.True(method.MethodImplementationFlags.HasFlag(MethodImplAttributes.PreserveSig), $"{name}.{method.Name} isn't PreserveSig");
            var parameters = method.GetParameters().Select(Describe).ToArray();
            Assert.True(expected.Params.SequenceEqual(parameters),
                $"{name}.{method.Name}: the header has ({string.Join(", ", expected.Params)}), the declaration ({string.Join(", ", parameters)})");
            Assert.Equal(expected.Params.Select(p => (p.Width, p.PointeeWidth)), parameters.Select(p => (p.Width, p.PointeeWidth)));
        }
    }

    private static MfParam Describe(ParameterInfo p)
    {
        var t = p.ParameterType;
        if (t.IsByRef)
            return new MfParam(t.GetElementType()!, p.IsOut ? MfPass.Out : p.IsIn ? MfPass.In : MfPass.Pointer);
        if (t.IsPointer)
        {
            var element = t.GetElementType()!;
            return element.IsPointer ? new MfParam(element.GetElementType()!, MfPass.PointerPointer) : new MfParam(element, MfPass.Pointer);
        }

        return t.IsInterface ? new MfParam(t, MfPass.Interface) : new MfParam(t, MfPass.Value);
    }

    [Fact]
    public void EveryDeclaredComInterface_IsInTheTable()
    {
        var declared = typeof(MediaFoundationH264).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.GetCustomAttributes().Any(a => a.GetType().Name == "GeneratedComInterfaceAttribute"))
            .Select(t => t.Name).Order();
        Assert.Equal(MfHeaders.All.Select(i => i.Name).Order(), declared);
    }

    // ---- values ----

    /// <summary>
    /// Each HRESULT and GUID the backend and the port use, with the SDK header that declares it. The device key's GUID
    /// isn't declared in the SDK headers (mfapi.h has no such name); its value is what Windows sets on a hardware
    /// encoder entry, read by GUID.
    /// </summary>
    public static TheoryData<string, string, string> Values => new()
    {
        { "S_FALSE", "winerror.h", Hr(Mf.SFalse) + "=00000001" },
        { "E_NOTIMPL", "winerror.h", Hr(Mf.ENotImpl) + "=80004001" },
        { "E_NOINTERFACE", "winerror.h", Hr(MfCom.ENoInterface) + "=80004002" },
        { "E_POINTER", "winerror.h", Hr(MfCom.EPointer) + "=80004003" },
        { "E_FAIL", "winerror.h", Hr(Mf.EFail) + "=80004005" },
        { "E_INVALIDARG", "winerror.h", Hr(Mf.EInvalidArg) + "=80070057" },
        { "E_OUTOFMEMORY", "winerror.h", Hr(Mf.EOutOfMemory) + "=8007000E" },
        { "DISP_E_TYPEMISMATCH", "winerror.h", Hr(MfCom.DispETypeMismatch) + "=80020005" },
        { "MF_E_INVALIDMEDIATYPE", "mferror.h", Hr(Mf.InvalidMediaType) + "=C00D36B4" },
        { "MF_E_NOTACCEPTING", "mferror.h", Hr(Mf.NotAccepting) + "=C00D36B5" },
        { "MF_E_HW_MFT_FAILED_START_STREAMING", "mferror.h", Hr(Mf.HwMftFailedStartStreaming) + "=C00D3704" },
        { "MF_E_SHUTDOWN", "mferror.h", Hr(Mf.Shutdown) + "=C00D3E85" },
        { "MF_E_TRANSFORM_TYPE_NOT_SET", "mferror.h", Hr(Mf.TransformTypeNotSet) + "=C00D6D60" },
        { "MF_E_TRANSFORM_STREAM_CHANGE", "mferror.h", Hr(Mf.TransformStreamChange) + "=C00D6D61" },
        { "MF_E_TRANSFORM_NEED_MORE_INPUT", "mferror.h", Hr(Mf.TransformNeedMoreInput) + "=C00D6D72" },
        { "MF_E_UNSUPPORTED_D3D_TYPE", "mferror.h", Hr(Mf.UnsupportedD3DType) + "=C00D6D76" },
        { "MFT_ENUM_FLAG_HARDWARE", "mfapi.h", $"{Mf.EnumHardware:X}=4" },
        { "MFT_ENUM_FLAG_SORTANDFILTER", "mfapi.h", $"{Mf.EnumSortAndFilter:X}=40" },
        { "MFT_MESSAGE_COMMAND_FLUSH", "mftransform.h", $"{Mf.MessageCommandFlush:X}=0" },
        { "MFT_MESSAGE_COMMAND_DRAIN", "mftransform.h", $"{Mf.MessageCommandDrain:X}=1" },
        { "MFT_MESSAGE_SET_D3D_MANAGER", "mftransform.h", $"{Mf.MessageSetD3DManager:X}=2" },
        { "MFT_MESSAGE_NOTIFY_BEGIN_STREAMING", "mftransform.h", $"{Mf.MessageNotifyBeginStreaming:X}=10000000" },
        { "MFT_MESSAGE_NOTIFY_END_STREAMING", "mftransform.h", $"{Mf.MessageNotifyEndStreaming:X}=10000001" },
        { "MFT_MESSAGE_NOTIFY_END_OF_STREAM", "mftransform.h", $"{Mf.MessageNotifyEndOfStream:X}=10000002" },
        { "MFT_MESSAGE_NOTIFY_START_OF_STREAM", "mftransform.h", $"{Mf.MessageNotifyStartOfStream:X}=10000003" },
        { "MEError", "mfobjects.h", $"{Mf.EventError}=1" },
        { "METransformNeedInput", "mfobjects.h", $"{Mf.EventNeedInput}=601" },
        { "METransformHaveOutput", "mfobjects.h", $"{Mf.EventHaveOutput}=602" },
        { "METransformDrainComplete", "mfobjects.h", $"{Mf.EventDrainComplete}=603" },
        { "MFT_OUTPUT_STREAM_PROVIDES_SAMPLES", "mftransform.h", $"{Mf.OutputStreamProvidesSamples:X}=100" },
        { "MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES", "mftransform.h", $"{Mf.OutputStreamCanProvideSamples:X}=200" },
        { "MFT_OUTPUT_DATA_BUFFER_NO_SAMPLE", "mftransform.h", $"{MfCom.OutputDataBufferNoSample:X}=300" },
        { "MFT_FRIENDLY_NAME_Attribute", "mfapi.h", $"{Mf.FriendlyName}=314ffbae-5b41-4c95-9c19-4e7d586face3" },
        { "MFT_ENUM_HARDWARE_VENDOR_ID_Attribute", "mfapi.h", $"{Mf.HardwareVendorId}=3aecb0cc-035b-4bcc-8185-2b8d551ef3af" },
        { "MFT_ENUM_HARDWARE_URL_Attribute", "mfapi.h", $"{Mf.HardwareUrl}=2fb866ac-b078-4942-ab6c-003d05cda674" },
        { "MFT_TRANSFORM_CLSID_Attribute", "mfapi.h", $"{Mf.TransformClsid}=6821c42b-65a4-4e82-99bc-9a88205ecd0c" },
        { "MFT_ENUM_ADAPTER_LUID", "mfapi.h", $"{Mf.AdapterLuid}=1d39518c-e220-4da8-a07f-ba172552d6b1" },
        { "MFT_GFX_DRIVER_VERSION_ID_Attribute", "mfapi.h", $"{Mf.DriverVersion}=f34b9093-05e0-4b16-993d-3e2a2cde6ad3" },
        { "(the device key: not in the SDK headers)", "none", $"{Mf.DeviceInstance}=556155e0-8b27-44ac-8dbf-2547b9bd7112" },
        { "MF_TRANSFORM_ASYNC", "mftransform.h", $"{Mf.TransformAsync}=f81a699a-649a-497d-8c73-29f8fed6ad7a" },
        { "MF_TRANSFORM_ASYNC_UNLOCK", "mftransform.h", $"{Mf.TransformAsyncUnlock}=e5666d6b-3422-4eb6-a421-da7db1f8e207" },
        { "MF_LOW_LATENCY", "mfapi.h", $"{Mf.LowLatency}=9c27891a-ed7a-40e1-88e8-b22727a024ee" },
        { "MFVideoFormat_H264", "mfapi.h", $"{Mf.H264}=34363248-0000-0010-8000-00aa00389b71" },
        { "MFVideoFormat_NV12", "mfapi.h", $"{Mf.Nv12}=3231564e-0000-0010-8000-00aa00389b71" },
        { "MFMediaType_Video", "mfapi.h", $"{MfCom.MediaTypeVideo}=73646976-0000-0010-8000-00aa00389b71" },
        { "MFT_CATEGORY_VIDEO_ENCODER", "mfapi.h", $"{MfCom.CategoryVideoEncoder}=f79eac7d-e545-4387-bdee-d647d7bde42a" },
        { "CODECAPI_AVEncCommonRateControlMode", "codecapi.h", $"{Mf.CodecRateControlMode}=1c0608e9-370c-4710-8a58-cb6181c42423" },
        { "CODECAPI_AVEncCommonMeanBitRate", "codecapi.h", $"{Mf.CodecMeanBitRate}=f7222374-2144-4815-b550-a37f8e12ee52" },
        { "CODECAPI_AVEncMPVDefaultBPictureCount", "codecapi.h", $"{Mf.CodecBFrames}=8d390aac-dc5c-4200-b57f-814d04babab2" },
        { "CODECAPI_AVEncMPVGOPSize", "codecapi.h", $"{Mf.CodecGopSize}=95f31b26-95a4-41aa-9303-246a7fc6eef1" },
        { "CODECAPI_AVLowLatencyMode", "codecapi.h", $"{Mf.CodecLowLatencyMode}=9c27891a-ed7a-40e1-88e8-b22727a024ee" },
        { "CODECAPI_AVEncVideoForceKeyFrame", "codecapi.h", $"{Mf.CodecForceKeyFrame}=398c1b98-8353-475a-9ef2-8f265d260345" },
        { "MF_MT_MAJOR_TYPE", "mfapi.h", $"{MfCom.MajorType}=48eba18e-f8c9-4687-bf11-0a74c9f96a8f" },
        { "MF_MT_SUBTYPE", "mfapi.h", $"{MfCom.Subtype}=f7e34c9a-42e8-4714-b74b-cb29d72c35e5" },
        { "MF_MT_FRAME_SIZE", "mfapi.h", $"{MfCom.FrameSize}=1652c33d-d6b2-4012-b834-72030849a37d" },
        { "MF_MT_FRAME_RATE", "mfapi.h", $"{MfCom.FrameRate}=c459a2e8-3d2c-4e44-b132-fee5156c7bb0" },
        { "MF_MT_PIXEL_ASPECT_RATIO", "mfapi.h", $"{MfCom.PixelAspectRatio}=c6376a1e-8d0a-4027-be45-6d9a0ad39bb6" },
        { "MF_MT_INTERLACE_MODE", "mfapi.h", $"{MfCom.InterlaceMode}=e2724bb8-e676-4806-b4b2-a8d6efb44ccd" },
        { "MF_MT_AVG_BITRATE", "mfapi.h", $"{MfCom.AvgBitrate}=20332624-fb0d-4d9e-bd0d-cbf6786c102e" },
        { "MF_MT_MPEG2_PROFILE", "mfapi.h", $"{MfCom.Mpeg2Profile}=ad76a80b-2d5c-4e0b-b375-64e520137036" },
        { "MF_MT_DEFAULT_STRIDE", "mfapi.h", $"{MfCom.DefaultStride}=644b4e48-1e02-4516-b0eb-c01ca9d49ac6" },
        { "MF_MT_SAMPLE_SIZE", "mfapi.h", $"{MfCom.SampleSize}=dad3ab78-1990-408b-bce2-eba673dacc10" },
        { "MF_MT_YUV_MATRIX", "mfapi.h", $"{MfCom.YuvMatrix}=3e23d450-2c75-4d25-a00e-b91670d12327" },
        { "MF_MT_VIDEO_PRIMARIES", "mfapi.h", $"{MfCom.VideoPrimaries}=dbfbe4d7-0740-4ee0-8192-850ab0e21935" },
        { "MF_MT_TRANSFER_FUNCTION", "mfapi.h", $"{MfCom.TransferFunction}=5fb0fce9-be5c-4935-a811-ec838f8eed93" },
        { "MF_MT_VIDEO_NOMINAL_RANGE", "mfapi.h", $"{MfCom.NominalRange}=c21b8ee5-b956-4071-8daf-325edf5cab11" },
        { "MFSampleExtension_CleanPoint", "mfapi.h", $"{MfCom.CleanPoint}=9cdf01d8-a0f0-43ba-b077-eaa06cbd728a" },
        { "MFSampleExtension_Discontinuity", "mfapi.h", $"{MfCom.Discontinuity}=9cdf01d9-a0f0-43ba-b077-eaa06cbd728a" },
        { "MF_VERSION", "mfapi.h", $"{MfNative.MfVersion:X8}=00020070" },
        { "eAVEncH264VProfile_Base", "codecapi.h", $"{Mf.ProfileBaseline}=66" },
        { "MFVideoInterlace_Progressive", "mfobjects.h", $"{Mf.InterlaceProgressive}=2" },
        { "MFVideoTransferMatrix_BT709", "mfobjects.h", $"{Mf.MatrixBt709}=1" },
        { "MFVideoPrimaries_BT709", "mfobjects.h", $"{Mf.PrimariesBt709}=2" },
        { "MFVideoTransFunc_709", "mfobjects.h", $"{Mf.TransferBt709}=5" },
        { "MFNominalRange_16_235", "mfobjects.h", $"{Mf.NominalRange16To235}=2" },
        { "eAVEncCommonRateControlMode_CBR", "codecapi.h", $"{Mf.RateControlCbr}=0" },
    };

    private static string Hr(int hr) => unchecked((uint)hr).ToString("X8");

    [Theory]
    [MemberData(nameof(Values))]
    public void EachValue_IsTheHeaders(string name, string header, string declaredEqualsDocumented)
    {
        var parts = declaredEqualsDocumented.Split('=');
        Assert.True(string.Equals(parts[0], parts[1], StringComparison.OrdinalIgnoreCase), $"{name} ({header}): declared {parts[0]}, documented {parts[1]}");
    }

    // ---- calls through the generated stubs into synthetic objects ----

    private static T Wrap<T>(SyntheticObject o, MfInterface face) where T : class => MfWrappers.Wrap<T>(o.Give(face));

    private static readonly Guid Key = new("11111111-2222-3333-4444-555555555555");
    private const int Odd = unchecked((int)0x8ABC1234);

    [Fact]
    public void GetUINT32_ReachesItsSlot_AndAFailingHresultComesBackUnchanged()
    {
        var live = MfWrappers.Live;
        var o = new SyntheticAttributes();
        o.Values[Key] = 42u;
        o.FailReads[Mf.HardwareUrl] = Odd;
        var a = Wrap<IMFAttributes>(o, MfHeaders.IMFAttributes);
        Assert.Equal(0, a.GetUINT32(Key, out var v));
        Assert.Equal(42u, v);
        Assert.Equal(Odd, a.GetUINT32(Mf.HardwareUrl, out _));
        Assert.Equal(SyntheticAttributes.AttributeNotFound, a.GetUINT32(Mf.FriendlyName, out _));
        Assert.Equal([$"GetUINT32 {Key}", $"GetUINT32 {Mf.HardwareUrl}", $"GetUINT32 {Mf.FriendlyName}"], o.Log);
        MfWrappers.Free(a);
        Balanced(o, 1);
        Assert.Equal(live, MfWrappers.Live);
    }

    [Fact]
    public void SetOutputType_PassesTheStreamTheTypeAndTheFlags_AndBalancesTheTypesReferences()
    {
        var transform = new SyntheticTransform();
        var type = new SyntheticMediaType();
        type.Values[MfCom.Subtype] = Mf.H264;
        var mft = Wrap<IMFTransform>(transform, MfHeaders.IMFTransform);
        var media = Wrap<IMFMediaType>(type, MfHeaders.IMFMediaType);
        Assert.Equal(0, mft.SetOutputType(0, media, 0));
        transform.SetOutputTypeHr = Odd;
        Assert.Equal(Odd, mft.SetOutputType(0, media, 0));
        Assert.Equal(["SetOutputType 0 type 0", "SetOutputType 0 type 0"], transform.Calls);
        Assert.Equal(Mf.H264, transform.LastOutputType![MfCom.Subtype]);
        MfWrappers.Free(media);
        MfWrappers.Free(mft);
        Balanced(type, 1);
        Balanced(transform, 1);
    }

    [Fact]
    public void ProcessOutput_FillsTheDataBuffer_AndBothPointersAreTheCallers()
    {
        var transform = new SyntheticTransform { ProvidesSamples = true, EventsOnOutput = true, ProcessOutputHr = Odd };
        var mft = Wrap<IMFTransform>(transform, MfHeaders.IMFTransform);
        var data = new MftOutputDataBuffer { StreamId = 0 };
        Assert.Equal(Odd, mft.ProcessOutput(0, 1, &data, out var status));
        Assert.Equal(0u, status);
        Assert.Equal(["ProcessOutput 0 1 0 none"], transform.Calls);
        // Set despite the failure: the caller's to release.
        Assert.IsType<SyntheticSample>(SyntheticObject.Behind(data.Sample));
        Assert.IsType<SyntheticAttributes>(SyntheticObject.Behind(data.Events));
        Marshal.Release(data.Sample);
        Marshal.Release(data.Events);
        MfWrappers.Free(mft);
        Assert.All(transform.Made, m => Balanced(m, 0));
        Balanced(transform, 1);
    }

    [Fact]
    public void ICodecAPI_SetValue_PassesTheVariant_AndAFailingHresultComesBackUnchanged()
    {
        var transform = new SyntheticTransform();
        var mft = Wrap<IMFTransform>(transform, MfHeaders.IMFTransform);
        var codec = (ICodecAPI)mft;
        var value = Variant.FromUInt(7);
        Assert.Equal(0, codec.SetValue(Mf.CodecGopSize, &value));
        var on = Variant.FromBool(true);
        Assert.Equal(0, codec.SetValue(Mf.CodecLowLatencyMode, &on));
        transform.SetValueHr = Odd;
        Assert.Equal(Odd, codec.SetValue(Mf.CodecGopSize, &value));
        Assert.Equal([$"SetValue {Mf.CodecGopSize} vt19 7", $"SetValue {Mf.CodecLowLatencyMode} vt11 -1", $"SetValue {Mf.CodecGopSize} vt19 7"], transform.Calls);
        MfWrappers.Free(mft);
        Balanced(transform, 1);
    }

    [Fact]
    public void GetEvent_HandsBackTheEvent_AndAFailingHresultComesBackUnchanged()
    {
        var transform = new SyntheticTransform();
        transform.Events.Enqueue((Mf.EventNeedInput, 0, 0, false));
        transform.Events.Enqueue((Mf.EventHaveOutput, 0, Odd, true));
        var events = Wrap<IMFMediaEventGenerator>(transform, MfHeaders.IMFMediaEventGenerator);
        Assert.Equal(0, events.GetEvent(0, out var p));
        var e = MfWrappers.Wrap<IMFMediaEvent>(p);
        Assert.Equal(0, e.GetEventType(out var type));
        Assert.Equal(Mf.EventNeedInput, type);
        MfWrappers.Free(e);
        Assert.Equal(Odd, events.GetEvent(0, out p));
        Assert.NotEqual(0, p);
        Marshal.Release(p);
        Assert.Equal(["GetEvent 0", "GetEvent 0"], transform.Calls);
        MfWrappers.Free(events);
        Assert.All(transform.Made, m => Balanced(m, 0));
        Balanced(transform, 1);
    }

    /// <summary>The object's count is <paramref name="refs"/> (its creator's own, or none) and every AddRef had its Release.</summary>
    private static void Balanced(SyntheticObject o, int refs)
    {
        Assert.True(o.Refs == refs, $"{o.GetType().Name}: {o.Refs} references, expected {refs}");
        Assert.Equal(o.AddRefs + 1 - refs, o.Releases);
    }

    // ---- the real port's call sites ----

    private sealed class Rig : IDisposable
    {
        public readonly long Live = MfWrappers.Live;
        public readonly SyntheticObjects Objects = new();
        public readonly SyntheticTransform Transform;
        public readonly SyntheticActivate Activate;
        public readonly MfComPort Port;

        public Rig(bool keeps)
        {
            Transform = new SyntheticTransform { Keeps = keeps };
            Activate = new SyntheticActivate(Transform);
            Port = new MfComPort(MfWrappers.Wrap<IMFActivate>(Activate.Give(MfHeaders.IMFActivate)), Objects);
            Assert.Equal(0, Port.Activate());
        }

        /// <summary>The port's teardown: shut down, then released; everything it made is given back.</summary>
        public void Dispose()
        {
            Port.Shutdown();
            Port.Release();
        }

        public void AssertAllGivenBack()
        {
            Assert.Equal(Live, MfWrappers.Live);
            Balanced(Transform, 1);
            Balanced(Activate, 1);
            Assert.All(Transform.Made, m => Balanced(m, 0));
        }
    }

    private static MfVideoType Output => new(Mf.H264, 322, 194)
    {
        FrameRate = 30,
        Bitrate = 2_000_000,
        Profile = Mf.ProfileBaseline,
        InterlaceMode = Mf.InterlaceProgressive,
        Matrix = Mf.MatrixBt709,
    };

    private static MfVideoType Input => new(Mf.Nv12, 322, 194) { FrameRate = 30, DefaultStride = 322, InterlaceMode = Mf.InterlaceProgressive };

    [Fact]
    public void SetOutputTypeAndSetInputType_AgainstATransformThatKeepsNothing_LeaveEachTypeWithTheTestsOneReference()
    {
        using (var rig = new Rig(keeps: false))
        {
            Assert.Equal(0, rig.Port.SetOutputType(Output));
            Assert.Equal(1, rig.Objects.Types[0].Refs);
            Assert.Equal(0, rig.Port.SetInputType(Input));
            Assert.Equal(1, rig.Objects.Types[1].Refs);
            rig.Transform.SetOutputTypeHr = Mf.InvalidMediaType;
            rig.Transform.SetInputTypeHr = Mf.InvalidMediaType;
            Assert.Equal(Mf.InvalidMediaType, rig.Port.SetOutputType(Output));
            Assert.Equal(1, rig.Objects.Types[2].Refs);
            Assert.Equal(Mf.InvalidMediaType, rig.Port.SetInputType(Input));
            Assert.Equal(1, rig.Objects.Types[3].Refs);

            // What the type carried: the frame, its rate, the hints, and never a sequence header.
            var output = rig.Transform.LastOutputType!;
            Assert.Equal(MfCom.MediaTypeVideo, output[MfCom.MajorType]);
            Assert.Equal(Mf.H264, output[MfCom.Subtype]);
            Assert.Equal((322UL << 32) | 194, output[MfCom.FrameSize]);
            Assert.Equal((30UL << 32) | 1, output[MfCom.FrameRate]);
            Assert.Equal(66u, output[MfCom.Mpeg2Profile]);
            Assert.Equal(2_000_000u, output[MfCom.AvgBitrate]);
            Assert.Equal(1u, output[MfCom.YuvMatrix]);
            Assert.Equal(322u, rig.Transform.LastInputType![MfCom.DefaultStride]);
            Assert.Equal(Mf.Nv12, rig.Transform.LastInputType[MfCom.Subtype]);
            Assert.Equal(4, rig.Transform.Calls.Count(c => c.StartsWith("Set", StringComparison.Ordinal)));
        }

        // Nothing to judge after: each type's count was read at once.
    }

    [Fact]
    public void SetOutputType_AgainstATransformThatKeepsWhatItAccepts_HoldsUntilShutdown_AndARefusedCallHoldsNothing()
    {
        var rig = new Rig(keeps: true);
        Assert.Equal(0, rig.Port.SetOutputType(Output));
        // Held by the transform as well as the test: more than one, which is the transform's, not a leak.
        Assert.Equal(2, rig.Objects.Types[0].Refs);
        Assert.Equal(0, rig.Port.SetInputType(Input));
        Assert.Equal(2, rig.Objects.Types[1].Refs);
        rig.Transform.SetOutputTypeHr = Mf.InvalidMediaType;
        Assert.Equal(Mf.InvalidMediaType, rig.Port.SetOutputType(Output));
        Assert.Equal(1, rig.Objects.Types[2].Refs);
        Assert.Equal(2, rig.Objects.Types[0].Refs);

        rig.Dispose();
        Assert.True(rig.Transform.IsShutDown);
        Assert.All(rig.Objects.Types, t => Assert.Equal(1, t.Refs));
        rig.Objects.DropTestReferences();
        Assert.All(rig.Objects.Types, t => Balanced(t, 0));
        rig.AssertAllGivenBack();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessInput_MakesASampleOfTheBuffer_AndGivesBothBack(bool keeps)
    {
        var rig = new Rig(keeps);
        Assert.Equal(0, rig.Port.CreateInputBuffer(4096, 64, out var input));
        var buffer = rig.Objects.Buffers[0];
        Assert.Equal((4096, 64), (buffer.MaxLength, buffer.Alignment));
        Assert.Equal(0, buffer.Address % 64);
        Assert.Equal(0, input!.Write([1, 2, 3, 4, 5]));
        Assert.Equal([1, 2, 3, 4, 5], buffer.Bytes);
        Assert.Equal(0, rig.Port.ProcessInput(input, 3_333_333, 333_333, discontinuity: false));
        Assert.Contains("ProcessInput 0 3333333 333333 0 1", rig.Transform.Calls);
        var sample = rig.Objects.Samples[0];
        // The test's reference, and the transform's while it holds the sample.
        Assert.Equal(keeps ? 2 : 1, sample.Refs);
        rig.Transform.ProcessInputHr = Mf.NotAccepting;
        Assert.Equal(Mf.NotAccepting, rig.Port.ProcessInput(input, 6_666_666, 333_333, discontinuity: false));
        Assert.Equal(1, rig.Objects.Samples[1].Refs);
        input.Dispose();
        // The buffer: the test's reference and one per sample the test still holds.
        Assert.Equal(1 + 2, buffer.Refs);

        rig.Dispose();
        Assert.All(rig.Objects.Samples, s => Assert.Equal(1, s.Refs));
        rig.Objects.DropTestReferences();
        Assert.All(rig.Objects.Samples.Cast<SyntheticObject>().Concat(rig.Objects.Buffers), o => Balanced(o, 0));
        rig.AssertAllGivenBack();
    }

    [Fact]
    public void ProcessInput_MarksTheSampleADiscontinuity_OnlyWhenAsked_AndARefusedMarkFailsTheCall()
    {
        var rig = new Rig(keeps: false);
        Assert.Equal(0, rig.Port.CreateInputBuffer(4096, 1, out var input));
        Assert.Equal(0, input!.Write([1, 2, 3]));
        Assert.Equal(0, rig.Port.ProcessInput(input, 0, 333_333, discontinuity: false));
        Assert.Equal(0, rig.Port.ProcessInput(input, 333_333, 333_333, discontinuity: true));
        var (plain, marked) = (rig.Objects.Samples[0], rig.Objects.Samples[1]);
        Assert.False(plain.Values.ContainsKey(MfCom.Discontinuity));
        Assert.DoesNotContain(plain.Log, l => l.StartsWith("SetUINT32", StringComparison.Ordinal));
        Assert.Equal(1u, marked.Values[MfCom.Discontinuity]);
        Assert.Equal([$"SetUINT32 {MfCom.Discontinuity} 1"], marked.Log.Where(l => l.StartsWith("Set", StringComparison.Ordinal)));
        Assert.Equal(["ProcessInput 0 0 333333 0 1", "ProcessInput 0 333333 333333 0 1"], rig.Transform.Calls.Where(c => c.StartsWith("ProcessInput", StringComparison.Ordinal)));

        // The mark refused: the call fails with its HRESULT, and the sample never reaches the transform.
        rig.Objects.SampleSetHr = Mf.EInvalidArg;
        Assert.Equal(Mf.EInvalidArg, rig.Port.ProcessInput(input, 666_666, 333_333, discontinuity: true));
        Assert.Equal(2, rig.Transform.Calls.Count(c => c.StartsWith("ProcessInput", StringComparison.Ordinal)));
        input.Dispose();

        rig.Dispose();
        rig.Objects.DropTestReferences();
        Assert.All(rig.Objects.Samples.Cast<SyntheticObject>().Concat(rig.Objects.Buffers), o => Balanced(o, 0));
        rig.AssertAllGivenBack();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessOutput_IntoTheEncodersSampleOrOurs_CopiesItOut_AndGivesEverythingBack(bool encoderProvides)
    {
        var rig = new Rig(keeps: false);
        rig.Transform.ProvidesSamples = encoderProvides;
        rig.Transform.EventsOnOutput = true;
        rig.Transform.OutputBytes = [0, 0, 0, 1, 0x65, 0x88, 0x84];
        rig.Transform.OutputTime = 666_666;
        var allocation = encoderProvides ? new MfOutputAllocation(true, 0, 0) : new MfOutputAllocation(false, 1 << 20, 16);
        Assert.Equal(0, rig.Port.ProcessOutput(allocation, out var output));
        Assert.Equal(rig.Transform.OutputBytes, output!.Data);
        Assert.Equal(666_666, output.SampleTime);
        Assert.True(output.CleanPoint);
        if (encoderProvides)
        {
            Assert.Null(rig.Transform.CallersSample);
            Assert.Empty(rig.Objects.Samples);
        }
        else
        {
            // Ours, of the allocation's size and alignment, in the call and given back after it.
            Assert.Same(rig.Objects.Samples[0], rig.Transform.CallersSample);
            Assert.Equal((1 << 20, 16), (rig.Objects.Buffers[0].MaxLength, rig.Objects.Buffers[0].Alignment));
            Assert.Equal(1, rig.Objects.Samples[0].Refs);
            Assert.Equal(2, rig.Objects.Buffers[0].Refs);
            Assert.Equal(0, rig.Objects.Buffers[0].Locks);
        }

        rig.Dispose();
        rig.Objects.DropTestReferences();
        Assert.All(rig.Objects.Samples.Cast<SyntheticObject>().Concat(rig.Objects.Buffers), o => Balanced(o, 0));
        rig.AssertAllGivenBack();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailingCall_ThatStillSetsItsOutPointer_HasThatPointerReleased(bool encoderProvides)
    {
        var rig = new Rig(keeps: false);
        rig.Transform.ProvidesSamples = encoderProvides;
        rig.Transform.EventsOnOutput = true;
        rig.Transform.ProcessOutputHr = Odd;
        Assert.Equal(Odd, rig.Port.ProcessOutput(encoderProvides ? new(true, 0, 0) : new(false, 4096, 1), out var output));
        Assert.Null(output);

        rig.Transform.Events.Enqueue((Mf.EventNeedInput, 0, Odd, true));
        Assert.Equal(Odd, rig.Port.NextEvent(out _));
        rig.Transform.Events.Enqueue((Mf.EventHaveOutput, 0, 0, false));
        Assert.Equal(0, rig.Port.NextEvent(out var e));
        Assert.Equal(new MfEvent(Mf.EventHaveOutput, 0), e);

        rig.Transform.GetTypeHr = Odd;
        rig.Transform.PointerOnFailure = true;
        Assert.Equal(Odd, rig.Port.GetOutputCurrentType(out var type));
        Assert.Null(type);
        Assert.Equal(Odd, rig.Port.GetOutputAvailableType(0, out _));
        Assert.Equal(Odd, rig.Port.GetInputCurrentLayout(out _));

        // At once, before the teardown: whatever a failing call handed back was released by the call.
        Assert.NotEmpty(rig.Transform.Made);
        Assert.All(rig.Transform.Made, m => Balanced(m, 0));
        rig.Dispose();
        rig.Objects.DropTestReferences();
        rig.AssertAllGivenBack();
    }

    [Fact]
    public void TheTypesAndLayout_AreReadBack_AndTheCodecApiTakesItsVariantTypes()
    {
        var rig = new Rig(keeps: false);
        rig.Transform.CurrentOutput[MfCom.Subtype] = Mf.H264;
        rig.Transform.CurrentOutput[MfCom.FrameSize] = (322UL << 32) | 194;
        rig.Transform.CurrentOutput[MfCom.Mpeg2Profile] = 66u;
        rig.Transform.CurrentInput[MfCom.FrameSize] = (322UL << 32) | 194;
        rig.Transform.CurrentInput[MfCom.DefaultStride] = unchecked((uint)-384);
        rig.Transform.CurrentInput[MfCom.SampleSize] = 111_744u;
        rig.Transform.InputInfo = new MftInputStreamInfo { Flags = 0xB, Size = 6_144_000, Alignment = 64 };
        rig.Transform.OutputInfo = new MftOutputStreamInfo { Flags = 0x103, Size = 0, Alignment = 0 };
        rig.Transform.Attributes.Values[Mf.TransformAsync] = 1u;
        rig.Transform.Attributes.Values[Mf.DriverVersion] = 0x0020_0000_5226_07D1UL;

        Assert.Equal(0, rig.Port.GetOutputCurrentType(out var current));
        Assert.Equal(new MfVideoType(Mf.H264, 322, 194) { Profile = 66 }, current);
        Assert.Equal(0, rig.Port.GetInputCurrentLayout(out var layout));
        Assert.Equal(new MfInputLayout(322, 194, -384, 111_744), layout);
        Assert.Equal(0, rig.Port.GetInputStreamInfo(out var input));
        Assert.Equal(new MfStreamInfo(0xB, 6_144_000, 64), input);
        Assert.Equal(0, rig.Port.GetOutputStreamInfo(out var output));
        Assert.Equal(new MfStreamInfo(0x103, 0, 0), output);
        Assert.Equal(0, rig.Port.GetUInt32(Mf.TransformAsync, out var isAsync));
        Assert.Equal(1u, isAsync);
        Assert.Equal(0, rig.Port.GetUInt64(Mf.DriverVersion, out var version));
        Assert.Equal(0x0020_0000_5226_07D1UL, version);
        Assert.Equal(0, rig.Port.SetUInt32(Mf.TransformAsyncUnlock, 1));
        Assert.Equal(1u, rig.Transform.Attributes.Values[Mf.TransformAsyncUnlock]);

        Assert.Equal(0, rig.Port.SetCodecValue(Mf.CodecLowLatencyMode, 1));
        Assert.Equal(0, rig.Port.SetCodecValue(Mf.CodecForceKeyFrame, 1));
        Assert.Equal((Variant.VtBool, (short)-1), (rig.Transform.CodecValues[Mf.CodecLowLatencyMode].Vt, rig.Transform.CodecValues[Mf.CodecLowLatencyMode].Bool));
        Assert.Equal((Variant.VtUI4, 1u), (rig.Transform.CodecValues[Mf.CodecForceKeyFrame].Vt, rig.Transform.CodecValues[Mf.CodecForceKeyFrame].UI4));
        Assert.Equal(0, rig.Port.GetCodecValue(Mf.CodecLowLatencyMode, out var lowLatency));
        Assert.Equal(1u, lowLatency);
        Assert.Equal(SyntheticObject.ENotImpl, rig.Port.GetCodecValue(Mf.CodecBFrames, out _));
        // Every read's VARIANT cleared, a scalar's and a failed read's included.
        Assert.Equal([Variant.VtBool, Variant.VtEmpty], rig.Objects.Cleared);
        Assert.Equal(0, rig.Port.ProcessMessage(Mf.MessageNotifyBeginStreaming));
        Assert.Contains("ProcessMessage 0x10000000 0", rig.Transform.Calls);

        rig.Dispose();
        Assert.Equal(1, rig.Activate.Shutdowns);
        rig.AssertAllGivenBack();
    }

    [Fact]
    public void TheDriverVersion_ReadAsAUInt64_OrAsAString_AndTheStringIsFreed()
    {
        var rig = new Rig(keeps: false);
        rig.Transform.Attributes.Values[Mf.DriverVersion] = 0x0020_0000_5226_07D1UL;
        Assert.Equal(0, rig.Port.GetUInt64(Mf.DriverVersion, out var number));
        Assert.Equal(0x0020_0000_5226_07D1UL, number);
        Assert.Equal(SyntheticAttributes.AttributeNotFound, rig.Port.GetString(Mf.DriverVersion, out var none));
        Assert.Null(none);

        // The type mfapi.h declares: a string, which the read frees.
        rig.Transform.Attributes.Values[Mf.DriverVersion] = "32.0.21030.2001";
        Assert.NotEqual(0, rig.Port.GetUInt64(Mf.DriverVersion, out _));
        Assert.Equal(0, rig.Port.GetString(Mf.DriverVersion, out var text));
        Assert.Equal("32.0.21030.2001", text);
        Assert.Equal(rig.Transform.Attributes.StringsGiven, rig.Objects.Freed);
        rig.Dispose();
        rig.AssertAllGivenBack();
    }

    [Fact]
    public void AnEncodersSampleThatIsntASample_IsReleasedOnce_WhenItsWrappingFails()
    {
        var rig = new Rig(keeps: false);
        rig.Transform.HandsBackNotASample = true;
        rig.Transform.EventsOnOutput = true;
        Assert.ThrowsAny<Exception>(() => rig.Port.ProcessOutput(new MfOutputAllocation(true, 0, 0), out _));
        Assert.NotEmpty(rig.Transform.Made);
        Assert.All(rig.Transform.Made, m => Balanced(m, 0));
        rig.Dispose();
        rig.AssertAllGivenBack();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EventsWithNoSample_AreNoOutput_NotAFailure_AndTheNextSampleIsRead(bool encoderProvides, bool flagged)
    {
        var rig = new Rig(keeps: false);
        rig.Transform.ProvidesSamples = encoderProvides;
        rig.Transform.EventsOnOutput = true;
        rig.Transform.NoSample = true;
        rig.Transform.FlagNoSample = flagged;
        var allocation = encoderProvides ? new MfOutputAllocation(true, 0, 0) : new MfOutputAllocation(false, 4096, 16);
        Assert.Equal(Mf.SFalse, rig.Port.ProcessOutput(allocation, out var output));
        Assert.Null(output);

        rig.Transform.NoSample = false;
        rig.Transform.FlagNoSample = false;
        Assert.Equal(0, rig.Port.ProcessOutput(allocation, out output));
        Assert.Equal(rig.Transform.OutputBytes, output!.Data);

        rig.Dispose();
        rig.Objects.DropTestReferences();
        Assert.All(rig.Objects.Samples.Cast<SyntheticObject>().Concat(rig.Objects.Buffers), o => Balanced(o, 0));
        rig.AssertAllGivenBack();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOwningValueFromGetValue_IsCleared_WhetherTheReadFailsOrIsOfATypeNotRead(bool fails)
    {
        var rig = new Rig(keeps: false);
        var owned = new SyntheticAttributes();
        rig.Transform.OwningValue = owned;
        rig.Transform.GetValueHr = fails ? Odd : 0;
        Assert.Equal(fails ? Odd : MfCom.DispETypeMismatch, rig.Port.GetCodecValue(Mf.CodecBFrames, out var value));
        Assert.Equal(0u, value);
        Assert.Equal([SyntheticTransform.VtUnknown], rig.Objects.Cleared);
        // Its creator's reference alone: the VARIANT's was released by the clear.
        Balanced(owned, 1);
        rig.Dispose();
        rig.AssertAllGivenBack();
    }

    // ---- the real catalog adapter ----

    private static readonly Guid ClsidA = new("aaaaaaaa-0000-0000-0000-000000000001");

    private static SyntheticActivate Entry(int i, string? vendor)
    {
        var a = new SyntheticActivate();
        a.Values[Mf.HardwareUrl] = $"url-{i}";
        a.Values[Mf.FriendlyName] = $"Encoder {i}";
        a.Values[Mf.TransformClsid] = ClsidA;
        a.Values[Mf.DeviceInstance] = $"PCI\\DEV_{i}";
        if (vendor != null)
            a.Values[Mf.HardwareVendorId] = vendor;
        return a;
    }

    public static TheoryData<string[]> VendorOrders()
    {
        string[] vendors = ["VEN_1002", "VEN_8086", "VEN_10DE", "VEN_1414", "", "VEN_ABCD"];
        var data = new TheoryData<string[]>();
        // Each rotation and its reverse: every vendor at every place, AMD's first, last and in between.
        for (var r = 0; r < vendors.Length; r++)
        {
            string[] rotated = [.. vendors.Skip(r), .. vendors.Take(r)];
            data.Add(rotated);
            data.Add([.. rotated.Reverse()]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VendorOrders))]
    public void TheAdapter_KeepsEveryEntry_InTheGivenOrder_WithItsAttributes(string[] vendors)
    {
        var live = MfWrappers.Live;
        var activates = vendors.Select((v, i) => Entry(i, v.Length == 0 ? null : v)).ToArray();
        var wrapped = activates.Select(a => Wrap<IMFActivate>(a, MfHeaders.IMFActivate)).ToList();
        var entries = MfCatalogAdapter.Entries(wrapped, new SyntheticObjects());

        Assert.Equal(vendors.Length, entries.Count);
        for (var i = 0; i < vendors.Length; i++)
        {
            Assert.Equal($"url-{i}", entries[i].GetString(Mf.HardwareUrl));
            Assert.Equal($"Encoder {i}", entries[i].GetString(Mf.FriendlyName));
            Assert.Equal(vendors[i].Length == 0 ? null : vendors[i], entries[i].GetString(Mf.HardwareVendorId));
            Assert.Equal(ClsidA, entries[i].GetGuid(Mf.TransformClsid));
            Assert.Equal($"PCI\\DEV_{i}", entries[i].GetString(Mf.DeviceInstance));
            Assert.Null(entries[i].GetUInt64(Mf.AdapterLuid));
        }

        foreach (var entry in entries)
            entry.Dispose();
        Assert.All(activates, a => Balanced(a, 1));
        Assert.Equal(live, MfWrappers.Live);
    }

    [Fact]
    public void AnEntrysPort_KeepsItsOwnReference_AndAStringReadThatFailsIsStillFreed()
    {
        var live = MfWrappers.Live;
        var transform = new SyntheticTransform();
        var activate = Entry(0, "VEN_10DE");
        activate.FailReads[Mf.FriendlyName] = Odd;
        var objects = new SyntheticObjects();
        var entry = MfCatalogAdapter.Entries([Wrap<IMFActivate>(activate, MfHeaders.IMFActivate)], objects)[0];
        Assert.Null(entry.GetString(Mf.FriendlyName));
        Assert.Equal("url-0", entry.GetString(Mf.HardwareUrl));
        // Both strings handed out, the failed read's and the good one's, freed, and nothing else.
        Assert.Equal(2, activate.StringsGiven.Count);
        Assert.Equal(activate.StringsGiven, objects.Freed);
        var port = entry.CreatePort();
        entry.Dispose();
        // The port's activation object is its own: activating after the entry is gone works (here the activate has no
        // transform, so it fails with the activate's HRESULT, which comes back unchanged).
        Assert.Equal(unchecked((int)0x80004005), port.Activate());
        Assert.Equal(1, activate.Activations);
        port.Release();
        Balanced(activate, 1);
        Balanced(transform, 1);
        Assert.Equal(live, MfWrappers.Live);
    }

    // ---- the real catalog's take-over of MFTEnumEx's answer ----

    /// <summary>An MFTEnumEx answer: a CoTaskMem array of activation pointers (0 a hole), each with a reference the array holds.</summary>
    private static nint AnswerOf(params SyntheticActivate?[] activates)
    {
        var array = Marshal.AllocCoTaskMem(activates.Length * IntPtr.Size);
        for (var i = 0; i < activates.Length; i++)
            ((nint*)array)[i] = activates[i]?.Give(MfHeaders.IMFActivate) ?? 0;
        return array;
    }

    [Fact]
    public void TheCatalogsTakeOver_MakesAnEntryOfEachPointer_AndFreesTheArray()
    {
        var live = MfWrappers.Live;
        var activates = Enumerable.Range(0, 3).Select(i => Entry(i, "VEN_10DE")).ToArray();
        var objects = new SyntheticObjects();
        var array = AnswerOf(activates[0], null, activates[1], activates[2]);
        var entries = MfComCatalog.TakeOver(array, 4, objects);
        Assert.Equal(["url-0", "url-1", "url-2"], entries.Select(e => e.GetString(Mf.HardwareUrl)));
        Assert.Contains(array, objects.Freed);
        foreach (var entry in entries)
            entry.Dispose();
        Assert.All(activates, a => Balanced(a, 1));
        Assert.Equal(live, MfWrappers.Live);
    }

    [Fact]
    public void TheCatalogsTakeOver_AWrapThatThrowsMidWalk_ReleasesEveryPointerNotTaken_FreesTheWrappersAndTheArray()
    {
        var live = MfWrappers.Live;
        var activates = Enumerable.Range(0, 5).Select(i => Entry(i, "VEN_8086")).ToArray();
        var objects = new SyntheticObjects();
        var array = AnswerOf(activates);
        var wraps = 0;
        Assert.Throws<OutOfMemoryException>(() => MfComCatalog.TakeOver(array, 5, objects, p =>
        {
            if (++wraps == 3)
            {
                // As MfWrappers.Wrap does: the reference is consumed even when wrapping throws.
                Marshal.Release(p);
                throw new OutOfMemoryException();
            }

            return MfWrappers.Wrap<IMFActivate>(p);
        }));
        Assert.Equal(3, wraps);
        Assert.All(activates, a => Balanced(a, 1));
        Assert.Equal([array], objects.Freed);
        Assert.Equal(live, MfWrappers.Live);
    }

    [Fact]
    public void TheCatalogsTakeOver_AnAdapterThatThrows_FreesEveryWrapperAndTheArray()
    {
        var live = MfWrappers.Live;
        var activates = Enumerable.Range(0, 4).Select(i => Entry(i, "VEN_1002")).ToArray();
        var objects = new SyntheticObjects();
        var array = AnswerOf(activates);
        Assert.Throws<OutOfMemoryException>(() => MfComCatalog.TakeOver(array, 4, objects, adapt: (_, _) => throw new OutOfMemoryException()));
        Assert.All(activates, a => Balanced(a, 1));
        Assert.Equal([array], objects.Freed);
        Assert.Equal(live, MfWrappers.Live);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaFoundationAbiCollection
{
    // The port's live-wrapper count is process-wide: the tests that read it run alone.
    public const string Name = "Media Foundation ABI";
}
