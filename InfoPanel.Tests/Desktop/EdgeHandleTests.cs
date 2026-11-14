using InfoPanel.Desktop;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// The edge handles a resizable desktop window draws on macOS (any OS: no platform code): where the band is, the handle's
/// shape, its state from hover to resize to release with the fades on a clock of the test's own, and which pointer source
/// moves it.
/// </summary>
public sealed class EdgeHandleTests
{
    private static readonly DesktopWindowStyle Resizable = new(WindowLevel.Normal, Movable: true, Resizable: true);
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    private static TimeSpan Ms(double ms) => T0 + TimeSpan.FromMilliseconds(ms);

    [Theory]
    [InlineData(200, 0, ResizeEdge.Top)]
    [InlineData(200, 3.9, ResizeEdge.Top)]
    [InlineData(200, 4, ResizeEdge.None)]
    [InlineData(200, 299.5, ResizeEdge.Bottom)]
    [InlineData(0, 150, ResizeEdge.Left)]
    [InlineData(399, 150, ResizeEdge.Right)]
    [InlineData(395, 150, ResizeEdge.None)]
    [InlineData(200, 150, ResizeEdge.None)]
    [InlineData(0, 0, ResizeEdge.Top | ResizeEdge.Left)]
    [InlineData(399, 299, ResizeEdge.Bottom | ResizeEdge.Right)]
    [InlineData(9, 1, ResizeEdge.Top | ResizeEdge.Left)] // along the top edge, within the corner
    [InlineData(10, 1, ResizeEdge.Top)]
    [InlineData(398, 9.5, ResizeEdge.Top | ResizeEdge.Right)] // along the right edge, within the corner
    [InlineData(398, 10, ResizeEdge.Right)]
    [InlineData(1, 291, ResizeEdge.Bottom | ResizeEdge.Left)]
    public void TheBand_IsTheOutermostFewPoints_ACornerTakingItsTwoEdges(double x, double y, ResizeEdge expected) =>
        Assert.Equal(expected, EdgeHandleBand.At(x, y, 400, 300, Resizable));

    [Theory]
    [InlineData(-0.5, 150)]
    [InlineData(400, 150)]
    [InlineData(200, -1)]
    [InlineData(200, 300)]
    public void OutsideTheWindow_IsNoBand(double x, double y) =>
        Assert.Equal(ResizeEdge.None, EdgeHandleBand.At(x, y, 400, 300, Resizable));

    [Fact]
    public void ASmallWindow_KeepsABodyToDrag()
    {
        // 9 × 6 points: the band is a third of each side (3 and 2 points), the middle third takes no handle.
        Assert.Equal(ResizeEdge.Top | ResizeEdge.Left, EdgeHandleBand.At(2.9, 1.9, 9, 6, Resizable));
        Assert.Equal(ResizeEdge.None, EdgeHandleBand.At(4.5, 3, 9, 6, Resizable));
        Assert.Equal(ResizeEdge.Bottom | ResizeEdge.Right, EdgeHandleBand.At(6, 4, 9, 6, Resizable));
        Assert.Equal(ResizeEdge.Right, EdgeHandleBand.At(6.5, 3, 9, 6, Resizable) & ResizeEdge.Right);
    }

    [Fact]
    public void AWindowThatCantBeResized_OrLetsClicksThrough_HasNoBand()
    {
        foreach (var style in new[] { Resizable with { Resizable = false }, Resizable with { ClickThrough = true } })
        {
            Assert.Equal(ResizeEdge.None, EdgeHandleBand.At(0, 0, 400, 300, style));
            Assert.Equal(ResizeEdge.None, EdgeHandleBand.At(399, 150, 400, 300, style));
        }
    }

    [Fact]
    public void AResize_TellsTheEdgesItMoved()
    {
        var before = new PointRect(100, 100, 400, 300);
        Assert.Equal(ResizeEdge.Right, EdgeHandleBand.Moved(before, before with { Width = 420 }));
        Assert.Equal(ResizeEdge.Bottom, EdgeHandleBand.Moved(before, before with { Height = 280 }));
        Assert.Equal(ResizeEdge.Top | ResizeEdge.Left, EdgeHandleBand.Moved(before, new PointRect(90, 80, 410, 320)));
        Assert.Equal(ResizeEdge.None, EdgeHandleBand.Moved(before, before));
    }

