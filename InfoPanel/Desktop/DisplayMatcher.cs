using InfoPanel.Spec;

namespace InfoPanel.Desktop;

/// <summary>
/// Which display a profile's window goes on, and the conversions between a profile's window placement (its
/// <c>window.x</c>/<c>window.y</c>: pixels from its display's top-left, 1.x's <c>WindowX</c>/<c>WindowY</c>) and desktop points.
/// </summary>
public static class DisplayMatcher
{
    /// <summary>
    /// A target's display (spec/docs/document.md, <c>window.display</c>; 1.x's matching): the display that is the target's
    /// monitor (<see cref="Is"/>: its id, else its name and pixel size; among several, the one at the target's
    /// <c>x</c>/<c>y</c>); unless <paramref name="strict"/>, then the first with its name, then the first with its size,
    /// then the first display. Strict with no exact match: null. No target: the main (first) display. A display's name
    /// is compared as <see cref="TargetOf"/> writes it (<see cref="MatchName"/>), so a target written or listed from a
    /// display with a long name matches it.
    /// </summary>
    public static DisplayInfo? Match(IReadOnlyList<DisplayInfo> displays, DisplayMatch? target, bool strict)
    {
        if (target == null)
            return displays.FirstOrDefault();

        var match = Exact(displays, target);
        if (!strict)
        {
            match ??= displays.FirstOrDefault(d => MatchName(d) == target.Name);
            match ??= displays.FirstOrDefault(d => d.PixelWidth == target.Width && d.PixelHeight == target.Height);
            match ??= displays.FirstOrDefault();
        }

        return match;
    }

    /// <summary>The display that is <paramref name="target"/>'s monitor (<see cref="Is"/>): of several, the one at its top-left, else the first.</summary>
    private static DisplayInfo? Exact(IReadOnlyList<DisplayInfo> displays, DisplayMatch target)
    {
        var exact = displays.Where(d => Is(d, target)).ToList();
        return exact.FirstOrDefault(d => (int)Math.Round(d.Frame.X) == target.X && (int)Math.Round(d.Frame.Y) == target.Y) ?? exact.FirstOrDefault();
    }

    /// <summary>
    /// Whether <paramref name="display"/> is <paramref name="target"/>'s monitor: the same id when both have one (whatever
    /// its name and resolution now), else the same name and pixel size.
    /// </summary>
    private static bool Is(DisplayInfo display, DisplayMatch target) =>
        display.Id != null && target.Id != null
            ? display.Id == target.Id
            : MatchName(display) == target.Name && display.PixelWidth == target.Width && display.PixelHeight == target.Height;

    /// <summary>The most places a window remembers on other displays (<c>window.spots</c>).</summary>
    public const int MaxSpots = 8;

    /// <summary>Where the window goes: <paramref name="Display"/>, its top-left <paramref name="X"/>, <paramref name="Y"/> pixels from that display's.</summary>
    public readonly record struct WindowPlace(DisplayInfo Display, int X, int Y);

    /// <summary>
    /// Where the window goes (spec/docs/document.md, "The desktop window"): the first of its places whose display is
    /// connected, at that place's x, y: <c>display</c> (a missing one is the main display), then each of <c>spots</c>, the
    /// most recent first; a monitor now at another size than the place recorded pulls the window in (<see cref="OnMonitor"/>).
    /// With none connected: null (hidden) when <c>strict</c>, the default; else <see cref="Match"/>'s
    /// looser search on <c>display</c>, the window's x, y pulled in so that its <paramref name="width"/> ×
    /// <paramref name="height"/> pixels lie wholly on that display where they fit (never written: the document keeps its
    /// places). No displays: null.
    /// </summary>
    public static WindowPlace? Place(IReadOnlyList<DisplayInfo> displays, WindowSettings? window, int width, int height)
    {
        if (displays.Count == 0)
            return null;

        int x = window?.X ?? 0, y = window?.Y ?? 0;
        if (window?.Display is not { } target)
            return new WindowPlace(displays[0], x, y);
        if (Exact(displays, target) is { } display)
            return OnMonitor(display, target, x, y, width, height);
        foreach (var spot in window.Spots ?? [])
        {
            if (Exact(displays, spot.Display) is { } at)
                return OnMonitor(at, spot.Display, spot.X ?? 0, spot.Y ?? 0, width, height);
        }

        if (window.Strict ?? true)
            return null;

        return PulledIn(Match(displays, target, strict: false)!, x, y, width, height);
    }

