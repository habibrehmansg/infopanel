using System.Runtime.InteropServices;

namespace InfoPanel.Desktop.Windows;

/// <summary>A top-level window as the desktop level reads it, in z-order: its handle, its thread, shown, topmost.</summary>
internal readonly record struct ZWindow(nint Handle, uint Thread, bool Visible, bool Topmost);

/// <summary>
/// The desktop level's choices, over a list of top-level windows from the top of the z-order down (no Win32 in it, so
/// it is tested on any OS).
/// </summary>
internal static class WinDesktopOrder
{
    /// <summary><c>HWND_TOP</c>.</summary>
    public const nint Top = 0;

    /// <summary><c>HWND_BOTTOM</c>.</summary>
    public const nint Bottom = 1;

    /// <summary><c>HWND_NOTOPMOST</c>: the top of the windows that aren't topmost, for a window that is topmost now.</summary>
    public const nint NoTopmost = -2;

    /// <summary>
    /// The desktop window D: the highest shown, non-topmost window of the shell desktop's thread (the icon host, whatever
    /// its class: the taskbar is topmost, File Explorer's windows run on threads of their own). 0 when there is none: no
    /// shell (<paramref name="shellThread"/> 0) or no such window of it.
    /// </summary>
    public static nint FindDesktop(IReadOnlyList<ZWindow> topToBottom, uint shellThread)
    {
        if (shellThread == 0)
            return 0;

        foreach (var window in topToBottom)
        {
            if (window.Thread == shellThread && window.Visible && !window.Topmost)
                return window.Handle;
        }

        return 0;
    }

    /// <summary>
    /// Where <paramref name="window"/> goes to be directly above <paramref name="desktop"/>: the window above D, or
    /// <see cref="Top"/> when that is topmost or there is none (<see cref="NoTopmost"/> when the window itself is
    /// topmost); <see cref="Bottom"/> when there is no D (0, or no longer in the list). Null when it is already there:
    /// not topmost, and only desktop-level windows (<paramref name="isDesktopLevel"/>) between it and D, so several of
    /// them keep their order instead of swapping on every run. Insert-after a window that isn't topmost, or the bottom,
    /// ends a topmost window's topmost state too.
    /// </summary>
    public static nint? InsertAfter(IReadOnlyList<ZWindow> topToBottom, nint desktop, nint window, Func<nint, bool> isDesktopLevel)
    {
        var below = desktop == 0 ? -1 : IndexOf(topToBottom, desktop);
        var found = below >= 0;
        if (!found)
            below = topToBottom.Count;

        for (var i = below - 1; i >= 0; i--)
        {
            var handle = topToBottom[i].Handle;
            if (handle == window)
            {
                if (!topToBottom[i].Topmost)
                    return null;
                break;
            }

            if (!isDesktopLevel(handle))
                break;
        }

        if (!found)
            return Bottom;
        if (below == 0 || topToBottom[below - 1].Topmost)
            return IsTopmost(topToBottom, window) ? NoTopmost : Top;
        return topToBottom[below - 1].Handle;
    }

    private static bool IsTopmost(IReadOnlyList<ZWindow> windows, nint handle)
    {
        var at = IndexOf(windows, handle);
        return at >= 0 && windows[at].Topmost;
    }

    private static int IndexOf(IReadOnlyList<ZWindow> windows, nint handle)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Handle == handle)
                return i;
        }

        return -1;
    }
}

