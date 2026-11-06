using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// A Windows desktop window's pointer and presses (Windows only; skipped elsewhere). The windows stay hidden and the
/// messages are sent to them directly, so these run in any session, over SSH too; a real drag is
/// <see cref="WinDesktopWindowTests"/>'.
/// <list type="bullet">
/// <item>The pointer over the window is the arrow: the class cursor (with none, Windows keeps the last cursor set).</item>
/// <item><c>WM_NCHITTEST</c> answers from the window's rectangle, its DPI's resize band and its style, as it changes.</item>
/// <item>A press on the caption or an edge is held for the whole of the system's move or size loop, then reported once,
/// moved or not; each size change in the loop is a user resize; a press on the client is reported once at its release;
/// <c>SetFrame</c> is never the user's; a double click or the right button on the caption does nothing.</item>
/// </list>
/// </summary>
public sealed class WinWindowInputTests
{
    private const string OnlyOnWindows = "Windows desktop windows exist only on Windows.";

    private const uint WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCRBUTTONUP = 0x00A5;
    private const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_CAPTURECHANGED = 0x0215;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTBOTTOMRIGHT = 17, GCLP_HCURSOR = -12, IDC_ARROW = 32512;

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static IntPtr Point(int x, int y) => (IntPtr)((y & 0xFFFF) << 16 | (x & 0xFFFF));

