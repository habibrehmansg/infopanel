using System.Runtime.Versioning;
using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using Xunit;
using static InfoPanel.Tests.Desktop.Windows.WinUiThreadTests;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// Click-through on Windows: <c>WS_EX_TRANSPARENT</c> on the layered window, so clicks go to the windows beneath it.
/// <list type="bullet">
/// <item>The extended style's one bit, set and cleared alone (on any OS: no Win32 in it).</item>
/// <item>With hidden windows (Windows only, any session, over SSH too): created with it, then turned off and on live with
/// the level unchanged; the other extended styles kept, a top window still topmost, a desktop-level window still directly
/// above the desktop (a hidden test window plays the shell's desktop window D, as in <see cref="WinDesktopLevelTests"/>).</item>
/// </list>
/// </summary>
[Collection(DesktopZOrderCollection.Name)]
public sealed class WinClickThroughTests
{
    private const string OnlyOnWindows = "Windows' extended window styles exist only on Windows.";

    private const uint Transparent = 0x00000020, Layered = 0x00080000, ToolWindow = 0x00000080, NoActivate = 0x08000000, Topmost = 0x00000008;

    private static DesktopWindowStyle At(WindowLevel level, bool clickThrough = false) => new(level, true, true, clickThrough);

    private static PointRect Frame => new(-20_000, -20_000, 40, 30);

#pragma warning disable CA1416 // Bit arithmetic and a window never created, its calls faked: no Win32 call, so these run on any OS.
    [Theory]
    [InlineData(Layered | ToolWindow | NoActivate)]
    [InlineData(Layered | ToolWindow | NoActivate | Topmost)]
    [InlineData(Layered | ToolWindow | NoActivate | Topmost | 0x00000100 /* WS_EX_WINDOWEDGE, another bit the system may hold */)]
    public void WithClickThrough_SetsAndClearsOnlyItsBit(uint exStyle)
    {
        var on = WinWindow.WithClickThrough(exStyle, true);
        Assert.Equal(exStyle | Transparent, on);
        Assert.Equal(on, WinWindow.WithClickThrough(on, true));

        var off = WinWindow.WithClickThrough(on, false);
        Assert.Equal(exStyle, off);
        Assert.Equal(off, WinWindow.WithClickThrough(off, false));
    }

    // A change whose native calls fail isn't recorded: the window keeps reporting the old value, so the next change or
    // reconcile (the style then differs) tries again. A window never created, its calls over a style in memory (any OS).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedWrite_IsNotRecorded_AndTheNextApplyTriesAgain(bool from)
    {
        var calls = new FaultyCalls { ExStyle = WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate | Topmost, from), SetFaults = 1 };
        var window = new WinWindow(At(WindowLevel.Top, from), handle: 1, calls);

        window.SetStyle(At(WindowLevel.Top, !from));
        Assert.Equal(from, window.Style.ClickThrough);
        Assert.Equal(from ? Transparent : 0, calls.ExStyle & Transparent);
        Assert.Equal((1, 0), (calls.Sets, calls.Flushes));

