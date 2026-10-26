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
/// floating level when topmost, on all Spaces and out of window cycling, movable by their background, never key,
/// taking every click (also on transparent pixels); a layer-hosting view whose layer shows the frame as a
/// <c>CGImage</c>, one frame pixel per backing pixel, anchored top-left, nearest-neighbour, without implicit
/// animations.</item>
/// <item>User moves are reported once, when the mouse button is up again; moves made by <see cref="IDesktopWindow.SetFrame"/>
/// never are.</item>
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

    /// <summary>How often a moved window checks whether the mouse button is up again.</summary>
    private static readonly TimeSpan MouseUpPoll = TimeSpan.FromMilliseconds(50);

    private static readonly Serilog.ILogger Logger = Log.ForContext<MacDesktopPlatform>();

    private static readonly Dictionary<IntPtr, MacWindow> Windows = [];
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
            displays.Add(new DisplayInfo(name, ToTopLeft(frame, primaryHeight), scale > 0 ? scale : 1));
        }

        return displays;
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
                ("mouseDownCanMoveWindow", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&Yes0, "c@:"));
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
        public static readonly IntPtr Object = ObjC.Sel("object");
        public static readonly IntPtr SetFrameDisplay = ObjC.Sel("setFrame:display:");
        public static readonly IntPtr InLiveResize = ObjC.Sel("inLiveResize");
        public static readonly IntPtr PressedMouseButtons = ObjC.Sel("pressedMouseButtons");
        public static readonly IntPtr SetContents = ObjC.Sel("setContents:");
        public static readonly IntPtr SetContentsScale = ObjC.Sel("setContentsScale:");
        public static readonly IntPtr Begin = ObjC.Sel("begin");
        public static readonly IntPtr Commit = ObjC.Sel("commit");
        public static readonly IntPtr SetDisableActions = ObjC.Sel("setDisableActions:");
        public static readonly IntPtr IsVisible = ObjC.Sel("isVisible");
        public static readonly IntPtr Type = ObjC.Sel("type");
        public static readonly IntPtr CATransaction = ObjC.Class("CATransaction");
    }

    private sealed class MacWindow : IDesktopWindow
    {
        private static readonly IntPtr SrgbSpace = ObjC.CGColorSpaceCreateWithName(ObjC.Constant("CoreGraphics", "kCGColorSpaceSRGB"));

        private readonly MacDesktopPlatform _platform;
        private IntPtr _window;
        private IntPtr _layer;
        private PointRect _knownFrame;
        private double _scale;
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
            ObjC.SendVoidBool(_window, ObjC.Sel("setIgnoresMouseEvents:"), 0);
            ObjC.SendVoidUInt(_window, ObjC.Sel("setCollectionBehavior:"), CollectionBehavior);
            ObjC.SendVoidBool(_window, ObjC.Sel("setMovableByWindowBackground:"), 1);
            ObjC.SendVoidInt(_window, ObjC.Sel("setAnimationBehavior:"), 2); // NSWindowAnimationBehaviorNone

            // A layer-hosting view: AppKit never draws into a layer it didn't create.
            _layer = ObjC.Send(ObjC.Send(ObjC.Class("CALayer"), ObjC.Sel("layer")), Sel.Retain);
            ObjC.SendVoidId(_layer, ObjC.Sel("setContentsGravity:"), ObjC.Constant("QuartzCore", "kCAGravityTopLeft"));
            var nearest = ObjC.Constant("QuartzCore", "kCAFilterNearest");
            ObjC.SendVoidId(_layer, ObjC.Sel("setMagnificationFilter:"), nearest);
            ObjC.SendVoidId(_layer, ObjC.Sel("setMinificationFilter:"), nearest);
            ObjC.SendVoidBool(_layer, ObjC.Sel("setOpaque:"), 0);

            var view = ObjC.SendIdRect(ObjC.Send(_viewClass, Sel.Alloc), ObjC.Sel("initWithFrame:"), new NSRect(0, 0, rect.Width, rect.Height));
            ObjC.SendVoidId(view, ObjC.Sel("setLayer:"), _layer);
            ObjC.SendVoidBool(view, ObjC.Sel("setWantsLayer:"), 1);
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

            ObjC.SendVoidInt(_window, ObjC.Sel("setLevel:"), style.Topmost ? FloatingWindowLevel : NormalWindowLevel);
            ObjC.SendVoidBool(_window, ObjC.Sel("setMovable:"), style.Movable ? (byte)1 : (byte)0);
        }

        public void SetVisible(bool visible)
        {
            if (_window == IntPtr.Zero || visible == _visible)
                return;
            _visible = visible;
            if (visible)
                ObjC.SendVoid(_window, ObjC.Sel("orderFrontRegardless"));
            else
                ObjC.SendVoidId(_window, ObjC.Sel("orderOut:"), IntPtr.Zero);
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
            ObjC.SendVoidId(_layer, Sel.SetContents, image);
            ObjC.SendVoid(transaction, Sel.Commit);
            ObjC.CGImageRelease(image);
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
            var frame = Frame;
            if (frame == _knownFrame)
                return;

            _knownFrame = frame;
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

        /// <summary>Reads the window's backing scale; one frame pixel per backing pixel. True when it changed.</summary>
        private bool UpdateScale()
        {
            var scale = ObjC.SendDouble(_window, ObjC.Sel("backingScaleFactor"));
            if (scale <= 0)
                scale = 1;
            if (scale == _scale)
                return false;

            _scale = scale;
            ObjC.SendVoidDouble(_layer, Sel.SetContentsScale, scale);
            ObjC.SendVoidSize(_window, ObjC.Sel("setContentMinSize:"), new NSSize(1 / scale, 1 / scale));
            var max = InfoPanel.Editing.Workspace.MaxDimension / scale;
            ObjC.SendVoidSize(_window, ObjC.Sel("setContentMaxSize:"), new NSSize(max, max));
            return true;
        }
    }
}