    [Fact]
    public void TheHandle_IsABarAlongAnEdge_OrAGripAtACorner_InsideTheWindow()
    {
        Assert.Equal([(396.0, 10.0), (400.0, 10.0), (400.0, 290.0), (396.0, 290.0)], EdgeHandleLayout.Polygon(ResizeEdge.Right, 400, 300));
        Assert.Equal([(10.0, 0.0), (390.0, 0.0), (390.0, 4.0), (10.0, 4.0)], EdgeHandleLayout.Polygon(ResizeEdge.Top, 400, 300));
        Assert.Equal([(400.0, 300.0), (390.0, 300.0), (390.0, 296.0), (396.0, 296.0), (396.0, 290.0), (400.0, 290.0)],
            EdgeHandleLayout.Polygon(ResizeEdge.Bottom | ResizeEdge.Right, 400, 300));
        Assert.Empty(EdgeHandleLayout.Polygon(ResizeEdge.None, 400, 300));

        foreach (var edge in new[] { ResizeEdge.Top, ResizeEdge.Left, ResizeEdge.Bottom, ResizeEdge.Right,
            ResizeEdge.Top | ResizeEdge.Left, ResizeEdge.Top | ResizeEdge.Right, ResizeEdge.Bottom | ResizeEdge.Left })
        {
            foreach (var (w, h) in new[] { (400.0, 300.0), (9.0, 6.0) })
                Assert.All(EdgeHandleLayout.Polygon(edge, w, h), p => Assert.True(p.X >= 0 && p.X <= w && p.Y >= 0 && p.Y <= h, $"{edge} {p} in {w}×{h}"));
        }
    }

    [Fact]
    public void TheHandle_FadesIn_WhenThePointerEntersTheBand_AndOut_WhenItLeaves()
    {
        var handles = new EdgeHandles();
        Assert.Equal(0, handles.Opacity(T0));
        Assert.Equal(ResizeEdge.None, handles.Edge);

        handles.Hover(ResizeEdge.Right, Ms(0));
        Assert.Equal(ResizeEdge.Right, handles.Edge);
        Assert.Equal(0, handles.Opacity(Ms(0)));
        Assert.Equal(0.5, handles.Opacity(Ms(50)), 6);
        Assert.True(handles.Fading(Ms(50)));
        Assert.Equal(1, handles.Opacity(Ms(100)));
        Assert.False(handles.Fading(Ms(100)));

        handles.Hover(ResizeEdge.Right, Ms(120)); // a move within the band changes nothing
        Assert.Equal(1, handles.Opacity(Ms(120)));
        handles.Hover(ResizeEdge.Top | ResizeEdge.Right, Ms(130)); // into the corner: the grip, at once
        Assert.Equal(ResizeEdge.Top | ResizeEdge.Right, handles.Edge);
        Assert.Equal(1, handles.Opacity(Ms(130)));

        handles.Hover(ResizeEdge.None, Ms(200));
        Assert.Equal(1, handles.Opacity(Ms(200)));
        Assert.Equal(0.5, handles.Opacity(Ms(275)), 6);
        Assert.Equal(0, handles.Opacity(Ms(350)));
        Assert.False(handles.Fading(Ms(350)));
        Assert.Equal(ResizeEdge.Top | ResizeEdge.Right, handles.Edge); // drawn where it was while it fades
    }

    [Fact]
    public void AFadeThatTurnsBack_StartsFromWhereItGot()
    {
        var handles = new EdgeHandles();
        handles.Hover(ResizeEdge.Left, Ms(0));
        handles.Hover(ResizeEdge.None, Ms(60)); // 0.6, fading out over 0.6 × 150 ms
        Assert.Equal(0.6, handles.Opacity(Ms(60)), 6);
        Assert.Equal(0.3, handles.Opacity(Ms(105)), 6);
        handles.Hover(ResizeEdge.Left, Ms(105)); // back from 0.3 over 0.7 × 100 ms
        Assert.Equal(0.3, handles.Opacity(Ms(105)), 6);
        Assert.Equal(1, handles.Opacity(Ms(175)), 6);
    }