        window.SetStyle(At(WindowLevel.Top, !from));
        Assert.Equal(!from, window.Style.ClickThrough);
        Assert.Equal(WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate | Topmost, !from), calls.ExStyle);
        Assert.Equal((2, 1), (calls.Sets, calls.Flushes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedFlush_IsNotRecorded_AndTheNextApplyTriesAgain(bool from)
    {
        var calls = new FaultyCalls { ExStyle = WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, from), FlushFaults = 1 };
        var window = new WinWindow(At(WindowLevel.Normal, from), handle: 1, calls);

        window.SetStyle(At(WindowLevel.Normal, !from));
        Assert.Equal(from, window.Style.ClickThrough);
        Assert.Equal((1, 1), (calls.Sets, calls.Flushes));

        window.SetStyle(At(WindowLevel.Normal, !from));
        Assert.Equal(!from, window.Style.ClickThrough);
        Assert.Equal(WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, !from), calls.ExStyle);
        Assert.Equal((2, 2), (calls.Sets, calls.Flushes));

        // Applied: the same style again asks nothing.
        window.SetStyle(At(WindowLevel.Normal, !from));
        Assert.Equal((2, 2), (calls.Sets, calls.Flushes));
    }

    // After a failed flush the native bit may already hold the new value: until a request is written and flushed, every
    // change writes its value, the opposite request (the window still reports it) and the same style again included.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedFlush_ThenTheOppositeRequest_LeavesTheBitAsRequested(bool from)
    {
        var calls = new FaultyCalls { ExStyle = WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, from), FlushFaults = 1 };
        var window = new WinWindow(At(WindowLevel.Normal, from), handle: 1, calls);

        window.SetStyle(At(WindowLevel.Normal, !from));
        Assert.Equal(from, window.Style.ClickThrough);
        Assert.Equal(!from ? Transparent : 0, calls.ExStyle & Transparent);

        // Back to what the window reports: still written, so the bit ends as requested.
        window.SetStyle(At(WindowLevel.Normal, from));
        Assert.Equal(from, window.Style.ClickThrough);
        Assert.Equal(WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, from), calls.ExStyle);
        Assert.Equal((2, 2), (calls.Sets, calls.Flushes));

        // Applied: the same style again asks nothing.
        window.SetStyle(At(WindowLevel.Normal, from));
        Assert.Equal((2, 2), (calls.Sets, calls.Flushes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedFlush_ThenTheSameStyleAgain_WritesItAgain(bool from)
    {
        var calls = new FaultyCalls { ExStyle = WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, from), FlushFaults = 1 };
        var window = new WinWindow(At(WindowLevel.Normal, from), handle: 1, calls);

        window.SetStyle(At(WindowLevel.Normal, !from));

        // A reconcile asks the style the window reports: written and flushed again, the bit as reported.
        window.SetStyle(window.Style);
        Assert.Equal(WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, from), calls.ExStyle);
        Assert.Equal((2, 2), (calls.Sets, calls.Flushes));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnotherChange_WhileUncertain_WritesClickThroughToo(bool from)
    {
        var calls = new FaultyCalls { ExStyle = WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, from), FlushFaults = 1 };
        var window = new WinWindow(At(WindowLevel.Normal, from), handle: 1, calls);

        window.SetStyle(At(WindowLevel.Normal, !from));

        // Only movable changes (the level would call Win32; that case runs on a real window below): click-through is
        // written with it, the bit as the window reports.
        window.SetStyle(new DesktopWindowStyle(WindowLevel.Normal, false, true, from));
        Assert.Equal((WindowLevel.Normal, false, from), (window.Style.Level, window.Style.Movable, window.Style.ClickThrough));
        Assert.Equal(WinWindow.WithClickThrough(Layered | ToolWindow | NoActivate, from), calls.ExStyle);
        Assert.Equal((2, 2), (calls.Sets, calls.Flushes));
    }

    [Fact]
    public void AWriteReturningZero_IsAFailureOnlyWithAnError()
    {
        // SetWindowLongPtrW returns the previous value: 0 with no error is a success.
        var calls = new FaultyCalls { ExStyle = Layered | ToolWindow | NoActivate | Transparent, SetReturnsZero = true };
        var window = new WinWindow(At(WindowLevel.Normal, clickThrough: true), handle: 1, calls);

        window.SetStyle(At(WindowLevel.Normal));
        Assert.False(window.Style.ClickThrough);
        Assert.Equal((1, 1), (calls.Sets, calls.Flushes));
    }

    /// <summary>
    /// The native click-through calls over a style in memory, or over <see cref="Inner"/> (a real window's), each failing
    /// as many times as asked, as Win32 fails: 0 and an error.
    /// </summary>
    private sealed class FaultyCalls : WinWindow.IExStyleCalls
    {
        private const int AccessDenied = 5, InvalidWindowHandle = 1400;

        public WinWindow.IExStyleCalls? Inner { get; init; }
        public uint ExStyle { get; set; }
        public int SetFaults { get; set; }
        public int FlushFaults { get; set; }
        public bool SetReturnsZero { get; init; }
        public int Sets { get; private set; }
        public int Flushes { get; private set; }

        public (nint Result, int Error) Get(nint hwnd) => Inner?.Get(hwnd) ?? ((nint)ExStyle, 0);

        public (nint Result, int Error) Set(nint hwnd, nint exStyle)
        {
            Sets++;
            if (SetFaults > 0)
            {
                SetFaults--;
                return (0, AccessDenied);
            }

            if (Inner is { } inner)
                return inner.Set(hwnd, exStyle);
            var previous = ExStyle;
            ExStyle = (uint)exStyle;
            return (SetReturnsZero ? 0 : (nint)previous, 0);
        }

        public (int Result, int Error) Flush(nint hwnd)
        {
            Flushes++;
            if (FlushFaults > 0)
            {
                FlushFaults--;
                return (0, InvalidWindowHandle);
            }

            return Inner?.Flush(hwnd) ?? (1, 0);
        }
    }
