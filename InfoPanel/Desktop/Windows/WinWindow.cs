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
/// taskbar and Alt+Tab, never activated (<c>MA_NOACTIVATE</c>); a click still reaches it. Click-through adds
/// <c>WS_EX_TRANSPARENT</c>, at creation or live (that bit alone, flushed with <c>SWP_FRAMECHANGED</c>, the place and
/// z-order untouched): every click goes to the windows beneath, so there is no hit test, drag or resize while it's on. A
/// live change whose calls fail is logged and not recorded in <see cref="Style"/>; the bit is then uncertain, so the next
/// change or reconcile writes and flushes the bit it asks for, even one equal to <see cref="Style"/>'s, until that
/// succeeds.</item>
/// <item>Place and size exactly as set, off-screen parts included; 1 × 1 up to 16,384 × 16,384
/// (<c>WM_GETMINMAXINFO</c>). <c>WM_DPICHANGED</c> is answered without applying the suggested rectangle, so the window
/// keeps its pixel size on a display of another scaling. <c>WM_CLOSE</c> is ignored: a window closes when its profile's
/// window is turned off.</item>
/// <item>Frames: a per-pixel-alpha layered window updated by <c>UpdateLayeredWindow</c> from a top-down 32-bit BGRA
/// premultiplied DIB section the window's size, which <see cref="DesktopFrameBlit"/> draws into over the floor (alpha
/// 1/255, so every pixel takes clicks unless click-through is on, as on macOS). A resize carries the shown pixels over, top-left, the rest at the
/// floor. A DIB that can't be allocated is logged once and the window stays as it was. A failed update (a disconnected
/// session) is logged once and tried again with the same pixels every half second until one succeeds, since a static
/// panel sends no new frame; a retry message still waiting once one has succeeded is ignored.</item>
/// <item>Drag and resize: <c>WM_NCHITTEST</c> answers an edge or corner within the resize band when resizable, else the
/// caption when movable, else the client (<see cref="WinHitTest"/>), so Windows runs its own move or size loop and shows
/// its own sizing cursors; the class cursor is the arrow. A press on the caption or an edge is <see cref="UserMoving"/>
/// until the loop returns at the release, then <see cref="UserMoved"/> once, moved or not; a press on the client (a
/// window that doesn't move) the same at its release. Each size change in the loop is <see cref="UserResized"/>, and the
/// window's pixels follow it; <see cref="SetFrame"/> reports nothing. A double click on the caption or an edge and the
/// right button there do nothing (no maximize, no system menu).</item>
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

    /// <summary><paramref name="exStyle"/> with <c>WS_EX_TRANSPARENT</c> set (<paramref name="on"/>) or cleared, every other bit kept.</summary>
    internal static uint WithClickThrough(uint exStyle, bool on) =>
        on ? exStyle | Win32.WS_EX_TRANSPARENT : exStyle & ~Win32.WS_EX_TRANSPARENT;

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

    // From a failed click-through change (logged once) until a change is written and flushed: the native bit may be
    // either value.
    private bool _clickThroughUncertain;

    // Drag and resize: SetFrame's own changes are never the user's; the system's move or size loop runs for a press on
    // the caption or an edge; a press on the client (a window that doesn't move) waits for its release.
    private bool _settingFrame;
    private bool _inLoop;
    private bool _pressed;

    // The desktop level: set around the engine's own z-order changes, which the guard lets through.
    private bool _placing;

    /// <summary>The desktop level this window joins at <see cref="WindowLevel.Desktop"/>.</summary>
    private readonly WinDesktopLevel _desktopLevel;

    /// <summary>
    /// A hidden window at <paramref name="frame"/>. <paramref name="desktopLevel"/>: the platform's desktop level, or (in
    /// tests) one of the window's own with no foreground hook.
    /// </summary>
    public WinWindow(DesktopWindowStyle style, PointRect frame, WinDesktopLevel? desktopLevel = null)
    {
        _desktopLevel = desktopLevel ?? new WinDesktopLevel(null);
        RegisterClass();
        var (x, y, width, height) = Pixels(frame);
        var instance = Win32.GetModuleHandleW(null);
        fixed (char* className = ClassName)
        fixed (char* title = "InfoPanel")
        {
            var exStyle = WithClickThrough(ExStyle | (style.Level == WindowLevel.Top ? Win32.WS_EX_TOPMOST : 0), style.ClickThrough);
            _hwnd = Win32.CreateWindowExW(exStyle, className, title, WindowStyle,
                x, y, width, height, 0, 0, instance, 0);
        }

        if (_hwnd == 0)
            throw new InvalidOperationException($"A desktop window couldn't be created (error {Marshal.GetLastPInvokeError()}).");

        (_width, _height) = (width, height);
        Style = style;
        ByHandle[_hwnd] = this;
        if (style.Level == WindowLevel.Desktop)
            _desktopLevel.Track(this);
    }

    /// <summary>
    /// For tests on any OS: a window that was never created, at <paramref name="handle"/> with <paramref name="style"/>,
    /// whose click-through goes through <paramref name="calls"/>. Only a click-through change may be asked of it: anything
    /// else calls Win32.
    /// </summary>
    internal WinWindow(DesktopWindowStyle style, nint handle, IExStyleCalls calls)
    {
        _desktopLevel = new WinDesktopLevel(null);
        _hwnd = handle;
        Style = style;
        ClickThroughCalls = calls;
    }

    /// <summary>The window's handle (for tests).</summary>
    internal nint Handle => _hwnd;

    /// <summary>
    /// The native calls click-through makes, each with its raw result and the error it left: Win32's, or (in tests) ones
    /// that fail.
    /// </summary>
    internal interface IExStyleCalls
    {
        /// <summary><c>GetWindowLongPtrW(GWL_EXSTYLE)</c>.</summary>
        (nint Result, int Error) Get(nint hwnd);

        /// <summary><c>SetWindowLongPtrW(GWL_EXSTYLE)</c>: the previous value.</summary>
        (nint Result, int Error) Set(nint hwnd, nint exStyle);

        /// <summary>The flush: <c>SetWindowPos</c> with <c>SWP_FRAMECHANGED</c>, nothing moved, sized or reordered.</summary>
        (int Result, int Error) Flush(nint hwnd);
    }

    /// <summary>Win32's calls (the stubs clear the last error before each call).</summary>
    internal sealed class Win32ExStyleCalls : IExStyleCalls
    {
        internal static readonly Win32ExStyleCalls Instance = new();

        public (nint Result, int Error) Get(nint hwnd)
        {
            var result = Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
            return (result, Marshal.GetLastPInvokeError());
        }

        public (nint Result, int Error) Set(nint hwnd, nint exStyle)
        {
            var result = Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, exStyle);
            return (result, Marshal.GetLastPInvokeError());
        }

        public (int Result, int Error) Flush(nint hwnd)
        {
            var result = Win32.SetWindowPos(hwnd, 0, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER
                | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_FRAMECHANGED);
            return (result, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>For tests: replaces the native calls click-through makes.</summary>
    internal IExStyleCalls ClickThroughCalls { get; set; } = Win32ExStyleCalls.Instance;

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

    /// <summary>From a press on the window until its release: the system's move or size loop, or a press on the client.</summary>
    public bool UserMoving => _inLoop || _pressed;

    public bool Visible => _visible;

    public DesktopWindowStyle Style { get; private set; }

    public event Action? UserMoved;

    public event Action? UserResized;

    /// <summary>For tests: replaces the system's move and size loop (<c>DefWindowProc</c> of the press), given its wParam and lParam.</summary>
    internal Action<nint, nint>? SystemLoop { get; set; }

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
        _settingFrame = true;
        try
        {
            if (Win32.SetWindowPos(_hwnd, 0, x, y, width, height, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER) == 0)
                Logger.Warning("A desktop window couldn't be placed at {Frame} ({Error})", frame, Marshal.GetLastPInvokeError());
        }
        finally
        {
            _settingFrame = false;
        }

        if (width == _width && height == _height)
            return;

        (_width, _height) = (width, height);
        if (_hasContent && EnsureDib(carry: true))
            Present();
    }

    public void SetStyle(DesktopWindowStyle style)
    {
        // After a failed click-through change the native bit is uncertain (a write may have landed before the flush
        // failed), so no request is skipped as unchanged until one is written and flushed.
        if (_hwnd == 0 || (style == Style && !_clickThroughUncertain))
            return;

        var (from, to) = (Style.Level, style.Level);
        // Click-through is recorded only once applied: after a failure the window reports the old value, so the next
        // change or reconcile tries again.
        var clickThrough = (style.ClickThrough == Style.ClickThrough && !_clickThroughUncertain) || ApplyClickThrough(style.ClickThrough)
            ? style.ClickThrough
            : Style.ClickThrough;
        Style = style with { ClickThrough = clickThrough };
        if (from == to)
            return;

        // normal → top and desktop → top: topmost; top → normal: not topmost (and in front); desktop → normal: in front.
        // → desktop: not topmost first when it was on top, then the desktop level's rule, tracked.
        if (to == WindowLevel.Top)
            Reorder(Win32.HWND_TOPMOST);
        else if (from == WindowLevel.Top)
            Reorder(Win32.HWND_NOTOPMOST);
        else if (from == WindowLevel.Desktop)
            Reorder(Win32.HWND_TOP);

        if (to == WindowLevel.Desktop)
        {
            _desktopLevel.Track(this);
            _desktopLevel.Place(this, show: false);
        }
        else if (from == WindowLevel.Desktop)
        {
            _desktopLevel.Untrack(this);
        }
    }

    /// <summary>
    /// Sets or clears <c>WS_EX_TRANSPARENT</c> alone (read, modify, write: <c>WS_EX_TOPMOST</c> is the system's to hold and
    /// <c>WS_EX_LAYERED</c> must stay), then flushes the cached style with <c>SWP_FRAMECHANGED</c>, moving, sizing and
    /// reordering nothing, so the desktop level's place and a top window's place are kept. False when a call failed
    /// (logged once until one succeeds).
    /// </summary>
    private bool ApplyClickThrough(bool on)
    {
        var (read, readError) = ClickThroughCalls.Get(_hwnd);
        var exStyle = (uint)(nuint)read;
        // The style is never 0 (WS_EX_LAYERED is in it), so 0 is a failure.
        if (exStyle == 0)
            return ClickThroughFailed("read", readError);

        // The previous value, never 0 either; but 0 is a failure only with an error (as the function's documentation says).
        var (previous, writeError) = ClickThroughCalls.Set(_hwnd, (nint)WithClickThrough(exStyle, on));
        if (previous == 0 && writeError != 0)
            return ClickThroughFailed("written", writeError);

        int flushed, flushError;
        _settingFrame = true;
        try
        {
            (flushed, flushError) = ClickThroughCalls.Flush(_hwnd);
        }
        finally
        {
            _settingFrame = false;
        }

        if (flushed == 0)
            return ClickThroughFailed("flushed", flushError);

        if (_clickThroughUncertain)
        {
            _clickThroughUncertain = false;
            Logger.Information("A desktop window's click-through is applied again");
        }

        return true;
    }

    private bool ClickThroughFailed(string step, int error)
    {
        if (!_clickThroughUncertain)
        {
            _clickThroughUncertain = true;
            Logger.Warning("A desktop window's click-through couldn't be {Step} ({Error}); it is tried again at the next change",
                step, error);
        }

        return false;
    }

    /// <summary>The engine's own z-order change (the guard lets it through).</summary>
    private void Reorder(nint after)
    {
        _placing = true;
        try
        {
            if (Win32.SetWindowPos(_hwnd, after, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE) == 0)
                Logger.Warning("A desktop window's level couldn't be applied ({Error})", Marshal.GetLastPInvokeError());
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>
    /// The desktop level's rule: directly after <paramref name="after"/> (null: where it is), shown in the same call with
    /// <paramref name="show"/>, so it never shows above apps first.
    /// </summary>
    internal void PlaceAfter(nint? after, bool show)
    {
        if (_hwnd == 0)
            return;

        var flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER
            | (after is null ? Win32.SWP_NOZORDER : 0) | (show ? Win32.SWP_SHOWWINDOW : 0);
        _placing = true;
        try
        {
            if (Win32.SetWindowPos(_hwnd, after ?? 0, 0, 0, 0, 0, flags) == 0)
                Logger.Warning("A desktop window couldn't be put above the desktop ({Error})", Marshal.GetLastPInvokeError());
        }
        finally
        {
            _placing = false;
        }
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

            if (Style.Level == WindowLevel.Desktop)
                _desktopLevel.Place(this, show: true);
            else
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
        if (Style.Level == WindowLevel.Desktop)
            _desktopLevel.Untrack(this);
        Win32.DestroyWindow(_hwnd); // its retry timer and the mouse capture of a press too
        _hwnd = 0;
        _visible = false;
        _pressed = false;
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
                // The arrow over the window: with no class cursor, Windows keeps whatever cursor was last set (the
                // busy one while the engine starts). The edges get the sizing cursors from the hit test.
                HCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
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

    /// <summary>The answer to a message that needs this window's state, or null for <see cref="Answer"/> and the default.</summary>
    private nint? OnMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case Win32.WM_TIMER when (nuint)wParam == RetryTimer:
                // KillTimer leaves a message already waiting: one that comes after the retry ended is ignored.
                if (_retrying)
                    Present();
                return 0;
            case Win32.WM_NCHITTEST:
                return HitTest(lParam);
            case Win32.WM_NCLBUTTONDOWN when WinHitTest.StartsLoop((int)wParam):
                return RunSystemLoop(hwnd, wParam, lParam);
            case Win32.WM_NCLBUTTONDBLCLK or Win32.WM_NCRBUTTONUP:
                return 0; // no maximize, no system menu
            case Win32.WM_LBUTTONDOWN:
                Press();
                return 0;
            case Win32.WM_LBUTTONUP:
                Release();
                return 0;
            case Win32.WM_CAPTURECHANGED:
                if (_pressed)
                {
                    // The press lost the mouse (another window took it): it is over.
                    _pressed = false;
                    UserMoved?.Invoke();
                }

                return 0;
            case Win32.WM_SIZE:
                OnSized();
                return 0;
            case Win32.WM_WINDOWPOSCHANGING when Style.Level == WindowLevel.Desktop && !_placing:
                // The guard: a z-order change the engine didn't make (a click, a drag's move loop, another app) is refused.
                ((WINDOWPOS*)lParam)->Flags |= Win32.SWP_NOZORDER;
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// <c>WM_NCHITTEST</c>: the screen point in <paramref name="lParam"/> (physical pixels: the window is per-monitor
    /// aware) against the window's rectangle, its DPI's resize band and its style (<see cref="WinHitTest"/>).
    /// </summary>
    private nint HitTest(nint lParam)
    {
        RECT r;
        if (Win32.GetWindowRect(_hwnd, &r) == 0)
            return WinHitTest.HTCLIENT;
        var x = (short)(lParam & 0xFFFF);
        var y = (short)((lParam >> 16) & 0xFFFF);
        var dpi = Win32.GetDpiForWindow(_hwnd);
        return WinHitTest.Hit(x - r.Left, y - r.Top, r.Right - r.Left, r.Bottom - r.Top, dpi == 0 ? 96 : (int)dpi, Style);
    }

    /// <summary>
    /// A press on the caption or an edge: Windows' own move or size loop, which returns once the button is up again.
    /// <see cref="UserMoving"/> holds until it returns, then <see cref="UserMoved"/> is raised once, moved or not.
    /// </summary>
    private nint RunSystemLoop(nint hwnd, nint wParam, nint lParam)
    {
        if (_inLoop || _pressed)
            return 0;

        _inLoop = true;
        try
        {
            if (SystemLoop is { } loop)
                loop(wParam, lParam);
            else
                Win32.DefWindowProcW(hwnd, Win32.WM_NCLBUTTONDOWN, wParam, lParam);
        }
        finally
        {
            _inLoop = false;
        }

        UserMoved?.Invoke();
        return 0;
    }

    /// <summary>A press on the client (a window that doesn't move): held, with the mouse, until its release.</summary>
    private void Press()
    {
        if (_pressed || _inLoop)
            return;
        _pressed = true;
        Win32.SetCapture(_hwnd);
    }

    private void Release()
    {
        if (!_pressed)
            return;
        _pressed = false;
        Win32.ReleaseCapture(); // its WM_CAPTURECHANGED finds no press
        UserMoved?.Invoke();
    }

    /// <summary>
    /// The window's size changed other than through <see cref="SetFrame"/>: its pixels follow, carried over, and during
    /// the system's size loop each change is reported (<see cref="UserResized"/>).
    /// </summary>
    private void OnSized()
    {
        if (_settingFrame || _hwnd == 0)
            return;

        RECT r;
        if (Win32.GetWindowRect(_hwnd, &r) == 0)
            return;
        var (width, height) = (r.Right - r.Left, r.Bottom - r.Top);
        if (width == _width && height == _height || width <= 0 || height <= 0)
            return;

        (_width, _height) = (width, height);
        if (_hasContent && EnsureDib(carry: true))
            Present();
        if (_inLoop)
            UserResized?.Invoke();
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (ByHandle.TryGetValue(hwnd, out var window) && window.OnMessage(hwnd, msg, wParam, lParam) is { } own)
                return own;

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
