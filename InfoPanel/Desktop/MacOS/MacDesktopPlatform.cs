using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace InfoPanel.Desktop.MacOS;

/// <summary>
/// Desktop windows on macOS through AppKit (libobjc interop, no bindings). Everything but <see cref="Post"/> runs on
/// the main thread, inside <see cref="MacMainLoop"/>.
/// <list type="bullet">
/// <item>Coordinates: AppKit's global space (points, y up from the primary display's bottom-left) is flipped about
/// the primary display (<c>NSScreen.screens[0]</c>) into <see cref="PointRect"/>'s top-left space.</item>
/// <item>Windows: borderless (plus the resizable mask when resizable), not opaque, clear background, no shadow,
/// at the profile's level (<see cref="Placement"/>: floating when on top, just above the desktop icons and stationary
/// when on the desktop), on all Spaces and out of window cycling, movable by their background, never key,
/// taking every click (also on transparent pixels); a layer-hosting view whose layer shows the frame as a
/// <c>CGImage</c>, one frame pixel per backing pixel, anchored top-left, nearest-neighbour, without implicit
/// animations (a frame rendered below scale 1 is stretched to fill the window, filtered linearly). A frame that doesn't
/// fit the window (<see cref="DesktopFrameBlit.Fits"/>: one rendered before a live resize or a size change) is stretched
/// over the whole window by the layer itself (<c>kCAGravityResize</c>, linear), at every live-resize step, until one
/// that fits arrives.</item>
/// <item>A press on a window is reported once, when the mouse button is up again, moved or not; moves made by
/// <see cref="IDesktopWindow.SetFrame"/> never are.</item>
/// <item>AppKit resizes a resizable window from its edges but shows no cursor for it (the window is never key, the app
/// never active), so the window draws its own edge handles (<see cref="EdgeHandles"/>): a shape layer above the content
/// layer, in the window only, never in a frame. The pointer's place comes from the content view's tracking area, or from
/// a poll of <c>+[NSEvent mouseLocation]</c> when the tracking area delivers nothing (<see cref="EdgeHoverChooser"/>,
/// <c>INFOPANEL_EDGE_HOVER</c>).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacDesktopPlatform : IDesktopPlatform
{
    private const nuint StyleBorderless = 0;
    private const nuint StyleResizable = 1 << 3;
    private const nuint BackingStoreBuffered = 2;
    private const nint NormalWindowLevel = 0;
    private const nint FloatingWindowLevel = 3;

    /// <summary><c>NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorIgnoresCycle</c>.</summary>
    private const nuint CollectionBehavior = (1 << 0) | (1 << 6);

    /// <summary><c>NSWindowCollectionBehaviorStationary</c>: unaffected by Mission Control and Show Desktop, like the desktop.</summary>
    private const nuint StationaryBehavior = 1 << 4;

    /// <summary><c>kCGDesktopIconWindowLevelKey</c> (CoreGraphics' <c>CGWindowLevel.h</c>).</summary>
    private const int DesktopIconWindowLevelKey = 18;

    /// <summary>The desktop level: one above the desktop icons, below every app window. Read once.</summary>
    private static readonly Lazy<nint> DesktopWindowLevel = new(() => ObjC.CGWindowLevelForKey(DesktopIconWindowLevelKey) + 1);

    /// <summary>
    /// A <see cref="WindowLevel"/>'s AppKit window level and collection behaviour: normal 0 and top floating (3), on every
    /// Space and out of window cycling; desktop just above the desktop icons, stationary as well.
    /// </summary>
    internal static (nint Level, nuint Behavior) Placement(WindowLevel level) => level switch
    {
        WindowLevel.Top => (FloatingWindowLevel, CollectionBehavior),
        WindowLevel.Desktop => (DesktopWindowLevel.Value, CollectionBehavior | StationaryBehavior),
        _ => (NormalWindowLevel, CollectionBehavior),
    };

    /// <summary>How often a moved window checks whether the mouse button is up again.</summary>
    private static readonly TimeSpan MouseUpPoll = TimeSpan.FromMilliseconds(50);

    private static readonly Serilog.ILogger Logger = Log.ForContext<MacDesktopPlatform>();

    private static readonly Dictionary<IntPtr, MacWindow> Windows = [];

    /// <summary>Where the edge handles' pointer comes from (<c>INFOPANEL_EDGE_HOVER</c>: <c>tracking</c>, <c>poll</c>, <c>probe</c>, else auto).</summary>
    private static readonly EdgeHoverChooser Hover = new(EdgeHoverChooser.ModeOf(Environment.GetEnvironmentVariable("INFOPANEL_EDGE_HOVER")));

    private static readonly long Epoch = Stopwatch.GetTimestamp();

    /// <summary>The edge handles' clock.</summary>
    private static TimeSpan Now => Stopwatch.GetElapsedTime(Epoch);

    /// <summary>Whether a poll of the pointer's place is scheduled.</summary>
    private static bool _polling;
    private static MacDesktopPlatform? _current;
    private static IntPtr _delegateClass;
    private static IntPtr _viewClass;
    private static IntPtr _windowClass;
    private static IntPtr _delegate;

    private readonly MacMainLoop _loop;
    private bool _initialized;

    public MacDesktopPlatform(MacMainLoop loop)
    {
        _loop = loop;
    }

    public bool IsSupported => true;

    public string? UnsupportedMessage => null;

    public event Action? DisplaysChanged;

    public void Post(Action action) => _loop.Post(action);

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        EnsureInitialized();
        var screens = ObjC.Send(ObjC.Class("NSScreen"), Sel.Screens);
        var count = (int)ObjC.SendUIntRet(screens, Sel.Count);
        if (count == 0)
            return [];

        var primaryHeight = ObjC.SendRect(ObjC.SendUInt(screens, Sel.ObjectAtIndex, 0), Sel.Frame).Height;
        var displays = new List<DisplayInfo>(count);
        for (var i = 0; i < count; i++)
        {
            var screen = ObjC.SendUInt(screens, Sel.ObjectAtIndex, (nuint)i);
            var frame = ObjC.SendRect(screen, Sel.Frame);
            var scale = ObjC.SendDouble(screen, Sel.BackingScaleFactor);
            var name = ObjC.ToManagedString(ObjC.Send(screen, Sel.LocalizedName)) ?? $"Display {i + 1}";
            displays.Add(new DisplayInfo(name, ToTopLeft(frame, primaryHeight), scale > 0 ? scale : 1, Id: MonitorId(screen)));
        }

        return displays;
    }

    /// <summary><c>NSScreenNumber</c>, the key of a screen's display id in its <c>deviceDescription</c> (retained once).</summary>
    private static readonly Lazy<IntPtr> ScreenNumberKey = new(() => ObjC.Send(ObjC.NSString("NSScreenNumber"), Sel.Retain));

    /// <summary>
    /// The screen's monitor id (<see cref="DisplayMatcher.IdOf"/>): its <c>NSScreenNumber</c> is the <c>CGDirectDisplayID</c>
    /// whose CoreGraphics vendor and model numbers are the EDID's maker and product code. Null when it reports none.
    /// </summary>
    private static string? MonitorId(IntPtr screen)
    {
        var number = ObjC.SendId(ObjC.Send(screen, Sel.DeviceDescription), Sel.ObjectForKey, ScreenNumberKey.Value);
        if (number == IntPtr.Zero)
            return null;

        var display = (uint)ObjC.SendUIntRet(number, Sel.UnsignedIntegerValue);
        return DisplayMatcher.IdOf(ObjC.CGDisplayVendorNumber(display), ObjC.CGDisplayModelNumber(display));
    }

    public IDesktopWindow CreateWindow(DesktopWindowStyle style, PointRect frame)
    {
        EnsureInitialized();
        return new MacWindow(this, style, frame);
    }

    /// <summary>Registers the delegate and view classes and the display-change observer, once, on the main thread.</summary>
    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        _initialized = true;
        _current = this;
        if (_delegateClass == IntPtr.Zero)
        {
            _delegateClass = ObjC.DefineClass("InfoPanelWindowDelegate", "NSObject",
                ("windowDidMove:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&WindowDidMove, "v@:@"),
                ("windowDidResize:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&WindowDidResize, "v@:@"),
                ("windowDidChangeBackingProperties:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&WindowDidChangeBackingProperties, "v@:@"),
                ("screenParametersChanged:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&ScreenParametersChanged, "v@:@"));
            _viewClass = ObjC.DefineClass("InfoPanelFrameView", "NSView",
                ("acceptsFirstMouse:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&Yes1, "c@:@"),
                ("mouseDownCanMoveWindow", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&Yes0, "c@:"),
                ("mouseEntered:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PointerEntered, "v@:@"),
                ("mouseMoved:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PointerMoved, "v@:@"),
                ("mouseExited:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PointerExited, "v@:@"));
            _windowClass = ObjC.DefineClass("InfoPanelWindow", "NSWindow",
                ("constrainFrameRect:toScreen:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, NSRect, IntPtr, NSRect>)&Unconstrained, "{CGRect={CGPoint=dd}{CGSize=dd}}@:{CGRect={CGPoint=dd}{CGSize=dd}}@"),
                ("sendEvent:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&SendEvent, "v@:@"));
            _delegate = ObjC.Send(ObjC.Send(_delegateClass, Sel.Alloc), Sel.Init);

            var center = ObjC.Send(ObjC.Class("NSNotificationCenter"), ObjC.Sel("defaultCenter"));
            ObjC.SendVoidIdSelIdId(center, ObjC.Sel("addObserver:selector:name:object:"), _delegate, ObjC.Sel("screenParametersChanged:"),
                ObjC.Constant("AppKit", "NSApplicationDidChangeScreenParametersNotification"), IntPtr.Zero);
        }
    }

    /// <summary>The primary display's height in points (AppKit's y axis is flipped about it).</summary>
    internal static double PrimaryHeight()
    {
        var screens = ObjC.Send(ObjC.Class("NSScreen"), Sel.Screens);
        if (ObjC.SendUIntRet(screens, Sel.Count) == 0)
            return 0;
        return ObjC.SendRect(ObjC.SendUInt(screens, Sel.ObjectAtIndex, 0), Sel.Frame).Height;
    }

    internal static PointRect ToTopLeft(NSRect rect, double primaryHeight) =>
        new(rect.X, primaryHeight - (rect.Y + rect.Height), rect.Width, rect.Height);

    internal static NSRect ToAppKit(PointRect rect, double primaryHeight) =>
        new(rect.X, primaryHeight - (rect.Y + rect.Height), rect.Width, rect.Height);

    private static MacWindow? WindowOf(IntPtr notification)
    {
        var window = ObjC.Send(notification, Sel.Object);
        return Windows.GetValueOrDefault(window);
    }

    [UnmanagedCallersOnly]
    private static void WindowDidMove(IntPtr self, IntPtr sel, IntPtr notification) => Guard(() => WindowOf(notification)?.OnMoved());

    [UnmanagedCallersOnly]
    private static void WindowDidResize(IntPtr self, IntPtr sel, IntPtr notification) => Guard(() => WindowOf(notification)?.OnResized());

    [UnmanagedCallersOnly]
    private static void WindowDidChangeBackingProperties(IntPtr self, IntPtr sel, IntPtr notification) =>
        Guard(() => WindowOf(notification)?.OnBackingChanged());

    [UnmanagedCallersOnly]
    private static void ScreenParametersChanged(IntPtr self, IntPtr sel, IntPtr notification) => Guard(() => _current?.DisplaysChanged?.Invoke());

    /// <summary>
    /// Windows go exactly where the profile puts them, as on Windows: AppKit would otherwise keep them below the menu
    /// bar and on a screen when they are ordered front, and that move would look like the user's.
    /// </summary>
    [UnmanagedCallersOnly]
    private static NSRect Unconstrained(IntPtr self, IntPtr sel, NSRect frame, IntPtr screen) => frame;

    /// <summary><c>NSEventTypeLeftMouseDown</c>.</summary>
    private const nuint LeftMouseDown = 1;

    /// <summary><c>-[InfoPanelWindow sendEvent:]</c>: notes a left mouse-down, then <c>[super sendEvent:]</c>.</summary>
    [UnmanagedCallersOnly]
    private static void SendEvent(IntPtr self, IntPtr sel, IntPtr nsEvent)
    {
        try
        {
            if (ObjC.SendUIntRet(nsEvent, Sel.Type) == LeftMouseDown && Windows.TryGetValue(self, out var window))
                window.OnMouseDown();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A desktop window callback failed");
        }

        var super = new ObjC.Super(self, ObjC.Class("NSWindow"));
        ObjC.SendSuperVoidId(&super, sel, nsEvent);
    }

    /// <summary><c>-[InfoPanelFrameView mouseEntered:]</c>, from the view's tracking area.</summary>
    [UnmanagedCallersOnly]
    private static void PointerEntered(IntPtr self, IntPtr sel, IntPtr nsEvent) => Guard(() => ViewWindow(self)?.OnTracking("enter"));

    /// <summary><c>-[InfoPanelFrameView mouseMoved:]</c>, from the view's tracking area.</summary>
    [UnmanagedCallersOnly]
    private static void PointerMoved(IntPtr self, IntPtr sel, IntPtr nsEvent) => Guard(() => ViewWindow(self)?.OnTracking("move"));

    /// <summary><c>-[InfoPanelFrameView mouseExited:]</c>, from the view's tracking area.</summary>
    [UnmanagedCallersOnly]
    private static void PointerExited(IntPtr self, IntPtr sel, IntPtr nsEvent) => Guard(() => ViewWindow(self)?.OnTracking("exit"));

    private static MacWindow? ViewWindow(IntPtr view) => Windows.GetValueOrDefault(ObjC.Send(view, Sel.Window));

    /// <summary>The pointer's place, in AppKit's screen space.</summary>
    private static NSPoint MouseLocation() => ObjC.SendPoint(Sel.NSEvent, Sel.MouseLocation);

    /// <summary>Polls the pointer's place from now on while <see cref="Hover"/> needs it and a window would show handles.</summary>
    private static void EnsurePolling()
    {
        if (_polling || !Hover.Polls || Windows.Count == 0 || _current == null)
            return;
        _polling = true;
        _current.Post(PollPointer);
    }

    /// <summary>
    /// One poll of the pointer's place: whether it is over a window that would show handles, with no other window above
    /// it there, decides the source with <see cref="Hover"/>; when the poll is the source, each such window's handles
    /// follow it. Fast while the pointer is near such a window, slow otherwise; stops once the tracking area is chosen.
    /// </summary>
    private static void PollPointer()
    {
        _polling = false;
        if (!Hover.Polls || Windows.Count == 0 || _current is not { } platform)
            return;

        var windows = Windows.Values.Where(w => w.ShowsHandles).ToList();
        if (windows.Count == 0)
            return; // until a window is shown, resizable and taking clicks again (EnsurePolling)

        var pointer = MouseLocation();
        var under = ObjC.SendIntPointInt(Sel.NSWindow, Sel.WindowNumberAtPoint, pointer, 0);
        var now = Now;
        var near = false;
        var inside = new bool[windows.Count];
        for (var i = 0; i < windows.Count; i++)
        {
            var frame = windows[i].ScreenFrame;
            near |= new PointRect(frame.X - EdgeHoverChooser.Near, frame.Y - EdgeHoverChooser.Near,
                frame.Width + 2 * EdgeHoverChooser.Near, frame.Height + 2 * EdgeHoverChooser.Near).Contains(pointer.X, pointer.Y);
            inside[i] = new PointRect(frame.X, frame.Y, frame.Width, frame.Height).Contains(pointer.X, pointer.Y)
                && under == windows[i].Number;
        }

        var undecided = Hover.Source == EdgeHoverSource.Undecided;
        var drives = Hover.Polled(inside.Any(x => x), now);
        if (undecided && drives)
            Logger.Information("Desktop windows' edge handles follow a poll of the pointer: no tracking event came within {Patience} of the pointer entering a window",
                EdgeHoverChooser.Patience);
        for (var i = 0; i < windows.Count; i++)
            windows[i].OnPolled(inside[i], pointer, drives, now);

        if (!Hover.Polls)
            return;
        _polling = true;
        _ = Task.Delay(Hover.Interval(near)).ContinueWith(_ => platform.Post(PollPointer), TaskScheduler.Default);
    }

    [UnmanagedCallersOnly]
    private static byte Yes1(IntPtr self, IntPtr sel, IntPtr arg) => 1;

    [UnmanagedCallersOnly]
    private static byte Yes0(IntPtr self, IntPtr sel) => 1;

    /// <summary>Exceptions must never unwind into Objective-C.</summary>
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "A desktop window callback failed");
        }
    }

    /// <summary>Selectors used per frame or per event, registered once.</summary>
    private static class Sel
    {
        public static readonly IntPtr Alloc = ObjC.Sel("alloc");
        public static readonly IntPtr Init = ObjC.Sel("init");
        public static readonly IntPtr Release = ObjC.Sel("release");
        public static readonly IntPtr Retain = ObjC.Sel("retain");
        public static readonly IntPtr Screens = ObjC.Sel("screens");
        public static readonly IntPtr Count = ObjC.Sel("count");
        public static readonly IntPtr ObjectAtIndex = ObjC.Sel("objectAtIndex:");
        public static readonly IntPtr Frame = ObjC.Sel("frame");
        public static readonly IntPtr BackingScaleFactor = ObjC.Sel("backingScaleFactor");
        public static readonly IntPtr LocalizedName = ObjC.Sel("localizedName");
        public static readonly IntPtr DeviceDescription = ObjC.Sel("deviceDescription");
        public static readonly IntPtr ObjectForKey = ObjC.Sel("objectForKey:");
        public static readonly IntPtr UnsignedIntegerValue = ObjC.Sel("unsignedIntegerValue");
        public static readonly IntPtr Object = ObjC.Sel("object");
        public static readonly IntPtr SetFrameDisplay = ObjC.Sel("setFrame:display:");
        public static readonly IntPtr InLiveResize = ObjC.Sel("inLiveResize");
        public static readonly IntPtr PressedMouseButtons = ObjC.Sel("pressedMouseButtons");
        public static readonly IntPtr SetContents = ObjC.Sel("setContents:");
        public static readonly IntPtr SetContentsScale = ObjC.Sel("setContentsScale:");
        public static readonly IntPtr SetMagnificationFilter = ObjC.Sel("setMagnificationFilter:");
        public static readonly IntPtr SetMinificationFilter = ObjC.Sel("setMinificationFilter:");
        public static readonly IntPtr SetContentsGravity = ObjC.Sel("setContentsGravity:");
        public static readonly IntPtr ContentsScale = ObjC.Sel("contentsScale");
        public static readonly IntPtr MagnificationFilter = ObjC.Sel("magnificationFilter");
        public static readonly IntPtr Begin = ObjC.Sel("begin");
        public static readonly IntPtr Commit = ObjC.Sel("commit");
        public static readonly IntPtr SetDisableActions = ObjC.Sel("setDisableActions:");
        public static readonly IntPtr IsVisible = ObjC.Sel("isVisible");
        public static readonly IntPtr Type = ObjC.Sel("type");
        public static readonly IntPtr CATransaction = ObjC.Class("CATransaction");
        public static readonly IntPtr Window = ObjC.Sel("window");
        public static readonly IntPtr WindowNumber = ObjC.Sel("windowNumber");
        public static readonly IntPtr WindowNumberAtPoint = ObjC.Sel("windowNumberAtPoint:belowWindowWithWindowNumber:");
        public static readonly IntPtr MouseLocation = ObjC.Sel("mouseLocation");
        public static readonly IntPtr NSEvent = ObjC.Class("NSEvent");
        public static readonly IntPtr NSWindow = ObjC.Class("NSWindow");
        public static readonly IntPtr SetOpacity = ObjC.Sel("setOpacity:");
        public static readonly IntPtr SetPath = ObjC.Sel("setPath:");
        public static readonly IntPtr SetLineWidth = ObjC.Sel("setLineWidth:");
        public static readonly IntPtr SetFrameRect = ObjC.Sel("setFrame:");
    }

    /// <summary>
    /// <c>NSTrackingMouseEnteredAndExited | NSTrackingMouseMoved | NSTrackingActiveAlways | NSTrackingInVisibleRect</c>
    /// (<c>NSTrackingArea.h</c>): pointer events whether or not the app is active (the window is never key), over the
    /// view's visible rect as it resizes.
    /// </summary>
    private const nuint PointerTracking = 0x01 | 0x02 | 0x80 | 0x200;

    private sealed class MacWindow : IDesktopWindow
    {
        private static readonly IntPtr SrgbSpace = ObjC.CGColorSpaceCreateWithName(ObjC.Constant("CoreGraphics", "kCGColorSpaceSRGB"));
        private static readonly IntPtr NearestFilter = ObjC.Constant("QuartzCore", "kCAFilterNearest");
        private static readonly IntPtr LinearFilter = ObjC.Constant("QuartzCore", "kCAFilterLinear");
        private static readonly IntPtr TopLeftGravity = ObjC.Constant("QuartzCore", "kCAGravityTopLeft");
        private static readonly IntPtr ResizeGravity = ObjC.Constant("QuartzCore", "kCAGravityResize");

        private readonly MacDesktopPlatform _platform;
        private IntPtr _window;
        private IntPtr _layer;
        private PointRect _knownFrame;
        private double _scale;
        private float _frameScale = 1;

        // The last frame's pixel size (0 until one is shown), and the layer's gravity and filters as last set.
        private int _frameWidth;
        private int _frameHeight;
        private (IntPtr Gravity, IntPtr Magnification, IntPtr Minification) _layerScaling = (TopLeftGravity, NearestFilter, NearestFilter);
        // The edge handle: its state, the shape layer that draws it above the content layer, and what was last drawn.
        private static readonly IntPtr HandleFill = ObjC.CGColorCreateGenericGray(1, 0.92);
        private static readonly IntPtr HandleLine = ObjC.CGColorCreateGenericGray(0, 0.6);
        private readonly EdgeHandles _handles = new();
        private IntPtr _handleLayer;
        private (ResizeEdge Edge, double Width, double Height, double Scale) _handleShape;
        private float _handleOpacity;
        private bool _handleFramePending;

        private bool _settingFrame;
        private bool _waitingForMouseUp;
        private bool _mouseDown;
        private bool _visible;

        public MacWindow(MacDesktopPlatform platform, DesktopWindowStyle style, PointRect frame)
        {
            _platform = platform;
            var primaryHeight = PrimaryHeight();
            var rect = ToAppKit(frame, primaryHeight);

            _window = ObjC.SendInitWindow(ObjC.Send(_windowClass, Sel.Alloc), ObjC.Sel("initWithContentRect:styleMask:backing:defer:"),
                rect, style.Resizable ? StyleResizable : StyleBorderless, BackingStoreBuffered, 0);
            ObjC.SendVoidBool(_window, ObjC.Sel("setReleasedWhenClosed:"), 0);
            ObjC.SendVoidBool(_window, ObjC.Sel("setOpaque:"), 0);
            ObjC.SendVoidId(_window, ObjC.Sel("setBackgroundColor:"), ObjC.Send(ObjC.Class("NSColor"), ObjC.Sel("clearColor")));
            ObjC.SendVoidBool(_window, ObjC.Sel("setHasShadow:"), 0);
            ObjC.SendVoidBool(_window, ObjC.Sel("setMovableByWindowBackground:"), 1);
            ObjC.SendVoidInt(_window, ObjC.Sel("setAnimationBehavior:"), 2); // NSWindowAnimationBehaviorNone

            // A layer-hosting view: AppKit never draws into a layer it didn't create.
            _layer = ObjC.Send(ObjC.Send(ObjC.Class("CALayer"), ObjC.Sel("layer")), Sel.Retain);
            ObjC.SendVoidId(_layer, Sel.SetContentsGravity, _layerScaling.Gravity);
            ObjC.SendVoidId(_layer, Sel.SetMagnificationFilter, _layerScaling.Magnification);
            ObjC.SendVoidId(_layer, Sel.SetMinificationFilter, _layerScaling.Minification);
            ObjC.SendVoidBool(_layer, ObjC.Sel("setOpaque:"), 0);

            // The edge handle, above the frame: a sublayer, so it is in the window only, never in a frame.
            _handleLayer = ObjC.Send(ObjC.Send(ObjC.Class("CAShapeLayer"), ObjC.Sel("layer")), Sel.Retain);
            ObjC.SendVoidId(_handleLayer, ObjC.Sel("setFillColor:"), HandleFill);
            ObjC.SendVoidId(_handleLayer, ObjC.Sel("setStrokeColor:"), HandleLine);
            ObjC.SendVoidFloat(_handleLayer, Sel.SetOpacity, 0);
            ObjC.SendVoidId(_layer, ObjC.Sel("addSublayer:"), _handleLayer);

            var view = ObjC.SendIdRect(ObjC.Send(_viewClass, Sel.Alloc), ObjC.Sel("initWithFrame:"), new NSRect(0, 0, rect.Width, rect.Height));
            ObjC.SendVoidId(view, ObjC.Sel("setLayer:"), _layer);
            ObjC.SendVoidBool(view, ObjC.Sel("setWantsLayer:"), 1);
            var tracking = ObjC.SendInitTrackingArea(ObjC.Send(ObjC.Class("NSTrackingArea"), Sel.Alloc),
                ObjC.Sel("initWithRect:options:owner:userInfo:"), default, PointerTracking, view, IntPtr.Zero);
            ObjC.SendVoidId(view, ObjC.Sel("addTrackingArea:"), tracking);
            ObjC.SendVoid(tracking, Sel.Release);
            ObjC.SendVoidId(_window, ObjC.Sel("setContentView:"), view);
            ObjC.SendVoid(view, Sel.Release);

            Windows[_window] = this;
            ObjC.SendVoidId(_window, ObjC.Sel("setDelegate:"), _delegate);
            Style = style;
            ApplyStyle(style);
            _knownFrame = Frame;
            UpdateScale();
            EnsurePolling();
        }

        public PointRect Frame => ToTopLeft(ObjC.SendRect(_window, Sel.Frame), PrimaryHeight());

        public double BackingScale => _scale;

        public bool UserMoving => _waitingForMouseUp;

        public bool Visible => _visible;

        public DesktopWindowStyle Style { get; private set; }

        public event Action? UserMoved;

        public event Action? UserResized;

        public event Action? BackingScaleChanged;

        public void SetFrame(PointRect frame)
        {
            if (_window == IntPtr.Zero)
                return;

            _settingFrame = true;
            try
            {
                ObjC.SendVoidRectBool(_window, Sel.SetFrameDisplay, ToAppKit(frame, PrimaryHeight()), 1);
            }
            finally
            {
                _settingFrame = false;
            }

            _knownFrame = Frame;
            UpdateScale();
            ApplyLayerScaling();
            DrawHandle();
        }

        public void SetStyle(DesktopWindowStyle style)
        {
            if (_window == IntPtr.Zero || style == Style)
                return;
            Style = style;
            ApplyStyle(style);
        }

        private void ApplyStyle(DesktopWindowStyle style)
        {
            _settingFrame = true;
            try
            {
                ObjC.SendVoidUInt(_window, ObjC.Sel("setStyleMask:"), style.Resizable ? StyleResizable : StyleBorderless);
            }
            finally
            {
                _settingFrame = false;
            }

            var (level, behavior) = Placement(style.Level);
            ObjC.SendVoidInt(_window, ObjC.Sel("setLevel:"), level);
            ObjC.SendVoidUInt(_window, ObjC.Sel("setCollectionBehavior:"), behavior);
            ObjC.SendVoidBool(_window, ObjC.Sel("setMovable:"), style.Movable ? (byte)1 : (byte)0);

            // An explicit NO is what makes transparent pixels take clicks; YES lets every click, scroll and the pointer
            // through to what's beneath, so no drag or resize reaches the window (Movable and the style mask are kept).
            ObjC.SendVoidBool(_window, ObjC.Sel("setIgnoresMouseEvents:"), style.ClickThrough ? (byte)1 : (byte)0);

            if (!style.Resizable || style.ClickThrough)
                DropHandle();

            EnsurePolling();
        }

        public void SetVisible(bool visible)
        {
            if (_window == IntPtr.Zero || visible == _visible)
                return;
            _visible = visible;
            if (visible)
            {
                ObjC.SendVoid(_window, ObjC.Sel("orderFrontRegardless"));
                EnsurePolling();
            }
            else
            {
                DropHandle();
                ObjC.SendVoidId(_window, ObjC.Sel("orderOut:"), IntPtr.Zero);
            }
        }

        /// <summary>No handle, at once (not resizable, letting clicks through, hidden); the next poll starts afresh.</summary>
        private void DropHandle()
        {
            _handles.Disable(Now);
            DrawHandle();
        }

        /// <summary>Whether the window would show its handles: shown, resizable and taking clicks.</summary>
        internal bool ShowsHandles => _window != IntPtr.Zero && _visible && Style.Resizable && !Style.ClickThrough;

        /// <summary>The window's frame in AppKit's screen space (y up).</summary>
        internal NSRect ScreenFrame => ObjC.SendRect(_window, Sel.Frame);

        /// <summary>The window server's number for the window.</summary>
        internal nint Number => ObjC.SendIntRet(_window, Sel.WindowNumber);

        /// <summary>The edge or corner at <paramref name="pointer"/>, in AppKit's screen space, by the current style.</summary>
        private ResizeEdge BandAt(NSPoint pointer)
        {
            var frame = ScreenFrame;
            return EdgeHandleBand.At(pointer.X - frame.X, frame.Y + frame.Height - pointer.Y, frame.Width, frame.Height, Style);
        }

        /// <summary>An event from the content view's tracking area (<paramref name="kind"/>: enter, move or exit).</summary>
        internal void OnTracking(string kind)
        {
            if (_window == IntPtr.Zero)
                return;

            var pointer = MouseLocation();
            if (Hover.Mode == EdgeHoverMode.Probe)
                Logger.Information("Edge probe: tracking {Kind} at ({X:0.#}, {Y:0.#}), window {Frame}", kind, pointer.X, pointer.Y, Frame);

            var undecided = Hover.Source == EdgeHoverSource.Undecided;
            if (!Hover.Tracked())
                return;
            if (undecided)
                Logger.Information("Desktop windows' edge handles follow the pointer through their tracking areas");
            if (!_visible)
                return;

            _handles.Hover(kind == "exit" ? ResizeEdge.None : BandAt(pointer), Now);
            DrawHandle();
        }

        /// <summary>
        /// A poll of the pointer's place (<see cref="PollPointer"/>): <paramref name="inside"/> the window with none above
        /// it there or not; the handle follows it when <paramref name="drives"/>. The probe logs each change.
        /// </summary>
        internal void OnPolled(bool inside, NSPoint pointer, bool drives, TimeSpan now)
        {
            var band = inside ? BandAt(pointer) : ResizeEdge.None;
            var (wasInside, wasBand) = (_handles.PolledInside, _handles.PolledBand);
            if (Hover.Mode == EdgeHoverMode.Probe && (inside != wasInside || band != wasBand || inside))
            {
                var kind = inside == wasInside ? "move" : inside ? "enter" : "exit";
                Logger.Information("Edge probe: poll {Kind} at ({X:0.#}, {Y:0.#}), band {Band}, window {Frame}", kind, pointer.X, pointer.Y, band, Frame);
            }

            if (_handles.Polled(inside, band, drives, now))
                DrawHandle();
        }

        /// <summary>
        /// Draws the handle as <see cref="_handles"/> has it now: the shape for its edge and the window's size, at its
        /// opacity, without implicit animations; while it fades, again about every 16 ms.
        /// </summary>
        private void DrawHandle()
        {
            if (_handleLayer == IntPtr.Zero)
                return;

            var now = Now;
            var opacity = (float)_handles.Opacity(now);
            var frame = Frame;
            var shape = (_handles.Edge, frame.Width, frame.Height, _scale);
            var reshape = opacity > 0 && shape != _handleShape;
            if (reshape || opacity != _handleOpacity)
            {
                var transaction = Sel.CATransaction;
                ObjC.SendVoid(transaction, Sel.Begin);
                ObjC.SendVoidBool(transaction, Sel.SetDisableActions, 1);
                if (reshape)
                {
                    // Core Animation's layer space here is y up from the bottom-left; the polygon's is y down.
                    var path = ObjC.CGPathCreateMutable();
                    var points = EdgeHandleLayout.Polygon(shape.Edge, frame.Width, frame.Height);
                    for (var i = 0; i < points.Count; i++)
                    {
                        var (x, y) = points[i];
                        if (i == 0)
                            ObjC.CGPathMoveToPoint(path, IntPtr.Zero, x, frame.Height - y);
                        else
                            ObjC.CGPathAddLineToPoint(path, IntPtr.Zero, x, frame.Height - y);
                    }

                    if (points.Count > 0)
                        ObjC.CGPathCloseSubpath(path);
                    ObjC.SendVoidRect(_handleLayer, Sel.SetFrameRect, new NSRect(0, 0, frame.Width, frame.Height));
                    ObjC.SendVoidDouble(_handleLayer, Sel.SetContentsScale, _scale > 0 ? _scale : 1);
                    ObjC.SendVoidId(_handleLayer, Sel.SetPath, path);
                    ObjC.CGPathRelease(path);

                    // A hairline of one backing pixel each side of the outline: one pixel shows at the window's edge.
                    ObjC.SendVoidDouble(_handleLayer, Sel.SetLineWidth, 2 / (_scale > 0 ? _scale : 1));
                    _handleShape = shape;
                }

                ObjC.SendVoidFloat(_handleLayer, Sel.SetOpacity, opacity);
                ObjC.SendVoid(transaction, Sel.Commit);
                _handleOpacity = opacity;
            }

            if (_handles.Fading(now) && !_handleFramePending)
            {
                _handleFramePending = true;
                _ = Task.Delay(TimeSpan.FromMilliseconds(16)).ContinueWith(_ => _platform.Post(() =>
                {
                    _handleFramePending = false;
                    DrawHandle();
                }), TaskScheduler.Default);
            }
        }

        public void ShowFrame(DesktopFrame frame)
        {
            if (_window == IntPtr.Zero)
                return;

            IntPtr data;
            fixed (byte* pixels = frame.Pixels)
                data = ObjC.CFDataCreate(IntPtr.Zero, pixels, (nint)frame.RowBytes * frame.Height);
            var provider = ObjC.CGDataProviderCreateWithCFData(data);
            ObjC.CFRelease(data);
            var image = ObjC.CGImageCreate((nuint)frame.Width, (nuint)frame.Height, 8, 32, (nuint)frame.RowBytes, SrgbSpace,
                ObjC.RgbaPremultiplied, provider, IntPtr.Zero, 0, 0);
            ObjC.CGDataProviderRelease(provider);
            if (image == IntPtr.Zero)
                return;

            var transaction = Sel.CATransaction;
            ObjC.SendVoid(transaction, Sel.Begin);
            ObjC.SendVoidBool(transaction, Sel.SetDisableActions, 1);
            var rescaled = frame.Scale != _frameScale;
            if (rescaled)
            {
                // A frame rendered smaller (over the frame pixel cap) is stretched to fill the window.
                _frameScale = frame.Scale;
                ObjC.SendVoidDouble(_layer, Sel.SetContentsScale, _scale * _frameScale);
            }

            (_frameWidth, _frameHeight) = (frame.Width, frame.Height);
            ApplyLayerScaling();
            ObjC.SendVoidId(_layer, Sel.SetContents, image);
            ObjC.SendVoid(transaction, Sel.Commit);
            ObjC.CGImageRelease(image);

            if (rescaled)
            {
                var (contentsScale, filter) = LayerScaling;
                Logger.Information("A desktop window shows frames rendered at {FrameScale}× at layer contents scale {ContentsScale}, {Filter:l} magnification",
                    _frameScale, contentsScale, filter);
            }
        }

        /// <summary>The layer's contents scale and magnification filter, read back from Core Animation.</summary>
        internal (double ContentsScale, string? Filter) LayerScaling =>
            (ObjC.SendDouble(_layer, Sel.ContentsScale), ObjC.ToManagedString(ObjC.Send(_layer, Sel.MagnificationFilter)));

        /// <summary>
        /// The layer's gravity and filters, from the last frame against the window's size in backing pixels
        /// (<see cref="DesktopFrameBlit.Fits"/>): a frame that fits shows one frame pixel per backing pixel, top-left,
        /// nearest (magnified linearly when rendered below scale 1, by the contents scale); any other (one rendered before
        /// a resize) is stretched over the whole window, filtered linearly, until one that fits arrives. Set only on a
        /// change, without implicit animations. Runs for each frame, each live-resize step, <see cref="SetFrame"/> and a
        /// change of backing scale.
        /// </summary>
        private void ApplyLayerScaling()
        {
            if (_layer == IntPtr.Zero || _frameWidth <= 0 || _frameHeight <= 0)
                return;

            var window = Frame;
            var fits = DesktopFrameBlit.Fits(_frameWidth, _frameHeight, _frameScale,
                (int)Math.Round(window.Width * _scale), (int)Math.Round(window.Height * _scale));
            var scaling = fits
                ? (TopLeftGravity, _frameScale < 1 ? LinearFilter : NearestFilter, NearestFilter)
                : (ResizeGravity, LinearFilter, LinearFilter);
            if (scaling == _layerScaling)
                return;

            _layerScaling = scaling;
            var transaction = Sel.CATransaction;
            ObjC.SendVoid(transaction, Sel.Begin);
            ObjC.SendVoidBool(transaction, Sel.SetDisableActions, 1);
            ObjC.SendVoidId(_layer, Sel.SetContentsGravity, scaling.Item1);
            ObjC.SendVoidId(_layer, Sel.SetMagnificationFilter, scaling.Item2);
            ObjC.SendVoidId(_layer, Sel.SetMinificationFilter, scaling.Item3);
            ObjC.SendVoid(transaction, Sel.Commit);
        }

        public void Dispose()
        {
            if (_window == IntPtr.Zero)
                return;

            ObjC.SendVoidId(_window, ObjC.Sel("orderOut:"), IntPtr.Zero);
            ObjC.SendVoidId(_window, ObjC.Sel("setDelegate:"), IntPtr.Zero);
            Windows.Remove(_window);
            ObjC.SendVoid(_window, ObjC.Sel("close"));
            ObjC.SendVoid(_window, Sel.Release);
            ObjC.SendVoid(_handleLayer, Sel.Release);
            ObjC.SendVoid(_layer, Sel.Release);
            _window = IntPtr.Zero;
            _handleLayer = IntPtr.Zero;
            _layer = IntPtr.Zero;
            _visible = false;
        }

        /// <summary>
        /// A move while the left button went down on this window is the user's (reported at mouse-up by
        /// <see cref="CheckMouseUp"/>); during a live resize it is part of the resize; any other move (AppKit moving
        /// windows off a display that went away) is not reported.
        /// </summary>
        internal void OnMoved()
        {
            if (_settingFrame || _window == IntPtr.Zero)
                return;

            if (ObjC.SendBool(_window, Sel.InLiveResize) != 0)
            {
                UserResized?.Invoke();
                return;
            }

            if (_mouseDown || LeftButtonDown())
            {
                _handles.Moved(Now); // a drag of the window, not of an edge
                DrawHandle();
                WaitForMouseUp();
            }
            else
            {
                _knownFrame = Frame;
            }
        }

        /// <summary>
        /// The left button went down on this window (<c>-sendEvent:</c>): a drag may follow, of an edge (the handle stays
        /// shown) or of the window.
        /// </summary>
        internal void OnMouseDown()
        {
            _mouseDown = true;
            _handles.Press(ShowsHandles ? BandAt(MouseLocation()) : ResizeEdge.None, Now);
            DrawHandle();
            WaitForMouseUp();
        }

        private void WaitForMouseUp()
        {
            if (_waitingForMouseUp)
                return;
            _waitingForMouseUp = true;
            CheckMouseUp();
        }

        private static bool LeftButtonDown() => (ObjC.SendUIntRet(ObjC.Class("NSEvent"), Sel.PressedMouseButtons) & 1) != 0;

        /// <summary>A drag ends when the left button is up; until then this checks again every <see cref="MouseUpPoll"/>.</summary>
        private void CheckMouseUp()
        {
            if (_window == IntPtr.Zero)
            {
                _waitingForMouseUp = false;
                _mouseDown = false;
                return;
            }

            if (LeftButtonDown())
            {
                _ = Task.Delay(MouseUpPoll).ContinueWith(_ => _platform.Post(CheckMouseUp), TaskScheduler.Default);
                return;
            }

            _waitingForMouseUp = false;
            _mouseDown = false;
            _knownFrame = Frame;
            _handles.Release(Now);
            DrawHandle();
            UserMoved?.Invoke();
        }

        internal void OnResized()
        {
            if (_settingFrame || _window == IntPtr.Zero)
                return;

            ApplyLayerScaling(); // a live-resize step: the picture stretched with the edge
            var frame = Frame;
            if (frame != _knownFrame && ObjC.SendBool(_window, Sel.InLiveResize) != 0)
                _handles.Resized(EdgeHandleBand.Moved(_knownFrame, frame), Now, ShowsHandles);
            DrawHandle(); // laid out again at the new size
            if (frame == _knownFrame)
                return;
            _knownFrame = frame;
            UserResized?.Invoke();
        }

        internal void OnBackingChanged()
        {
            if (_window == IntPtr.Zero)
                return;
            if (!UpdateScale())
                return;
            ApplyLayerScaling();
            BackingScaleChanged?.Invoke();
        }

        /// <summary>Reads the window's backing scale; one frame pixel per backing pixel at frame scale 1. True when it changed.</summary>
        private bool UpdateScale()
        {
            var scale = ObjC.SendDouble(_window, ObjC.Sel("backingScaleFactor"));
            if (scale <= 0)
                scale = 1;
            if (scale == _scale)
                return false;

            _scale = scale;
            ObjC.SendVoidDouble(_layer, Sel.SetContentsScale, scale * _frameScale);
            ObjC.SendVoidSize(_window, ObjC.Sel("setContentMinSize:"), new NSSize(1 / scale, 1 / scale));
            var max = InfoPanel.Documents.Workspace.MaxDimension / scale;
            ObjC.SendVoidSize(_window, ObjC.Sel("setContentMaxSize:"), new NSSize(max, max));
            return true;
        }
    }
}
