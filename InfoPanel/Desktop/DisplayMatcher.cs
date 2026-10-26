using InfoPanel.Models;

namespace InfoPanel.Desktop;

/// <summary>
/// Which display a profile's window goes on, and the conversions between a profile's window placement (pixels from
/// its display's top-left, 1.x's <c>WindowX</c>/<c>WindowY</c>) and desktop points.
/// </summary>
public static class DisplayMatcher
{
    /// <summary>
    /// 1.x's matching (<c>DisplayWindow.SetWindowPositionRelativeToScreenCore</c>): the display with the target's
    /// name and pixel size; unless <paramref name="strict"/>, then the first with its name, then the first with its
    /// size, then the first display. Strict with no exact match (or no target): null, and the window is hidden.
    /// </summary>
    public static DisplayInfo? Match(IReadOnlyList<DisplayInfo> displays, TargetWindow? target, bool strict)
    {
        DisplayInfo? match = null;
        if (target != null)
        {
            match = displays.FirstOrDefault(d => d.Name == target.DeviceName && d.PixelWidth == target.Width && d.PixelHeight == target.Height);
            if (!strict)
            {
                match ??= displays.FirstOrDefault(d => d.Name == target.DeviceName);
                match ??= displays.FirstOrDefault(d => d.PixelWidth == target.Width && d.PixelHeight == target.Height);
            }
        }

        if (!strict)
            match ??= displays.FirstOrDefault();

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
    /// The <see cref="TargetWindow"/> 1.x writes after a drag: the display's top-left in desktop points (identity
    /// only; matching ignores it), its size in backing pixels and its name.
    /// </summary>
    public static TargetWindow TargetOf(DisplayInfo display) =>
        new((int)Math.Round(display.Frame.X), (int)Math.Round(display.Frame.Y), display.PixelWidth, display.PixelHeight, display.Name);

    /// <summary>True when two frames are the same to within a thousandth of a point.</summary>
    public static bool SameFrame(PointRect a, PointRect b) =>
        Math.Abs(a.X - b.X) < 1e-3 && Math.Abs(a.Y - b.Y) < 1e-3 && Math.Abs(a.Width - b.Width) < 1e-3 && Math.Abs(a.Height - b.Height) < 1e-3;
}
