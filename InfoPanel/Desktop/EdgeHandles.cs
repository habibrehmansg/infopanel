namespace InfoPanel.Desktop;

/// <summary>An edge or corner of a desktop window (a corner is its two edges together).</summary>
[Flags]
public enum ResizeEdge
{
    None = 0,
    Top = 1 << 0,
    Left = 1 << 1,
    Bottom = 1 << 2,
    Right = 1 << 3,
}

/// <summary>
/// Where a resizable desktop window's edge handles show (macOS: the system resizes a borderless window from its edges but
/// shows no cursor for it in an app that is never active, so the window draws its own handles). No platform code in it,
/// so it is tested on any OS.
/// </summary>
public static class EdgeHandleBand
{
    /// <summary>The band inside each edge, in points: the outermost few points, where a press resizes the window.</summary>
    public const double Band = 4;

    /// <summary>How far from a corner, along either edge, the band counts as that corner, in points.</summary>
    public const double Corner = 10;

    /// <summary>
    /// The edge or corner at (<paramref name="x"/>, <paramref name="y"/>), in points from the window's top-left, of a
    /// <paramref name="width"/> × <paramref name="height"/> window: none unless the window is resizable and takes clicks,
    /// and none outside it. The band is at most a third of a side and a corner at most a third of an edge, so a small
    /// window keeps a body to drag.
    /// </summary>
    public static ResizeEdge At(double x, double y, double width, double height, DesktopWindowStyle style)
    {
        if (!style.Resizable || style.ClickThrough || x < 0 || y < 0 || x >= width || y >= height)
            return ResizeEdge.None;

        var bandX = Math.Min(Band, width / 3);
        var bandY = Math.Min(Band, height / 3);
        var cornerX = Math.Max(bandX, Math.Min(Corner, width / 3));
        var cornerY = Math.Max(bandY, Math.Min(Corner, height / 3));
        var top = y < bandY;
        var bottom = !top && y >= height - bandY;
        var left = x < bandX;
        var right = !left && x >= width - bandX;
        if (!(top || bottom || left || right))
            return ResizeEdge.None;

        var edge = ResizeEdge.None;
        if (top || ((left || right) && y < cornerY))
            edge |= ResizeEdge.Top;
        else if (bottom || ((left || right) && y >= height - cornerY))
            edge |= ResizeEdge.Bottom;
        if (left || ((top || bottom) && x < cornerX))
            edge |= ResizeEdge.Left;
        else if (right || ((top || bottom) && x >= width - cornerX))
            edge |= ResizeEdge.Right;
        return edge;
    }

    /// <summary>The edges a resize moved, from the window's frame before and after it (top-left space).</summary>
    public static ResizeEdge Moved(PointRect before, PointRect after)
    {
        var edge = ResizeEdge.None;
        if (after.Y != before.Y)
            edge |= ResizeEdge.Top;
        else if (after.Y + after.Height != before.Y + before.Height)
            edge |= ResizeEdge.Bottom;
        if (after.X != before.X)
            edge |= ResizeEdge.Left;
        else if (after.X + after.Width != before.X + before.Width)
            edge |= ResizeEdge.Right;
        return edge;
    }
}

/// <summary>The shape of a shown handle: a bar along an edge, an L-shaped grip at a corner.</summary>
public static class EdgeHandleLayout
{
    /// <summary>
    /// The handle of <paramref name="edge"/> on a <paramref name="width"/> × <paramref name="height"/> window, as one
    /// polygon in points from the window's top-left, inside the window: a bar <see cref="EdgeHandleBand.Band"/> thick
    /// along an edge, between its corners, or a grip whose arms run <see cref="EdgeHandleBand.Corner"/> along a corner's
    /// two edges. Empty for none.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Polygon(ResizeEdge edge, double width, double height)
    {
        if (edge == ResizeEdge.None || width <= 0 || height <= 0)
            return [];

        var tx = Math.Min(EdgeHandleBand.Band, width / 3);
        var ty = Math.Min(EdgeHandleBand.Band, height / 3);
        var cx = Math.Max(tx, Math.Min(EdgeHandleBand.Corner, width / 3));
        var cy = Math.Max(ty, Math.Min(EdgeHandleBand.Corner, height / 3));
        var vertical = edge & (ResizeEdge.Top | ResizeEdge.Bottom);
        var horizontal = edge & (ResizeEdge.Left | ResizeEdge.Right);
        if (vertical != 0 && horizontal != 0)
        {
            // The grip drawn at the top-left corner, then mirrored into the corner asked for.
            (double X, double Y)[] grip = [(0, 0), (cx, 0), (cx, ty), (tx, ty), (tx, cy), (0, cy)];
            var flipX = horizontal == ResizeEdge.Right;
            var flipY = vertical == ResizeEdge.Bottom;
            return [.. grip.Select(p => (flipX ? width - p.X : p.X, flipY ? height - p.Y : p.Y))];
        }

        return edge switch
        {
            ResizeEdge.Top => Rect(cx, 0, width - cx, ty),
            ResizeEdge.Bottom => Rect(cx, height - ty, width - cx, height),
            ResizeEdge.Left => Rect(0, cy, tx, height - cy),
            _ => Rect(width - tx, cy, width, height - cy),
        };
    }

