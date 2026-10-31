using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;
using SkiaSharp;

namespace InfoPanel.Desktop.Windows;

/// <summary>
/// A desktop window on Windows (UI thread only; created on the per-monitor DPI aware v2 desktop thread, so its
/// coordinates are physical pixels and its scale is always 1).
/// <list type="bullet">
/// <item>Style: <c>WS_POPUP</c> only (no caption, frame or system menu, so no Snap or maximize), extended
/// <c>WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c> (plus <c>WS_EX_TOPMOST</c> when on top), no owner: out of the
/// taskbar and Alt+Tab, never activated (<c>MA_NOACTIVATE</c>); a click still reaches it.</item>
/// <item>Place and size exactly as set, off-screen parts included; 1 × 1 up to 16,384 × 16,384
/// (<c>WM_GETMINMAXINFO</c>). <c>WM_DPICHANGED</c> is answered without applying the suggested rectangle, so the window
/// keeps its pixel size on a display of another scaling. <c>WM_CLOSE</c> is ignored: a window closes when its profile's
/// window is turned off.</item>
/// <item>Frames: a per-pixel-alpha layered window updated by <c>UpdateLayeredWindow</c> from a top-down 32-bit BGRA
/// premultiplied DIB section the window's size, which <see cref="DesktopFrameBlit"/> draws into over the floor (alpha
/// 1/255, so every pixel takes clicks, as on macOS). A resize carries the shown pixels over, top-left, the rest at the
/// floor. A DIB that can't be allocated is logged once and the window stays as it was. A failed update (a disconnected
/// session) is logged once and tried again with the same pixels every half second until one succeeds, since a static
/// panel sends no new frame; a retry message still waiting once one has succeeded is ignored.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinWindow : IDesktopWindow
{
    internal const string ClassName = "InfoPanelDesktopWindow";

    /// <summary>The window style: a popup, nothing else.</summary>
    internal const uint WindowStyle = Win32.WS_POPUP;

    /// <summary>The extended style without <c>WS_EX_TOPMOST</c>.</summary>
    internal const uint ExStyle = Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;

    /// <summary>Whether the floor keeps transparent pixels taking clicks (as on macOS); false would let them through.</summary>
    internal const bool TransparentPixelsTakeClicks = true;

    private static readonly Serilog.ILogger Logger = Log.ForContext<WinWindow>();

    /// <summary>The timer that tries a failed update again, and how often.</summary>
    private const nuint RetryTimer = 1;
    private const uint RetryMilliseconds = 500;

    /// <summary>Each window by its handle, for the messages that need it (the retry timer).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, WinWindow> ByHandle = new();

    private static bool _classRegistered;

    private nint _hwnd;
    private int _width;
    private int _height;
    private bool _visible;

    // The DIB section the window shows: selected into _dc, _dibWidth × _dibHeight, possibly larger than the window.
    private nint _dc;
    private nint _bitmap;
    private nint _originalBitmap;
    private byte* _bits;
    private int _dibWidth;
    private int _dibHeight;

    // The window size the DIB's pixels were last made whole for (a frame drawn, or carried over and the rest cleared):
    // past it, inside the DIB, are pixels of an earlier, larger size.
    private int _shownWidth;
    private int _shownHeight;
    private bool _hasContent;
    private bool _allocationFailed;
    private bool _updateFailing;
    private bool _retrying;

    public WinWindow(DesktopWindowStyle style, PointRect frame)
    {
        RegisterClass();
        var (x, y, width, height) = Pixels(frame);
        var instance = Win32.GetModuleHandleW(null);
        fixed (char* className = ClassName)
        fixed (char* title = "InfoPanel")
        {
            _hwnd = Win32.CreateWindowExW(ExStyle | (style.Topmost ? Win32.WS_EX_TOPMOST : 0), className, title, WindowStyle,
                x, y, width, height, 0, 0, instance, 0);
        }

        if (_hwnd == 0)
            throw new InvalidOperationException($"A desktop window couldn't be created (error {Marshal.GetLastPInvokeError()}).");

        (_width, _height) = (width, height);
        Style = style;
        ByHandle[_hwnd] = this;
    }

    /// <summary>The window's handle (for tests).</summary>
    internal nint Handle => _hwnd;

    /// <summary>For tests: replaces <c>UpdateLayeredWindow</c>, returning whether the update succeeded.</summary>
    internal Func<bool>? UpdateLayered { get; set; }

    /// <summary>For tests: the window's pixel at (<paramref name="x"/>, <paramref name="y"/>), BGRA as a little-endian uint.</summary>
    internal uint PixelAt(int x, int y) => *(uint*)(_bits + (long)y * _dibWidth * 4 + (long)x * 4);

    public PointRect Frame
    {
        get
        {
            if (_hwnd == 0)
                return default;
            RECT r;
            if (Win32.GetWindowRect(_hwnd, &r) == 0)
                return default;
            return new PointRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>Always 1: the desktop's coordinates are pixels on Windows.</summary>
    public double BackingScale => 1;

    /// <summary>Never set by this window: dragging comes with drag and resize.</summary>
    public bool UserMoving => false;

    public bool Visible => _visible;

    public DesktopWindowStyle Style { get; private set; }

    public event Action? UserMoved
    {
        add { }
        remove { }
    }

    public event Action? UserResized
    {
        add { }
        remove { }
    }

    /// <summary>Never raised: the scale is always 1.</summary>
    public event Action? BackingScaleChanged
    {
        add { }
        remove { }
    }

    public void SetFrame(PointRect frame)
    {
        if (_hwnd == 0)
            return;

        var (x, y, width, height) = Pixels(frame);
        if (Win32.SetWindowPos(_hwnd, 0, x, y, width, height, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER) == 0)
            Logger.Warning("A desktop window couldn't be placed at {Frame} ({Error})", frame, Marshal.GetLastPInvokeError());

        if (width == _width && height == _height)
            return;

        (_width, _height) = (width, height);
        if (_hasContent && EnsureDib(carry: true))
            Present();
    }

    public void SetStyle(DesktopWindowStyle style)
    {
        if (_hwnd == 0 || style == Style)
            return;

        if (style.Topmost != Style.Topmost)
        {
            var after = style.Topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST;
            if (Win32.SetWindowPos(_hwnd, after, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE) == 0)
                Logger.Warning("A desktop window couldn't be put {Where} ({Error})", style.Topmost ? "on top" : "back", Marshal.GetLastPInvokeError());
        }

        Style = style;
    }

    public void SetVisible(bool visible)
    {
        if (_hwnd == 0 || visible == _visible)
            return;

        _visible = visible;
        if (visible)
        {
            // A layered window shows nothing until its first update: the floor until the first frame comes.
            if (!_hasContent && EnsureDib(carry: false))
            {
                _hasContent = true;
                Present();
            }

            Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        }
        else
        {
            Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        }
    }

    public void ShowFrame(DesktopFrame frame)
    {
        if (_hwnd == 0 || !EnsureDib(carry: false))
            return;

        DesktopFrameBlit.Draw(frame, (nint)_bits, _width, _height, _dibWidth * 4, TransparentPixelsTakeClicks);
        _hasContent = true;
        Present();
    }

    public void Dispose()
    {
        if (_hwnd == 0)
            return;

        ByHandle.TryRemove(_hwnd, out _);
        Win32.DestroyWindow(_hwnd); // its retry timer too
        _hwnd = 0;
        _visible = false;
        FreeDib();
    }

    /// <summary>The frame in whole pixels, at least 1 × 1.</summary>
    private static (int X, int Y, int Width, int Height) Pixels(PointRect frame) =>
        ((int)Math.Round(frame.X), (int)Math.Round(frame.Y),
         Math.Clamp((int)Math.Round(frame.Width), 1, InfoPanel.Documents.Workspace.MaxDimension),
         Math.Clamp((int)Math.Round(frame.Height), 1, InfoPanel.Documents.Workspace.MaxDimension));

    /// <summary>
    /// Makes the DIB hold the window's size: a new one when it grows past the DIB or shrinks to under half its pixels.
    /// With <paramref name="carry"/>, the pixels shown so far stay top-left and the rest of the window is the floor;
    /// without it, the caller draws the whole window. False when it can't be allocated (logged once).
    /// </summary>
    private bool EnsureDib(bool carry)
    {
        if (_bits != null && _width <= _dibWidth && _height <= _dibHeight && (long)_width * _height * 2 >= (long)_dibWidth * _dibHeight)
        {
            if (carry)
                ClearOutsideShown();
            (_shownWidth, _shownHeight) = (_width, _height);
            return true;
        }

        if (_dc == 0)
        {
            _dc = Win32.CreateCompatibleDC(0);
            if (_dc == 0)
                return AllocationFailed();
        }

        var header = new BITMAPINFOHEADER
        {
            BiSize = (uint)sizeof(BITMAPINFOHEADER),
            BiWidth = _width,
            BiHeight = -_height, // top-down
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = Win32.BI_RGB,
        };
        void* bits;
        var bitmap = Win32.CreateDIBSection(0, &header, Win32.DIB_RGB_COLORS, &bits, 0, 0);
        if (bitmap == 0 || bits == null)
            return AllocationFailed();

        var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var surface = SKSurface.Create(info, (nint)bits, _width * 4))
        {
            surface.Canvas.Clear(TransparentPixelsTakeClicks ? DesktopFrameBlit.Floor : SKColors.Transparent);
            // Only what was shown: the old DIB past it holds pixels of an earlier, larger size.
            var (shownWidth, shownHeight) = (Math.Min(_shownWidth, _dibWidth), Math.Min(_shownHeight, _dibHeight));
            if (carry && _bits != null && shownWidth > 0 && shownHeight > 0)
            {
                using var old = SKImage.FromPixels(new SKImageInfo(shownWidth, shownHeight, SKColorType.Bgra8888, SKAlphaType.Premul), (nint)_bits, _dibWidth * 4);
                using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
                surface.Canvas.DrawImage(old, 0, 0, paint);
            }

            surface.Canvas.Flush();
        }

        var previous = Win32.SelectObject(_dc, bitmap);
        if (_bitmap == 0)
            _originalBitmap = previous;
        else
            Win32.DeleteObject(_bitmap);
        _bitmap = bitmap;
        _bits = (byte*)bits;
        (_dibWidth, _dibHeight) = (_width, _height);
        (_shownWidth, _shownHeight) = (_width, _height);
        _allocationFailed = false;
        return true;
    }

    /// <summary>Clears the window's pixels outside the size last shown to the floor (the DIB is kept, the window grew within it).</summary>
    private void ClearOutsideShown()
    {
        if (_width <= _shownWidth && _height <= _shownHeight)
            return;

        var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, (nint)_bits, _dibWidth * 4);
        surface.Canvas.ClipRect(new SKRect(0, 0, _shownWidth, _shownHeight), SKClipOperation.Difference);
        surface.Canvas.Clear(TransparentPixelsTakeClicks ? DesktopFrameBlit.Floor : SKColors.Transparent);
        surface.Canvas.Flush();
    }

    private bool AllocationFailed()
    {
        if (!_allocationFailed)
        {
            _allocationFailed = true;
            Logger.Error("A {Width}×{Height} desktop window's pixels couldn't be allocated ({Error}); it isn't updated",
                _width, _height, Marshal.GetLastPInvokeError());
        }

        return false;
    }

    private void FreeDib()
    {
        if (_dc != 0)
        {
            if (_bitmap != 0)
            {
                Win32.SelectObject(_dc, _originalBitmap);
                Win32.DeleteObject(_bitmap);
            }

            Win32.DeleteDC(_dc);
        }

        (_dc, _bitmap, _originalBitmap) = (0, 0, 0);
        _bits = null;
    }

    /// <summary>
    /// Shows the DIB's top-left window-sized part, per-pixel alpha, where the window is. When that fails, the retry timer
    /// calls it again until it succeeds.
    /// </summary>
    private void Present()
    {
        Win32.GdiFlush();
        var size = new SIZE { Cx = _width, Cy = _height };
        var source = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };
        if (UpdateLayered?.Invoke() ?? Win32.UpdateLayeredWindow(_hwnd, 0, null, &size, _dc, &source, 0, &blend, Win32.ULW_ALPHA) != 0)
        {
            if (_retrying)
            {
                Win32.KillTimer(_hwnd, RetryTimer);
                _retrying = false;
            }

            if (_updateFailing)
            {
                _updateFailing = false;
                Logger.Information("A desktop window is updated again");
            }

            return;
        }

        if (!_updateFailing)
        {
            _updateFailing = true;
            Logger.Warning("A desktop window couldn't be updated ({Error}); it shows its last frame until an update succeeds",
                Marshal.GetLastPInvokeError());
        }

        if (!_retrying)
        {
            if (Win32.SetTimer(_hwnd, RetryTimer, RetryMilliseconds, 0) != 0)
                _retrying = true;
            else
                Logger.Warning("A desktop window's update can't be tried again ({Error}); it waits for the next frame", Marshal.GetLastPInvokeError());
        }
    }

    private static void RegisterClass()
    {
        if (_classRegistered)
            return;

        fixed (char* className = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                CbSize = (uint)sizeof(WNDCLASSEXW),
                LpfnWndProc = &WndProc,
                HInstance = Win32.GetModuleHandleW(null),
                LpszClassName = className,
            };
            if (Win32.RegisterClassExW(&wc) == 0 && Marshal.GetLastPInvokeError() != 1410) // ERROR_CLASS_ALREADY_EXISTS
                throw new InvalidOperationException($"The desktop window class couldn't be registered (error {Marshal.GetLastPInvokeError()}).");
        }

        _classRegistered = true;
    }

    /// <summary>
    /// The answer to a message every desktop window gives the same way, or null for the default handling (no state
    /// needed, so it holds from the window's first message, before its handle is known).
    /// </summary>
    internal static nint? Answer(uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_MOUSEACTIVATE:
                return Win32.MA_NOACTIVATE;
            case Win32.WM_CLOSE:
                return 0;
            case Win32.WM_DPICHANGED:
                // Keeps its pixel size: the suggested rectangle is not applied.
                return 0;
            case Win32.WM_GETMINMAXINFO:
                var info = (MINMAXINFO*)lParam;
                info->PtMinTrackSize = new POINT { X = 1, Y = 1 };
                info->PtMaxTrackSize = new POINT { X = InfoPanel.Documents.Workspace.MaxDimension, Y = InfoPanel.Documents.Workspace.MaxDimension };
                return 0;
            default:
                return null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == Win32.WM_TIMER && (nuint)wParam == RetryTimer && ByHandle.TryGetValue(hwnd, out var window))
            {
                // KillTimer leaves a message already waiting: one that comes after the retry ended is ignored.
                if (window._retrying)
                    window.Present();
                return 0;
            }

            if (Answer(msg, wParam, lParam) is { } answer)
                return answer;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A desktop window callback failed");
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