    /// <summary>
    /// A place on its own monitor: as saved while the monitor is at the pixel size <paramref name="recorded"/> holds, an
    /// overhang included; at another size, <see cref="PulledIn"/> (never written), so a window saved at the right of a
    /// larger resolution isn't left off-screen.
    /// </summary>
    private static WindowPlace OnMonitor(DisplayInfo display, DisplayMatch recorded, int x, int y, int width, int height) =>
        display.PixelWidth == recorded.Width && display.PixelHeight == recorded.Height
            ? new WindowPlace(display, x, y)
            : PulledIn(display, x, y, width, height);

    /// <summary>The window at <paramref name="x"/>, <paramref name="y"/> on <paramref name="display"/>, moved only as far as it takes to lie wholly on it (<see cref="PullIn"/>).</summary>
    private static WindowPlace PulledIn(DisplayInfo display, int x, int y, int width, int height) =>
        new(display, PullIn(x, width, display.PixelWidth), PullIn(y, height, display.PixelHeight));

    /// <summary>A coordinate moved only as far as it takes for <paramref name="size"/> to lie within <paramref name="room"/>, never below 0.</summary>
    private static int PullIn(int at, int size, int room) => Math.Max(0, Math.Min(at, room - size));

    /// <summary>
    /// The window put on <paramref name="display"/> at <paramref name="x"/>, <paramref name="y"/> (a drag or resize that
    /// ends there): that becomes its <c>display</c>, the previous <c>display</c> with its x, y goes first in <c>spots</c>
    /// (none: nothing is remembered), a spot for the same monitor as <paramref name="display"/> (<see cref="SameMonitor"/>)
    /// is dropped and only the <see cref="MaxSpots"/> most recent are kept; no spots: <c>spots</c> is left out. Every other
    /// member is kept.
    /// </summary>
    public static WindowSettings Promote(WindowSettings window, DisplayMatch display, int x, int y)
    {
        var spots = new List<WindowSpot>(MaxSpots + 1);
        if (window.Display is { } previous)
            spots.Add(new WindowSpot { Display = previous, X = window.X, Y = window.Y });
        spots.AddRange(window.Spots ?? []);
        spots.RemoveAll(spot => SameMonitor(spot.Display, display));
        if (spots.Count > MaxSpots)
            spots.RemoveRange(MaxSpots, spots.Count - MaxSpots);

        return window with { Display = display, X = x, Y = y, Spots = spots.Count == 0 ? null : spots };
    }

    /// <summary>
    /// Two places on the same monitor: the same id when both have one, else the same name and size, and the same top-left
    /// when both know theirs (a place written without <c>x</c>/<c>y</c> is at an unknown one, not at 0, 0).
    /// </summary>
    private static bool SameMonitor(DisplayMatch a, DisplayMatch b) =>
        a.Id != null && b.Id != null
            ? a.Id == b.Id
            : a.Name == b.Name && a.Width == b.Width && a.Height == b.Height
              && (a.X == null || a.Y == null || b.X == null || b.Y == null || (a.X == b.X && a.Y == b.Y));

