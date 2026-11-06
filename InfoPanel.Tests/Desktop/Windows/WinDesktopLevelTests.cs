using System.Runtime.Versioning;
using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using Xunit;
using static InfoPanel.Tests.Desktop.Windows.WinUiThreadTests;

namespace InfoPanel.Tests.Desktop.Windows;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DesktopZOrderCollection
{
    /// <summary>
    /// Tests that read the desktop's z-order exactly, one at a time and apart from the other tests' windows: a window
    /// another test creates between the rule's read and its <c>SetWindowPos</c> would land between D and the window.
    /// </summary>
    public const string Name = "Desktop z-order";
}

/// <summary>
/// The desktop level on Windows (skipped elsewhere), with hidden windows only, so these run in any session, over SSH
/// too: a hidden test window plays the shell's desktop window D (<see cref="WinDesktopLevel.FindDesktop"/>).
/// <list type="bullet">
/// <item>A desktop-level window is never topmost; an outside raise leaves it where it was (the guard).</item>
/// <item>The rule puts it directly above D, with D raised to the top (Win+D) and with D at the bottom.</item>
/// <item>Each live change between the three levels sets the style and the order.</item>
/// <item>The foreground event is hooked while a desktop-level window exists, and the poll runs while D's thread has
/// the foreground, from the start when the desktop already has it; its timer brings the window back after Win+D.</item>
/// <item>A window above D that turns topmost during the rule leaves no desktop-level window topmost.</item>
/// </list>
/// </summary>
[Collection(DesktopZOrderCollection.Name)]
public sealed class WinDesktopLevelTests
{
    private const string OnlyOnWindows = "The Windows desktop level exists only on Windows.";
    private const uint Topmost = 0x00000008;

    private static DesktopWindowStyle At(WindowLevel level) => new(level, true, true);

    private static PointRect Frame => new(-20_000, -20_000, 40, 30);