    private static (double X, double Y)[] Rect(double left, double top, double right, double bottom) =>
        [(left, top), (right, top), (right, bottom), (left, bottom)];
}

/// <summary>
/// One window's edge handle: which edge or corner it shows and how opaque it is, from the pointer's hovers, presses,
/// the resize and its release (no platform code; the time is passed in, so tests drive it with a clock of their own).
/// Hidden, it fades in within <see cref="FadeIn"/> once the pointer is in the band; it fades out within
/// <see cref="FadeOut"/> once the pointer leaves it or a resize ends; while a resize runs, the grabbed edge stays shown
/// whatever the pointer does. After a resize it stays hidden until the pointer has left the band. A fade that turns
/// back starts from the opacity it reached.
/// </summary>
public sealed class EdgeHandles
{
    public static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(150);

    private double _from;
    private double _to;
    private TimeSpan _start;
    private TimeSpan _duration;
    private bool _waitForLeave;
    private bool _polledInside;
    private ResizeEdge _polledBand;
    private bool _polledDrove;

    /// <summary>The edge or corner shown, or last shown while it fades out (none once hidden at once).</summary>
    public ResizeEdge Edge { get; private set; }

    /// <summary>Whether a resize from this window's edge is running (from the press in the band to the release).</summary>
    public bool Dragging { get; private set; }

    /// <summary>How opaque the handle is at <paramref name="now"/>, 0 to 1.</summary>
    public double Opacity(TimeSpan now)
    {
        if (_duration <= TimeSpan.Zero || now - _start >= _duration)
            return _to;
        if (now <= _start)
            return _from;
        return _from + (_to - _from) * ((now - _start) / _duration);
    }

    /// <summary>Whether the handle is still fading at <paramref name="now"/> (the window redraws it until it isn't).</summary>
    public bool Fading(TimeSpan now) => Opacity(now) != _to;

    /// <summary>The pointer is over the window at <paramref name="band"/> (<see cref="EdgeHandleBand.At"/>; none: off the band or away).</summary>
    public void Hover(ResizeEdge band, TimeSpan now)
    {
        if (Dragging)
            return;
        if (band == ResizeEdge.None)
        {
            _waitForLeave = false;
            FadeTo(0, now);
            return;
        }

        if (_waitForLeave)
            return;
        Edge = band;
        FadeTo(1, now);
    }

    /// <summary>Whether the last poll found the pointer inside the window.</summary>
    public bool PolledInside => _polledInside;

    /// <summary>The band the last poll found, none when it found the pointer outside the window.</summary>
    public ResizeEdge PolledBand => _polledBand;

    /// <summary>
    /// A poll of the pointer's place: <paramref name="inside"/> the window (with none above it there) at
    /// <paramref name="band"/>; the hover follows it when <paramref name="drives"/> (the poll is the pointer's source),
    /// the first time and whenever it changed. True when it followed this poll.
    /// </summary>
    public bool Polled(bool inside, ResizeEdge band, bool drives, TimeSpan now)
    {
        var changed = inside != _polledInside || band != _polledBand;
        var drove = _polledDrove;
        (_polledInside, _polledBand, _polledDrove) = (inside, band, drives);
        if (!drives || (!changed && drove))
            return false;
        Hover(band, now);
        return true;
    }

    /// <summary>
    /// The left button went down on the window at <paramref name="band"/>: in the band a resize starts and its edge stays
    /// shown; elsewhere the press drags the window, and the handle fades out.
    /// </summary>
    public void Press(ResizeEdge band, TimeSpan now)
    {
        if (band == ResizeEdge.None)
        {
            Dragging = false;
            FadeTo(0, now);
            return;
        }

        Dragging = true;
        Edge = band;
        FadeTo(1, now);
    }

    /// <summary>
    /// A live-resize step moved <paramref name="moved"/> (<see cref="EdgeHandleBand.Moved"/>): the handle shows the edge
    /// the system actually resizes, a resize the press didn't announce included. Nothing while the window doesn't
    /// <paramref name="shows"/> handles (made non-resizable or click-through, or hidden, mid-resize).
    /// </summary>
    public void Resized(ResizeEdge moved, TimeSpan now, bool shows = true)
    {
        if (!shows || moved == ResizeEdge.None)
            return;
        Dragging = true;
        Edge = moved;
        FadeTo(1, now);
    }

    /// <summary>The window moved without resizing during a press: a drag of the window, not of an edge.</summary>
    public void Moved(TimeSpan now)
    {
        if (!Dragging && _to == 0)
            return;
        Dragging = false;
        FadeTo(0, now);
    }