/// <summary>
/// The desktop level on Windows (UI thread only): the windows at <see cref="WindowLevel.Desktop"/>, each kept directly
/// above the shell's desktop window D (<see cref="WinDesktopOrder"/>), with documented calls only.
/// <list type="bullet">
/// <item>The rule: <c>SetWindowPos</c> with the window above D as insert-after (<c>HWND_TOP</c> when that is topmost or
/// none, <c>HWND_BOTTOM</c> without D), nothing when already there; the window refuses other z-order changes itself. A
/// window that comes out of it topmost is put back and placed again once.</item>
/// <item>While at least one window is tracked, <c>EVENT_SYSTEM_FOREGROUND</c> is hooked (out of context, other processes
/// only) and each event runs the rule; while D's thread has the foreground (the wallpaper clicked, then Win+D changes
/// nothing in the foreground), a 0.5 s timer on the UI thread's hidden window runs it too, from the first tracked window
/// on when the desktop has the foreground already.</item>
/// <item>D is read at each run, never kept: Explorer may restart.</item>
/// </list>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed unsafe class WinDesktopLevel
{
    /// <summary>The poll's timer on the hidden window, and how often it runs.</summary>
    internal const nuint PollTimer = 0x4450; // "DP"
    internal const uint PollMilliseconds = 500;

    /// <summary>At most this many top-level windows are read per run.</summary>
    internal const int MaxWindows = 10_000;

    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<WinDesktopLevel>();

    /// <summary>Each level by its hook, for the foreground event.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, WinDesktopLevel> ByHook = new();

    [ThreadStatic]
    private static List<ZWindow>? _reading;

    private readonly WinUiThread? _thread;
    private readonly List<WinWindow> _windows = [];
    private nint _hook;
    private bool _polling;

    /// <summary>A level whose foreground hook and poll run on <paramref name="thread"/>'s loop; none without one.</summary>
    public WinDesktopLevel(WinUiThread? thread)
    {
        _thread = thread;
        thread?.AddMessageHook(OnMessage);
    }

    /// <summary>For tests: finds D in the windows from the top of the z-order down (the shell's, by default).</summary>
    internal Func<IReadOnlyList<ZWindow>, nint> FindDesktop { get; set; } = FindShellDesktop;

    /// <summary>For tests: reads the top-level windows from the top of the z-order down (<c>EnumWindows</c>, by default).</summary>
    internal Func<IReadOnlyList<ZWindow>> ReadWindows { get; set; } = ReadZOrder;

    /// <summary>For tests: the foreground window (<c>GetForegroundWindow</c>, by default).</summary>
    internal Func<nint> Foreground { get; set; } = Win32.GetForegroundWindow;

    /// <summary>Whether the foreground event is hooked (while at least one window is at the desktop level).</summary>
    internal bool Hooked => _hook != 0;

    /// <summary>Whether the poll runs (while the desktop is the foreground window).</summary>
    internal bool Polling => _polling;

    public void Track(WinWindow window)
    {
        if (!_windows.Contains(window))
            _windows.Add(window);
        if (_hook == 0 && _thread != null)
        {
            _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, &OnWinEvent, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            if (_hook == 0)
                Logger.Warning("The foreground event couldn't be hooked ({Error}); desktop windows aren't brought back after Show desktop",
                    System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            else
                ByHook[_hook] = this;

            // Tracking begins: the desktop may have the foreground already, and no event comes for that.
            UpdatePolling(Foreground());
        }
    }

    public void Untrack(WinWindow window)
    {
        _windows.Remove(window);
        if (_windows.Count > 0)
            return;

        StopPolling();
        if (_hook != 0)
        {
            ByHook.TryRemove(_hook, out _);
            Win32.UnhookWinEvent(_hook);
            _hook = 0;
        }
    }

    /// <summary>
    /// The rule for one window; with <paramref name="show"/>, it is shown in the same call (never above apps first). A
    /// window above D that turns topmost between the read and the call takes the window inserted after it along: that
    /// one is put back (not topmost) and placed again from a new read, once per run; were it to happen again, it is
    /// left at the top of the normal windows, not topmost, until the next foreground event or poll.
    /// </summary>
    public void Place(WinWindow window, bool show)
    {
        var after = InsertAfter(window);
        if (after is null && !show)
            return;
        window.PlaceAfter(after, show);
        if (!IsTopmost(window.Handle))
            return;

        window.PlaceAfter(WinDesktopOrder.NoTopmost, show: false);
        if (InsertAfter(window) is { } again)
            window.PlaceAfter(again, show: false);
        if (IsTopmost(window.Handle))
            window.PlaceAfter(WinDesktopOrder.NoTopmost, show: false);
    }

    /// <summary>The rule for every tracked window, each in turn.</summary>
    public void PlaceAll()
    {
        foreach (var window in _windows.ToArray())
            Place(window, show: false);
    }

    /// <summary>A foreground change (another process's): the poll while D's thread has it, and the rule.</summary>
    internal void OnForeground(nint foreground)
    {
        UpdatePolling(foreground);
        PlaceAll();
    }

    /// <summary>The poll runs while <paramref name="foreground"/> is a window of D's thread, and stops otherwise.</summary>
    private void UpdatePolling(nint foreground)
    {
        var desktop = FindDesktop(ReadWindows());
        if (desktop != 0 && foreground != 0 && Win32.GetWindowThreadProcessId(foreground, null) == Win32.GetWindowThreadProcessId(desktop, null))
            StartPolling();
        else
            StopPolling();
    }

    /// <summary>The rule's choice for <paramref name="window"/> from a new read of the z-order.</summary>
    private nint? InsertAfter(WinWindow window)
    {
        var windows = ReadWindows();
        return WinDesktopOrder.InsertAfter(windows, FindDesktop(windows), window.Handle, IsTracked);
    }

    private static bool IsTopmost(nint hwnd) => ((long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOPMOST) != 0;

    private bool IsTracked(nint handle)
    {
        foreach (var window in _windows)
        {
            if (window.Handle == handle)
                return true;
        }

        return false;
    }

    private void StartPolling()
    {
        if (_polling || _thread == null)
            return;
        if (Win32.SetTimer(_thread.Hwnd, PollTimer, PollMilliseconds, 0) != 0)
            _polling = true;
        else
            Logger.Warning("The desktop's poll couldn't start ({Error})", System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }

    private void StopPolling()
    {
        if (!_polling || _thread == null)
            return;
        Win32.KillTimer(_thread.Hwnd, PollTimer);
        _polling = false;
    }

    /// <summary>The hidden window's messages: the poll's timer.</summary>
    private nint? OnMessage(uint msg, nint wParam, nint lParam)
    {
        if (msg != Win32.WM_TIMER || (nuint)wParam != PollTimer)
            return null;
        // KillTimer leaves a message already waiting: one after the poll stopped does nothing.
        if (_polling)
            PlaceAll();
        return 0;
    }

    /// <summary>D as the shell has it: <see cref="WinDesktopOrder.FindDesktop"/> on the thread of <c>GetShellWindow()</c>.</summary>
    private static nint FindShellDesktop(IReadOnlyList<ZWindow> topToBottom)
    {
        var shell = Win32.GetShellWindow();
        return WinDesktopOrder.FindDesktop(topToBottom, shell == 0 ? 0 : Win32.GetWindowThreadProcessId(shell, null));
    }

    /// <summary>The top-level windows from the top of the z-order down (<c>EnumWindows</c>), at most <see cref="MaxWindows"/>.</summary>
    private static List<ZWindow> ReadZOrder()
    {
        var windows = new List<ZWindow>();
        _reading = windows;
        try
        {
            Win32.EnumWindows(&Collect, 0);
        }
        finally
        {
            _reading = null;
        }

        return windows;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint data)
    {
        var windows = _reading;
        if (windows == null)
            return 0;
        windows.Add(new ZWindow(hwnd, Win32.GetWindowThreadProcessId(hwnd, null), Win32.IsWindowVisible(hwnd) != 0, IsTopmost(hwnd)));
        return windows.Count < MaxWindows ? 1 : 0;
    }

    [UnmanagedCallersOnly]
    private static void OnWinEvent(nint hook, uint @event, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (ByHook.TryGetValue(hook, out var level))
                level.OnForeground(hwnd);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The desktop level's foreground event failed");
        }
    }
}
