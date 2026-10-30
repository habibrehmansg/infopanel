namespace InfoPanel.Desktop;

/// <summary>
/// A rectangle in desktop points: the platform's global coordinate space with the origin at the primary display's
/// top-left and y growing down (on macOS, AppKit's y-up space flipped about the primary display).
/// </summary>
public readonly record struct PointRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;

    /// <summary>The squared distance from (<paramref name="x"/>, <paramref name="y"/>) to the nearest point of the rectangle.</summary>
    public double DistanceSquared(double x, double y)
    {
        var dx = Math.Max(Math.Max(X - x, 0), x - (X + Width));
        var dy = Math.Max(Math.Max(Y - y, 0), y - (Y + Height));
        return dx * dx + dy * dy;
    }

    public override string ToString() => $"({X}, {Y}) {Width}×{Height}";
}

/// <summary>
/// A display: its <paramref name="Name"/>, which a profile's <c>window.display</c> matches (macOS:
/// <c>NSScreen.localizedName</c>), its <paramref name="Frame"/> in desktop points, its <paramref name="Scale"/> (backing
/// pixels per point), and its <paramref name="Label"/>, the name the system shows people, where that isn't
/// <paramref name="Name"/> (null: the same).
/// </summary>
public sealed record DisplayInfo(string Name, PointRect Frame, double Scale, string? Label = null)
{
    /// <summary>The display's name for people: <see cref="Label"/>, else <see cref="Name"/>.</summary>
    public string DisplayLabel => Label ?? Name;

    /// <summary>The display's width in backing pixels.</summary>
    public int PixelWidth => (int)Math.Round(Frame.Width * Scale);

    /// <summary>The display's height in backing pixels.</summary>
    public int PixelHeight => (int)Math.Round(Frame.Height * Scale);
}

/// <summary>How a desktop window behaves: always on top, movable by dragging, resizable from its edges.</summary>
public readonly record struct DesktopWindowStyle(bool Topmost, bool Movable, bool Resizable);

/// <summary>
/// One frame for a desktop window: RGBA, premultiplied (<c>SKColorType.Rgba8888</c>, <c>SKAlphaType.Premul</c>),
/// <paramref name="RowBytes"/> apart. <paramref name="Pixels"/> may be longer than the frame (a pooled buffer).
/// <paramref name="Scale"/> is the scale it was rendered at: 1, or less for a profile over the frame pixel cap.
/// </summary>
public sealed record DesktopFrame(byte[] Pixels, int Width, int Height, int RowBytes, float Scale = 1);

/// <summary>
/// The operating system's windows, for <see cref="DisplayWindowManager"/>. Everything but <see cref="Post"/> must be
/// called on the platform's UI thread (the process main thread on macOS), and every event is raised there.
/// </summary>
public interface IDesktopPlatform
{
    /// <summary>False when this platform can't show windows (see <see cref="UnsupportedMessage"/>).</summary>
    bool IsSupported { get; }

    /// <summary>Logged once when a profile would have been shown but <see cref="IsSupported"/> is false; null: say nothing.</summary>
    string? UnsupportedMessage { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread, after the actions posted before it. Any thread.</summary>
    void Post(Action action);

    /// <summary>The displays, the primary first.</summary>
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>A new, hidden, borderless window at <paramref name="frame"/>.</summary>
    IDesktopWindow CreateWindow(DesktopWindowStyle style, PointRect frame);

    /// <summary>The display configuration changed (added, removed, rearranged, rescaled).</summary>
    event Action? DisplaysChanged;
}

/// <summary>A borderless desktop window showing frames. UI thread only. Dispose closes it.</summary>
public interface IDesktopWindow : IDisposable
{
    /// <summary>The window's frame in desktop points.</summary>
    PointRect Frame { get; }

    /// <summary>Backing pixels per point of the display the window is on.</summary>
    double BackingScale { get; }

    /// <summary>The user may be dragging the window: true from the press on it until just before <see cref="UserMoved"/>.</summary>
    bool UserMoving { get; }

    bool Visible { get; }

    DesktopWindowStyle Style { get; }

    /// <summary>Moves and sizes the window. Never raises <see cref="UserMoved"/> or <see cref="UserResized"/>.</summary>
    void SetFrame(PointRect frame);

    void SetStyle(DesktopWindowStyle style);

    /// <summary>Shows the window without activating the app, or hides it.</summary>
    void SetVisible(bool visible);

    /// <summary>
    /// Shows <paramref name="frame"/> anchored top-left at one frame pixel per backing pixel, or, rendered at a
    /// <see cref="DesktopFrame.Scale"/> below 1, stretched by 1 / <c>Scale</c> to fill the window. The pixels are copied.
    /// </summary>
    void ShowFrame(DesktopFrame frame);

    /// <summary>
    /// The mouse button was released after a press on the window (raised once per press, whether or not the window
    /// moved: a drag, a click, the end of a resize).
    /// </summary>
    event Action? UserMoved;

    /// <summary>The user is resizing the window (raised for each change while resizing).</summary>
    event Action? UserResized;

    /// <summary>The window's <see cref="BackingScale"/> changed (it moved to a display of another scale).</summary>
    event Action? BackingScaleChanged;
}
