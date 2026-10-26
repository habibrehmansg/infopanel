namespace InfoPanel.Desktop;

/// <summary>
/// The platform where desktop windows can't be shown (Windows and Linux for now, <c>--no-windows</c>, an entry point
/// off the main thread on macOS). <see cref="DisplayWindowManager"/> never creates windows on it and logs
/// <see cref="UnsupportedMessage"/> once when a profile would have been shown.
/// </summary>
public sealed class NullDesktopPlatform(string? unsupportedMessage) : IDesktopPlatform
{
    public bool IsSupported => false;

    public string? UnsupportedMessage { get; } = unsupportedMessage;

    public event Action? DisplaysChanged
    {
        add { }
        remove { }
    }

    public void Post(Action action) => action();

    public IReadOnlyList<DisplayInfo> GetDisplays() => [];

    public IDesktopWindow CreateWindow(DesktopWindowStyle style, PointRect frame) =>
        throw new NotSupportedException("Desktop windows aren't supported here.");
}
