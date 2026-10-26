using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// A process's windows as the window server lists them (<c>CGWindowListCopyWindowInfo</c>): bounds in global
/// top-left points, layer (0 normal, 3 floating) and whether they are on screen. Reading bounds needs no Screen
/// Recording permission.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacWindowList
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint OptionAll = 0;
    private const int NumberIntType = 9; // kCFNumberIntType

    public sealed record Window(PointRect Bounds, int Layer, bool OnScreen);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphics)]
    private static extern byte CGRectMakeWithDictionaryRepresentation(IntPtr dict, out Rect rect);

    [DllImport(CoreGraphics)]
    private static extern uint CGMainDisplayID();

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetWidth(IntPtr mode);

    [DllImport(CoreGraphics)]
    private static extern void CGDisplayModeRelease(IntPtr mode);

    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(IntPtr array);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);

    [DllImport(CoreFoundation)]
    private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [DllImport(CoreFoundation)]
    private static extern byte CFNumberGetValue(IntPtr number, int type, out int value);

    [DllImport(CoreFoundation)]
    private static extern byte CFBooleanGetValue(IntPtr boolean);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public double X, Y, Width, Height;
    }

    private static readonly IntPtr Graphics = NativeLibrary.Load(CoreGraphics);

    private static IntPtr Key(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Graphics, name));

    /// <summary>Backing pixels per point of the main display.</summary>
    public static double MainDisplayScale()
    {
        var mode = CGDisplayCopyDisplayMode(CGMainDisplayID());
        try
        {
            return (double)CGDisplayModeGetPixelWidth(mode) / CGDisplayModeGetWidth(mode);
        }
        finally
        {
            CGDisplayModeRelease(mode);
        }
    }

    public static List<Window> Of(int pid)
    {
        var result = new List<Window>();
        var list = CGWindowListCopyWindowInfo(OptionAll, 0);
        if (list == IntPtr.Zero)
            return result;

        try
        {
            var ownerPid = Key("kCGWindowOwnerPID");
            var bounds = Key("kCGWindowBounds");
            var layer = Key("kCGWindowLayer");
            var onScreen = Key("kCGWindowIsOnscreen");
            for (nint i = 0; i < CFArrayGetCount(list); i++)
            {
                var window = CFArrayGetValueAtIndex(list, i);
                if (CFNumberGetValue(CFDictionaryGetValue(window, ownerPid), NumberIntType, out var owner) == 0 || owner != pid)
                    continue;

                CGRectMakeWithDictionaryRepresentation(CFDictionaryGetValue(window, bounds), out var rect);
                CFNumberGetValue(CFDictionaryGetValue(window, layer), NumberIntType, out var level);
                var visible = CFDictionaryGetValue(window, onScreen) is var flag && flag != IntPtr.Zero && CFBooleanGetValue(flag) != 0;
                result.Add(new Window(new PointRect(rect.X, rect.Y, rect.Width, rect.Height), level, visible));
            }
        }
        finally
        {
            CFRelease(list);
        }

        return result;
    }
}