#pragma warning restore CA1416

    [Fact]
    public async Task AFailedChange_OnAWindow_IsNotRecorded_AndTheNextApplyTriesAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await FailedChanges();
    }

    [SupportedOSPlatform("windows")]
    private static async Task FailedChanges()
    {
        var thread = new WinUiThread();
        try
        {
            uint ExStyle(WinWindow w) => WinTestInterop.Read(w.Handle).ExStyle;
            const uint Kept = Layered | ToolWindow | NoActivate;
            var calls = new FaultyCalls { Inner = WinWindow.Win32ExStyleCalls.Instance };
            var top = await On(thread, () => new WinWindow(At(WindowLevel.Top, clickThrough: true), Frame) { ClickThroughCalls = calls });

            // Off, the write failing: still on, in the window and as it reports; then off.
            calls.SetFaults = 1;
            await On(thread, () => top.SetStyle(At(WindowLevel.Top)));
            Assert.True(top.Style.ClickThrough);
            Assert.Equal(Kept | Topmost | Transparent, ExStyle(top) & (Kept | Topmost | Transparent));
            await On(thread, () => top.SetStyle(At(WindowLevel.Top)));
            Assert.False(top.Style.ClickThrough);
            Assert.Equal(Kept | Topmost, ExStyle(top) & (Kept | Topmost | Transparent));

            // On, the flush failing: reported off; then on.
            calls.FlushFaults = 1;
            await On(thread, () => top.SetStyle(At(WindowLevel.Top, clickThrough: true)));
            Assert.False(top.Style.ClickThrough);
            await On(thread, () => top.SetStyle(At(WindowLevel.Top, clickThrough: true)));
            Assert.True(top.Style.ClickThrough);
            Assert.Equal(Kept | Topmost | Transparent, ExStyle(top) & (Kept | Topmost | Transparent));

            // Off with a level change, the write failing: the level applied, click-through still on; then off.
            calls.SetFaults = 1;
            await On(thread, () => top.SetStyle(At(WindowLevel.Normal)));
            Assert.Equal((WindowLevel.Normal, true), (top.Style.Level, top.Style.ClickThrough));
            Assert.Equal(Kept | Transparent, ExStyle(top) & (Kept | Topmost | Transparent));
            await On(thread, () => top.SetStyle(At(WindowLevel.Normal)));
            Assert.False(top.Style.ClickThrough);
            Assert.Equal(Kept, ExStyle(top) & (Kept | Topmost | Transparent));

            // On, the flush failing: the bit already set, reported off; then a level change alone (click-through off, as
            // reported) clears the bit with it.
            calls.FlushFaults = 1;
            await On(thread, () => top.SetStyle(At(WindowLevel.Normal, clickThrough: true)));
            Assert.False(top.Style.ClickThrough);
            Assert.Equal(Kept | Transparent, ExStyle(top) & (Kept | Topmost | Transparent));
            await On(thread, () => top.SetStyle(At(WindowLevel.Top)));
            Assert.Equal((WindowLevel.Top, false), (top.Style.Level, top.Style.ClickThrough));
            Assert.Equal(Kept | Topmost, ExStyle(top) & (Kept | Topmost | Transparent));

            // Off, the flush failing: the bit already cleared, reported on; then on again (as reported) sets it.
            await On(thread, () => top.SetStyle(At(WindowLevel.Top, clickThrough: true)));
            calls.FlushFaults = 1;
            await On(thread, () => top.SetStyle(At(WindowLevel.Top)));
            Assert.True(top.Style.ClickThrough);
            Assert.Equal(Kept | Topmost, ExStyle(top) & (Kept | Topmost | Transparent));
            await On(thread, () => top.SetStyle(At(WindowLevel.Top, clickThrough: true)));
            Assert.True(top.Style.ClickThrough);
            Assert.Equal(Kept | Topmost | Transparent, ExStyle(top) & (Kept | Topmost | Transparent));

            await On(thread, top.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ClickThrough_IsSetAtCreation_AndTurnedOffAndOnLive_KeepingTheOtherStylesAndTheLevel()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await LiveToggles();
    }

    [SupportedOSPlatform("windows")]
    private static async Task LiveToggles()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            var b = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            uint ExStyle(WinWindow w) => WinTestInterop.Read(w.Handle).ExStyle;
            const uint Kept = Layered | ToolWindow | NoActivate;

            // Off by default; on at creation.
            var normal = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            Assert.Equal((Kept, 0u), (ExStyle(normal) & Kept, ExStyle(normal) & Transparent));
            var top = await On(thread, () => new WinWindow(At(WindowLevel.Top, clickThrough: true), Frame));
            Assert.Equal(Kept | Topmost | Transparent, ExStyle(top) & (Kept | Topmost | Transparent));

            // A top window, the level unchanged: off, then on again, live; still topmost and layered.
            await On(thread, () => top.SetStyle(At(WindowLevel.Top)));
            Assert.Equal(Kept | Topmost, ExStyle(top) & (Kept | Topmost | Transparent));
            await On(thread, () => top.SetStyle(At(WindowLevel.Top, clickThrough: true)));
            Assert.Equal(Kept | Topmost | Transparent, ExStyle(top) & (Kept | Topmost | Transparent));

            // A normal window: on and off live, never topmost.
            await On(thread, () => normal.SetStyle(At(WindowLevel.Normal, clickThrough: true)));
            Assert.Equal(Kept | Transparent, ExStyle(normal) & (Kept | Topmost | Transparent));
            await On(thread, () => normal.SetStyle(At(WindowLevel.Normal)));
            Assert.Equal(Kept, ExStyle(normal) & (Kept | Topmost | Transparent));

            // A desktop-level window: a toggle keeps it directly above D and under B, not topmost, tracked.
            var w = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            await On(thread, level.PlaceAll);
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            await On(thread, () => w.SetStyle(At(WindowLevel.Desktop, clickThrough: true)));
            Assert.Equal(Kept | Transparent, ExStyle(w) & (Kept | Topmost | Transparent));
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(WinTestInterop.IsAbove(b.Handle, w.Handle), "B is still above the desktop-level window");
            Assert.True(level.Hooked);
            await On(thread, () => w.SetStyle(At(WindowLevel.Desktop)));
            Assert.Equal(Kept, ExStyle(w) & (Kept | Topmost | Transparent));
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));

            // A level change and click-through together: both applied.
            await On(thread, () => w.SetStyle(At(WindowLevel.Top, clickThrough: true)));
            Assert.Equal(Kept | Topmost | Transparent, ExStyle(w) & (Kept | Topmost | Transparent));
            Assert.False(level.Hooked);

            foreach (var window in new[] { w, top, normal, b, d })
                await On(thread, window.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }
}
