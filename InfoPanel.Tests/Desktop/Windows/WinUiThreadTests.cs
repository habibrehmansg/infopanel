using System.Diagnostics;
using System.Runtime.Versioning;
using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// The Windows UI thread and its windows on Windows (skipped elsewhere). Nothing is shown: the thread's window and the
/// windows made here stay hidden, so these run in any session, over SSH too.
/// <list type="bullet">
/// <item>The thread: started on first use, an STA thread running posted work in order; work posted while an action runs a
/// modal loop (which dispatches window messages and drops thread messages) still runs, inside that loop; a hook sees the
/// hidden window's messages; under posted work that never stops, a due timer is still served; it stops.</item>
/// <item>A window: exactly a layered tool popup that never activates, topmost when asked, per-monitor DPI aware (v2), as
/// large as 16,384 pixels whatever the screens (its track sizes too), keeping its pixel size when the DPI changes, deaf
/// to <c>WM_CLOSE</c>; a failed update tried again until one succeeds, a retry already waiting then ignored; grown
/// again, the floor where its last frame didn't reach.</item>
/// </list>
/// </summary>
public sealed class WinUiThreadTests
{
    private const string OnlyOnWindows = "The Windows UI thread and windows exist only on Windows.";

    [Fact]
    public async Task TheThread_StartsOnFirstUse_AndRunsPostedWorkInOrder_OnOneStaThread()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await PostedWorkRunsInOrder();
    }

    [SupportedOSPlatform("windows")]
    private static async Task PostedWorkRunsInOrder()
    {
        var thread = new WinUiThread();
        Assert.False(thread.Started);
        try
        {
            var ran = new List<(int N, int Thread, ApartmentState Apartment, bool Access)>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            for (var i = 0; i < 100; i++)
            {
                var n = i;
                thread.Post(() => ran.Add((n, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), thread.CheckAccess())));
            }

            thread.Post(() => done.SetResult());
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(thread.Started);
            Assert.NotEqual(0, thread.Hwnd);
            Assert.False(thread.CheckAccess());
            Assert.Equal(Enumerable.Range(0, 100), ran.Select(r => r.N));
            Assert.Single(ran.Select(r => r.Thread).Distinct());
            Assert.All(ran, r => Assert.Equal(ApartmentState.STA, r.Apartment));
            Assert.All(ran, r => Assert.True(r.Access));
            Assert.True(WinTestInterop.IsPerMonitorV2(thread.Hwnd));
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task WorkPostedWhileAnActionRunsAModalLoop_RunsInsideThatLoop()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await PostedWorkRunsInAModalLoop();
    }

    [SupportedOSPlatform("windows")]
    private static async Task PostedWorkRunsInAModalLoop()
    {
        var thread = new WinUiThread();
        try
        {
            var inLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loopEnded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ranInside = false;
            var looping = false;

            // As a modal loop (a window's move loop, a menu) does: dispatch every message, thread messages included (which
            // reach no window and are dropped), until the work posted meanwhile has run, or 5 s.
            thread.Post(() =>
            {
                looping = true;
                inLoop.SetResult();
                var clock = Stopwatch.StartNew();
                while (!ranInside && clock.Elapsed < TimeSpan.FromSeconds(5))
                {
                    if (WinTestInterop.PeekMessageW(out var msg, IntPtr.Zero, 0, 0, WinTestInterop.PM_REMOVE))
                        WinTestInterop.DispatchMessageW(ref msg);
                    else
                        Thread.Sleep(1);
                }

                looping = false;
                loopEnded.SetResult(ranInside);
            });

            await inLoop.Task.WaitAsync(TimeSpan.FromSeconds(10));
            thread.Post(() => ranInside = looping);

            Assert.True(await loopEnded.Task.WaitAsync(TimeSpan.FromSeconds(10)), "the work posted during the loop didn't run inside it");
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task UnderPostedWorkThatNeverStops_ATimerIsStillServed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await TimerServedUnderLoad();
    }

    /// <summary>
    /// An animated panel's frames: each action posts the next, so a wake is always waiting when the message loop looks
    /// again. Windows takes posted messages ahead of input and timers; the thread still serves a due timer (a failed
    /// update's retry) while the work goes on.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task TimerServedUnderLoad()
    {
        const nuint timer = 77;
        var thread = new WinUiThread();
        var stop = false;
        var actions = 0L;
        var served = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.AddMessageHook((msg, wParam, _) =>
        {
            if (msg != WinTestInterop.WM_TIMER || (nuint)wParam != timer)
                return null;
            served.TrySetResult(!Volatile.Read(ref stop)); // served while the work still goes on
            return 0;
        });

        try
        {
            void Next()
            {
                actions++;
                if (!Volatile.Read(ref stop))
                    thread.Post(Next);
            }

            thread.Post(Next);
            await On(thread, () => WinTestInterop.SetTimer(thread.Hwnd, timer, 10, IntPtr.Zero));

            var duringLoad = await served.Task.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(t => t.IsCompletedSuccessfully && t.Result,
                TaskScheduler.Default);
            var ranMeanwhile = Interlocked.Read(ref actions);
            Volatile.Write(ref stop, true);
            await On(thread, () => WinTestInterop.KillTimer(thread.Hwnd, timer));

            Assert.True(duringLoad, $"a 10 ms timer wasn't served within 2 s while posted work ran ({ranMeanwhile} actions)");
            Assert.True(ranMeanwhile > 100, $"the posted work didn't keep going: {ranMeanwhile} actions");
        }
        finally
        {
            Volatile.Write(ref stop, true);
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AHook_SeesTheHiddenWindowsMessages_AndTheThreadStops()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await HookAndStop();
    }

    [SupportedOSPlatform("windows")]
    private static async Task HookAndStop()
    {
        const uint custom = 0x8000 + 0x123; // WM_APP + 0x123
        var thread = new WinUiThread();
        var seen = new TaskCompletionSource<(nint, nint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.AddMessageHook((msg, wParam, lParam) =>
        {
            if (msg != custom)
                return null;
            seen.TrySetResult((wParam, lParam));
            return 7;
        });

        Assert.Equal((IntPtr)7, WinTestInterop.SendMessageW(thread.Hwnd, custom, 11, 22));
        Assert.Equal(((nint)11, (nint)22), await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        // WM_CLOSE doesn't close it; Stop does.
        WinTestInterop.SendMessageW(thread.Hwnd, WinTestInterop.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Assert.True(WinTestInterop.IsWindow(thread.Hwnd));
        var hwnd = thread.Hwnd;
        thread.Stop(TimeSpan.FromSeconds(5));
        Assert.False(WinTestInterop.IsWindow(hwnd));
    }

    [Fact]
    public async Task AWindow_IsALayeredToolPopup_ThatNeverActivates_OnTopWhenAsked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await WindowStyles();
    }

    /// <summary><c>WS_POPUP</c>, and <c>WS_CLIPSIBLINGS</c>, which Windows adds to every top-level window.</summary>
    private const uint Popup = 0x80000000 | 0x04000000;
    private const uint Layered = 0x00080000, ToolWindow = 0x00000080, NoActivate = 0x08000000, Topmost = 0x00000008;

    [SupportedOSPlatform("windows")]
    private static async Task WindowStyles()
    {
        var thread = new WinUiThread();
        try
        {
            var window = await On(thread, () => new WinWindow(new DesktopWindowStyle(false, true, true), new PointRect(100, 100, 400, 200)));
            var read = WinTestInterop.Read(window.Handle);
            Assert.Equal("InfoPanelDesktopWindow", read.Class);
            Assert.Equal(Popup, read.Style);
            Assert.Equal(Layered | ToolWindow | NoActivate, read.ExStyle);
            Assert.Equal((IntPtr)3, WinTestInterop.SendMessageW(window.Handle, WinTestInterop.WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero)); // MA_NOACTIVATE
            Assert.True(WinTestInterop.IsPerMonitorV2(window.Handle));

            await On(thread, () => window.SetStyle(new DesktopWindowStyle(true, true, true)));
            Assert.Equal(Layered | ToolWindow | NoActivate | Topmost, WinTestInterop.Read(window.Handle).ExStyle);
            await On(thread, () => window.SetStyle(new DesktopWindowStyle(false, true, true)));
            Assert.Equal(Layered | ToolWindow | NoActivate, WinTestInterop.Read(window.Handle).ExStyle);

            var topmost = await On(thread, () => new WinWindow(new DesktopWindowStyle(true, true, true), new PointRect(0, 0, 10, 10)));
            Assert.Equal(Layered | ToolWindow | NoActivate | Topmost, WinTestInterop.Read(topmost.Handle).ExStyle);

            // WM_CLOSE is ignored: a window closes when its profile's window is turned off.
            WinTestInterop.SendMessageW(window.Handle, WinTestInterop.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Assert.True(WinTestInterop.IsWindow(window.Handle));

            var handles = new[] { window.Handle, topmost.Handle };
            await On(thread, window.Dispose);
            await On(thread, topmost.Dispose);
            Assert.All(handles, h => Assert.False(WinTestInterop.IsWindow(h)));
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AWindow_KeepsItsPixelSize_WhenTheDpiChanges_AndGrowsPastEveryScreen()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await WindowSizes();
    }

    [SupportedOSPlatform("windows")]
    private static async Task WindowSizes()
    {
        var thread = new WinUiThread();
        try
        {
            var window = await On(thread, () => new WinWindow(new DesktopWindowStyle(false, true, true), new PointRect(100, 100, 400, 200)));
            Assert.Equal(new PointRect(100, 100, 400, 200), WinTestInterop.Read(window.Handle).Bounds);
            Assert.Equal(new PointRect(100, 100, 400, 200), await On(thread, () => window.Frame));
            Assert.Equal(1, window.BackingScale);

            // Moved to a display at 150 %: Windows suggests 1.5 times the size; the window keeps its pixels.
            var suggested = new WinTestInterop.Rect { Left = 100, Top = 100, Right = 700, Bottom = 400 };
            WinTestInterop.SendMessageW(window.Handle, WinTestInterop.WM_DPICHANGED, (144 << 16) | 144, ref suggested);
            Assert.Equal(new PointRect(100, 100, 400, 200), WinTestInterop.Read(window.Handle).Bounds);

            // Larger than every screen, to the document's largest size.
            await On(thread, () => window.SetFrame(new PointRect(-50, -60, 10_000, 10_000)));
            Assert.Equal(new PointRect(-50, -60, 10_000, 10_000), WinTestInterop.Read(window.Handle).Bounds);
            await On(thread, () => window.SetFrame(new PointRect(0, 0, 16_384, 16_384)));
            Assert.Equal(new PointRect(0, 0, 16_384, 16_384), WinTestInterop.Read(window.Handle).Bounds);
            await On(thread, () => window.SetFrame(new PointRect(0, 0, 1, 1)));
            Assert.Equal(new PointRect(0, 0, 1, 1), WinTestInterop.Read(window.Handle).Bounds);

            // Its track sizes, which the system's own sizing honours: 1 × 1 to 16,384 × 16,384, whatever the screens.
            var minMax = new int[10]; // MINMAXINFO: five POINTs
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(minMax, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                WinTestInterop.SendMessageW(window.Handle, WinTestInterop.WM_GETMINMAXINFO, IntPtr.Zero, pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }

            Assert.Equal((1, 1, 16_384, 16_384), (minMax[6], minMax[7], minMax[8], minMax[9]));

            await On(thread, window.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AWindowWhoseUpdateFailed_ShowsItsPixelsAgain_OnceAnUpdateSucceeds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await UpdateRetried();
    }

    /// <summary>
    /// A static panel's frame meets a failing update (a disconnected session): no other frame comes, since the sink skips
    /// identical ones, so the window tries its retained pixels again until an update succeeds, then stops trying.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task UpdateRetried()
    {
        var thread = new WinUiThread();
        try
        {
            var updates = 0;
            var window = await On(thread, () => new WinWindow(new DesktopWindowStyle(false, true, true), new PointRect(-30_000, -30_000, 40, 20))
            {
                UpdateLayered = () => ++updates > 2, // the first two updates fail
            });

            await On(thread, () => window.ShowFrame(Solid(40, 20, 255, 0, 0)));
            var deadline = Environment.TickCount64 + 10_000;
            while (await On(thread, () => updates) < 3)
            {
                Assert.True(Environment.TickCount64 < deadline, $"the window tried {await On(thread, () => updates)} updates, not 3");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }

            await Task.Delay(2_000, TestContext.Current.CancellationToken);
            Assert.Equal(3, await On(thread, () => updates));
            await On(thread, window.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ARetryAlreadyQueued_WhenAnUpdateSucceeds_IsIgnored()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await QueuedRetryIgnored();
    }

    /// <summary>
    /// An update fails and its retry timer comes due (its message waiting in the queue); a newer frame's update succeeds,
    /// which cancels the retry, but <c>KillTimer</c> leaves the waiting message. Dispatched afterwards, it must neither
    /// update the window again nor, when that update would fail, start retrying anew.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task QueuedRetryIgnored()
    {
        var thread = new WinUiThread();
        try
        {
            var updates = 0;
            var succeed = false;
            var window = await On(thread, () => new WinWindow(new DesktopWindowStyle(false, true, true), new PointRect(-30_000, -30_000, 40, 20))
            {
                UpdateLayered = () =>
                {
                    updates++;
                    return succeed;
                },
            });

            var (queued, before, after) = await On(thread, () =>
            {
                window.ShowFrame(Solid(40, 20, 255, 0, 0)); // fails: the retry timer starts
                Thread.Sleep(700); // it comes due
                var waiting = WinTestInterop.PeekMessageW(out _, window.Handle, WinTestInterop.WM_TIMER, WinTestInterop.WM_TIMER, WinTestInterop.PM_NOREMOVE);

                succeed = true;
                window.ShowFrame(Solid(40, 20, 0, 0, 255)); // succeeds: the retry is cancelled
                var updatesBefore = updates;

                succeed = false; // were the old retry to update, it would fail and start retrying again
                while (WinTestInterop.PeekMessageW(out var msg, window.Handle, WinTestInterop.WM_TIMER, WinTestInterop.WM_TIMER, WinTestInterop.PM_REMOVE))
                    WinTestInterop.DispatchMessageW(ref msg);
                return (waiting, updatesBefore, updates);
            });

            Assert.True(queued, "the retry timer's message wasn't waiting");
            Assert.Equal(2, before);
            Assert.Equal(2, after);

            await Task.Delay(1_500, TestContext.Current.CancellationToken);
            Assert.Equal(2, await On(thread, () => updates));
            await On(thread, window.Dispose);
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AWindowGrownAgain_ShowsTheFloor_WhereItsLastFrameDidntReach()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await GrownAgain();
    }

    /// <summary>
    /// Red at 400×200, shrunk to 300×150, blue there, then grown again, into the same pixels (400×200) or past them
    /// (500×250, new pixels): what the blue frame didn't cover is the floor, never the red of before.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static async Task GrownAgain()
    {
        const uint red = 0xFFFF0000, blue = 0xFF0000FF, floor = 0x01000000; // BGRA read as a little-endian uint
        var thread = new WinUiThread();
        try
        {
            foreach (var (width, height) in new[] { (400, 200), (500, 250) })
            {
                var window = await On(thread, () => new WinWindow(new DesktopWindowStyle(false, true, true), new PointRect(-30_000, -30_000, 400, 200)));
                await On(thread, () => window.ShowFrame(Solid(400, 200, 255, 0, 0)));
                Assert.Equal(red, await On(thread, () => window.PixelAt(350, 175)));

                await On(thread, () => window.SetFrame(new PointRect(-30_000, -30_000, 300, 150)));
                await On(thread, () => window.ShowFrame(Solid(300, 150, 0, 0, 255)));
                await On(thread, () => window.SetFrame(new PointRect(-30_000, -30_000, width, height)));

                Assert.Equal((blue, floor, floor, floor, floor), await On(thread, () =>
                    (window.PixelAt(100, 100), window.PixelAt(350, 100), window.PixelAt(100, 175), window.PixelAt(350, 175),
                     window.PixelAt(width - 1, height - 1))));
                await On(thread, window.Dispose);
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>An opaque frame of one colour.</summary>
    internal static DesktopFrame Solid(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, 255);
        return new DesktopFrame(pixels, width, height, width * 4);
    }

    /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result.</summary>
    [SupportedOSPlatform("windows")]
    internal static Task<T> On<T>(WinUiThread thread, Func<T> func)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() =>
        {
            try
            {
                result.SetResult(func());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [SupportedOSPlatform("windows")]
    internal static Task On(WinUiThread thread, Action action) => On(thread, () =>
    {
        action();
        return true;
    });
}
