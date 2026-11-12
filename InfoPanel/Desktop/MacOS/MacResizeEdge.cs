namespace InfoPanel.Desktop.MacOS;

/// <summary>
/// The edge or corner of a macOS desktop window a pointer lies on, for its resize cursor (no AppKit in it, so it is
/// tested on any OS). The values are <c>NSCursorFrameResizePosition</c>'s (AppKit's <c>NSCursor.h</c>), so a corner is
/// its two edges together.
/// </summary>
[Flags]
internal enum MacResizeEdge
{
    None = 0,
    Top = 1 << 0,
    Left = 1 << 1,
    Bottom = 1 << 2,
    Right = 1 << 3,
}

/// <summary>
/// Where a macOS desktop window shows a resize cursor: AppKit resizes the window itself and, the app never active and the
/// window never key, shows no cursor for it, so the window sets one over a band inside each edge.
/// </summary>
internal static class MacResizeBand
{
    /// <summary>
    /// The band inside each edge, in points: narrow, so the cursor never says resize where a press would drag the window
    /// (AppKit's own resize zone isn't published).
    /// </summary>
    public const double Points = 4;

    /// <summary>
    /// The edge or corner of the point (<paramref name="x"/>, <paramref name="y"/>), in points from the window's top-left,
    /// of a <paramref name="width"/> × <paramref name="height"/> window: none unless the window is resizable and doesn't
    /// let clicks through. The band is at most a third of each side, so a small window keeps a body to drag.
    /// </summary>
    public static MacResizeEdge At(double x, double y, double width, double height, DesktopWindowStyle style)
    {
        if (!style.Resizable || style.ClickThrough || x < 0 || y < 0 || x >= width || y >= height)
            return MacResizeEdge.None;

        var bandX = Math.Min(Points, width / 3);
        var bandY = Math.Min(Points, height / 3);
        var edge = MacResizeEdge.None;
        if (y < bandY)
            edge |= MacResizeEdge.Top;
        else if (y >= height - bandY)
            edge |= MacResizeEdge.Bottom;
        if (x < bandX)
            edge |= MacResizeEdge.Left;
        else if (x >= width - bandX)
            edge |= MacResizeEdge.Right;
        return edge;
    }
}

/// <summary>What a window does to the pointer's cursor: nothing, the arrow again, or <see cref="Edge"/>'s resize cursor.</summary>
internal readonly record struct MacCursorChange(MacCursorChangeKind Kind, MacResizeEdge Edge = MacResizeEdge.None)
{
    public static readonly MacCursorChange Nothing = new(MacCursorChangeKind.Nothing);
    public static readonly MacCursorChange Arrow = new(MacCursorChangeKind.Arrow);

    public static MacCursorChange Resize(MacResizeEdge edge) => new(MacCursorChangeKind.Resize, edge);
}

internal enum MacCursorChangeKind
{
    Nothing,
    Arrow,
    Resize,
}

/// <summary>
/// The one owner of the pointer's resize cursor across every desktop window of the process: the window whose band the
/// pointer is in, or none (0). Tracking areas don't promise the order of one window's exit and another's enter, so
/// ownership, not order, decides: only the owner puts the arrow back. A hidden or closed window gives the cursor up and
/// never takes it, so its late events change nothing. No AppKit in it; a window id is any non-zero number.
/// </summary>
internal sealed class MacResizeCursorOwner
{
    /// <summary>The window whose resize cursor is shown; 0 when none is.</summary>
    public nint Owner { get; private set; }

    /// <summary>
    /// The pointer entered or moved over <paramref name="window"/>, <paramref name="band"/> its edge or corner there
    /// (<see cref="MacResizeBand.At"/>, by the window's style): the window takes the cursor while in its band, and
    /// gives it back, the arrow, when it owned it and the pointer left the band.
    /// </summary>
    public MacCursorChange Moved(nint window, bool visible, MacResizeEdge band)
    {
        if (!visible)
            return MacCursorChange.Nothing;
        if (band != MacResizeEdge.None)
        {
            Owner = window;
            return MacCursorChange.Resize(band);
        }

        return Release(window);
    }

    /// <summary>
    /// <paramref name="window"/>'s style changed (resizable or click-through toggled), <paramref name="band"/> read at
    /// the pointer's current place: as a move there, whatever cursor was shown before. Only the window the pointer is
    /// over (<paramref name="frontmost"/>: no other window above it there) can take the cursor; a covered one is as out
    /// of its band, so the cursor of the window on top stays.
    /// </summary>
    public MacCursorChange Restyled(nint window, bool visible, MacResizeEdge band, bool frontmost) =>
        Moved(window, visible, frontmost ? band : MacResizeEdge.None);

    /// <summary>The pointer left <paramref name="window"/>: the arrow if it owned the cursor, else nothing.</summary>
    public MacCursorChange Exited(nint window) => Release(window);

    /// <summary><paramref name="window"/> is hidden or closed: the arrow if it owned the cursor, else nothing.</summary>
    public MacCursorChange Hidden(nint window) => Release(window);

    private MacCursorChange Release(nint window)
    {
        if (Owner != window)
            return MacCursorChange.Nothing;
        Owner = 0;
        return MacCursorChange.Arrow;
    }
}