    /// <summary>
    /// A monitor's <c>id</c> (<c>DEL40F4</c>): its EDID's maker, the PNP id packed into bytes 8 and 9 read as a
    /// big-endian number (as macOS' <c>CGDisplayVendorNumber</c> gives it: three 5-bit letters, A = 1, the top bit 0), and
    /// its 16-bit product code as four hex digits. Null when <paramref name="maker"/> is no PNP id (0, a letter outside
    /// A–Z, macOS' <c>unkn</c> for a display without an EDID) or <paramref name="product"/> is over 16 bits. No serial
    /// number: monitors of one model share an id.
    /// </summary>
    public static string? IdOf(uint maker, uint product)
    {
        if (maker > 0x7FFF || product > 0xFFFF)
            return null;

        Span<char> id = stackalloc char[7];
        for (var i = 0; i < 3; i++)
        {
            var letter = (maker >> (10 - 5 * i)) & 31;
            if (letter is < 1 or > 26)
                return null;
            id[i] = (char)('A' + letter - 1);
        }

        product.TryFormat(id[3..], out _, "X4");
        return new string(id);
    }

    /// <summary>The display containing the point, else the nearest one; null when there are none.</summary>
    public static DisplayInfo? At(IReadOnlyList<DisplayInfo> displays, double x, double y) =>
        displays.FirstOrDefault(d => d.Frame.Contains(x, y))
        ?? displays.OrderBy(d => d.Frame.DistanceSquared(x, y)).FirstOrDefault();

    /// <summary>
    /// The window's frame in desktop points: its top-left <paramref name="windowX"/>, <paramref name="windowY"/>
    /// pixels from <paramref name="display"/>'s top-left (pixels of that display), its size
    /// <paramref name="width"/> × <paramref name="height"/> pixels at <paramref name="sizeScale"/> (the backing scale
    /// of the display the window is on), so one frame pixel is one backing pixel.
    /// </summary>
    public static PointRect Frame(DisplayInfo display, int windowX, int windowY, int width, int height, double sizeScale) =>
        new(display.Frame.X + windowX / display.Scale,
            display.Frame.Y + windowY / display.Scale,
            width / sizeScale,
            height / sizeScale);

    /// <summary>The window's top-left as pixels from <paramref name="display"/>'s top-left (1.x's <c>WindowX</c>/<c>WindowY</c>).</summary>
    public static (int X, int Y) Position(DisplayInfo display, PointRect frame) =>
        ((int)Math.Round((frame.X - display.Frame.X) * display.Scale),
         (int)Math.Round((frame.Y - display.Frame.Y) * display.Scale));

    /// <summary>
    /// The <c>window.display</c> written after a drag (1.x's <c>TargetWindow</c>): the monitor's id when it reports one,
    /// the display's name, its size in backing pixels and its top-left in desktop points (which only tells equal displays
    /// apart).
    /// </summary>
    public static DisplayMatch TargetOf(DisplayInfo display) => new()
    {
        Id = display.Id,
        Name = MatchName(display),
        Width = display.PixelWidth,
        Height = display.PixelHeight,
        X = (int)Math.Round(display.Frame.X),
        Y = (int)Math.Round(display.Frame.Y),
    };

    /// <summary>The most Unicode characters (code points) a <c>window.display.name</c> holds, and so a listed display's name and label.</summary>
    public const int NameLength = 256;

    /// <summary>
    /// The display's name as a <c>window.display.name</c> holds it: <see cref="Cut"/> to its 256 characters, the longest
    /// the document takes.
    /// </summary>
    public static string MatchName(DisplayInfo display) => Cut(display.Name);

    /// <summary>
    /// <paramref name="text"/> cut to at most <see cref="NameLength"/> Unicode characters, as the schema's <c>maxLength</c>
    /// counts them: never inside a surrogate pair, which would leave a lone surrogate that JSON writes as U+FFFD and a
    /// comparison keeps.
    /// </summary>
    public static string Cut(string text)
    {
        if (text.Length <= NameLength)
            return text;

        var end = 0;
        for (var count = 0; count < NameLength && end < text.Length; count++)
            end += char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
        return text[..end];
    }

    /// <summary>True when two frames are the same to within a thousandth of a point.</summary>
    public static bool SameFrame(PointRect a, PointRect b) =>
        Math.Abs(a.X - b.X) < 1e-3 && Math.Abs(a.Y - b.Y) < 1e-3 && Math.Abs(a.Width - b.Width) < 1e-3 && Math.Abs(a.Height - b.Height) < 1e-3;
}
