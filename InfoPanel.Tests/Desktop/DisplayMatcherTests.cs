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

    // A window's places (spec/docs/document.md, "The desktop window"): monitors that report their EDID's id.
    private static readonly DisplayInfo Laptop = new("Built-in Retina Display", new PointRect(0, 0, 1512, 982), 2, Id: "APPA050"); // 3024×1964 px
    private static readonly DisplayInfo Work = new("DELL U2720Q", new PointRect(-408, -1440, 2560, 1440), 1, Id: "DEL40F4");      // 2560×1440 px
    private static readonly DisplayInfo Home = new("LG HDR 4K", new PointRect(1512, 0, 1920, 1080), 2, Id: "GSM5B7F");            // 3840×2160 px

    private static DisplayMatch TargetOf(DisplayInfo display) => DisplayMatcher.TargetOf(display);

    private static WindowSpot Spot(DisplayInfo display, int x, int y) => new() { Display = TargetOf(display), X = x, Y = y };

    /// <summary>A window placed at <paramref name="x"/>, <paramref name="y"/> on <paramref name="display"/>, remembering <paramref name="spots"/>.</summary>
    private static WindowSettings On(DisplayInfo display, int x, int y, params WindowSpot[] spots) =>
        new() { Enabled = true, X = x, Y = y, Display = TargetOf(display), Spots = spots.Length == 0 ? null : spots };

    private static DisplayMatcher.WindowPlace At(DisplayInfo display, int x, int y) => new(display, x, y);

    /// <summary>
    /// A monitor's id from its EDID's maker word (bytes 8-9 as a big-endian number, as CoreGraphics'
    /// <c>CGDisplayVendorNumber</c> gives it: three 5-bit letters, A = 1) and product code, for known monitors' codes.
    /// </summary>
    [Theory]
    [InlineData(0x10ACu, 0x40F4u, "DEL40F4")]     // Dell
    [InlineData(0x0610u, 0xA050u, "APPA050")]     // Apple
    [InlineData(0x1E6Du, 0x5B7Fu, "GSM5B7F")]     // LG
    [InlineData(0x4C2Du, 0x0F0Bu, "SAM0F0B")]     // Samsung
    [InlineData(0x10ACu, 0x0007u, "DEL0007")]     // four hex digits, always
    [InlineData(0u, 0x1234u, null)]               // no maker
    [InlineData(0x756E6B6Eu, 0x0717u, null)]      // macOS's "unkn": a display without an EDID
    [InlineData(0x90ACu, 0x40F4u, null)]          // the top bit set: no PNP id
    [InlineData(0x0001u, 0x40F4u, null)]          // a letter 0
    [InlineData(0x7FFFu, 0x40F4u, null)]          // letters past Z
    [InlineData(0x10ACu, 0x10000u, null)]         // a product code over 16 bits
    public void IdOf_IsThePnpMakerAndTheProductCode(uint maker, uint product, string? id) =>
        Assert.Equal(id, DisplayMatcher.IdOf(maker, product));

    [Fact]
    public void TargetOf_CarriesTheDisplaysId_WhenItReportsOne()
    {
        Assert.Equal("DEL40F4", TargetOf(Work).Id);
        Assert.Null(TargetOf(Primary).Id);
    }

    [Fact]
    public void Place_IsTheMostRecentPlaceWhoseDisplayIsConnected()
    {
        var window = On(Work, 100, 50, Spot(Home, 300, 60), Spot(Laptop, 10, 20));

        Assert.Equal(At(Work, 100, 50), DisplayMatcher.Place([Laptop, Home, Work], window, 800, 480)); // both known: the most recent
        Assert.Equal(At(Home, 300, 60), DisplayMatcher.Place([Laptop, Home], window, 800, 480));
        Assert.Equal(At(Laptop, 10, 20), DisplayMatcher.Place([Laptop], window, 800, 480));
    }

    [Fact]
    public void Place_MatchesAMonitorByItsId_BeforeItsNameAndSize()
    {
        // The same monitor at another resolution, under another name: the same id.
        var rescaled = new DisplayInfo("Dell monitor", new PointRect(-408, -1440, 1920, 1080), 1, Id: "DEL40F4");
        Assert.Equal(At(rescaled, 100, 50), DisplayMatcher.Place([Laptop, rescaled], On(Work, 100, 50), 800, 480));

        // Another model with the same name and size isn't it: strict (the default), the window hides.
        Assert.Null(DisplayMatcher.Place([Laptop, Work with { Id = "DEL4100" }], On(Work, 100, 50), 800, 480));

        // A place written before ids matches by name and size as before, and so does a display that reports none.
        var withoutId = On(Work, 100, 50) with { Display = TargetOf(Work) with { Id = null } };
        Assert.Equal(At(Work, 100, 50), DisplayMatcher.Place([Laptop, Work], withoutId, 800, 480));
        var reportsNone = Work with { Id = null };
        Assert.Equal(At(reportsNone, 100, 50), DisplayMatcher.Place([Laptop, reportsNone], On(Work, 100, 50), 800, 480));
    }

    [Fact]
    public void SameModelMonitors_ShareASpot()
    {
        // The same model at work and at home: one id, so the place found at work is the place at home.
        var homeTwin = new DisplayInfo("DELL U2720Q", new PointRect(1512, 0, 2560, 1440), 1, Id: "DEL40F4");
        Assert.Equal(At(homeTwin, 100, 50), DisplayMatcher.Place([Laptop, homeTwin], On(Work, 100, 50, Spot(Laptop, 0, 0)), 800, 480));

        // Both connected at once: the one at the place's top-left, else the first listed.
        Assert.Equal(At(homeTwin, 100, 50), DisplayMatcher.Place([Laptop, Work, homeTwin], On(homeTwin, 100, 50), 800, 480));
        var elsewhere = On(Work, 100, 50) with { Display = TargetOf(Work) with { X = 9000 } };
        Assert.Equal(At(Work, 100, 50), DisplayMatcher.Place([Laptop, Work, homeTwin], elsewhere, 800, 480));

        // Dragged from one onto the other, the second's place replaces the first's: one spot for both.
        var dragged = DisplayMatcher.Promote(On(Work, 100, 50), TargetOf(homeTwin), 7, 8);
        Assert.Equal(On(homeTwin, 7, 8), dragged);
        Assert.Null(dragged.Spots);
    }

    [Fact]
    public void Place_NoneConnected_HidesTheWindow_StrictByDefault()
    {
        var window = On(Work, 100, 50, Spot(Home, 300, 60));
        Assert.Null(window.Strict);
        Assert.Null(DisplayMatcher.Place([Laptop], window, 800, 480));
        Assert.Null(DisplayMatcher.Place([Laptop], window with { Strict = true }, 800, 480));
    }

    [Fact]
    public void Place_NoneConnected_NotStrict_IsTheLooseMatchOfItsDisplay_ElseTheMainOne_PulledInWhereItFits()
    {
        var window = On(Work, 2000, 1200, Spot(Home, 300, 60)) with { Strict = false };

        // The main display (3024×1964 pixels): the window keeps its place where it lies wholly on it, else is pulled in.
        Assert.Equal(At(Laptop, 2000, 1200), DisplayMatcher.Place([Laptop], window, 800, 480));
        Assert.Equal(At(Laptop, 3024 - 1200, 1964 - 900), DisplayMatcher.Place([Laptop], window, 1200, 900));
        Assert.Equal(At(Laptop, 0, 0), DisplayMatcher.Place([Laptop], window with { X = -50, Y = -20 }, 800, 480));
        Assert.Equal(At(Laptop, 0, 0), DisplayMatcher.Place([Laptop], window, 4000, 2000)); // larger than the display: top-left 0, 0

        // Before the main display: the first with its display's name (another model, 1920×1080), else with its size.
        var sameName = new DisplayInfo("DELL U2720Q", new PointRect(1512, 0, 1920, 1080), 1, Id: "DEL4100");
        var sameSize = new DisplayInfo("Projector", new PointRect(1512, 0, 2560, 1440), 1);
        Assert.Equal(At(sameName, 1920 - 800, 1080 - 480), DisplayMatcher.Place([Laptop, sameSize, sameName], window, 800, 480));
        Assert.Equal(At(sameSize, 2000, 1440 - 720), DisplayMatcher.Place([Laptop, sameSize], window, 560, 720));
    }

    [Fact]
    public void Place_WithoutADisplay_IsTheMainDisplay_StrictOrNot_AsGiven()
    {
        var window = new WindowSettings { Enabled = true, X = 5000, Y = 10 };
        Assert.Equal(At(Laptop, 5000, 10), DisplayMatcher.Place([Laptop, Work], window, 800, 480)); // its own place: not pulled in
        Assert.Equal(At(Laptop, 5000, 10), DisplayMatcher.Place([Laptop, Work], window with { Strict = false }, 800, 480));
        Assert.Equal(At(Work, 0, 0), DisplayMatcher.Place([Work, Laptop], null, 800, 480));
        Assert.Null(DisplayMatcher.Place([], window, 800, 480));
        Assert.Null(DisplayMatcher.Place([], window with { Strict = false }, 800, 480));
    }

    [Fact]
    public void Promote_MakesTheDisplayTheWindowsPlace_AndRemembersThePreviousOneFirst()
    {
        var window = On(Work, 100, 50, Spot(Home, 300, 60)) with { Strict = false, Level = "top" };

        var promoted = DisplayMatcher.Promote(window, TargetOf(Laptop), 10, 20);

        Assert.Equal(window with { Display = TargetOf(Laptop), X = 10, Y = 20, Spots = [Spot(Work, 100, 50), Spot(Home, 300, 60)] }, promoted);
    }

    [Fact]
    public void Promote_DropsTheSpotOfTheSameMonitor_ByItsIdElseByNameSizeAndTopLeft()
    {
        var window = On(Work, 100, 50, Spot(Home, 300, 60), Spot(Laptop, 10, 20));
        Assert.Equal([Spot(Work, 100, 50), Spot(Laptop, 10, 20)], DisplayMatcher.Promote(window, TargetOf(Home), 1, 2).Spots);
        Assert.Equal([Spot(Work, 100, 50), Spot(Laptop, 10, 20)],
            DisplayMatcher.Promote(window, TargetOf(Home) with { Name = "LG", Width = 1920, Height = 1080 }, 1, 2).Spots); // same id

        // Without ids: the same name, size and top-left; at another top-left it is another display.
        var plain = new DisplayInfo("Projector", new PointRect(1512, 0, 1920, 1080), 1);
        var withPlain = On(Work, 100, 50, Spot(plain, 5, 6));
        Assert.Equal([Spot(Work, 100, 50)], DisplayMatcher.Promote(withPlain, TargetOf(plain), 1, 2).Spots);
        var moved = plain with { Frame = plain.Frame with { X = -1920 } };
        Assert.Equal([Spot(Work, 100, 50), Spot(plain, 5, 6)], DisplayMatcher.Promote(withPlain, TargetOf(moved), 1, 2).Spots);
    }

    [Fact]
    public void Promote_OntoTheDisplayItIsOn_MovesItThere_AndKeepsTheSpots()
    {
        var window = On(Work, 100, 50, Spot(Home, 300, 60));
        Assert.Equal(window with { X = 120, Y = 64 }, DisplayMatcher.Promote(window, TargetOf(Work), 120, 64));

        // A place written before ids is the same monitor at the same top-left: it takes the id, remembering nothing more.
        var withoutId = window with { Display = TargetOf(Work) with { Id = null } };
        Assert.Equal(window with { X = 120, Y = 64 }, DisplayMatcher.Promote(withoutId, TargetOf(Work), 120, 64));
    }

    [Fact]
    public void Promote_KeepsTheEightMostRecentSpots()
    {
        var displays = Enumerable.Range(0, 10).Select(i => new DisplayInfo($"Display {i}", new PointRect(i * 2000, 0, 1920, 1080), 1)).ToArray();
        var window = On(displays[0], 1, 1, [.. displays[1..9].Select((d, i) => Spot(d, i, i))]);
        Assert.Equal(DisplayMatcher.MaxSpots, window.Spots!.Count);

        var promoted = DisplayMatcher.Promote(window, TargetOf(displays[9]), 0, 0);

        // The previous place first; Display 8, the least recent, dropped.
        Assert.Equal([Spot(displays[0], 1, 1), .. displays[1..8].Select((d, i) => Spot(d, i, i))], promoted.Spots);
    }

    [Fact]
    public void Promote_FromNoDisplay_RemembersNothing_AndLeavesSpotsOut()
    {
        var promoted = DisplayMatcher.Promote(new WindowSettings { Enabled = true, X = 40, Y = 30 }, TargetOf(Work), 1, 2);
        Assert.Equal(new WindowSettings { Enabled = true, X = 1, Y = 2, Display = TargetOf(Work) }, promoted);
        Assert.Null(promoted.Spots);
    }

    [Fact]
    public void Place_AKnownMonitorBackAtASmallerSize_PullsTheWindowOnToIt_AsItsPlaceOrAsASpot_StrictOrNot()
    {
        // Saved at 3040 on the monitor at 3840×2160 pixels; it comes back at 1920×1080: the 800-pixel window moves to 1120.
        var wide = new DisplayInfo("LG HDR 4K", new PointRect(1512, 0, 3840, 2160), 1, Id: "GSM5B7F");
        var narrow = wide with { Frame = new PointRect(1512, 0, 1920, 1080) };

        Assert.Equal(At(narrow, 1120, 100), DisplayMatcher.Place([Laptop, narrow], On(wide, 3040, 100), 800, 480));
        Assert.Equal(At(narrow, 1120, 100), DisplayMatcher.Place([Laptop, narrow], On(wide, 3040, 100) with { Strict = false }, 800, 480));
        Assert.Equal(At(narrow, 1120, 600), DisplayMatcher.Place([Laptop, narrow], On(Work, 100, 50, Spot(wide, 3040, 1900)), 800, 480));
        Assert.Equal(At(narrow, 0, 0), DisplayMatcher.Place([Laptop, narrow], On(wide, 3040, -40), 2400, 1200)); // larger than it: top-left 0, 0

        // Still wholly on it at its new size: the saved place as it is; back at its recorded size: the saved place.
        Assert.Equal(At(narrow, 100, 50), DisplayMatcher.Place([Laptop, narrow], On(wide, 100, 50), 800, 480));
        Assert.Equal(At(wide, 3040, 100), DisplayMatcher.Place([Laptop, wide], On(wide, 3040, 100), 800, 480));
    }

    [Fact]
    public void Place_AMonitorAtItsRecordedSize_KeepsTheSavedPlaceExactly_OverhangIncluded()
    {
        // Half off the work monitor's right and top edges, by its id, by its name and size, and as a spot.
        Assert.Equal(At(Work, 2400, -100), DisplayMatcher.Place([Laptop, Work], On(Work, 2400, -100), 800, 480));
        var withoutId = Work with { Id = null };
        Assert.Equal(At(withoutId, 2400, -100), DisplayMatcher.Place([Laptop, withoutId], On(withoutId, 2400, -100), 800, 480));
        Assert.Equal(At(Work, -300, 1200), DisplayMatcher.Place([Laptop, Work], On(Home, 0, 0, Spot(Work, -300, 1200)), 800, 480));
    }

    [Fact]
    public void Promote_AnOlderPlaceWithoutATopLeft_IsTheSameMonitor_OnlyKnownTopLeftsTellDisplaysApart()
    {
        // A target written before display coordinates: {"name": "Panel", "width": 1920, "height": 1080}.
        var panel = new DisplayInfo("Panel", new PointRect(0, 0, 1920, 1080), 1);
        var older = new DisplayMatch { Name = "Panel", Width = 1920, Height = 1080 };
        var window = new WindowSettings { Enabled = true, X = 10, Y = 20, Display = older };

        var promoted = DisplayMatcher.Promote(window, TargetOf(panel), 30, 40);
        Assert.Equal(window with { Display = TargetOf(panel), X = 30, Y = 40 }, promoted);
        Assert.Null(promoted.Spots);

        // As a spot, and with the display now reporting an id: one place, the new one.
        var asSpot = On(Work, 100, 50) with { Spots = [new WindowSpot { Display = older, X = 10, Y = 20 }] };
        Assert.Equal([Spot(Work, 100, 50)], DisplayMatcher.Promote(asSpot, TargetOf(panel with { Id = "SAM0F0B" }), 30, 40).Spots);

        // Both top-lefts known and different: two displays.
        var placed = window with { Display = older with { X = 1920, Y = 0 } };
        Assert.Equal([new WindowSpot { Display = older with { X = 1920, Y = 0 }, X = 10, Y = 20 }], DisplayMatcher.Promote(placed, TargetOf(panel), 30, 40).Spots);
    }

    [Fact]
    public void WindowSettings_CompareEveryMember_TheirSpotsSpotBySpot()
    {
        var a = On(Work, 1, 2, Spot(Home, 3, 4)) with { Strict = true, Level = "top", ClickThrough = true, Draggable = false, Resizable = false };
        var b = On(Work, 1, 2, Spot(Home, 3, 4)) with { Strict = true, Level = "top", ClickThrough = true, Draggable = false, Resizable = false };
        Assert.NotSame(a.Spots, b.Spots);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        // Each member counts: one that differs makes them unequal (a member added later must be compared too).
        foreach (var property in typeof(WindowSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            var value = property.GetValue(a);
            object other = value switch
            {
                bool v => !v,
                int v => v + 1,
                string v => v + "x",
                DisplayMatch => TargetOf(Laptop),
                IReadOnlyList<WindowSpot> => new[] { Spot(Laptop, 0, 0) },
                _ => throw new Xunit.Sdk.XunitException($"WindowSettings.{property.Name}: give it a differing value here"),
            };
            var changed = a with { };
            property.SetValue(changed, other);
            Assert.False(a.Equals(changed), $"WindowSettings.{property.Name} isn't compared");
        }
    }

    [Fact]
    public void Cut_KeepsAtMost256UnicodeCharacters_NeverSplittingASurrogatePair()
    {
        var emoji = "\U0001F600";
        Assert.Equal(new string('a', 255) + emoji, DisplayMatcher.Cut(new string('a', 255) + emoji)); // 256 characters, 257 UTF-16 units
        Assert.Equal(new string('a', 256), DisplayMatcher.Cut(new string('a', 256) + emoji));
        Assert.Equal(new string('a', 255) + emoji, DisplayMatcher.Cut(new string('a', 255) + emoji + "b"));
        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 256)), DisplayMatcher.Cut(string.Concat(Enumerable.Repeat(emoji, 300))));
        Assert.Equal(new string('n', 256), DisplayMatcher.Cut(new string('n', 300)));
        Assert.Equal("", DisplayMatcher.Cut(""));
    }
}