    /// <summary>The left button is up again: a resize ends, and the handle fades out until the pointer leaves the band and comes back.</summary>
    public void Release(TimeSpan now)
    {
        if (!Dragging)
            return;
        Dragging = false;
        _waitForLeave = true;
        (_polledInside, _polledBand, _polledDrove) = (false, ResizeEdge.None, false); // the poll delivers what it finds again
        FadeTo(0, now);
    }

    /// <summary>The window stopped being resizable, lets clicks through, is hidden or is closed: no handle, at once.</summary>
    public void Disable(TimeSpan now)
    {
        Dragging = false;
        _waitForLeave = false;
        (_polledInside, _polledBand, _polledDrove) = (false, ResizeEdge.None, false); // the next poll starts afresh
        Edge = ResizeEdge.None;
        (_from, _to, _start, _duration) = (0, 0, now, TimeSpan.Zero);
    }

    private void FadeTo(double target, TimeSpan now)
    {
        var current = Opacity(now);
        if (target == _to)
            return;
        var full = target > current ? FadeIn : FadeOut;
        (_from, _to, _start, _duration) = (current, target, now, full * Math.Abs(target - current));
    }
}

/// <summary>Which pointer source moves a window's edge handles (<c>INFOPANEL_EDGE_HOVER</c>).</summary>
public enum EdgeHoverMode
{
    /// <summary>The tracking area, or the poll when the tracking area delivers nothing (<see cref="EdgeHoverChooser"/>).</summary>
    Auto,

    /// <summary>The tracking area only.</summary>
    Tracking,

    /// <summary>The poll only.</summary>
    Poll,

    /// <summary>As <see cref="Auto"/>, with both sources running and every event of each logged: the hover check's probe.</summary>
    Probe,
}

/// <summary>The source chosen: none yet, the window's tracking area, or the poll of the pointer's place.</summary>
public enum EdgeHoverSource
{
    Undecided,
    Tracking,
    Poll,
}

/// <summary>
/// Chooses, once per process, where the pointer's place comes from for the edge handles. A window's tracking area is
/// asked first: its first event decides for it. A slow watch of the pointer's place runs until then; once the pointer
/// has been over a window that would show handles for <see cref="Patience"/> with no tracking event, the poll is chosen
/// (fast while the pointer is within <see cref="Near"/> of such a window, slow otherwise). No platform code in it.
/// </summary>
public sealed class EdgeHoverChooser(EdgeHoverMode mode)
{
    /// <summary>How long the pointer stays over a window with no tracking event before the poll is chosen.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

    /// <summary>The poll's interval while the pointer is near a window: 30 times a second.</summary>
    public static readonly TimeSpan FastPoll = TimeSpan.FromSeconds(1.0 / 30);

    /// <summary>The poll's interval otherwise, and the watch's before the choice.</summary>
    public static readonly TimeSpan SlowPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>How near a window, in points, the pointer makes the poll fast.</summary>
    public const double Near = 40;

    private TimeSpan? _insideSince;

    public EdgeHoverMode Mode => mode;

    public EdgeHoverSource Source { get; private set; } = mode switch
    {
        EdgeHoverMode.Tracking => EdgeHoverSource.Tracking,
        EdgeHoverMode.Poll => EdgeHoverSource.Poll,
        _ => EdgeHoverSource.Undecided,
    };

    /// <summary>Whether the pointer's place must be polled: until the tracking area is chosen, and always in the probe.</summary>
    public bool Polls => Source != EdgeHoverSource.Tracking || mode == EdgeHoverMode.Probe;

    /// <summary>The poll's next interval, <paramref name="near"/> a window that would show handles or not.</summary>
    public TimeSpan Interval(bool near) =>
        near && (Source == EdgeHoverSource.Poll || mode == EdgeHoverMode.Probe) ? FastPoll : SlowPoll;

    /// <summary>A tracking event arrived: true when it moves the handles (the tracking area is, or now becomes, the source).</summary>
    public bool Tracked()
    {
        if (Source == EdgeHoverSource.Undecided)
            Source = EdgeHoverSource.Tracking;
        return Source == EdgeHoverSource.Tracking;
    }

    /// <summary>
    /// A poll found the pointer over a window that would show handles (<paramref name="inside"/>: within it, with no other
    /// window above it there) or not: true when the poll moves the handles (it is, or now becomes, the source).
    /// </summary>
    public bool Polled(bool inside, TimeSpan now)
    {
        if (Source == EdgeHoverSource.Undecided)
        {
            if (!inside)
                _insideSince = null;
            else if ((_insideSince ??= now) <= now - Patience)
                Source = EdgeHoverSource.Poll;
        }

        return Source == EdgeHoverSource.Poll;
    }

    /// <summary><c>INFOPANEL_EDGE_HOVER</c>'s value: <c>tracking</c>, <c>poll</c>, <c>probe</c>, else auto.</summary>
    public static EdgeHoverMode ModeOf(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "tracking" => EdgeHoverMode.Tracking,
        "poll" => EdgeHoverMode.Poll,
        "probe" => EdgeHoverMode.Probe,
        _ => EdgeHoverMode.Auto,
    };
}
