using InfoPanel.Desktop;
using InfoPanel.Desktop.MacOS;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// Where a macOS desktop window shows a resize cursor (on any OS: the mapping has no AppKit in it): within 4 points inside
/// an edge, that edge, and both edges at a corner, when the window is resizable and doesn't let clicks through; the band
/// is at most a third of each side; nothing elsewhere, outside the window, or when it can't be resized.
/// </summary>
public sealed class MacResizeBandTests
{
    private static readonly DesktopWindowStyle Resizable = new(WindowLevel.Normal, true, true);

    [Fact]
    public void TheEdges_AreNSCursorFrameResizePositions()
    {
        // NSCursor.h: Top 1 << 0, Left 1 << 1, Bottom 1 << 2, Right 1 << 3, each corner its two edges.
        Assert.Equal(1, (int)MacResizeEdge.Top);
        Assert.Equal(2, (int)MacResizeEdge.Left);
        Assert.Equal(4, (int)MacResizeEdge.Bottom);
        Assert.Equal(8, (int)MacResizeEdge.Right);
        Assert.Equal(3, (int)MacResizeBand.At(0, 0, 600, 300, Resizable));
        Assert.Equal(12, (int)MacResizeBand.At(599.5, 299.5, 600, 300, Resizable));
    }

    [Fact]
    public void TheBand_Is4PointsInsideEachEdge()
    {
        Assert.Equal(MacResizeEdge.Left, MacResizeBand.At(3.9, 150, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(4, 150, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.Right, MacResizeBand.At(596, 150, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(595.9, 150, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.Top, MacResizeBand.At(300, 3.9, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(300, 4, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.Bottom, MacResizeBand.At(300, 296, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(300, 295.9, 600, 300, Resizable));
    }

    [Theory]
    [InlineData(0, 0, (int)(MacResizeEdge.Top | MacResizeEdge.Left))]
    [InlineData(599, 0, (int)(MacResizeEdge.Top | MacResizeEdge.Right))]
    [InlineData(0, 299, (int)(MacResizeEdge.Bottom | MacResizeEdge.Left))]
    [InlineData(599, 299, (int)(MacResizeEdge.Bottom | MacResizeEdge.Right))]
    [InlineData(3, 3, (int)(MacResizeEdge.Top | MacResizeEdge.Left))]
    [InlineData(4, 3, (int)(MacResizeEdge.Top))]
    [InlineData(597, 296, (int)(MacResizeEdge.Bottom | MacResizeEdge.Right))]
    [InlineData(597, 295, (int)(MacResizeEdge.Right))]
    [InlineData(300, 150, (int)(MacResizeEdge.None))]
    public void EachEdgeAndCorner_IsItsOwn_AndTheBodyIsNone(double x, double y, int expected)
    {
        Assert.Equal((MacResizeEdge)expected, MacResizeBand.At(x, y, 600, 300, Resizable));
    }

    [Fact]
    public void OnASmallWindow_TheBandIsAThirdOfASide_SoTheBandsNeverOverlap()
    {
        // A third of 9 is 3 points: 0–3 and 6–9 are the edges, 3–6 the body.
        Assert.Equal(MacResizeEdge.Left, MacResizeBand.At(2.9, 4.5, 9, 9, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(3, 4.5, 9, 9, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(5.9, 4.5, 9, 9, Resizable));
        Assert.Equal(MacResizeEdge.Right, MacResizeBand.At(6, 4.5, 9, 9, Resizable));
        Assert.Equal(MacResizeEdge.Bottom | MacResizeEdge.Right, MacResizeBand.At(8, 8, 9, 9, Resizable));

        // A side's band is a third of that side: a wide, short window has a thin top and bottom band.
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(300, 2, 600, 6, Resizable));
        Assert.Equal(MacResizeEdge.Top, MacResizeBand.At(300, 1.9, 600, 6, Resizable));
        Assert.Equal(MacResizeEdge.Left, MacResizeBand.At(3.9, 3, 600, 6, Resizable));
    }

    [Fact]
    public void Nothing_WhenTheWindowIsntResizable_OrLetsClicksThrough()
    {
        var notResizable = new DesktopWindowStyle(WindowLevel.Normal, true, false);
        var clickThrough = new DesktopWindowStyle(WindowLevel.Top, true, true, ClickThrough: true);

        foreach (var (x, y) in new[] { (0.0, 0.0), (300.0, 0.0), (599.0, 299.0), (0.0, 150.0), (599.0, 150.0), (300.0, 299.0) })
        {
            Assert.Equal(MacResizeEdge.None, MacResizeBand.At(x, y, 600, 300, notResizable));
            Assert.Equal(MacResizeEdge.None, MacResizeBand.At(x, y, 600, 300, clickThrough));
        }
    }

    [Fact]
    public void APointOutsideTheWindow_IsNone()
    {
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(-0.5, 150, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(600, 150, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(300, -0.5, 600, 300, Resizable));
        Assert.Equal(MacResizeEdge.None, MacResizeBand.At(300, 300, 600, 300, Resizable));
    }
}
