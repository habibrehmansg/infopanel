using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace InfoPanel.Preview.Video;

/// <summary>A Core Media time: <c>value / timescale</c> seconds.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CMTime
{
    public long Value;
    public int Timescale;
    public uint Flags;
    public long Epoch;

    public static CMTime Invalid => default;

    public static CMTime Make(long value, int timescale) => new() { Value = value, Timescale = timescale, Flags = 1 };
}

/// <summary>
/// CoreFoundation, CoreMedia, CoreVideo and VideoToolbox through plain P/Invoke, only what the hardware H.264
/// encoder needs. Constants the frameworks export as globals (<c>CFStringRef</c> keys and values) are read from their
/// symbols once.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class VTNative
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreMedia = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string CoreVideo = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    private const string VideoToolbox = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";

    public const uint CodecH264 = 0x61766331; // 'avc1'
    public const uint PixelFormatBgra = 0x42475241; // 'BGRA'
    public const uint PixelFormatRgba = 0x52474241; // 'RGBA'
    // CFNumberType is a CFIndex: pointer-sized.
    public const nint NumberSInt32Type = 3;
    public const nint NumberSInt64Type = 4;
    public const nint NumberDoubleType = 13;
    public const uint StringEncodingUtf8 = 0x08000100;

    // CoreFoundation
    [DllImport(CoreFoundation)] public static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] public static extern IntPtr CFNumberCreate(IntPtr alloc, nint type, void* value);
    [DllImport(CoreFoundation)] public static extern byte CFNumberGetValue(IntPtr number, nint type, void* value);
    [DllImport(CoreFoundation)] public static extern IntPtr CFStringCreateWithCString(IntPtr alloc, byte* cStr, uint encoding);
    [DllImport(CoreFoundation)] public static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(CoreFoundation)] public static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CoreFoundation)] public static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] public static extern byte CFBooleanGetValue(IntPtr boolean);
    [DllImport(CoreFoundation)] public static extern byte CFEqual(IntPtr cf1, IntPtr cf2);
    [DllImport(CoreFoundation)] public static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] public static extern byte* CFDataGetBytePtr(IntPtr data);

    // CoreMedia
    [DllImport(CoreMedia)] public static extern IntPtr CMSampleBufferGetDataBuffer(IntPtr sampleBuffer);
    [DllImport(CoreMedia)] public static extern IntPtr CMSampleBufferGetFormatDescription(IntPtr sampleBuffer);
    [DllImport(CoreMedia)] public static extern IntPtr CMSampleBufferGetSampleAttachmentsArray(IntPtr sampleBuffer, byte createIfNecessary);
    [DllImport(CoreMedia)] public static extern nuint CMBlockBufferGetDataLength(IntPtr blockBuffer);
    [DllImport(CoreMedia)] public static extern int CMBlockBufferCopyDataBytes(IntPtr blockBuffer, nuint offset, nuint length, void* destination);
    [DllImport(CoreMedia)] public static extern IntPtr CMFormatDescriptionGetExtension(IntPtr description, IntPtr key);

    // CoreVideo
    [DllImport(CoreVideo)] public static extern int CVPixelBufferPoolCreatePixelBuffer(IntPtr alloc, IntPtr pool, IntPtr* pixelBuffer);
    [DllImport(CoreVideo)] public static extern int CVPixelBufferLockBaseAddress(IntPtr pixelBuffer, ulong flags);
    [DllImport(CoreVideo)] public static extern int CVPixelBufferUnlockBaseAddress(IntPtr pixelBuffer, ulong flags);
    [DllImport(CoreVideo)] public static extern byte* CVPixelBufferGetBaseAddress(IntPtr pixelBuffer);
    [DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetBytesPerRow(IntPtr pixelBuffer);
    [DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetWidth(IntPtr pixelBuffer);
    [DllImport(CoreVideo)] public static extern nuint CVPixelBufferGetHeight(IntPtr pixelBuffer);

    // VideoToolbox
    [DllImport(VideoToolbox)]
    public static extern int VTCompressionSessionCreate(IntPtr alloc, int width, int height, uint codecType, IntPtr encoderSpecification,
        IntPtr sourceImageBufferAttributes, IntPtr compressedDataAllocator, IntPtr outputCallback, IntPtr outputCallbackRefCon, IntPtr* session);
    [DllImport(VideoToolbox)] public static extern int VTSessionSetProperty(IntPtr session, IntPtr key, IntPtr value);
    [DllImport(VideoToolbox)] public static extern int VTSessionCopyProperty(IntPtr session, IntPtr key, IntPtr alloc, IntPtr* value);
    [DllImport(VideoToolbox)] public static extern int VTCompressionSessionPrepareToEncodeFrames(IntPtr session);
    [DllImport(VideoToolbox)] public static extern IntPtr VTCompressionSessionGetPixelBufferPool(IntPtr session);
    [DllImport(VideoToolbox)]
    public static extern int VTCompressionSessionEncodeFrame(IntPtr session, IntPtr imageBuffer, CMTime presentationTimeStamp, CMTime duration,
        IntPtr frameProperties, IntPtr sourceFrameRefcon, uint* infoFlagsOut);
    [DllImport(VideoToolbox)] public static extern int VTCompressionSessionCompleteFrames(IntPtr session, CMTime completeUntilPresentationTimeStamp);
    [DllImport(VideoToolbox)] public static extern void VTCompressionSessionInvalidate(IntPtr session);

    private static readonly IntPtr CoreFoundationHandle = NativeLibrary.Load(CoreFoundation);
    private static readonly IntPtr CoreMediaHandle = NativeLibrary.Load(CoreMedia);
    private static readonly IntPtr CoreVideoHandle = NativeLibrary.Load(CoreVideo);
    private static readonly IntPtr VideoToolboxHandle = NativeLibrary.Load(VideoToolbox);

    /// <summary>The value of an exported global (a <c>CFStringRef</c> or <c>CFBooleanRef</c>); zero when the OS doesn't have it.</summary>
    private static IntPtr Global(IntPtr library, string name) =>
        NativeLibrary.TryGetExport(library, name, out var address) ? *(IntPtr*)address : IntPtr.Zero;

    public static readonly IntPtr TypeDictionaryKeyCallBacks = NativeLibrary.GetExport(CoreFoundationHandle, "kCFTypeDictionaryKeyCallBacks");
    public static readonly IntPtr TypeDictionaryValueCallBacks = NativeLibrary.GetExport(CoreFoundationHandle, "kCFTypeDictionaryValueCallBacks");
    public static readonly IntPtr True = Global(CoreFoundationHandle, "kCFBooleanTrue");
    public static readonly IntPtr False = Global(CoreFoundationHandle, "kCFBooleanFalse");

    public static readonly IntPtr SampleAttachmentNotSync = Global(CoreMediaHandle, "kCMSampleAttachmentKey_NotSync");
    public static readonly IntPtr ExtensionAtoms = Global(CoreMediaHandle, "kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms");

    public static readonly IntPtr PixelBufferPixelFormatType = Global(CoreVideoHandle, "kCVPixelBufferPixelFormatTypeKey");
    public static readonly IntPtr PixelBufferWidth = Global(CoreVideoHandle, "kCVPixelBufferWidthKey");
    public static readonly IntPtr PixelBufferHeight = Global(CoreVideoHandle, "kCVPixelBufferHeightKey");
    public static readonly IntPtr PixelBufferIOSurfaceProperties = Global(CoreVideoHandle, "kCVPixelBufferIOSurfacePropertiesKey");
    public static readonly IntPtr Primaries709 = Global(CoreVideoHandle, "kCVImageBufferColorPrimaries_ITU_R_709_2");
    public static readonly IntPtr Transfer709 = Global(CoreVideoHandle, "kCVImageBufferTransferFunction_ITU_R_709_2");
    public static readonly IntPtr Matrix709 = Global(CoreVideoHandle, "kCVImageBufferYCbCrMatrix_ITU_R_709_2");

    public static readonly IntPtr RequireHardwareEncoder = Global(VideoToolboxHandle, "kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder");
    public static readonly IntPtr UsingHardwareEncoder = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder");
    public static readonly IntPtr RealTime = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_RealTime");
    public static readonly IntPtr AllowFrameReordering = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_AllowFrameReordering");
    public static readonly IntPtr MaxFrameDelayCount = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_MaxFrameDelayCount");
    public static readonly IntPtr ProfileLevel = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_ProfileLevel");
    public static readonly IntPtr H264BaselineAutoLevel = Global(VideoToolboxHandle, "kVTProfileLevel_H264_Baseline_AutoLevel");
    public static readonly IntPtr AverageBitRate = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_AverageBitRate");
    public static readonly IntPtr ExpectedFrameRate = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_ExpectedFrameRate");
    public static readonly IntPtr MaxKeyFrameInterval = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_MaxKeyFrameInterval");
    public static readonly IntPtr PrioritizeSpeed = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality");
    public static readonly IntPtr ColorPrimaries = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_ColorPrimaries");
    public static readonly IntPtr TransferFunction = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_TransferFunction");
    public static readonly IntPtr YCbCrMatrix = Global(VideoToolboxHandle, "kVTCompressionPropertyKey_YCbCrMatrix");
    public static readonly IntPtr ForceKeyFrame = Global(VideoToolboxHandle, "kVTEncodeFrameOptionKey_ForceKeyFrame");

    /// <summary>A new CFString (the caller releases it).</summary>
    public static IntPtr Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes)
            return CFStringCreateWithCString(IntPtr.Zero, p, StringEncodingUtf8);
    }

    /// <summary>A new CFNumber (the caller releases it).</summary>
    public static IntPtr Int32(int value) => CFNumberCreate(IntPtr.Zero, NumberSInt32Type, &value);

    public static IntPtr Int64(long value) => CFNumberCreate(IntPtr.Zero, NumberSInt64Type, &value);

    public static IntPtr Double(double value) => CFNumberCreate(IntPtr.Zero, NumberDoubleType, &value);

    /// <summary>A session's boolean property as it reads back: null when it can't be read.</summary>
    public static bool? CopyBoolean(IntPtr session, IntPtr key)
    {
        if (session == IntPtr.Zero || key == IntPtr.Zero)
            return null;
        IntPtr value;
        if (VTSessionCopyProperty(session, key, IntPtr.Zero, &value) != 0 || value == IntPtr.Zero)
            return null;
        var result = CFBooleanGetValue(value) != 0;
        CFRelease(value);
        return result;
    }

    /// <summary>Whether a session's property reads back as <paramref name="expected"/> (CFEqual): null when it can't be read.</summary>
    public static bool? CopyEquals(IntPtr session, IntPtr key, IntPtr expected)
    {
        if (session == IntPtr.Zero || key == IntPtr.Zero || expected == IntPtr.Zero)
            return null;
        IntPtr value;
        if (VTSessionCopyProperty(session, key, IntPtr.Zero, &value) != 0 || value == IntPtr.Zero)
            return null;
        var result = CFEqual(value, expected) != 0;
        CFRelease(value);
        return result;
    }

    /// <summary>A new CFDictionary of CF values (retained by it; the caller releases the dictionary).</summary>
    public static IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] entries)
    {
        var keys = stackalloc IntPtr[Math.Max(1, entries.Length)];
        var values = stackalloc IntPtr[Math.Max(1, entries.Length)];
        for (var i = 0; i < entries.Length; i++)
        {
            keys[i] = entries[i].Key;
            values[i] = entries[i].Value;
        }

        return CFDictionaryCreate(IntPtr.Zero, keys, values, entries.Length, TypeDictionaryKeyCallBacks, TypeDictionaryValueCallBacks);
    }
}
