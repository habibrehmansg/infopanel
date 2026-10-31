using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// Where a press on a Windows desktop window lands (on any OS: the hit test has no Win32 in it): within the resize band
/// of an edge or corner, that edge or corner when the window is resizable; elsewhere the caption when it is movable, so
/// the system's own move loop drags it; otherwise the client. The band is 10 device-independent pixels (1.4.x's
/// <c>ResizeBorderThickness</c>), so 10, 15 and 20 pixels at 96, 144 and 192 DPI, and at most a third of each side.
/// </summary>
public sealed class WinHitTestTests
{
    private static readonly DesktopWindowStyle Both = new(false, true, true);

    [Theory]
    [InlineData(96, 10)]
    [InlineData(120, 13)]
    [InlineData(144, 15)]
    [InlineData(192, 20)]
    public void TheBand_Is10DeviceIndependentPixels(int dpi, int pixels)
    {
        Assert.Equal(pixels, WinHitTest.Band(dpi));

        // Its last pixel is the edge's, the next one the body's, on every side.
        Assert.Equal(WinHitTest.HTLEFT, WinHitTest.Hit(pixels - 1, 150, 600, 300, dpi, Both));
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(pixels, 150, 600, 300, dpi, Both));
        Assert.Equal(WinHitTest.HTRIGHT, WinHitTest.Hit(600 - pixels, 150, 600, 300, dpi, Both));
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(600 - pixels - 1, 150, 600, 300, dpi, Both));
        Assert.Equal(WinHitTest.HTTOP, WinHitTest.Hit(300, pixels - 1, 600, 300, dpi, Both));
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(300, pixels, 600, 300, dpi, Both));
        Assert.Equal(WinHitTest.HTBOTTOM, WinHitTest.Hit(300, 300 - pixels, 600, 300, dpi, Both));
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(300, 300 - pixels - 1, 600, 300, dpi, Both));
    }

    [Theory]
    [InlineData(0, 0, WinHitTest.HTTOPLEFT)]
    [InlineData(300, 0, WinHitTest.HTTOP)]
    [InlineData(599, 0, WinHitTest.HTTOPRIGHT)]
    [InlineData(0, 150, WinHitTest.HTLEFT)]
    [InlineData(599, 150, WinHitTest.HTRIGHT)]
    [InlineData(0, 299, WinHitTest.HTBOTTOMLEFT)]
    [InlineData(300, 299, WinHitTest.HTBOTTOM)]
    [InlineData(599, 299, WinHitTest.HTBOTTOMRIGHT)]
    [InlineData(9, 9, WinHitTest.HTTOPLEFT)]
    [InlineData(590, 290, WinHitTest.HTBOTTOMRIGHT)]
    [InlineData(10, 9, WinHitTest.HTTOP)]
    [InlineData(589, 290, WinHitTest.HTBOTTOM)]
    [InlineData(300, 150, WinHitTest.HTCAPTION)]
    public void EachEdgeAndCorner_IsItsOwn_AndTheBodyIsTheCaption(int x, int y, int expected)
    {
        Assert.Equal(expected, WinHitTest.Hit(x, y, 600, 300, 96, Both));
    }

    [Fact]
    public void TheBody_IsTheCaption_OnlyWhenTheWindowIsMovable()
    {
        var fixedInPlace = new DesktopWindowStyle(false, false, true);

        Assert.Equal(WinHitTest.HTCLIENT, WinHitTest.Hit(300, 150, 600, 300, 96, fixedInPlace));
        Assert.Equal(WinHitTest.HTBOTTOMRIGHT, WinHitTest.Hit(599, 299, 600, 300, 96, fixedInPlace)); // still resizable
        Assert.Equal(WinHitTest.HTCLIENT, WinHitTest.Hit(300, 150, 600, 300, 96, new DesktopWindowStyle(true, false, false)));
    }

    [Fact]
    public void NoBand_WhenTheWindowIsntResizable()
    {
        var movableOnly = new DesktopWindowStyle(false, true, false);

        foreach (var (x, y) in new[] { (0, 0), (300, 0), (599, 299), (0, 150), (599, 150), (300, 299) })
        {
            Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(x, y, 600, 300, 96, movableOnly));
            Assert.Equal(WinHitTest.HTCLIENT, WinHitTest.Hit(x, y, 600, 300, 192, new DesktopWindowStyle(false, false, false)));
        }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void OnA20By20Window_TheBandIsAThirdOfASide_SoTheBodyStaysMovable(int dpi)
    {
        // A third of 20 is 6 pixels: 0–5 and 14–19 are the edges, 6–13 the body.
        Assert.Equal(WinHitTest.HTLEFT, WinHitTest.Hit(5, 10, 20, 20, dpi, Both));
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(6, 10, 20, 20, dpi, Both));
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(13, 10, 20, 20, dpi, Both));
        Assert.Equal(WinHitTest.HTRIGHT, WinHitTest.Hit(14, 10, 20, 20, dpi, Both));
        Assert.Equal(WinHitTest.HTTOP, WinHitTest.Hit(10, 5, 20, 20, dpi, Both));
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(10, 6, 20, 20, dpi, Both));
        Assert.Equal(WinHitTest.HTBOTTOMRIGHT, WinHitTest.Hit(14, 14, 20, 20, dpi, Both));

        // A side's band is a third of that side: a wide, short window has a thin top and bottom band.
        Assert.Equal(WinHitTest.HTCAPTION, WinHitTest.Hit(300, 6, 600, 20, dpi, Both));
        Assert.Equal(WinHitTest.HTLEFT, WinHitTest.Hit(dpi / 96 * 10 - 1, 10, 600, 20, dpi, Both));
    }

    [Fact]
    public void APointOutsideTheWindow_IsNowhere()
    {
        Assert.Equal(WinHitTest.HTNOWHERE, WinHitTest.Hit(-1, 150, 600, 300, 96, Both));
        Assert.Equal(WinHitTest.HTNOWHERE, WinHitTest.Hit(600, 150, 600, 300, 96, Both));
        Assert.Equal(WinHitTest.HTNOWHERE, WinHitTest.Hit(300, 300, 600, 300, 96, Both));
    }
}
