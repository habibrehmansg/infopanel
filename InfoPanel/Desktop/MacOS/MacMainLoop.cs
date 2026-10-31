using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfoPanel.Desktop.MacOS;

/// <summary>
/// The AppKit event loop on the process main thread, which AppKit requires. The engine creates it on the main thread
/// at start-up (<see cref="TryCreate"/>), runs the host on a worker thread and calls <see cref="RunUntil"/>, which
/// runs <c>-[NSApplication run]</c> until the host task completes. Work reaches the main thread through
/// <see cref="Post"/> (<c>dispatch_async_f</c> on the main queue).
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacMainLoop
{
    /// <summary><c>NSApplicationActivationPolicyAccessory</c>: no Dock icon, no menu bar, not in Cmd-Tab.</summary>
    private const nint ActivationPolicyAccessory = 1;

    /// <summary><c>NSEventTypeApplicationDefined</c>.</summary>
    private const nuint ApplicationDefinedEvent = 15;

    private readonly IntPtr _app;

    private MacMainLoop(IntPtr app)
    {
        _app = app;
    }

    /// <summary>True on the process main thread (<c>pthread_main_np</c>).</summary>
    public static bool IsMainThread => ObjC.pthread_main_np() == 1;

    /// <summary>
    /// Sets up <c>NSApplication</c> (accessory policy) when called on the process main thread; null otherwise (an
    /// entry point run by a test host on a worker thread), and then nothing touches AppKit.
    /// </summary>
    public static MacMainLoop? TryCreate()
    {
        if (!IsMainThread)
            return null;

        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var app = ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));
            ObjC.SendVoidInt(app, ObjC.Sel("setActivationPolicy:"), ActivationPolicyAccessory);
            return new MacMainLoop(app);
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    /// <summary>Runs <paramref name="action"/> on the main thread, in order, inside an autorelease pool. Exceptions are logged.</summary>
    public void Post(Action action) => PostToMain(action);

    public static void PostToMain(Action action)
    {
        var handle = GCHandle.Alloc(action);
        ObjC.dispatch_async_f(ObjC.MainQueue, GCHandle.ToIntPtr(handle), &RunPosted);
    }

    [UnmanagedCallersOnly]
    private static void RunPosted(IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var action = (Action)handle.Target!;
        handle.Free();

        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.ForContext<MacMainLoop>().Error(ex, "A desktop window action failed");
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Runs the AppKit event loop on this (the main) thread until <paramref name="until"/> completes, then stops it
    /// and returns. Call it on the thread that called <see cref="TryCreate"/>.
    /// </summary>
    public void RunUntil(Task until)
    {
        if (!IsMainThread)
            throw new InvalidOperationException("The AppKit loop must run on the main thread.");

        until.ContinueWith(_ => Post(Stop), TaskScheduler.Default);
        ObjC.SendVoid(_app, ObjC.Sel("run"));
    }

    /// <summary>
    /// The system asked the app to quit and was told to wait (<c>NSTerminateLater</c>, the menu-bar item's app delegate):
    /// once the host has stopped, the loop answers yes instead of stopping, and AppKit ends the process.
    /// </summary>
    public static volatile bool TerminateLater;

    /// <summary>
    /// <c>-[NSApplication stop:]</c> takes effect after the next event, so an application-defined event is posted
    /// to wake the loop. When the system is waiting for a quit (<see cref="TerminateLater"/>), the log is written out and
    /// the quit is allowed instead: AppKit exits the process (with 0) once it is told.
    /// </summary>
    private void Stop()
    {
        if (TerminateLater)
        {
            Log.CloseAndFlush();
            ObjC.SendVoidBool(_app, ObjC.Sel("replyToApplicationShouldTerminate:"), 1);
            return;
        }

        ObjC.SendVoidId(_app, ObjC.Sel("stop:"), IntPtr.Zero);
        var wake = ObjC.SendOtherEvent(ObjC.Class("NSEvent"),
            ObjC.Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            ApplicationDefinedEvent, new NSPoint(0, 0), 0, 0, 0, IntPtr.Zero, 0, 0, 0);
        ObjC.SendVoidIdBool(_app, ObjC.Sel("postEvent:atStart:"), wake, 1);
    }
}