    [Fact]
    public async Task ThePointerOverAWindow_IsTheArrow()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await Arrow();
    }

    [SupportedOSPlatform("windows")]
    private static async Task Arrow()
    {
        var thread = new WinUiThread();
        try
        {
            var window = await WinUiThreadTests.On(thread, () => new WinWindow(new DesktopWindowStyle(WindowLevel.Normal, true, true), new PointRect(100, 100, 400, 200)));
            try
            {
                var arrow = LoadCursorW(IntPtr.Zero, IDC_ARROW);
                Assert.NotEqual(IntPtr.Zero, arrow);
                Assert.Equal(arrow, GetClassLongPtr(window.Handle, GCLP_HCURSOR));
            }
            finally
            {
                await WinUiThreadTests.On(thread, window.Dispose);
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TheHitTest_FollowsTheWindowsPlace_ItsDpi_AndItsStyle()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await HitTests();
    }

    [SupportedOSPlatform("windows")]
    private static async Task HitTests()
    {
        var thread = new WinUiThread();
        try
        {
            var window = await WinUiThreadTests.On(thread, () => new WinWindow(new DesktopWindowStyle(WindowLevel.Normal, true, true), new PointRect(100, 100, 400, 200)));
            try
            {
                int Hit(int x, int y) => (int)WinTestInterop.SendMessageW(window.Handle, WM_NCHITTEST, IntPtr.Zero, Point(x, y));

                var band = WinHitTest.Band((int)GetDpiForWindow(window.Handle));
                Assert.InRange(band, 10, 40);
                Assert.Equal(HTCAPTION, Hit(300, 200));
                Assert.Equal(HTBOTTOMRIGHT, Hit(499, 299));
                Assert.Equal(HTLEFT, Hit(100 + band - 1, 200));
                Assert.Equal(HTCAPTION, Hit(100 + band, 200));

                // Moved: the band moves with it.
                await WinUiThreadTests.On(thread, () => window.SetFrame(new PointRect(-500, 40, 400, 200)));
                Assert.Equal(HTLEFT, Hit(-500, 140));
                Assert.Equal(HTBOTTOMRIGHT, Hit(-101, 239));

                // Not movable: the body is the client, the edges still resize; not resizable either: all client.
                await WinUiThreadTests.On(thread, () => window.SetStyle(new DesktopWindowStyle(WindowLevel.Normal, false, true)));
                Assert.Equal(HTCLIENT, Hit(-300, 140));
                Assert.Equal(HTBOTTOMRIGHT, Hit(-101, 239));
                await WinUiThreadTests.On(thread, () => window.SetStyle(new DesktopWindowStyle(WindowLevel.Normal, false, false)));
                Assert.Equal(HTCLIENT, Hit(-101, 239));
                await WinUiThreadTests.On(thread, () => window.SetStyle(new DesktopWindowStyle(WindowLevel.Normal, true, false)));
                Assert.Equal(HTCAPTION, Hit(-500, 40));
            }
            finally
            {
                await WinUiThreadTests.On(thread, window.Dispose);
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task APressOnTheCaptionOrAnEdge_IsHeldThroughTheSystemsLoop_ThenReportedOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await SystemLoopPresses();
    }

    [SupportedOSPlatform("windows")]
    private static async Task SystemLoopPresses()
    {
        var thread = new WinUiThread();
        try
        {
            var window = await WinUiThreadTests.On(thread, () => new WinWindow(new DesktopWindowStyle(WindowLevel.Normal, true, true), new PointRect(100, 100, 400, 200)));
            var events = new List<string>();
            var loops = new List<(nint WParam, bool Moving)>();
            Action<nint, nint>? inLoop = null;
            await WinUiThreadTests.On(thread, () =>
            {
                window.UserMoved += () => events.Add($"moved {window.UserMoving} {window.Frame}");
                window.UserResized += () => events.Add($"resized {window.UserMoving} {window.Frame}");
                window.SystemLoop = (wParam, _) =>
                {
                    loops.Add((wParam, window.UserMoving));
                    inLoop?.Invoke(wParam, 0);
                };
            });
            try
            {
                // A drag: the loop moves the window; held all through, reported once after.
                inLoop = (_, _) =>
                {
                    Win32.SetWindowPos(window.Handle, 0, 300, 100, 400, 200, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
                    events.Add($"in loop {window.UserMoving}");
                };
                Assert.Equal(IntPtr.Zero, WinTestInterop.SendMessageW(window.Handle, WM_NCLBUTTONDOWN, HTCAPTION, Point(300, 200)));
                Assert.Equal(new[] { ((nint)HTCAPTION, true) }, loops);
                Assert.Equal(["in loop True", "moved False (300, 100) 400×200"], events);

                // A click: the loop moves nothing; still reported once.
                inLoop = null;
                events.Clear();
                WinTestInterop.SendMessageW(window.Handle, WM_NCLBUTTONDOWN, HTCAPTION, Point(400, 200));
                Assert.Equal(["moved False (300, 100) 400×200"], events);

                // A resize from the bottom-right corner: each size change is the user's, while held; then the release.
                events.Clear();
                inLoop = (_, _) =>
                {
                    Win32.SetWindowPos(window.Handle, 0, 0, 0, 500, 260, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOMOVE);
                    Win32.SetWindowPos(window.Handle, 0, 0, 0, 520, 300, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOMOVE);
                };
                WinTestInterop.SendMessageW(window.Handle, WM_NCLBUTTONDOWN, HTBOTTOMRIGHT, Point(699, 299));
                Assert.Equal(["resized True (300, 100) 500×260", "resized True (300, 100) 520×300", "moved False (300, 100) 520×300"], events);
                Assert.Equal((nint)HTBOTTOMRIGHT, loops[^1].WParam);

                // The engine's own changes are never the user's.
                events.Clear();
                await WinUiThreadTests.On(thread, () => window.SetFrame(new PointRect(10, 10, 300, 150)));
                Assert.Empty(events);

                // A double click on the caption and the right button there: nothing (no maximize, no system menu).
                var loopsBefore = loops.Count;
                Assert.Equal(IntPtr.Zero, WinTestInterop.SendMessageW(window.Handle, WM_NCLBUTTONDBLCLK, HTCAPTION, Point(100, 50)));
                Assert.Equal(IntPtr.Zero, WinTestInterop.SendMessageW(window.Handle, WM_NCRBUTTONUP, HTCAPTION, Point(100, 50)));
                Assert.Equal(loopsBefore, loops.Count);
                Assert.Empty(events);
                Assert.Equal(new PointRect(10, 10, 300, 150), window.Frame);
            }
            finally
            {
                await WinUiThreadTests.On(thread, window.Dispose);
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task APressOnTheClient_IsHeldUntilItsRelease_ThenReportedOnce()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), OnlyOnWindows);
        if (OperatingSystem.IsWindows())
            await ClientPresses();
    }

    [SupportedOSPlatform("windows")]
    private static async Task ClientPresses()
    {
        var thread = new WinUiThread();
        try
        {
            var window = await WinUiThreadTests.On(thread, () => new WinWindow(new DesktopWindowStyle(WindowLevel.Normal, false, false), new PointRect(100, 100, 400, 200)));
            var moved = 0;
            await WinUiThreadTests.On(thread, () => window.UserMoved += () => moved++);
            try
            {
                Assert.False(await WinUiThreadTests.On(thread, () => window.UserMoving));
                WinTestInterop.SendMessageW(window.Handle, WM_LBUTTONDOWN, IntPtr.Zero, Point(10, 10));
                Assert.True(await WinUiThreadTests.On(thread, () => window.UserMoving));
                Assert.Equal(0, moved);

                WinTestInterop.SendMessageW(window.Handle, WM_LBUTTONUP, IntPtr.Zero, Point(10, 10));
                Assert.False(await WinUiThreadTests.On(thread, () => window.UserMoving));
                Assert.Equal(1, moved);

                // The release's capture change, and a stray one later, report nothing more.
                WinTestInterop.SendMessageW(window.Handle, WM_CAPTURECHANGED, IntPtr.Zero, IntPtr.Zero);
                WinTestInterop.SendMessageW(window.Handle, WM_LBUTTONUP, IntPtr.Zero, Point(10, 10));
                Assert.Equal(1, moved);

                // A press whose mouse another window takes ends there, once.
                WinTestInterop.SendMessageW(window.Handle, WM_LBUTTONDOWN, IntPtr.Zero, Point(10, 10));
                WinTestInterop.SendMessageW(window.Handle, WM_CAPTURECHANGED, IntPtr.Zero, IntPtr.Zero);
                WinTestInterop.SendMessageW(window.Handle, WM_LBUTTONUP, IntPtr.Zero, Point(10, 10));
                Assert.Equal(2, moved);
                Assert.False(await WinUiThreadTests.On(thread, () => window.UserMoving));
            }
            finally
            {
                await WinUiThreadTests.On(thread, window.Dispose);
            }
        }
        finally
        {
            thread.Stop(TimeSpan.FromSeconds(5));
        }
    }
}
