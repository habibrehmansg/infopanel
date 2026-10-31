using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfoPanel.Desktop.Windows;

/// <summary>
/// The engine's Windows UI thread: one per process, for desktop windows and (later) the notification-area icon. A
/// background STA thread named "InfoPanel desktop", started on first use (<see cref="Post"/>, <see cref="Hwnd"/>), so a
/// host whose platform a test replaced never starts it. It:
/// <list type="bullet">
/// <item>is per-monitor DPI aware (v2) before it creates any window, so every window it creates works in physical pixels;</item>
/// <item>owns one hidden top-level window (not a message-only one, which gets no broadcasts): it receives the system's
/// broadcasts (<c>WM_DISPLAYCHANGE</c>, <c>WM_POWERBROADCAST</c>, <c>WM_ENDSESSION</c>, …), session changes
/// (<c>WTSRegisterSessionNotification</c>) and the posted work's wake;</item>
/// <item>runs <c>GetMessageW</c>/<c>TranslateMessage</c>/<c>DispatchMessageW</c> until <see cref="Stop"/>; after a drain
/// that leaves work queued, it serves the input and timer messages waiting before the next wake, which Windows would
/// otherwise take first.</item>
/// </list>
/// Work reaches it through <see cref="Post"/>: a <see cref="PostQueue"/> whose wake is one <c>WM_APP</c> posted to the
/// hidden window. Posting to a window, not the thread, matters: the system's modal loops (a window's move or size loop,
/// a menu) dispatch window messages but drop thread messages. Message hooks (<see cref="AddMessageHook"/>) see the hidden
/// window's messages first; window procedure bodies never let an exception unwind into user32.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WinUiThread
{
    private const string ClassName = "InfoPanelMessageWindow";
    private const uint WakeMessage = Win32.WM_APP;
    private const uint QuitMessage = Win32.WM_APP + 1;
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly Serilog.ILogger Logger = Log.ForContext<WinUiThread>();

    /// <summary>Each thread by its hidden window (one per process in the engine; tests may start their own).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<nint, WinUiThread> Threads = new();

    private readonly Lock _lock = new();
    private readonly PostQueue _queue;
    private readonly List<Func<uint, nint, nint, nint?>> _hooks = [];
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private int _managedThreadId;
    private volatile bool _stopped;
    private nint _hwnd;
    private bool _wakeFailedLogged;

    public WinUiThread()
    {
        _queue = new PostQueue(Wake, ex => Logger.Error(ex, "A desktop window action failed"));
    }

    /// <summary>True once the thread has been started (and so has a hidden window, unless it failed).</summary>
    public bool Started => _thread != null;

    /// <summary>The hidden window: a notification icon's callback window. Starts the thread.</summary>
    public nint Hwnd
    {
        get
        {
            EnsureStarted();
            return _hwnd;
        }
    }

    /// <summary>True on this UI thread.</summary>
    public bool CheckAccess() => _thread != null && Environment.CurrentManagedThreadId == _managedThreadId;

    /// <summary>Runs <paramref name="action"/> on the UI thread after everything posted before it. Any thread; starts the thread.</summary>
    public void Post(Action action)
    {
        EnsureStarted();
        _queue.Post(action);
    }

    /// <summary>
    /// Sees the hidden window's messages before its default handling: return a value to answer the message with it, or
    /// null to pass it on. Called on the UI thread; any thread may add one.
    /// </summary>
    public void AddMessageHook(Func<uint, nint, nint, nint?> hook)
    {
        lock (_lock)
            _hooks.Add(hook);
    }

    /// <summary>Ends the message loop and waits for the thread, up to <paramref name="wait"/>. Nothing when it never started.</summary>
    public void Stop(TimeSpan wait)
    {
        Thread? thread;
        lock (_lock)
        {
            _stopped = true;
            thread = _thread;
        }

        if (thread == null)
            return;

        if (_hwnd != 0 && Win32.PostMessageW(_hwnd, QuitMessage, 0, 0) == 0)
            Logger.Warning("The desktop thread couldn't be told to stop ({Error})", Marshal.GetLastPInvokeError());
        if (!thread.Join(wait))
            Logger.Warning("The desktop thread didn't stop within {Seconds} s", wait.TotalSeconds);
    }

    /// <summary>Starts the thread once and waits until its hidden window exists (or it failed), whoever started it.</summary>
    private void EnsureStarted()
    {
        if (_ready.IsSet)
            return;

        lock (_lock)
        {
            if (_thread == null && !_stopped)
            {
                var thread = new Thread(Run) { IsBackground = true, Name = "InfoPanel desktop" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                _thread = thread;
            }
        }

        if (_thread != null && !CheckAccess() && !_ready.Wait(TimeSpan.FromSeconds(10)))
            Logger.Error("The desktop thread didn't start within 10 s");
    }

    private bool Wake()
    {
        if (_hwnd == 0 || _stopped)
            return false;
        if (Win32.PostMessageW(_hwnd, WakeMessage, 0, 0) != 0)
            return true;

        if (!_wakeFailedLogged)
        {
            _wakeFailedLogged = true;
            Logger.Error("The desktop thread couldn't be woken ({Error}); posted work waits for the next post", Marshal.GetLastPInvokeError());
        }

        return false;
    }

    private void Run()
    {
        _managedThreadId = Environment.CurrentManagedThreadId;
        try
        {
            if (Win32.SetThreadDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) == 0)
                Logger.Warning("The desktop thread couldn't be made per-monitor DPI aware ({Error}); windows may be scaled", Marshal.GetLastPInvokeError());

            _hwnd = CreateHiddenWindow();
            if (_hwnd == 0)
                return;
            Threads[_hwnd] = this;

            if (Win32.WTSRegisterSessionNotification(_hwnd, Win32.NOTIFY_FOR_THIS_SESSION) == 0)
                Logger.Warning("Session changes won't be seen ({Error})", Marshal.GetLastPInvokeError());
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The desktop thread couldn't start");
            return;
        }
        finally
        {
            _ready.Set();
        }

        try
        {
            MSG msg;
            int result;
            while ((result = Win32.GetMessageW(&msg, 0, 0, 0)) != 0)
            {
                if (result == -1)
                {
                    Logger.Error("The desktop thread's message loop failed ({Error})", Marshal.GetLastPInvokeError());
                    break;
                }

                Win32.TranslateMessage(&msg);
                Win32.DispatchMessageW(&msg);

                if (msg.Message == WakeMessage && msg.Hwnd == _hwnd && _queue.Count > 0 && !ServeInputAndTimers())
                    break;
            }
        }
        finally
        {
            Win32.WTSUnRegisterSessionNotification(_hwnd);
            Threads.TryRemove(_hwnd, out _);
            Win32.DestroyWindow(_hwnd);
        }
    }

    /// <summary>The most input, and the most timer, messages served between two drains.</summary>
    private const int MaxServedBetweenDrains = 64;

    /// <summary>
    /// After a drain that left work queued (its wake already waiting again): serves the input and timer messages waiting,
    /// up to <see cref="MaxServedBetweenDrains"/> of each, before the loop takes that wake. Windows takes posted messages
    /// ahead of input and timers, so work that never stops (animated panels under load) would otherwise keep clicks and a
    /// failed update's retry waiting until it stopped. Only this loop does it: a modal loop handles its own input. False
    /// when a quit was met, which ends the loop as <c>GetMessageW</c> returning 0 would.
    /// </summary>
    private static bool ServeInputAndTimers()
    {
        MSG msg;
        for (var i = 0; i < MaxServedBetweenDrains && Win32.PeekMessageW(&msg, 0, 0, 0, Win32.PM_REMOVE | Win32.PM_QS_INPUT) != 0; i++)
        {
            if (!Serve(&msg))
                return false;
        }

        for (var i = 0; i < MaxServedBetweenDrains && Win32.PeekMessageW(&msg, 0, Win32.WM_TIMER, Win32.WM_TIMER, Win32.PM_REMOVE) != 0; i++)
        {
            if (!Serve(&msg))
                return false;
        }

        return true;

        static bool Serve(MSG* msg)
        {
            if (msg->Message == Win32.WM_QUIT) // a peek returns a quit whatever its filter
                return false;

            Win32.TranslateMessage(msg);
            Win32.DispatchMessageW(msg);
            return true;
        }
    }

    private static nint CreateHiddenWindow()
    {
        var instance = Win32.GetModuleHandleW(null);
        fixed (char* className = ClassName)
        fixed (char* title = "InfoPanel")
        {
            var wc = new WNDCLASSEXW
            {
                CbSize = (uint)sizeof(WNDCLASSEXW),
                LpfnWndProc = &WndProc,
                HInstance = instance,
                LpszClassName = className,
            };
            if (Win32.RegisterClassExW(&wc) == 0 && Marshal.GetLastPInvokeError() != ErrorClassAlreadyExists)
            {
                Logger.Error("The desktop thread's window class couldn't be registered ({Error})", Marshal.GetLastPInvokeError());
                return 0;
            }

            // A top-level popup that is never shown: it gets broadcasts, which a message-only window doesn't.
            var hwnd = Win32.CreateWindowExW(0, className, title, Win32.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
            if (hwnd == 0)
                Logger.Error("The desktop thread's window couldn't be created ({Error})", Marshal.GetLastPInvokeError());
            return hwnd;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (Threads.TryGetValue(hwnd, out var thread))
            {
                switch (msg)
                {
                    case WakeMessage:
                        thread._queue.Drain();
                        return 0;
                    case QuitMessage:
                        Win32.PostQuitMessage(0);
                        return 0;
                    case Win32.WM_CLOSE:
                        return 0; // closed only by Stop
                }

                Func<uint, nint, nint, nint?>[] hooks;
                lock (thread._lock)
                    hooks = [.. thread._hooks];
                foreach (var hook in hooks)
                {
                    if (hook(msg, wParam, lParam) is { } answer)
                        return answer;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The desktop thread's window callback failed");
        }

        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }
}
