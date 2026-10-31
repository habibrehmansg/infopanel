namespace InfoPanel.Desktop.Windows;

/// <summary>
/// Where a point of a desktop window lands for <c>WM_NCHITTEST</c> (no Win32 in it, so it is tested on any OS): within
/// the resize band of an edge or corner, that edge or corner when the window is resizable; elsewhere the caption when it
/// is movable, so the system's own move loop drags it; otherwise the client. Windows then runs its own move or size loop
/// and shows its own sizing cursors, as 1.4.x's <c>DragMove</c> and <c>WindowChrome</c> did.
/// </summary>
internal static class WinHitTest
{
    public const int HTNOWHERE = 0;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    /// <summary>The resize band in device-independent pixels (1.4.x's <c>ResizeBorderThickness="10"</c>).</summary>
    public const int BandDips = 10;

    /// <summary>The resize band in pixels at <paramref name="dpi"/> (96 is 100 %), rounded half up as <c>MulDiv</c>.</summary>
    public static int Band(int dpi) => (int)Math.Round(BandDips * (double)dpi / 96, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The hit-test code of the pixel (<paramref name="x"/>, <paramref name="y"/>), relative to the window's top-left,
    /// of a <paramref name="width"/> × <paramref name="height"/> window at <paramref name="dpi"/>. The band is at most a
    /// third of each side, so a small window keeps a body to drag.
    /// </summary>
    public static int Hit(int x, int y, int width, int height, int dpi, DesktopWindowStyle style)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
            return HTNOWHERE;

        if (style.Resizable)
        {
            var band = Band(dpi);
            var bandX = Math.Min(band, width / 3);
            var bandY = Math.Min(band, height / 3);
            var left = x < bandX;
            var right = x >= width - bandX;
            var top = y < bandY;
            var bottom = y >= height - bandY;
            if (top)
                return left ? HTTOPLEFT : right ? HTTOPRIGHT : HTTOP;
            if (bottom)
                return left ? HTBOTTOMLEFT : right ? HTBOTTOMRIGHT : HTBOTTOM;
            if (left)
                return HTLEFT;
            if (right)
                return HTRIGHT;
        }

        return style.Movable ? HTCAPTION : HTCLIENT;
    }

    /// <summary>Whether <paramref name="hit"/> starts the system's move or size loop.</summary>
    public static bool StartsLoop(int hit) => hit == HTCAPTION || hit is >= HTLEFT and <= HTBOTTOMRIGHT;
}
