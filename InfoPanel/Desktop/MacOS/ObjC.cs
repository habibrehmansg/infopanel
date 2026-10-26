using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace InfoPanel.Desktop.MacOS;

/// <summary>An AppKit <c>NSRect</c>/<c>CGRect</c>: points, y up from the primary display's bottom-left.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NSRect(double x, double y, double width, double height)
{
    public double X = x;
    public double Y = y;
    public double Width = width;
    public double Height = height;

    public override readonly string ToString() => $"{{{X}, {Y}, {Width}×{Height}}}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSSize(double width, double height)
{
    public double Width = width;
    public double Height = height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSPoint(double x, double y)
{
    public double X = x;
    public double Y = y;
}

/// <summary>
/// The Objective-C runtime, AppKit, Core Graphics and libdispatch through plain P/Invoke: <c>objc_msgSend</c> is
/// called through typed function pointers, one per method shape, so every call uses the method's real C prototype
/// (required on arm64, where variadic and fixed arguments are passed differently).
/// <para>
/// On x64, methods that return a struct larger than 16 bytes (<c>NSRect</c>) go through <c>objc_msgSend_stret</c>:
/// the .NET x64 ABI passes the hidden return buffer in the first register exactly as that entry point expects, so
/// the same typed pointer works. Only arm64 has been run; x64 is untested.
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string QuartzCorePath = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";
    private const string CoreGraphicsPath = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    private static readonly IntPtr ObjCHandle = NativeLibrary.Load(LibObjC);
    private static readonly IntPtr AppKitHandle = NativeLibrary.Load(AppKitPath);
    private static readonly IntPtr QuartzCoreHandle = NativeLibrary.Load(QuartzCorePath);
    private static readonly IntPtr LibSystemHandle = NativeLibrary.Load(LibSystem);

    private static readonly IntPtr MsgSend = NativeLibrary.GetExport(ObjCHandle, "objc_msgSend");

    /// <summary><c>objc_msgSend</c> for struct returns over 16 bytes: <c>objc_msgSend_stret</c> on x64 (untested), the plain one on arm64.</summary>
    private static readonly IntPtr MsgSendStret = RuntimeInformation.ProcessArchitecture == Architecture.X64
        ? NativeLibrary.GetExport(ObjCHandle, "objc_msgSend_stret")
        : MsgSend;

    // id/void/scalar shapes of objc_msgSend.
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr> Send = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr> SendId = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr> SendUInt = (delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, void> SendVoid = (delegate* unmanaged<IntPtr, IntPtr, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> SendVoidId = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, byte, void> SendVoidBool = (delegate* unmanaged<IntPtr, IntPtr, byte, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, nint, void> SendVoidInt = (delegate* unmanaged<IntPtr, IntPtr, nint, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, nuint, void> SendVoidUInt = (delegate* unmanaged<IntPtr, IntPtr, nuint, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, double, void> SendVoidDouble = (delegate* unmanaged<IntPtr, IntPtr, double, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, NSSize, void> SendVoidSize = (delegate* unmanaged<IntPtr, IntPtr, NSSize, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, NSRect, byte, void> SendVoidRectBool = (delegate* unmanaged<IntPtr, IntPtr, NSRect, byte, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, void> SendVoidIdBool = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void> SendVoidIdSelIdId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, byte> SendBool = (delegate* unmanaged<IntPtr, IntPtr, byte>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, double> SendDouble = (delegate* unmanaged<IntPtr, IntPtr, double>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, nuint> SendUIntRet = (delegate* unmanaged<IntPtr, IntPtr, nuint>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, nint> SendIntRet = (delegate* unmanaged<IntPtr, IntPtr, nint>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, NSRect, IntPtr> SendIdRect = (delegate* unmanaged<IntPtr, IntPtr, NSRect, IntPtr>)MsgSend;
    public static readonly delegate* unmanaged<IntPtr, IntPtr, NSRect, nuint, nuint, byte, IntPtr> SendInitWindow =
        (delegate* unmanaged<IntPtr, IntPtr, NSRect, nuint, nuint, byte, IntPtr>)MsgSend;

    /// <summary><c>+[NSEvent otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:]</c>.</summary>
    public static readonly delegate* unmanaged<IntPtr, IntPtr, nuint, NSPoint, nuint, double, nint, IntPtr, short, nint, nint, IntPtr> SendOtherEvent =
        (delegate* unmanaged<IntPtr, IntPtr, nuint, NSPoint, nuint, double, nint, IntPtr, short, nint, nint, IntPtr>)MsgSend;

    /// <summary><c>struct objc_super</c>: the receiver and the class whose superclass's method is called.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Super(IntPtr receiver, IntPtr superClass)
    {
        public IntPtr Receiver = receiver;
        public IntPtr SuperClass = superClass;
    }

    /// <summary><c>objc_msgSendSuper</c> for <c>-(void)method:(id)arg</c>.</summary>
    public static readonly delegate* unmanaged<Super*, IntPtr, IntPtr, void> SendSuperVoidId =
        (delegate* unmanaged<Super*, IntPtr, IntPtr, void>)NativeLibrary.GetExport(ObjCHandle, "objc_msgSendSuper");

    /// <summary>A method returning <c>NSRect</c> (<c>-frame</c>).</summary>
    public static readonly delegate* unmanaged<IntPtr, IntPtr, NSRect> SendRect = (delegate* unmanaged<IntPtr, IntPtr, NSRect>)MsgSendStret;

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(byte* name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(byte* name);

    [DllImport(LibObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, byte* name, nuint extraBytes);

    [DllImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    public static extern void RegisterClassPair(IntPtr cls);

    [DllImport(LibObjC, EntryPoint = "class_addMethod")]
    private static extern byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, byte* types);

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPush")]
    public static extern IntPtr AutoreleasePoolPush();

    [DllImport(LibObjC, EntryPoint = "objc_autoreleasePoolPop")]
    public static extern void AutoreleasePoolPop(IntPtr pool);

    [DllImport(LibSystem, EntryPoint = "pthread_main_np")]
    public static extern int pthread_main_np();

    [DllImport(LibSystem, EntryPoint = "dispatch_async_f")]
    public static extern void dispatch_async_f(IntPtr queue, IntPtr context, delegate* unmanaged<IntPtr, void> work);

    /// <summary><c>dispatch_get_main_queue()</c> is the address of <c>_dispatch_main_q</c>.</summary>
    public static readonly IntPtr MainQueue = NativeLibrary.GetExport(LibSystemHandle, "_dispatch_main_q");

    [DllImport(CoreFoundationPath, EntryPoint = "CFRelease")]
    public static extern void CFRelease(IntPtr cf);

    [DllImport(CoreFoundationPath, EntryPoint = "CFDataCreate")]
    public static extern IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

    [DllImport(CoreGraphicsPath, EntryPoint = "CGDataProviderCreateWithCFData")]
    public static extern IntPtr CGDataProviderCreateWithCFData(IntPtr data);

    [DllImport(CoreGraphicsPath, EntryPoint = "CGDataProviderRelease")]
    public static extern void CGDataProviderRelease(IntPtr provider);

    [DllImport(CoreGraphicsPath, EntryPoint = "CGColorSpaceCreateWithName")]
    public static extern IntPtr CGColorSpaceCreateWithName(IntPtr name);

    [DllImport(CoreGraphicsPath, EntryPoint = "CGImageCreate")]
    public static extern IntPtr CGImageCreate(nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow,
        IntPtr space, uint bitmapInfo, IntPtr provider, IntPtr decode, byte shouldInterpolate, int intent);

    [DllImport(CoreGraphicsPath, EntryPoint = "CGImageRelease")]
    public static extern void CGImageRelease(IntPtr image);

    /// <summary><c>kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big</c>: bytes R, G, B, A, premultiplied (<c>SKColorType.Rgba8888</c>).</summary>
    public const uint RgbaPremultiplied = 1 | (4 << 12);

    /// <summary>The value of a global <c>NSString *</c>/<c>CFStringRef</c> constant exported by a framework.</summary>
    public static IntPtr Constant(string framework, string symbol)
    {
        var handle = framework switch
        {
            "AppKit" => AppKitHandle,
            "QuartzCore" => QuartzCoreHandle,
            "CoreGraphics" => NativeLibrary.Load(CoreGraphicsPath),
            _ => throw new ArgumentException($"Unknown framework {framework}", nameof(framework)),
        };
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, symbol));
    }

    public static IntPtr Class(string name)
    {
        var bytes = Utf8(name);
        fixed (byte* p = bytes)
        {
            var cls = objc_getClass(p);
            return cls != IntPtr.Zero ? cls : throw new InvalidOperationException($"Objective-C class {name} not found.");
        }
    }

    public static IntPtr Sel(string name)
    {
        var bytes = Utf8(name);
        fixed (byte* p = bytes)
            return sel_registerName(p);
    }

    /// <summary>Registers a subclass of <paramref name="superclass"/> with the given methods; returns the class.</summary>
    public static IntPtr DefineClass(string name, string superclass, params (string Selector, IntPtr Imp, string Types)[] methods)
    {
        var nameBytes = Utf8(name);
        IntPtr cls;
        fixed (byte* p = nameBytes)
            cls = objc_allocateClassPair(Class(superclass), p, 0);
        if (cls == IntPtr.Zero)
            return Class(name); // already defined in this process

        foreach (var (selector, imp, types) in methods)
        {
            var typeBytes = Utf8(types);
            fixed (byte* t = typeBytes)
                class_addMethod(cls, Sel(selector), imp, t);
        }

        RegisterClassPair(cls);
        return cls;
    }

    /// <summary>A UTF-8 C string from an <c>NSString</c> (null when <paramref name="nsString"/> is nil).</summary>
    public static string? ToManagedString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
            return null;
        var utf8 = Send(nsString, Sel("UTF8String"));
        return Marshal.PtrToStringUTF8(utf8);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");
}