    [Fact]
    public void WhileAResizeRuns_TheGrabbedEdgeStaysShown_ThenFadesAtTheRelease()
    {
        var handles = new EdgeHandles();
        handles.Hover(ResizeEdge.Bottom, Ms(0));
        handles.Press(ResizeEdge.Bottom, Ms(150));
        Assert.True(handles.Dragging);

        handles.Hover(ResizeEdge.None, Ms(200)); // the pointer runs ahead of the edge: still shown
        Assert.Equal((ResizeEdge.Bottom, 1.0), (handles.Edge, handles.Opacity(Ms(400))));

        handles.Resized(ResizeEdge.Bottom | ResizeEdge.Right, Ms(450)); // the system resizes the corner: its grip
        Assert.Equal((ResizeEdge.Bottom | ResizeEdge.Right, 1.0), (handles.Edge, handles.Opacity(Ms(450))));

        handles.Release(Ms(1000));
        Assert.False(handles.Dragging);
        Assert.Equal(1, handles.Opacity(Ms(1000)));
        Assert.Equal(0, handles.Opacity(Ms(1150)));

        // Still in the band after the release: hidden until the pointer leaves the band and comes back.
        handles.Hover(ResizeEdge.Bottom | ResizeEdge.Right, Ms(1200));
        Assert.Equal(0, handles.Opacity(Ms(1400)));
        handles.Hover(ResizeEdge.None, Ms(1500));
        handles.Hover(ResizeEdge.Right, Ms(1600));
        Assert.Equal((ResizeEdge.Right, 1.0), (handles.Edge, handles.Opacity(Ms(1700))));
    }

    [Fact]
    public void AResizeThePressDidntAnnounce_ShowsItsEdge_AndEndsAtTheRelease()
    {
        var handles = new EdgeHandles();
        handles.Resized(ResizeEdge.Left, Ms(0));
        Assert.True(handles.Dragging);
        Assert.Equal(1, handles.Opacity(Ms(100)));
        handles.Release(Ms(200));
        Assert.Equal(0, handles.Opacity(Ms(350)));
    }

    [Fact]
    public void APressOffTheBand_OrAMoveOfTheWindow_HidesTheHandle()
    {
        var handles = new EdgeHandles();
        handles.Hover(ResizeEdge.Top, Ms(0));
        handles.Press(ResizeEdge.None, Ms(100));
        Assert.False(handles.Dragging);
        Assert.Equal(0, handles.Opacity(Ms(250)));

        handles.Hover(ResizeEdge.Top, Ms(300));
        handles.Press(ResizeEdge.Top, Ms(400));
        handles.Moved(Ms(450)); // the system dragged the window instead
        Assert.False(handles.Dragging);
        Assert.Equal(0, handles.Opacity(Ms(600)));
    }

    [Fact]
    public void AWindowThatStopsBeingResizable_DropsItsHandleAtOnce_EvenMidResize()
    {
        var handles = new EdgeHandles();
        handles.Hover(ResizeEdge.Right, Ms(0));
        handles.Press(ResizeEdge.Right, Ms(100));
        handles.Disable(Ms(120));
        Assert.Equal((ResizeEdge.None, 0.0, false), (handles.Edge, handles.Opacity(Ms(120)), handles.Dragging));
        Assert.False(handles.Fading(Ms(120)));

        // A hover after it, with the band the style now gives, shows nothing.
        handles.Hover(EdgeHandleBand.At(399, 150, 400, 300, Resizable with { Resizable = false }), Ms(200));
        Assert.Equal(0, handles.Opacity(Ms(400)));
    }

    [Fact]
    public void ALiveResizeStep_AfterTheWindowStoppedShowingHandles_ShowsNone()
    {
        // Click-through turned on, resizing turned off or the window hidden: the window passes shows: false.
        var handles = new EdgeHandles();
        handles.Hover(ResizeEdge.Right, Ms(0));
        handles.Press(ResizeEdge.Right, Ms(100));
        handles.Resized(ResizeEdge.Right, Ms(150));
        handles.Disable(Ms(200)); // ApplyStyle's drop, mid-resize

        handles.Resized(ResizeEdge.Right, Ms(250), shows: false); // the next live-resize notification
        Assert.Equal((ResizeEdge.None, 0.0, false), (handles.Edge, handles.Opacity(Ms(250)), handles.Dragging));
        Assert.Equal(0, handles.Opacity(Ms(400)));
        Assert.False(handles.Fading(Ms(400)));

        // Once the window shows handles again, a resize shows its edge as before.
        handles.Resized(ResizeEdge.Right, Ms(500), shows: true);
        Assert.Equal((ResizeEdge.Right, 1.0), (handles.Edge, handles.Opacity(Ms(600))));
    }

    [Fact]
    public void ADragReleasedOutsideTheBand_ShowsTheHandleAgain_WhenThePollFindsThePointerBackAtTheEdge()
    {
        var handles = new EdgeHandles();
        Assert.True(handles.Polled(true, ResizeEdge.Right, true, Ms(0))); // pointer at the edge: shown
        handles.Press(ResizeEdge.Right, Ms(100));
        Assert.True(handles.Polled(true, ResizeEdge.None, true, Ms(150))); // dragged out of the band: Hover ignores it
        Assert.False(handles.Polled(true, ResizeEdge.None, true, Ms(200))); // unchanged: not delivered again
        handles.Release(Ms(250)); // released outside the band

        // The poll's cache is invalid after the release: the same band is delivered again, which ends the wait for a leave.
        Assert.True(handles.Polled(true, ResizeEdge.None, true, Ms(300)));
        Assert.False(handles.Polled(true, ResizeEdge.None, true, Ms(350)));
        Assert.True(handles.Polled(true, ResizeEdge.Right, true, Ms(400))); // back at the edge
        Assert.Equal((ResizeEdge.Right, 1.0), (handles.Edge, handles.Opacity(Ms(500))));
    }

