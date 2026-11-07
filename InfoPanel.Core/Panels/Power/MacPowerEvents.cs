using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace InfoPanel.Panels.Power;

/// <summary>
/// macOS's sleep and wake: IOKit's system power notifications (<c>IORegisterForSystemPower</c>) on a CoreFoundation
/// run loop of their own, on a thread of their own, so they come with or without the AppKit loop. Will-sleep tells
/// <see cref="PowerHandlers.SleepArriving"/>, runs the sleep's work and allows the sleep (<c>IOAllowPowerChange</c>) once it has finished or
/// <see cref="PowerEvents.SuspendBound"/> has passed; an idle sleep's question is allowed at once (the panels stop at its
/// will-sleep); powered-on runs <see cref="PowerHandlers.Resumed"/>. <see cref="Dispose"/> stops the run loop (only
/// while its thread is still in it: CoreFoundation frees it when the thread ends), the thread unregisters, and it waits
/// for the thread at most <see cref="PowerEvents.SuspendBound"/> (a will-sleep under way ends by then); a message after
/// it reaches no handler.
/// </summary>
public sealed class MacPowerEvents : IPowerEvents
{
    // iokit_common_msg(n): sys_iokit (0x38 << 26) | sub_iokit_common (0) | n (IOKit/IOMessage.h).
    internal const uint CanSystemSleep = 0xE0000270;
    internal const uint SystemWillSleep = 0xE0000280;
    internal const uint SystemWillNotSleep = 0xE0000290;
    internal const uint SystemHasPoweredOn = 0xE0000300;
    internal const uint SystemWillPowerOn = 0xE0000320;

    /// <summary>How long <see cref="Start"/> waits for the thread to register.</summary>
    private static readonly TimeSpan ThreadBound = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly Action<IntPtr>? _allow; // tests: stands for IOAllowPowerChange
    private readonly Action<IntPtr>? _stopLoop; // tests: stands for CFRunLoopStop
    private PowerHandlers? _handlers;
    private Thread? _thread;
    private volatile bool _stopping;
    private readonly Lock _loopLock = new(); // the run loop's stop and its thread's exit, one at a time
    private IntPtr _runLoop; // the thread's CFRunLoop, for CFRunLoopStop; zero once the thread is leaving (under _loopLock)
    private uint _rootPort; // the io_connect_t IOAllowPowerChange answers on
    private volatile bool _registered;

    public MacPowerEvents(ILogger logger) => _logger = logger;

    /// <summary>
    /// For tests: messages delivered through <see cref="OnMessage"/>, the sleep allowed through <paramref name="allow"/>,
    /// the run loop (<see cref="LoopStarted"/>, <see cref="LoopExiting"/>) stopped through <paramref name="stopLoop"/>.
    /// </summary>
    internal MacPowerEvents(ILogger logger, Action<IntPtr> allow, PowerHandlers handlers, Action<IntPtr>? stopLoop = null)
    {
        _logger = logger;
        _allow = allow;
        _handlers = handlers;
        _stopLoop = stopLoop;
    }

    public string Name => "IOKit power notifications";

    /// <summary>The run loop's thread, once started (tests).</summary>
    internal Thread? RunLoopThread => _thread;

    /// <summary>True from registration until the thread has unregistered.</summary>
    internal bool Registered => _registered;

    public bool Start(PowerHandlers handlers) => OperatingSystem.IsMacOS() && _thread == null && StartOnMac(handlers);

