using InfoPanel.Spec;

namespace InfoPanel.Desktop;

/// <summary>
/// Which display a profile's window goes on, and the conversions between a profile's window placement (its
/// <c>window.x</c>/<c>window.y</c>: pixels from its display's top-left, 1.x's <c>WindowX</c>/<c>WindowY</c>) and desktop points.
/// </summary>
public static class DisplayMatcher
{
    /// <summary>
    /// The window's display (spec/docs/document.md, <c>window.display</c>; 1.x's matching): the display with the
    /// target's name and pixel size (among several, the one at the target's <c>x</c>/<c>y</c>); unless
    /// <paramref name="strict"/>, then the first with its name, then the first with its size, then the first display.
    /// Strict with no exact match: null, and the window is hidden. No target: the main (first) display. A display's name
    /// is compared as <see cref="TargetOf"/> writes it (<see cref="MatchName"/>), so a target written or listed from a
    /// display with a long name matches it.
    /// </summary>
    public static DisplayInfo? Match(IReadOnlyList<DisplayInfo> displays, DisplayMatch? target, bool strict)
    {
        if (target == null)
            return displays.FirstOrDefault();

        var exact = displays.Where(d => MatchName(d) == target.Name && d.PixelWidth == target.Width && d.PixelHeight == target.Height).ToList();
        var match = exact.FirstOrDefault(d => (int)Math.Round(d.Frame.X) == target.X && (int)Math.Round(d.Frame.Y) == target.Y) ?? exact.FirstOrDefault();
        if (!strict)
        {
            match ??= displays.FirstOrDefault(d => MatchName(d) == target.Name);
            match ??= displays.FirstOrDefault(d => d.PixelWidth == target.Width && d.PixelHeight == target.Height);
            match ??= displays.FirstOrDefault();
        }

        return match;
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
    /// The <c>window.display</c> written after a drag (1.x's <c>TargetWindow</c>): the display's name, its size in
    /// backing pixels and its top-left in desktop points (which only tells equal displays apart).
    /// </summary>
    public static DisplayMatch TargetOf(DisplayInfo display) => new()
    {
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
