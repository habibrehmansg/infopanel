using InfoPanel.Desktop.MacOS;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// Who owns the pointer's resize cursor across macOS desktop windows (on any OS: the rules have no AppKit in it): the
/// window whose band the pointer is in, or none. Only the owner puts the arrow back; a hidden or closed window gives the
/// cursor up and never takes it; a style change looks at where the pointer rests now.
/// </summary>
public sealed class MacResizeCursorOwnerTests
{
    private const nint A = 0x1000;
    private const nint B = 0x2000;

    [Fact]
    public void MovingFromOneWindowsBandIntoAnothers_KeepsTheSecondsCursor_WhicheverOrderTheEventsCome()
    {
        // B's enter before A's exit (tracking areas don't promise an order): A's late exit leaves B's cursor alone.
        var owner = new MacResizeCursorOwner();
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Right), owner.Moved(A, true, MacResizeEdge.Right));
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Left), owner.Moved(B, true, MacResizeEdge.Left));
        Assert.Equal(MacCursorChange.Nothing, owner.Exited(A));
        Assert.Equal(B, owner.Owner);

        // A's own move out of its band after B took over doesn't put the arrow back either; B's does.
        Assert.Equal(MacCursorChange.Nothing, owner.Moved(A, true, MacResizeEdge.None));
        Assert.Equal(MacCursorChange.Arrow, owner.Moved(B, true, MacResizeEdge.None));
        Assert.Equal((nint)0, owner.Owner);

        // A's exit before B's enter: the arrow, then B's cursor.
        var inOrder = new MacResizeCursorOwner();
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Right), inOrder.Moved(A, true, MacResizeEdge.Right));
        Assert.Equal(MacCursorChange.Arrow, inOrder.Exited(A));
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Left), inOrder.Moved(B, true, MacResizeEdge.Left));
        Assert.Equal(B, inOrder.Owner);
    }

    [Fact]
    public void HidingOrClosingTheWindowThatOwnsTheCursor_PutsTheArrowBack_AndItsLateEventsAreIgnored()
    {
        var owner = new MacResizeCursorOwner();
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Top), owner.Moved(A, true, MacResizeEdge.Top));
        Assert.Equal(MacCursorChange.Arrow, owner.Hidden(A));
        Assert.Equal((nint)0, owner.Owner);

        // Hidden, its frame unchanged under the pointer: a late move, exit or style change takes nothing.
        Assert.Equal(MacCursorChange.Nothing, owner.Moved(A, false, MacResizeEdge.Top));
        Assert.Equal(MacCursorChange.Nothing, owner.Exited(A));
        Assert.Equal(MacCursorChange.Nothing, owner.Restyled(A, false, MacResizeEdge.Top, frontmost: true));
        Assert.Equal((nint)0, owner.Owner);

        // Closing (hidden first, as Dispose orders it out) does the same; a window that doesn't own it does nothing.
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Bottom), owner.Moved(B, true, MacResizeEdge.Bottom));
        Assert.Equal(MacCursorChange.Nothing, owner.Hidden(A));
        Assert.Equal(B, owner.Owner);
        Assert.Equal(MacCursorChange.Arrow, owner.Hidden(B));
        Assert.Equal(MacCursorChange.Nothing, owner.Hidden(B));
        Assert.Equal((nint)0, owner.Owner);
    }

    [Fact]
    public void AStyleChange_LooksAtWhereThePointerRestsNow()
    {
        // The pointer rests in the band of a window that isn't resizable (no band), then it becomes resizable.
        var owner = new MacResizeCursorOwner();
        Assert.Equal(MacCursorChange.Nothing, owner.Moved(A, true, MacResizeEdge.None));
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Left), owner.Restyled(A, true, MacResizeEdge.Left, frontmost: true));
        Assert.Equal(A, owner.Owner);

        // Resizing off (or click-through on) while it owns the cursor: the arrow.
        Assert.Equal(MacCursorChange.Arrow, owner.Restyled(A, true, MacResizeEdge.None, frontmost: true));
        Assert.Equal((nint)0, owner.Owner);

        // Another window's style change, the pointer not in its band, leaves the owner's cursor alone.
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Left), owner.Moved(A, true, MacResizeEdge.Left));
        Assert.Equal(MacCursorChange.Nothing, owner.Restyled(B, true, MacResizeEdge.None, frontmost: true));
        Assert.Equal(A, owner.Owner);
    }

    [Fact]
    public void AStyleChange_OnAWindowCoveredAtThePointer_LeavesTheCursorOfTheWindowOnTop()
    {
        // A lies behind B, A's left band under B's top band: the pointer rests there with B's cursor.
        var owner = new MacResizeCursorOwner();
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Top), owner.Moved(B, true, MacResizeEdge.Top));

        // A's style changes (draggable or resizable toggled): its band is under the pointer, but B is on top there.
        Assert.Equal(MacCursorChange.Nothing, owner.Restyled(A, true, MacResizeEdge.Left, frontmost: false));
        Assert.Equal(B, owner.Owner);

        // Brought to the front at the pointer, the same style change takes the cursor.
        Assert.Equal(MacCursorChange.Resize(MacResizeEdge.Left), owner.Restyled(A, true, MacResizeEdge.Left, frontmost: true));
        Assert.Equal(A, owner.Owner);

        // Covered again (B ordered over it, no pointer event yet) while it owns the cursor: it gives it up.
        Assert.Equal(MacCursorChange.Arrow, owner.Restyled(A, true, MacResizeEdge.Left, frontmost: false));
        Assert.Equal((nint)0, owner.Owner);
    }
}
