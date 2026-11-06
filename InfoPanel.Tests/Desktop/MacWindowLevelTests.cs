using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using InfoPanel.Desktop;
using InfoPanel.Desktop.MacOS;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>
/// The macOS window level and collection behaviour of each <see cref="WindowLevel"/> (macOS only, every run; no window
/// opens): normal 0 and top 3 (floating), both on every Space and out of window cycling; desktop one level above the
/// desktop icons, below every normal window, and stationary as well (unaffected by Mission Control and Show Desktop).
/// </summary>
public sealed partial class MacWindowLevelTests
{
    private const nuint CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4, IgnoresCycle = 1 << 6;

    /// <summary><c>kCGDesktopWindowLevelKey</c> and <c>kCGDesktopIconWindowLevelKey</c> (CoreGraphics' <c>CGWindowLevel.h</c>).</summary>
    internal const int DesktopKey = 2, DesktopIconKey = 18;

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    internal static partial int CGWindowLevelForKey(int key);

    [Fact]
    public void EachLevel_HasItsWindowLevelAndCollectionBehaviour()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS's window levels.");
        if (OperatingSystem.IsMacOS())
            Check();
    }

    [SupportedOSPlatform("macos")]
    private static void Check()
    {
        var icons = CGWindowLevelForKey(DesktopIconKey);
        Assert.Equal(CGWindowLevelForKey(DesktopKey) + 20, icons); // the header's kCGDesktopIconWindowLevel

        Assert.Equal(((nint)0, CanJoinAllSpaces | IgnoresCycle), MacDesktopPlatform.Placement(WindowLevel.Normal));
        Assert.Equal(((nint)3, CanJoinAllSpaces | IgnoresCycle), MacDesktopPlatform.Placement(WindowLevel.Top));

        var (desktop, behaviour) = MacDesktopPlatform.Placement(WindowLevel.Desktop);
        Assert.Equal(icons + 1, desktop);
        Assert.True(desktop < 0, $"{desktop} is below the normal level");
        Assert.Equal(CanJoinAllSpaces | Stationary | IgnoresCycle, behaviour);
    }
}
