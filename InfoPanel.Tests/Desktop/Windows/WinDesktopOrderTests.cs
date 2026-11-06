using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// The desktop level's choices on Windows, on any OS (<see cref="WinDesktopOrder"/>): the desktop window D is the highest
/// shown, non-topmost window of the shell's thread; a desktop-level window goes directly above it, or to the top when
/// the window above D is topmost or there is none, or to the bottom when there is no D; nothing when already there.
/// </summary>
public sealed class WinDesktopOrderTests
{
    private const uint Shell = 10, App = 20, Explorer = 30;

    private static ZWindow Win(nint handle, uint thread, bool visible = true, bool topmost = false) => new(handle, thread, visible, topmost);

    private static bool None(nint handle) => false;

    [Fact]
    public void TheDesktop_IsTheHighestShownWindowOfTheShellsThread()
    {
        var windows = new[] { Win(1, App), Win(2, Shell), Win(3, App), Win(4, Shell) };

        Assert.Equal(2, WinDesktopOrder.FindDesktop(windows, Shell));
    }

    [Fact]
    public void ATopmostWindowOfTheShellsThread_TheTaskbar_IsSkipped()
    {
        var windows = new[] { Win(1, Shell, topmost: true), Win(2, App), Win(3, Shell) };

        Assert.Equal(3, WinDesktopOrder.FindDesktop(windows, Shell));
    }

    [Fact]
    public void WindowsOfOtherThreads_FileExplorers_AreSkipped()
    {
        var windows = new[] { Win(1, Explorer), Win(2, App), Win(3, Explorer), Win(4, Shell) };

        Assert.Equal(4, WinDesktopOrder.FindDesktop(windows, Shell));
    }

    [Fact]
    public void HiddenWindowsOfTheShellsThread_AreSkipped()
    {
        var windows = new[] { Win(1, Shell, visible: false), Win(2, App), Win(3, Shell) };

        Assert.Equal(3, WinDesktopOrder.FindDesktop(windows, Shell));
    }

    [Fact]
    public void NoShell_OrNoShownWindowOfIt_IsNoDesktop()
    {
        var windows = new[] { Win(1, App), Win(2, Shell, visible: false), Win(3, Shell, topmost: true) };

        Assert.Equal(0, WinDesktopOrder.FindDesktop(windows, 0));
        Assert.Equal(0, WinDesktopOrder.FindDesktop(windows, Shell));
        Assert.Equal(0, WinDesktopOrder.FindDesktop([], Shell));
    }

    [Fact]
    public void TheInsertAfter_IsTheWindowAboveTheDesktop()
    {
        // W (9) on top of the apps; D (5) below them, a window below D.
        var windows = new[] { Win(9, App), Win(1, App), Win(2, App), Win(5, Shell), Win(6, App, visible: false) };

        Assert.Equal(2, WinDesktopOrder.InsertAfter(windows, 5, 9, None));
    }

    [Fact]
    public void WhenTheWindowAboveTheDesktopIsTopmost_OrNone_TheInsertAfterIsTheTop()
    {
        // Win+D: D raised to the top of the normal windows, under the taskbar.
        var raised = new[] { Win(1, Shell, topmost: true), Win(5, Shell), Win(2, App), Win(9, App) };
        Assert.Equal(WinDesktopOrder.Top, WinDesktopOrder.InsertAfter(raised, 5, 9, None));

        var first = new[] { Win(5, Shell), Win(2, App), Win(9, App) };
        Assert.Equal(WinDesktopOrder.Top, WinDesktopOrder.InsertAfter(first, 5, 9, None));
    }

    [Fact]
    public void NoDesktop_TheInsertAfterIsTheBottom()
    {
        var windows = new[] { Win(1, App), Win(9, App), Win(2, App) };

        Assert.Equal(WinDesktopOrder.Bottom, WinDesktopOrder.InsertAfter(windows, 0, 9, None));
        // A desktop gone since it was found: the same.
        Assert.Equal(WinDesktopOrder.Bottom, WinDesktopOrder.InsertAfter(windows, 5, 9, None));
    }

    [Fact]
    public void AWindowAlreadyDirectlyAboveTheDesktop_IsLeftThere()
    {
        var windows = new[] { Win(1, App), Win(9, App), Win(5, Shell) };
        Assert.Null(WinDesktopOrder.InsertAfter(windows, 5, 9, None));

        var atTheBottom = new[] { Win(1, App), Win(9, App) };
        Assert.Null(WinDesktopOrder.InsertAfter(atTheBottom, 0, 9, None));
    }

    [Fact]
    public void SeveralDesktopLevelWindows_DirectlyAboveTheDesktop_AreEachLeftThere()
    {
        // 9 and 8 both desktop-level, together directly above D: neither moves (no swapping on every run).
        var windows = new[] { Win(1, App), Win(9, App), Win(8, App), Win(5, Shell) };
        bool Group(nint h) => h is 8 or 9;

        Assert.Null(WinDesktopOrder.InsertAfter(windows, 5, 9, Group));
        Assert.Null(WinDesktopOrder.InsertAfter(windows, 5, 8, Group));
        // A normal window between them: 9 isn't directly above D any more.
        var split = new[] { Win(9, App), Win(1, App), Win(8, App), Win(5, Shell) };
        Assert.Equal(8, WinDesktopOrder.InsertAfter(split, 5, 9, Group));
    }

    [Fact]
    public void ATopmostWindowDirectlyAboveTheDesktop_IsntInPlace_AndGoesToTheTopOfTheNormalWindows()
    {
        // The window above D turned topmost between the read and the call: W, inserted after it, came out topmost too.
        var topmost = new[] { Win(1, App, topmost: true), Win(9, App, topmost: true), Win(5, Shell) };
        Assert.Equal(WinDesktopOrder.NoTopmost, WinDesktopOrder.InsertAfter(topmost, 5, 9, None));

        // A topmost W with a normal window above D: after that window, which ends its topmost state.
        var normal = new[] { Win(9, App, topmost: true), Win(2, App), Win(5, Shell) };
        Assert.Equal(2, WinDesktopOrder.InsertAfter(normal, 5, 9, None));
    }
}
