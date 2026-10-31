using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace InfoPanel.Desktop.Windows;

// The Win32 structures the desktop platform passes to user32, gdi32 and wtsapi32: blittable, laid out as the 64-bit
// Windows headers lay them out (x64 and arm64 share these layouts). Their sizes and the offsets the code reads are pinned
// by Win32LayoutTests, which runs on any 64-bit OS: a wrong field would otherwise shift silently.

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int Cx;
    public int Cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint Hwnd;
    public uint Message;
    public nint WParam;
    public nint LParam;
    public uint Time;
    public POINT Pt;
    public uint LPrivate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint CbSize;
    public uint Style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> LpfnWndProc;
    public int CbClsExtra;
    public int CbWndExtra;
    public nint HInstance;
    public nint HIcon;
    public nint HCursor;
    public nint HbrBackground;
    public char* LpszMenuName;
    public char* LpszClassName;
    public nint HIconSm;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MONITORINFOEXW
{
    public uint CbSize;
    public RECT RcMonitor;
    public RECT RcWork;
    public uint DwFlags;
    public fixed char SzDevice[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT PtReserved;
    public POINT PtMaxSize;
    public POINT PtMaxPosition;
    public POINT PtMinTrackSize;
    public POINT PtMaxTrackSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint BiSize;
    public int BiWidth;
    public int BiHeight;
    public ushort BiPlanes;
    public ushort BiBitCount;
    public uint BiCompression;
    public uint BiSizeImage;
    public int BiXPelsPerMeter;
    public int BiYPelsPerMeter;
    public uint BiClrUsed;
    public uint BiClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct USEROBJECTFLAGS
{
    public int FInherit;
    public int FReserved;
    public uint DwFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID AdapterId;
    public uint Id;
    public uint ModeInfoIdx;
    public uint OutputTechnology;
    public uint Rotation;
    public uint Scaling;
    public DISPLAYCONFIG_RATIONAL RefreshRate;
    public uint ScanLineOrdering;
    public int TargetAvailable;
    public uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO SourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO TargetInfo;
    public uint Flags;
}

/// <summary>A 64-byte union (target, source or desktop image mode) the code only allocates and passes back.</summary>
[StructLayout(LayoutKind.Sequential, Size = 64)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public uint InfoType;
    public uint Id;
    public LUID AdapterId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public uint Type;
    public uint Size;
    public LUID AdapterId;
    public uint Id;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    public fixed char ViewGdiDeviceName[32];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
    public uint Flags;
    public uint OutputTechnology;
    public ushort EdidManufactureId;
    public ushort EdidProductCodeId;
    public uint ConnectorInstance;
    public fixed char MonitorFriendlyDeviceName[64];
    public fixed char MonitorDevicePath[128];
}

/// <summary>
/// The Win32 functions the Windows desktop platform calls: hand-written <c>LibraryImport</c>s with blittable arguments
/// (no marshalling, no dependency), each returning the raw <c>BOOL</c>, handle or code.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Win32
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Wtsapi32 = "wtsapi32.dll";

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_NOACTIVATE = 0x08000000;

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;

    public const uint PM_REMOVE = 0x0001;

    /// <summary><c>PM_QS_INPUT</c>: <c>QS_INPUT</c> (mouse, keys, raw input, touch, pointer) in the high word.</summary>
    public const uint PM_QS_INPUT = 0x1C07u << 16;

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ENDSESSION = 0x0016;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_POWERBROADCAST = 0x0218;
    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_GETDPISCALEDSIZE = 0x02E4;
    public const uint WM_APP = 0x8000;

    public const int MA_NOACTIVATE = 3;

    public const nint PBT_APMRESUMESUSPEND = 0x7;
    public const nint PBT_APMRESUMEAUTOMATIC = 0x12;

    public const nint WTS_CONSOLE_CONNECT = 0x1;
    public const nint WTS_CONSOLE_DISCONNECT = 0x2;
    public const nint WTS_REMOTE_CONNECT = 0x3;
    public const nint WTS_REMOTE_DISCONNECT = 0x4;
    public const uint NOTIFY_FOR_THIS_SESSION = 0;

    public const uint ULW_ALPHA = 0x2;
    public const byte AC_SRC_OVER = 0;
    public const byte AC_SRC_ALPHA = 1;

    public const uint MONITORINFOF_PRIMARY = 1;

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    public const int UOI_FLAGS = 1;
    public const uint WSF_VISIBLE = 1;

    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>.</summary>
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    // user32: windows and messages

    [LibraryImport(User32, SetLastError = true)]
    public static partial ushort RegisterClassExW(WNDCLASSEXW* wndClass);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int DestroyWindow(nint hwnd);

    [LibraryImport(User32)]
    public static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int GetMessageW(MSG* msg, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport(User32)]
    public static partial int PeekMessageW(MSG* msg, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport(User32)]
    public static partial int TranslateMessage(MSG* msg);

    [LibraryImport(User32)]
    public static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport(User32)]
    public static partial void PostQuitMessage(int exitCode);

    [LibraryImport(User32)]
    public static partial int ShowWindow(nint hwnd, int command);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int GetWindowRect(nint hwnd, RECT* rect);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int UpdateLayeredWindow(nint hwnd, nint hdcDst, POINT* pptDst, SIZE* psize, nint hdcSrc, POINT* pptSrc, uint crKey,
        BLENDFUNCTION* blend, uint flags);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nuint SetTimer(nint hwnd, nuint id, uint elapse, nint timerProc);

    [LibraryImport(User32, SetLastError = true)]
    public static partial int KillTimer(nint hwnd, nuint id);

    // user32: displays and DPI

    [LibraryImport(User32)]
    public static partial int EnumDisplayMonitors(nint hdc, RECT* clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint data);

    [LibraryImport(User32)]
    public static partial int GetMonitorInfoW(nint monitor, MONITORINFOEXW* info);

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport(User32)]
    public static partial nint GetWindowDpiAwarenessContext(nint hwnd);

    [LibraryImport(User32)]
    public static partial int AreDpiAwarenessContextsEqual(nint a, nint b);

    [LibraryImport(User32)]
    public static partial int GetDisplayConfigBufferSizes(uint flags, uint* pathCount, uint* modeCount);

    [LibraryImport(User32)]
    public static partial int QueryDisplayConfig(uint flags, uint* pathCount, DISPLAYCONFIG_PATH_INFO* paths, uint* modeCount,
        DISPLAYCONFIG_MODE_INFO* modes, nint currentTopologyId);

    [LibraryImport(User32)]
    public static partial int DisplayConfigGetDeviceInfo(DISPLAYCONFIG_DEVICE_INFO_HEADER* request);

    // user32: the window station

    [LibraryImport(User32, SetLastError = true)]
    public static partial nint GetProcessWindowStation();

    [LibraryImport(User32, SetLastError = true)]
    public static partial int GetUserObjectInformationW(nint obj, int index, void* info, uint length, uint* needed);

    // gdi32

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport(Gdi32)]
    public static partial int DeleteDC(nint hdc);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport(Gdi32)]
    public static partial nint SelectObject(nint hdc, nint obj);

    [LibraryImport(Gdi32)]
    public static partial int DeleteObject(nint obj);

    [LibraryImport(Gdi32)]
    public static partial int GdiFlush();

    // kernel32

    [LibraryImport(Kernel32)]
    public static partial nint GetModuleHandleW(char* name);

    // wtsapi32

    [LibraryImport(Wtsapi32, SetLastError = true)]
    public static partial int WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport(Wtsapi32)]
    public static partial int WTSUnRegisterSessionNotification(nint hwnd);
}
