using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// Windows' monitors and their friendly names as displays (on any OS: the mapping has no Win32 in it): the primary
/// first, then by device name; physical-pixel rectangles of the virtual screen, so scale 1; the friendly name as the
/// label, none when it is empty or missing; one friendly name per source.
/// </summary>
public sealed class WinDisplaysTests
{
    private static readonly Dictionary<string, string> NoNames = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ThePrimaryComesFirst_ThenTheOthersByDeviceName()
    {
        var displays = WinDisplays.Map(
        [
            new MonitorRecord(@"\\.\DISPLAY3", 2560, 0, 4480, 1080, false),
            new MonitorRecord(@"\\.\DISPLAY10", -1920, 0, 0, 1080, false),
            new MonitorRecord(@"\\.\DISPLAY2", 0, 0, 2560, 1440, true),
            new MonitorRecord(@"\\.\DISPLAY1", 0, 1440, 1920, 2520, false),
        ], NoNames);

        Assert.Equal([@"\\.\DISPLAY2", @"\\.\DISPLAY1", @"\\.\DISPLAY10", @"\\.\DISPLAY3"], displays.Select(d => d.Name));
    }

    [Fact]
    public void Rectangles_ArePhysicalPixels_LeftOfAndAboveThePrimaryNegative_AtScale1()
    {
        var displays = WinDisplays.Map(
        [
            new MonitorRecord(@"\\.\DISPLAY1", 0, 0, 2560, 1440, true),
            new MonitorRecord(@"\\.\DISPLAY2", -3840, -360, 0, 1800, false),
        ], NoNames);

        Assert.Equal(new PointRect(0, 0, 2560, 1440), displays[0].Frame);
        Assert.Equal(new PointRect(-3840, -360, 3840, 2160), displays[1].Frame);
        Assert.All(displays, d => Assert.Equal(1, d.Scale));
        Assert.Equal((3840, 2160), (displays[1].PixelWidth, displays[1].PixelHeight));

        // What a profile's window.display gets from it: 1.4.x's TargetWindow field for field.
        var target = DisplayMatcher.TargetOf(displays[1]);
        Assert.Equal((@"\\.\DISPLAY2", 3840, 2160, -3840, -360), (target.Name, target.Width, target.Height, target.X, target.Y));
    }

    [Fact]
    public void TheLabel_IsTheFriendlyName_NullWhenItIsEmptyOrMissing()
    {
        var names = WinDisplays.FriendlyNames(
        [
            (@"\\.\DISPLAY1", "DELL U2723QE"),
            (@"\\.\DISPLAY2", ""),
            (@"\\.\DISPLAY3", "  LG ULTRAWIDE  "),
        ]);
        var displays = WinDisplays.Map(
        [
            new MonitorRecord(@"\\.\DISPLAY1", 0, 0, 3840, 2160, true),
            new MonitorRecord(@"\\.\DISPLAY2", 3840, 0, 5760, 1080, false),
            new MonitorRecord(@"\\.\DISPLAY3", 5760, 0, 9200, 1440, false),
            new MonitorRecord(@"\\.\DISPLAY4", 9200, 0, 11120, 1080, false),
        ], names);

        Assert.Equal(["DELL U2723QE", null, "LG ULTRAWIDE", null], displays.Select(d => d.Label));
        Assert.Equal(["DELL U2723QE", @"\\.\DISPLAY2", "LG ULTRAWIDE", @"\\.\DISPLAY4"], displays.Select(d => d.DisplayLabel));
    }

    [Fact]
    public void TwoPathsOnOneSource_GiveItsFirstFriendlyName()
    {
        // Mirrored displays: one source, two targets. The first non-empty name wins; device names match in any case.
        var names = WinDisplays.FriendlyNames(
        [
            (@"\\.\DISPLAY1", ""),
            (@"\\.\DISPLAY1", "Projector"),
            (@"\\.\display1", "TV"),
            (null, "Nothing"),
        ]);

        Assert.Equal("Projector", Assert.Single(names).Value);
        var display = Assert.Single(WinDisplays.Map([new MonitorRecord(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true)], names));
        Assert.Equal("Projector", display.Label);
    }
}