    [Fact]
    public void ADragReleasedOutsideTheWindow_ShowsTheHandleAgain_WhenThePointerComesBackToTheEdge()
    {
        var handles = new EdgeHandles();
        handles.Polled(true, ResizeEdge.Bottom, true, Ms(0));
        handles.Press(ResizeEdge.Bottom, Ms(100));
        handles.Polled(false, ResizeEdge.None, true, Ms(150)); // the pointer ran off the window
        handles.Release(Ms(250));
        handles.Polled(false, ResizeEdge.None, true, Ms(300));
        handles.Polled(true, ResizeEdge.Bottom, true, Ms(350));
        Assert.Equal((ResizeEdge.Bottom, 1.0), (handles.Edge, handles.Opacity(Ms(500))));
    }

    [Fact]
    public void TheTrackingArea_IsChosen_ByItsFirstEvent()
    {
        var chooser = new EdgeHoverChooser(EdgeHoverMode.Auto);
        Assert.Equal(EdgeHoverSource.Undecided, chooser.Source);
        Assert.True(chooser.Polls);
        Assert.False(chooser.Polled(inside: true, Ms(0)));
        Assert.True(chooser.Tracked());
        Assert.Equal(EdgeHoverSource.Tracking, chooser.Source);
        Assert.False(chooser.Polls);
        Assert.False(chooser.Polled(inside: true, Ms(5000)));
    }

    [Fact]
    public void ThePoll_IsChosen_WhenThePointerStaysOverAWindowForASecondWithNoTrackingEvent()
    {
        var chooser = new EdgeHoverChooser(EdgeHoverMode.Auto);
        Assert.False(chooser.Polled(inside: true, Ms(0)));
        Assert.False(chooser.Polled(inside: true, Ms(900)));
        Assert.False(chooser.Polled(inside: false, Ms(950))); // left: the second starts again
        Assert.False(chooser.Polled(inside: true, Ms(1000)));
        Assert.False(chooser.Polled(inside: true, Ms(1999)));
        Assert.True(chooser.Polled(inside: true, Ms(2000)));
        Assert.Equal(EdgeHoverSource.Poll, chooser.Source);
        Assert.False(chooser.Tracked()); // a late tracking event moves nothing
        Assert.Equal(EdgeHoverChooser.FastPoll, chooser.Interval(near: true));
        Assert.Equal(EdgeHoverChooser.SlowPoll, chooser.Interval(near: false));
    }

    [Fact]
    public void EachMode_PinsItsSource_AndTheProbeKeepsPolling()
    {
        var tracking = new EdgeHoverChooser(EdgeHoverMode.Tracking);
        Assert.Equal(EdgeHoverSource.Tracking, tracking.Source);
        Assert.False(tracking.Polls);
        Assert.False(tracking.Polled(inside: true, Ms(5000)));

        var poll = new EdgeHoverChooser(EdgeHoverMode.Poll);
        Assert.Equal(EdgeHoverSource.Poll, poll.Source);
        Assert.False(poll.Tracked());
        Assert.True(poll.Polled(inside: false, Ms(0)));

        var probe = new EdgeHoverChooser(EdgeHoverMode.Probe);
        Assert.True(probe.Tracked());
        Assert.True(probe.Polls); // still polled, for the log
        Assert.Equal(EdgeHoverChooser.FastPoll, probe.Interval(near: true));
        Assert.False(probe.Polled(inside: true, Ms(5000)));

        Assert.Equal(EdgeHoverMode.Auto, EdgeHoverChooser.ModeOf(null));
        Assert.Equal(EdgeHoverMode.Auto, EdgeHoverChooser.ModeOf("other"));
        Assert.Equal(EdgeHoverMode.Poll, EdgeHoverChooser.ModeOf(" Poll "));
        Assert.Equal(EdgeHoverMode.Probe, EdgeHoverChooser.ModeOf("probe"));
        Assert.Equal(EdgeHoverMode.Tracking, EdgeHoverChooser.ModeOf("tracking"));
    }
}
