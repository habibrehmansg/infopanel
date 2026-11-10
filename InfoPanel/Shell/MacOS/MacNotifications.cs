using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop.MacOS;
using Serilog;

namespace InfoPanel.Shell.MacOS;

/// <summary>
/// The system's notifications on macOS through <c>UNUserNotificationCenter</c> (the UserNotifications framework, loaded
/// by path, through <see cref="ObjC"/>). Only from the app bundle: without a bundle identifier (<c>dotnet run</c>, the
/// tests) the centre raises an Objective-C exception that ends the process, so it is never asked. Permission is asked
/// the first time there is something to show, never at launch (the system asks the user once; later asks answer at once).
/// One notification at a time, under one identifier, so a newer one replaces an older; a click on it (the delegate's
/// <c>didReceiveNotificationResponse</c>) runs its callback off the centre's thread. The centre's calls take blocks: each
/// is a global block made once (<see cref="ObjC.GlobalBlock"/>), its state in this class's fields. A centre that can't
/// be set up (the framework or a class missing) is logged once and never thrown; no notification is shown in that run.
/// Once the engine stops (<see cref="Stop()"/>), none is: a permission the system answers late submits nothing.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacNotifications
{
    private const string FrameworkPath = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";

    /// <summary>The request's identifier: one InfoPanel notification at a time.</summary>
    internal const string Identifier = "infopanel.notice";

    /// <summary><c>UNAuthorizationOptionAlert</c>: banners only, no sound or badge.</summary>
    private const nuint AlertOption = 1 << 2;

    /// <summary><c>UNNotificationPresentationOptionList | UNNotificationPresentationOptionBanner</c>.</summary>
    private const nuint PresentOptions = (1 << 3) | (1 << 4);

    /// <summary><c>objc_msgSend</c> for <c>requestAuthorizationWithOptions:completionHandler:</c>.</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr, void> SendVoidUIntId =
        (delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr, void>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    /// <summary><c>objc_msgSend</c> for <c>requestWithIdentifier:content:trigger:</c>.</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> SendIdIdIdId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    /// <summary><c>objc_msgSend</c> for <c>addNotificationRequest:withCompletionHandler:</c>.</summary>
    private static readonly delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void> SendVoidIdId =
        (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    private static readonly Serilog.ILogger Logger = Log.ForContext(typeof(MacNotifications));

    private static readonly Lock Gate = new();
    private static IntPtr _centre;
    private static IntPtr _delegate;
    private static IntPtr _authorised;
    private static IntPtr _added;
    private static (ShellNotification Note, CentreSetup Setup)? _waiting;
    private static Func<Task>? _clicked;

    /// <summary>The process's centre, set up by <see cref="Initialise"/> the first time a notification is shown.</summary>
    private static readonly CentreSetup Process = new(Initialise, Logger);

    /// <summary>How many times the centre was asked for (a test checks that a process without a bundle never asks).</summary>
    internal static int CentreRequests => Process.Requests;

    /// <summary>The process's bundle identifier, or null outside an app bundle.</summary>
    internal static string? BundleIdentifier()
    {
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var bundle = ObjC.Send(ObjC.Class("NSBundle"), ObjC.Sel("mainBundle"));
            return bundle == IntPtr.Zero ? null : ObjC.ToManagedString(ObjC.Send(bundle, ObjC.Sel("bundleIdentifier")));
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// Shows <paramref name="note"/>, asking for permission first (the system asks the user only once); a click runs
    /// <paramref name="clicked"/>. False, and nothing asked of the centre, outside an app bundle. Any thread.
    /// </summary>
    public static bool Show(ShellNotification note, Func<Task> clicked) => Show(note, clicked, BundleIdentifier(), Process);

    /// <summary>
    /// As <see cref="Show(ShellNotification, Func{Task})"/>, in the bundle <paramref name="bundle"/> (null: none), with the
    /// centre <paramref name="setup"/> gives: false when there is none.
    /// </summary>
    internal static bool Show(ShellNotification note, Func<Task> clicked, string? bundle, CentreSetup setup)
    {
        if (string.IsNullOrEmpty(bundle))
        {
            Logger.Information("Notifications need the app bundle: {Title} not shown", note.Title);
            return false;
        }

        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var centre = setup.Get(); // makes the blocks
            if (centre == IntPtr.Zero)
                return false;
            lock (Gate)
            {
                if (setup.Stopped)
                {
                    Logger.Information("InfoPanel is stopping: {Title} not shown", note.Title);
                    return false;
                }

                _waiting = (note, setup);
                _clicked = clicked;
            }

            setup.Ask(centre);
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }

        return true;
    }

    /// <summary>
    /// The centre, set up once by its initialiser (<see cref="Initialise"/> for the process). A setup that fails (the
    /// framework, a class or the delegate missing) is logged once and never thrown: there is no centre for the rest of
    /// the run, and it isn't tried again.
    /// </summary>
    /// <param name="ask">Asks the centre for permission (tests); null: <c>requestAuthorizationWithOptions:</c>, answered in <see cref="Authorised"/>.</param>
    /// <param name="submit">Hands a notification to the centre (tests); null: <c>addNotificationRequest:</c>.</param>
    internal sealed class CentreSetup(Func<IntPtr> initialise, Serilog.ILogger logger, Action<IntPtr>? ask = null, Action<IntPtr, ShellNotification>? submit = null)
    {
        private readonly Lock _gate = new();
        private IntPtr _centre;
        private bool _failed;

        /// <summary>How many times the centre was asked for.</summary>
        public int Requests { get; private set; }

        /// <summary>Set by <see cref="MacNotifications.Stop(CentreSetup)"/>, under its gate: the engine is stopping.</summary>
        internal bool Stopped { get; set; }

        /// <summary>Asks <paramref name="centre"/> for permission; the answer comes to <see cref="Permission"/>.</summary>
        internal void Ask(IntPtr centre)
        {
            if (ask is not null)
                ask(centre);
            else
                SendVoidUIntId(centre, ObjC.Sel("requestAuthorizationWithOptions:completionHandler:"), AlertOption, _authorised);
        }

        /// <summary>Hands <paramref name="note"/> to the centre.</summary>
        internal void Submit(ShellNotification note)
        {
            if (submit is not null)
                submit(_centre, note);
            else
                Add(_centre, note);
        }

        /// <summary>The centre, set up the first time; <see cref="IntPtr.Zero"/> when it couldn't be.</summary>
        public IntPtr Get()
        {
            lock (_gate)
            {
                Requests++;
                if (_centre != IntPtr.Zero || _failed)
                    return _centre;
                try
                {
                    _centre = initialise();
                }
                catch (Exception ex)
                {
                    _failed = true;
                    logger.Warning(ex, "Notifications can't be shown: the notification centre couldn't be set up");
                }

                return _centre;
            }
        }
    }

    /// <summary>The process's centre, its delegate set and the blocks made. Throws when any of it can't be.</summary>
    private static IntPtr Initialise()
    {
        lock (Gate)
        {
            NativeLibrary.Load(FrameworkPath);
            var boolType = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c";
            _authorised = ObjC.GlobalBlock((IntPtr)(delegate* unmanaged<IntPtr, byte, IntPtr, void>)&Authorised, $"v@?{boolType}@");
            _added = ObjC.GlobalBlock((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&Added, "v@?@");
            var delegateClass = ObjC.DefineClass("InfoPanelNotificationDelegate", "NSObject",
                ("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:",
                    (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidReceive, "v@:@@@?"),
                ("userNotificationCenter:willPresentNotification:withCompletionHandler:",
                    (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&WillPresent, "v@:@@@?"));
            // The centre holds its delegate weakly: this one is kept for the process.
            _delegate = ObjC.Send(ObjC.Send(delegateClass, ObjC.Sel("alloc")), ObjC.Sel("init"));
            _centre = ObjC.Send(ObjC.Class("UNUserNotificationCenter"), ObjC.Sel("currentNotificationCenter"));
            if (_centre == IntPtr.Zero)
                throw new InvalidOperationException("UNUserNotificationCenter gave no centre.");
            ObjC.Send(_centre, ObjC.Sel("retain"));
            ObjC.SendVoidId(_centre, ObjC.Sel("setDelegate:"), _delegate);
            return _centre;
        }
    }

    /// <summary>
    /// The engine is stopping (the icon removed): the notification waiting for permission is dropped, and none is shown
    /// after, so a permission answered late submits nothing. Any thread.
    /// </summary>
    public static void Stop() => Stop(Process);

    /// <summary>As <see cref="Stop()"/>, for the centre <paramref name="setup"/> gives.</summary>
    internal static void Stop(CentreSetup setup)
    {
        lock (Gate)
        {
            setup.Stopped = true;
            if (_waiting is { } waiting && waiting.Setup == setup)
                _waiting = null;
        }
    }

    /// <summary>The permission's answer, from the centre's thread: <see cref="Permission"/>.</summary>
    [UnmanagedCallersOnly]
    private static void Authorised(IntPtr block, byte granted, IntPtr error) => Guard(() => Permission(granted != 0, error));

    /// <summary>
    /// The permission's answer: the waiting notification is handed to the centre when allowed, unless the engine has
    /// stopped meanwhile (<see cref="Stop()"/>).
    /// </summary>
    internal static void Permission(bool granted, IntPtr error)
    {
        (ShellNotification Note, CentreSetup Setup)? waiting;
        lock (Gate)
        {
            waiting = _waiting;
            _waiting = null;
        }

        if (waiting is not { } taken)
            return;
        var (note, setup) = taken;
        if (!granted)
        {
            Logger.Information("Notifications are off for InfoPanel in System Settings: {Title} not shown{Error}", note.Title, Describe(error));
            return;
        }

        lock (Gate)
        {
            // Stopped while the system asked: nothing is handed to it.
            if (setup.Stopped)
            {
                Logger.Information("InfoPanel is stopping: {Title} not shown", note.Title);
                return;
            }
        }

        setup.Submit(note);
    }

    /// <summary>Hands <paramref name="note"/> to <paramref name="centre"/> under <see cref="Identifier"/>; the answer comes to <see cref="Added"/>.</summary>
    private static void Add(IntPtr centre, ShellNotification note)
    {
        var pool = ObjC.AutoreleasePoolPush();
        try
        {
            var content = ObjC.Send(ObjC.Send(ObjC.Class("UNMutableNotificationContent"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            ObjC.SendVoidId(content, ObjC.Sel("setTitle:"), ObjC.NSString(note.Title));
            ObjC.SendVoidId(content, ObjC.Sel("setBody:"), ObjC.NSString(note.Text));
            var request = SendIdIdIdId(ObjC.Class("UNNotificationRequest"), ObjC.Sel("requestWithIdentifier:content:trigger:"),
                ObjC.NSString(Identifier), content, IntPtr.Zero);
            ObjC.SendVoid(content, ObjC.Sel("release"));
            SendVoidIdId(centre, ObjC.Sel("addNotificationRequest:withCompletionHandler:"), request, _added);
        }
        finally
        {
            ObjC.AutoreleasePoolPop(pool);
        }
    }

    /// <summary>The request's answer: nil when the notification was handed to the system.</summary>
    [UnmanagedCallersOnly]
    private static void Added(IntPtr block, IntPtr error) => Guard(() =>
    {
        if (error == IntPtr.Zero)
            Logger.Information("Notification shown");
        else
            Logger.Warning("The notification couldn't be shown{Error}", Describe(error));
    });

    /// <summary>A click on the notification: runs its callback off this thread, then tells the centre it's handled.</summary>
    [UnmanagedCallersOnly]
    private static void DidReceive(IntPtr self, IntPtr sel, IntPtr centre, IntPtr response, IntPtr completion)
    {
        Guard(() =>
        {
            var request = ObjC.Send(ObjC.Send(response, ObjC.Sel("notification")), ObjC.Sel("request"));
            if (ObjC.ToManagedString(ObjC.Send(request, ObjC.Sel("identifier"))) != Identifier)
                return;
            Func<Task>? clicked;
            lock (Gate)
                clicked = _clicked;
            Logger.Information("The notification was clicked");
            if (clicked != null)
                _ = Task.Run(clicked);
        });
        if (completion != IntPtr.Zero)
            ((delegate* unmanaged<IntPtr, void>)ObjC.BlockInvoke(completion))(completion);
    }

    /// <summary>A notification while the app is active (an open prompt): shown as a banner and kept in the list.</summary>
    [UnmanagedCallersOnly]
    private static void WillPresent(IntPtr self, IntPtr sel, IntPtr centre, IntPtr notification, IntPtr completion)
    {
        if (completion != IntPtr.Zero)
            ((delegate* unmanaged<IntPtr, nuint, void>)ObjC.BlockInvoke(completion))(completion, PresentOptions);
    }

    /// <summary>An <c>NSError</c>'s description, or nothing.</summary>
    private static string Describe(IntPtr error) =>
        error == IntPtr.Zero ? "" : $": {ObjC.ToManagedString(ObjC.Send(error, ObjC.Sel("localizedDescription")))}";

    /// <summary>Exceptions must never unwind into Objective-C.</summary>
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A notification callback failed");
        }
    }
}