    [SupportedOSPlatform("macos")]
    private bool StartOnMac(PowerHandlers handlers)
    {
        _handlers = handlers;
        var registered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() => Run(registered)) { IsBackground = true, Name = "Power" };
        _thread.Start();
        if (registered.Task.Wait(ThreadBound) && registered.Task.Result)
            return true;
        Dispose();
        return false;
    }

    [SupportedOSPlatform("macos")]
    private unsafe void Run(TaskCompletionSource<bool> registered)
    {
        var self = GCHandle.Alloc(this);
        IntPtr port = IntPtr.Zero;
        uint notifier = 0;
        IntPtr source = IntPtr.Zero;
        IntPtr mode = IntPtr.Zero;
        IntPtr runLoop = IntPtr.Zero;
        try
        {
            mode = Native.DefaultMode();
            _rootPort = Native.IORegisterForSystemPower(GCHandle.ToIntPtr(self), &port, &OnPower, &notifier);
            if (_rootPort == 0 || port == IntPtr.Zero || mode == IntPtr.Zero)
            {
                _logger.Warning("USB panels: IOKit refused the power notifications");
                registered.TrySetResult(false);
                return;
            }
            source = Native.IONotificationPortGetRunLoopSource(port);
            runLoop = Native.CFRunLoopGetCurrent();
            Native.CFRunLoopAddSource(runLoop, source, mode);
            LoopStarted(runLoop);
            _registered = true;
            registered.TrySetResult(true);
            // A stop asked before the loop runs is seen here: each turn returns within a second, or at once when stopped.
            while (!_stopping)
                Native.CFRunLoopRunInMode(mode, 1.0, 0);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "USB panels: the power notifications' thread failed");
            registered.TrySetResult(false);
        }
        finally
        {
            LoopExiting(); // no stop reaches the run loop from here on: it is freed when this thread ends
            if (source != IntPtr.Zero)
                Native.CFRunLoopRemoveSource(runLoop, source, mode);
            if (notifier != 0)
                Native.IODeregisterForSystemPower(&notifier);
            if (_rootPort != 0)
                Native.IOServiceClose(_rootPort);
            if (port != IntPtr.Zero)
                Native.IONotificationPortDestroy(port);
            _registered = false;
            self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnPower(IntPtr refcon, uint service, uint messageType, IntPtr argument)
    {
        try
        {
            if (GCHandle.FromIntPtr(refcon).Target is MacPowerEvents events)
                events.OnMessage(messageType, argument);
        }
        catch (Exception)
        {
            // never across the native frame
        }
    }

    /// <summary>One power message, on the run loop's thread (tests call it directly).</summary>
    internal void OnMessage(uint type, IntPtr argument)
    {
        var handlers = _stopping ? null : _handlers; // once disposing, sleep is allowed at once and a wake goes nowhere
        switch (type)
        {
            case CanSystemSleep:
                Allow(argument); // an idle sleep may go ahead; its will-sleep turns the panels off
                break;
            case SystemWillSleep:
                if (handlers != null && !PowerEvents.WaitForSuspending(handlers, PowerEvents.SuspendBound, _logger))
                    _logger.Warning("USB panels: the computer sleeps before every panel was off ({Bound} s)", PowerEvents.SuspendBound.TotalSeconds);
                Allow(argument);
                break;
            case SystemHasPoweredOn:
                handlers?.Resumed();
                break;
        }
    }

    private void Allow(IntPtr notification)
    {
        if (_allow != null)
            _allow(notification);
        else if (OperatingSystem.IsMacOS())
            Native.IOAllowPowerChange(_rootPort, notification);
    }

    /// <summary>The thread's run loop runs: <see cref="Dispose"/> may stop it from now on.</summary>
    internal void LoopStarted(IntPtr runLoop)
    {
        lock (_loopLock)
            _runLoop = runLoop;
    }

    /// <summary>The thread is leaving its run loop, which CoreFoundation frees when the thread ends: no stop after this.</summary>
    internal void LoopExiting()
    {
        lock (_loopLock) // waits for a stop under way
            _runLoop = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        StopLoop();
        if (_thread is { } thread && thread != Thread.CurrentThread && !thread.Join(PowerEvents.SuspendBound))
            _logger.Warning("USB panels: the power notifications' thread didn't end");
    }

    /// <summary>Stops the run loop while its thread is still in it: the thread can't leave (and free it) meanwhile.</summary>
    private void StopLoop()
    {
        lock (_loopLock)
        {
            if (_runLoop == IntPtr.Zero)
                return;
            if (_stopLoop != null)
                _stopLoop(_runLoop);
            else if (OperatingSystem.IsMacOS())
                Native.CFRunLoopStop(_runLoop);
        }
    }

    /// <summary>IOKit and CoreFoundation's run loop through P/Invoke, as the macOS sensors call them.</summary>
    [SupportedOSPlatform("macos")]
    private static unsafe class Native
    {
        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(IOKit)]
        public static extern uint IORegisterForSystemPower(IntPtr refcon, IntPtr* notifyPort,
            delegate* unmanaged<IntPtr, uint, uint, IntPtr, void> callback, uint* notifier);

        [DllImport(IOKit)] public static extern int IODeregisterForSystemPower(uint* notifier);
        [DllImport(IOKit)] public static extern int IOAllowPowerChange(uint kernelPort, IntPtr notificationId);
        [DllImport(IOKit)] public static extern int IOServiceClose(uint connect);
        [DllImport(IOKit)] public static extern IntPtr IONotificationPortGetRunLoopSource(IntPtr notifyPort);
        [DllImport(IOKit)] public static extern void IONotificationPortDestroy(IntPtr notifyPort);
        [DllImport(CoreFoundation)] public static extern IntPtr CFRunLoopGetCurrent();
        [DllImport(CoreFoundation)] public static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
        [DllImport(CoreFoundation)] public static extern void CFRunLoopRemoveSource(IntPtr runLoop, IntPtr source, IntPtr mode);
        [DllImport(CoreFoundation)] public static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, byte returnAfterSourceHandled);
        [DllImport(CoreFoundation)] public static extern void CFRunLoopStop(IntPtr runLoop);

        /// <summary><c>kCFRunLoopDefaultMode</c>, the CFString CoreFoundation exports.</summary>
        public static IntPtr DefaultMode()
        {
            var library = NativeLibrary.Load(CoreFoundation);
            return *(IntPtr*)NativeLibrary.GetExport(library, "kCFRunLoopDefaultMode");
        }
    }
}
