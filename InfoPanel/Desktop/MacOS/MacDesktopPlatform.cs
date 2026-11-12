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
/// animations (a frame rendered below scale 1 is stretched to fill the window, filtered linearly).</item>
/// <item>A press on a window is reported once, when the mouse button is up again, moved or not; moves made by
/// <see cref="IDesktopWindow.SetFrame"/> never are.</item>
/// <item>AppKit resizes a resizable window but shows no cursor for it (never key, the app never active): the view's
/// tracking area sets the frame resize cursor over <see cref="MacResizeBand"/>'s band and the arrow again after it, one
/// window owning the cursor at a time (<see cref="MacResizeCursorOwner"/>).</item>
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

    /// <summary>Which window's resize cursor is shown, across every window (one pointer, one cursor).</summary>
    private static readonly MacResizeCursorOwner CursorOwner = new();
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
                ("mouseEntered:", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PointerMoved, "v@:@"),
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

    /// <summary>
    /// <c>-[InfoPanelFrameView mouseEntered:]</c> and <c>mouseMoved:</c>, from the view's tracking area: the window's
    /// resize cursor follows the pointer.
    /// </summary>
    [UnmanagedCallersOnly]
    private static void PointerMoved(IntPtr self, IntPtr sel, IntPtr nsEvent) =>
        Guard(() => Windows.GetValueOrDefault(ObjC.Send(self, Sel.Window))?.OnPointerMoved());

    /// <summary><c>-[InfoPanelFrameView mouseExited:]</c>: the window gives up the resize cursor if it has it.</summary>
    [UnmanagedCallersOnly]
    private static void PointerExited(IntPtr self, IntPtr sel, IntPtr nsEvent) =>
        Guard(() => Windows.GetValueOrDefault(ObjC.Send(self, Sel.Window))?.OnPointerExited());

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
        public static readonly IntPtr ContentsScale = ObjC.Sel("contentsScale");
        public static readonly IntPtr MagnificationFilter = ObjC.Sel("magnificationFilter");
        public static readonly IntPtr Begin = ObjC.Sel("begin");
        public static readonly IntPtr Commit = ObjC.Sel("commit");
        public static readonly IntPtr SetDisableActions = ObjC.Sel("setDisableActions:");
        public static readonly IntPtr IsVisible = ObjC.Sel("isVisible");
        public static readonly IntPtr Type = ObjC.Sel("type");
        public static readonly IntPtr CATransaction = ObjC.Class("CATransaction");
        public static readonly IntPtr Window = ObjC.Sel("window");
        public static readonly IntPtr MouseLocation = ObjC.Sel("mouseLocation");
        public static readonly IntPtr FrameResizeCursor = ObjC.Sel("frameResizeCursorFromPosition:inDirections:");
        public static readonly IntPtr ArrowCursor = ObjC.Sel("arrowCursor");
        public static readonly IntPtr Set = ObjC.Sel("set");
        public static readonly IntPtr NSCursor = ObjC.Class("NSCursor");
        public static readonly IntPtr NSEvent = ObjC.Class("NSEvent");
        public static readonly IntPtr NSWindow = ObjC.Class("NSWindow");
        public static readonly IntPtr WindowNumber = ObjC.Sel("windowNumber");
        public static readonly IntPtr WindowNumberAtPoint = ObjC.Sel("windowNumberAtPoint:belowWindowWithWindowNumber:");
    }

    /// <summary>
    /// <c>NSTrackingMouseEnteredAndExited | NSTrackingMouseMoved | NSTrackingActiveAlways | NSTrackingInVisibleRect</c>
    /// (<c>NSTrackingArea.h</c>): pointer events whether or not the app is active (the window is never key), over the
    /// view's visible rect as it resizes. <c>NSTrackingCursorUpdate</c> can't be active always.
    /// </summary>
    private const nuint CursorTracking = 0x01 | 0x02 | 0x80 | 0x200;

    /// <summary><c>NSCursorFrameResizeDirectionsAll</c> (<c>NSCursor.h</c>, macOS 15, the engine's minimum).</summary>
    private const nuint AllDirections = 0x03;

    private sealed class MacWindow : IDesktopWindow
    {
        private static readonly IntPtr SrgbSpace = ObjC.CGColorSpaceCreateWithName(ObjC.Constant("CoreGraphics", "kCGColorSpaceSRGB"));
        private static readonly IntPtr NearestFilter = ObjC.Constant("QuartzCore", "kCAFilterNearest");
        private static readonly IntPtr LinearFilter = ObjC.Constant("QuartzCore", "kCAFilterLinear");

        private readonly MacDesktopPlatform _platform;
        private IntPtr _window;
        private IntPtr _layer;
        private PointRect _knownFrame;
        private double _scale;
        private float _frameScale = 1;
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
            ObjC.SendVoidId(_layer, ObjC.Sel("setContentsGravity:"), ObjC.Constant("QuartzCore", "kCAGravityTopLeft"));
            ObjC.SendVoidId(_layer, Sel.SetMagnificationFilter, NearestFilter);
            ObjC.SendVoidId(_layer, ObjC.Sel("setMinificationFilter:"), NearestFilter);
            ObjC.SendVoidBool(_layer, ObjC.Sel("setOpaque:"), 0);

            var view = ObjC.SendIdRect(ObjC.Send(_viewClass, Sel.Alloc), ObjC.Sel("initWithFrame:"), new NSRect(0, 0, rect.Width, rect.Height));
            ObjC.SendVoidId(view, ObjC.Sel("setLayer:"), _layer);
            ObjC.SendVoidBool(view, ObjC.Sel("setWantsLayer:"), 1);
            var tracking = ObjC.SendInitTrackingArea(ObjC.Send(ObjC.Class("NSTrackingArea"), Sel.Alloc),
                ObjC.Sel("initWithRect:options:owner:userInfo:"), default, CursorTracking, view, IntPtr.Zero);
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

            // Wherever the pointer rests now, whatever cursor was shown before; only if no window covers this one there.
            var pointer = ObjC.SendPoint(Sel.NSEvent, Sel.MouseLocation);
            SetCursor(CursorOwner.Restyled(_window, _visible, BandAt(pointer), _visible && FrontmostAt(pointer)));
        }

        /// <summary>
        /// Whether this window is the one a click at <paramref name="pointer"/> (screen space) would reach: no window, of
        /// any app, above it there (0: the search starts at the frontmost window).
        /// </summary>
        private bool FrontmostAt(NSPoint pointer) =>
            ObjC.SendIntPointInt(Sel.NSWindow, Sel.WindowNumberAtPoint, pointer, 0) == ObjC.SendIntRet(_window, Sel.WindowNumber);

        /// <summary>The pointer entered or moved over the window: its band's resize cursor, or the arrow after it.</summary>
        internal void OnPointerMoved()
        {
            if (_window != IntPtr.Zero)
                SetCursor(CursorOwner.Moved(_window, _visible, BandAt(ObjC.SendPoint(Sel.NSEvent, Sel.MouseLocation))));
        }

        /// <summary>The pointer left the window: the arrow, if this window's resize cursor was shown.</summary>
        internal void OnPointerExited()
        {
            if (_window != IntPtr.Zero)
                SetCursor(CursorOwner.Exited(_window));
        }

        /// <summary>The edge or corner at <paramref name="pointer"/>, in screen space (<see cref="MacResizeBand"/>, by the current style).</summary>
        private MacResizeEdge BandAt(NSPoint pointer)
        {
            // Screen space is y up, as the window's frame: the point from the window's top-left.
            var frame = ObjC.SendRect(_window, Sel.Frame);
            return MacResizeBand.At(pointer.X - frame.X, frame.Y + frame.Height - pointer.Y, frame.Width, frame.Height, Style);
        }

        private static void SetCursor(MacCursorChange change)
        {
            if (change.Kind == MacCursorChangeKind.Resize)
                ObjC.SendVoid(ObjC.SendIdUIntUInt(Sel.NSCursor, Sel.FrameResizeCursor, (nuint)change.Edge, AllDirections), Sel.Set);
            else if (change.Kind == MacCursorChangeKind.Arrow)
                ObjC.SendVoid(ObjC.Send(Sel.NSCursor, Sel.ArrowCursor), Sel.Set);
        }

        public void SetVisible(bool visible)
        {
            if (_window == IntPtr.Zero || visible == _visible)
                return;
            _visible = visible;
            if (visible)
                ObjC.SendVoid(_window, ObjC.Sel("orderFrontRegardless"));
            else
            {
                // Hidden, it gives up the cursor it shows; its late tracking events change nothing (not visible).
                SetCursor(CursorOwner.Hidden(_window));
                ObjC.SendVoidId(_window, ObjC.Sel("orderOut:"), IntPtr.Zero);
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
                ObjC.SendVoidId(_layer, Sel.SetMagnificationFilter, _frameScale < 1 ? LinearFilter : NearestFilter);
            }

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

        public void Dispose()
        {
            if (_window == IntPtr.Zero)
                return;

            SetCursor(CursorOwner.Hidden(_window));
            ObjC.SendVoidId(_window, ObjC.Sel("orderOut:"), IntPtr.Zero);
            ObjC.SendVoidId(_window, ObjC.Sel("setDelegate:"), IntPtr.Zero);
            Windows.Remove(_window);
            ObjC.SendVoid(_window, ObjC.Sel("close"));
            ObjC.SendVoid(_window, Sel.Release);
            ObjC.SendVoid(_layer, Sel.Release);
            _window = IntPtr.Zero;
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
                WaitForMouseUp();
            else
                _knownFrame = Frame;
        }

        /// <summary>The left button went down on this window (<c>-sendEvent:</c>): a drag may follow.</summary>
        internal void OnMouseDown()
        {
            _mouseDown = true;
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
            UserMoved?.Invoke();
        }

        internal void OnResized()
        {
            if (_settingFrame || _window == IntPtr.Zero)
                return;

            var frame = Frame;
            if (frame == _knownFrame)
                return;
            _knownFrame = frame;
            UserResized?.Invoke();
        }

        internal void OnBackingChanged()
        {
            if (_window == IntPtr.Zero)
                return;
            if (UpdateScale())
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