    [Fact]
    public async Task AnOutsideRaise_LeavesADesktopWindow_BelowTheWindowAboveIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await Guard();
    }

    [SupportedOSPlatform("windows")]
    private static async Task Guard()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            var w = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            var b = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            await On(thread, level.PlaceAll);
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(WinTestInterop.IsAbove(b.Handle, w.Handle));

            // Another thread raises it, as a click, a drag's move loop or another app would.
            Assert.True(WinTestInterop.SetWindowPos(w.Handle, WinTestInterop.HwndTop, 0, 0, 0, 0,
                WinTestInterop.SwpNoMove | WinTestInterop.SwpNoSize | WinTestInterop.SwpNoActivate));
            await On(thread, () => { });

            Assert.True(WinTestInterop.IsAbove(b.Handle, w.Handle), "B is still above the desktop-level window");
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));

            await On(thread, w.Dispose);
            await On(thread, b.Dispose);
            await On(thread, d.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TheRule_PutsADesktopWindowDirectlyAboveTheDesktop_RaisedToTheTop_OrAtTheBottom()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await Rule();
    }

    [SupportedOSPlatform("windows")]
    private static async Task Rule()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            var w = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            var b = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));

            // Win+D: the desktop raised over the apps.
            await On(thread, () => WinTestInterop.SetWindowPos(d.Handle, WinTestInterop.HwndTop, 0, 0, 0, 0,
                WinTestInterop.SwpNoMove | WinTestInterop.SwpNoSize | WinTestInterop.SwpNoActivate));
            Assert.True(WinTestInterop.IsAbove(d.Handle, b.Handle));
            await On(thread, level.PlaceAll);
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(WinTestInterop.IsAbove(w.Handle, b.Handle), "on the raised desktop, above the apps");

            // Back under the apps: the desktop at the bottom.
            await On(thread, () => WinTestInterop.SetWindowPos(d.Handle, WinTestInterop.HwndBottom, 0, 0, 0, 0,
                WinTestInterop.SwpNoMove | WinTestInterop.SwpNoSize | WinTestInterop.SwpNoActivate));
            await On(thread, level.PlaceAll);
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(WinTestInterop.IsAbove(b.Handle, w.Handle), "under the apps again");

            // Two desktop-level windows: both directly above D, and a second run moves neither.
            var w2 = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            await On(thread, level.PlaceAll);
            var order = (WinTestInterop.Above(d.Handle), WinTestInterop.Above(WinTestInterop.Above(d.Handle)));
            Assert.Contains(w.Handle, new[] { order.Item1, order.Item2 });
            Assert.Contains(w2.Handle, new[] { order.Item1, order.Item2 });
            await On(thread, level.PlaceAll);
            Assert.Equal(order, (WinTestInterop.Above(d.Handle), WinTestInterop.Above(WinTestInterop.Above(d.Handle))));

            foreach (var window in new[] { w, w2, b, d })
                await On(thread, window.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task EachLiveChangeOfLevel_SetsTheStyleAndTheOrder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await LiveChanges();
    }

    [SupportedOSPlatform("windows")]
    private static async Task LiveChanges()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            var w = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame, level));
            var b = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            bool IsTopmost() => (WinTestInterop.Read(w.Handle).ExStyle & Topmost) != 0;

            // normal → desktop: directly above D, under B, tracked.
            await On(thread, () => w.SetStyle(At(WindowLevel.Desktop)));
            Assert.False(IsTopmost());
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(WinTestInterop.IsAbove(b.Handle, w.Handle));
            Assert.True(level.Hooked);

            // desktop → normal: in front of the normal windows, untracked.
            await On(thread, () => w.SetStyle(At(WindowLevel.Normal)));
            Assert.False(IsTopmost());
            Assert.True(WinTestInterop.IsAbove(w.Handle, b.Handle));
            Assert.False(level.Hooked);

            // normal → top → desktop: not topmost any more, directly above D, tracked.
            await On(thread, () => w.SetStyle(At(WindowLevel.Top)));
            Assert.True(IsTopmost());
            await On(thread, () => w.SetStyle(At(WindowLevel.Desktop)));
            Assert.False(IsTopmost());
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(level.Hooked);

            // desktop → top: topmost, untracked; top → normal: not topmost.
            await On(thread, () => w.SetStyle(At(WindowLevel.Top)));
            Assert.True(IsTopmost());
            Assert.False(level.Hooked);
            await On(thread, () => w.SetStyle(At(WindowLevel.Normal)));
            Assert.False(IsTopmost());

            // Created at the desktop level: not topmost, tracked from the start.
            var created = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            Assert.Equal(0u, WinTestInterop.Read(created.Handle).ExStyle & Topmost);
            Assert.True(level.Hooked);

            foreach (var window in new[] { created, w, b, d })
                await On(thread, window.Dispose);
            Assert.False(level.Hooked);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TheForegroundHook_IsThereWhileADesktopWindowIs_AndThePoll_WhileTheDesktopIsForeground()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await HookAndPoll();
    }

    [SupportedOSPlatform("windows")]
    private static async Task HookAndPoll()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            Assert.False(level.Hooked);

            var w1 = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            var w2 = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            Assert.True(level.Hooked);

            // The desktop's thread has the foreground (the wallpaper clicked): the poll runs, until another window has it.
            Assert.False(level.Polling);
            await On(thread, () => level.OnForeground(d.Handle));
            Assert.True(level.Polling);
            await On(thread, () => level.OnForeground(0));
            Assert.False(level.Polling);
            await On(thread, () => level.OnForeground(d.Handle));
            Assert.True(level.Polling);

            // One closed, one changed level: none left, so no hook and no poll.
            await On(thread, w1.Dispose);
            Assert.True(level.Hooked);
            await On(thread, () => w2.SetStyle(At(WindowLevel.Normal)));
            Assert.False(level.Hooked);
            Assert.False(level.Polling);

            await On(thread, w2.Dispose);
            await On(thread, d.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AWindowAboveTheDesktopTurningTopmost_DuringTheRule_LeavesNoDesktopWindowTopmost()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await TopmostRace();
    }

    [SupportedOSPlatform("windows")]
    private static async Task TopmostRace()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            // A topmost window already there, as the taskbar is: A, made topmost, goes above it, and so would W.
            var t = await On(thread, () => new WinWindow(At(WindowLevel.Top), Frame));
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            var a = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            var w = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            var b = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            bool IsTopmost(nint hwnd) => (WinTestInterop.Read(hwnd).ExStyle & Topmost) != 0;
            Assert.Equal(a.Handle, WinTestInterop.Above(d.Handle));

            // A, the window above D in the rule's read, turns topmost before the rule's call (only the first read).
            var reads = 0;
            var read = level.ReadWindows;
            level.ReadWindows = () =>
            {
                var windows = read();
                if (reads++ == 0)
                    WinTestInterop.SetWindowPos(a.Handle, WinTestInterop.HwndTopmost, 0, 0, 0, 0,
                        WinTestInterop.SwpNoMove | WinTestInterop.SwpNoSize | WinTestInterop.SwpNoActivate);
                return windows;
            };
            await On(thread, level.PlaceAll);

            Assert.True(IsTopmost(a.Handle));
            Assert.False(IsTopmost(w.Handle), "the desktop-level window isn't topmost");
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(WinTestInterop.IsAbove(b.Handle, w.Handle), "B is above the desktop-level window");

            foreach (var window in new[] { w, b, a, d, t })
                await On(thread, window.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TrackingBegun_WhileTheDesktopIsForeground_StartsThePoll()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await PollFromTheStart();
    }

    [SupportedOSPlatform("windows")]
    private static async Task PollFromTheStart()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            level.Foreground = () => d.Handle;

            // The wallpaper had the foreground before the first desktop-level window: no foreground event will come.
            var w = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            Assert.True(level.Polling);

            await On(thread, w.Dispose);
            Assert.False(level.Polling);
            await On(thread, d.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ThePollsTimer_BringsADesktopWindowBack_AfterTheDesktopIsRaised()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await PollRestores();
    }

    [SupportedOSPlatform("windows")]
    private static async Task PollRestores()
    {
        var thread = new WinUiThread();
        try
        {
            var level = new WinDesktopLevel(thread);
            var d = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            level.FindDesktop = _ => d.Handle;
            level.Foreground = () => 0;
            var w = await On(thread, () => new WinWindow(At(WindowLevel.Desktop), Frame, level));
            var b = await On(thread, () => new WinWindow(At(WindowLevel.Normal), Frame));
            await On(thread, () => level.OnForeground(d.Handle));
            Assert.True(level.Polling);
            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));

            // Win+D with the desktop already foreground: no foreground event, only the poll's timer messages.
            await On(thread, () => WinTestInterop.SetWindowPos(d.Handle, WinTestInterop.HwndTop, 0, 0, 0, 0,
                WinTestInterop.SwpNoMove | WinTestInterop.SwpNoSize | WinTestInterop.SwpNoActivate));
            Assert.True(WinTestInterop.IsAbove(d.Handle, w.Handle));
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (WinTestInterop.Above(d.Handle) != w.Handle && waited.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(50);

            Assert.Equal(w.Handle, WinTestInterop.Above(d.Handle));
            Assert.True(WinTestInterop.IsAbove(w.Handle, b.Handle), "on the raised desktop, above the apps");

            foreach (var window in new[] { w, b, d })
                await On(thread, window.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }
}
