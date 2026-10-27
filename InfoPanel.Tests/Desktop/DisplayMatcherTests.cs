using InfoPanel.Desktop;
using InfoPanel.Spec;
using Xunit;

namespace InfoPanel.Tests.Desktop;

/// <summary>Display matching (<c>window.display</c>, 1.x's rules) and the point/pixel conversions.</summary>
public sealed class DisplayMatcherTests
{
    private static readonly DisplayInfo Primary = new("Built-in", new PointRect(0, 0, 1512, 982), 2);       // 3024×1964 px
    private static readonly DisplayInfo Wide = new("Monitor", new PointRect(1512, 0, 2560, 1440), 1);       // 2560×1440 px
    private static readonly DisplayInfo Twin = new("Monitor", new PointRect(-1920, 0, 1920, 1080), 1);      // 1920×1080 px
    private static readonly DisplayInfo[] All = [Primary, Wide, Twin];

    private static DisplayMatch Target(string name, int width, int height, int? x = null, int? y = null) =>
        new() { Name = name, Width = width, Height = height, X = x, Y = y };

    [Theory]
    [InlineData("Monitor", 1920, 1080, false, "Monitor", 1920)]  // name and size: the second "Monitor", not the first
    [InlineData("Monitor", 1920, 1080, true, "Monitor", 1920)]   // strict too
    [InlineData("Monitor", 800, 600, false, "Monitor", 2560)]    // name only: the first with the name
    [InlineData("Other", 2560, 1440, false, "Monitor", 2560)]    // size only
    [InlineData("Other", 3024, 1964, false, "Built-in", 3024)]
    [InlineData("Other", 800, 600, false, "Built-in", 3024)]     // nothing: the first display
    [InlineData("Monitor", 800, 600, true, null, 0)]             // strict: name and size or nothing
    [InlineData("Other", 2560, 1440, true, null, 0)]
    public void Match_FollowsOneX(string name, int width, int height, bool strict, string? expectedName, int expectedWidth)
    {
        var match = DisplayMatcher.Match(All, Target(name, width, height), strict);
        Assert.Equal(expectedName, match?.Name);
        if (match != null)
            Assert.Equal(expectedWidth, match.PixelWidth);
    }

    [Fact]
    public void Match_WithoutATarget_IsTheMainDisplay_StrictOrNot()
    {
        // spec/docs/document.md: an absent display is the main display.
        Assert.Same(Primary, DisplayMatcher.Match(All, null, strict: false));
        Assert.Same(Primary, DisplayMatcher.Match(All, null, strict: true));
        Assert.Null(DisplayMatcher.Match([], Target("Monitor", 1920, 1080), strict: false));
    }

    [Fact]
    public void Match_AmongEqualDisplays_IsTheOneAtTheTargetsXAndY()
    {
        var left = new DisplayInfo("Panel", new PointRect(-800, 0, 800, 480), 1);
        var right = new DisplayInfo("Panel", new PointRect(3000, 0, 800, 480), 1);
        DisplayInfo[] displays = [Primary, left, right];
        Assert.Same(right, DisplayMatcher.Match(displays, Target("Panel", 800, 480, 3000, 0), strict: true));
        Assert.Same(left, DisplayMatcher.Match(displays, Target("Panel", 800, 480, 1, 1), strict: true)); // no such place: the first equal one
        Assert.Same(left, DisplayMatcher.Match(displays, Target("Panel", 800, 480), strict: false));
    }

    [Fact]
    public void FrameAndPosition_ArePixelsOfTheDisplay_RoundTripped()
    {
        var frame = DisplayMatcher.Frame(Primary, 201, 100, 800, 480, 2);
        Assert.Equal(new PointRect(100.5, 50, 400, 240), frame);
        Assert.Equal((201, 100), DisplayMatcher.Position(Primary, frame));

        var onWide = DisplayMatcher.Frame(Wide, 10, 20, 800, 480, 1);
        Assert.Equal(new PointRect(1522, 20, 800, 480), onWide);
        Assert.Equal((10, 20), DisplayMatcher.Position(Wide, onWide));
    }

    [Fact]
    public void At_IsTheDisplayHoldingThePoint_ElseTheNearest()
    {
        Assert.Same(Wide, DisplayMatcher.At(All, 1600, 10));
        Assert.Same(Twin, DisplayMatcher.At(All, -5, 10));
        Assert.Same(Primary, DisplayMatcher.At(All, 100, -30)); // above the primary, off every display
        Assert.Null(DisplayMatcher.At([], 0, 0));
    }

    [Fact]
    public void TargetOf_IsTheDisplaysOriginPixelSizeAndName()
    {
        var target = DisplayMatcher.TargetOf(Primary);
        Assert.Equal((0, 0, 3024, 1964, "Built-in"), (target.X, target.Y, target.Width, target.Height, target.Name));
    }
}
