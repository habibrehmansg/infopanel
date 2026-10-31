using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using InfoPanel.Desktop;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// What the Windows tests read of windows, any process's: the top-level windows of a process with their class, styles
/// and rectangle, the foreground window, the window at a point, the screen's pixels, DPI awareness, and messages sent to
/// a window. Every
/// rectangle is read on a per-monitor DPI aware (v2) thread context (<see cref="InPixels{T}"/>), so it is in physical
/// pixels whatever scaling a display has.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WinTestInterop
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_MOUSEACTIVATE = 0x0021;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_TIMER = 0x0113;
    public const uint PM_NOREMOVE = 0x0000;
    public const uint PM_REMOVE = 0x0001;
    public static readonly IntPtr PerMonitorAwareV2 = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly PointRect ToPointRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
        public uint Private;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int length);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, ref Rect lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref Msg msg);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    /// <summary>A top-level window as the tests see it.</summary>
    public sealed record Window(IntPtr Handle, string Class, uint Style, uint ExStyle, PointRect Bounds, bool Visible);

    /// <summary>Runs <paramref name="read"/> with this thread per-monitor DPI aware (v2), so rectangles are physical pixels.</summary>
    public static T InPixels<T>(Func<T> read)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return read();
        }
        finally
        {
            if (previous != IntPtr.Zero)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>The top-level windows of process <paramref name="pid"/> whose class is <paramref name="className"/>.</summary>
    public static List<Window> WindowsOf(int pid, string className) => InPixels(() =>
    {
        var handles = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid)
                handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        return handles.Select(Read).Where(w => w.Class == className).ToList();
    });

    /// <summary>A window's class, styles, rectangle and visibility.</summary>
    public static Window Read(IntPtr hwnd) => InPixels(() =>
    {
        var name = new StringBuilder(256);
        GetClassNameW(hwnd, name, name.Capacity);
        GetWindowRect(hwnd, out var rect);
        return new Window(hwnd, name.ToString(), (uint)(long)GetWindowLongPtr(hwnd, GWL_STYLE), (uint)(long)GetWindowLongPtr(hwnd, GWL_EXSTYLE),
            rect.ToPointRect(), IsWindowVisible(hwnd));
    });

    /// <summary>The top-level window at the physical pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static IntPtr TopLevelAt(int x, int y) => InPixels(() => GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2 /* GA_ROOT */));

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>
    /// The screen's colour at the physical pixel (<paramref name="x"/>, <paramref name="y"/>), layered windows included
    /// (<c>CAPTUREBLT</c>).
    /// </summary>
    public static (byte R, byte G, byte B) ScreenPixel(int x, int y) => InPixels(() =>
    {
        const uint srcCopy = 0x00CC0020, captureBlt = 0x40000000;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, 1, 1);
        var previous = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, 1, 1, screen, x, y, srcCopy | captureBlt);
            var colour = GetPixel(memory, 0, 0); // 0x00BBGGRR
            return ((byte)colour, (byte)(colour >> 8), (byte)(colour >> 16));
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    });

    /// <summary>Whether the window is per-monitor DPI aware (v2).</summary>
    public static bool IsPerMonitorV2(IntPtr hwnd) => AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd), PerMonitorAwareV2);
}
