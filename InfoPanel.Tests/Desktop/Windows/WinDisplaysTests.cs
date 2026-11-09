using InfoPanel.Desktop;
using InfoPanel.Desktop.Windows;
using Xunit;

namespace InfoPanel.Tests.Desktop.Windows;

/// <summary>
/// Windows' monitors and their friendly names as displays (on any OS: the mapping has no Win32 in it): the primary
/// first, then by device name; physical-pixel rectangles of the virtual screen, so scale 1; the friendly name as the
/// label, none when it is empty or missing; one target per source, which gives both the label and the id.
/// </summary>
public sealed class WinDisplaysTests
{
    private static readonly Dictionary<string, TargetName> NoNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A display path as the display configuration lists it, without a device path.</summary>
    private static TargetName Path(string device, string friendly, string? id = null) => new(device, friendly, id, "");

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
        var names = WinDisplays.Sources(
        [
            Path(@"\\.\DISPLAY1", "DELL U2723QE"),
            Path(@"\\.\DISPLAY2", ""),
            Path(@"\\.\DISPLAY3", "  LG ULTRAWIDE  "),
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
        var names = WinDisplays.Sources(
        [
            Path(@"\\.\DISPLAY1", ""),
            Path(@"\\.\DISPLAY1", "Projector"),
            Path(@"\\.\display1", "TV"),
            Path("", "Nothing"),
        ]);

        Assert.Equal("Projector", Assert.Single(names).Value.Friendly);
        var display = Assert.Single(WinDisplays.Map([new MonitorRecord(@"\\.\DISPLAY1", 0, 0, 1920, 1080, true)], names));
        Assert.Equal("Projector", display.Label);
    }

    /// <summary>
    /// A monitor's id from <c>DISPLAYCONFIG_TARGET_DEVICE_NAME</c>: only with <c>edidIdsValid</c> (0x4) set; the maker word
    /// as Windows gives it, the EDID's bytes 8 and 9 in memory order (Dell's 10 AC as 0xAC10), so the same string as
    /// macOS' (which reads 0x10AC); the product code as the number. The ASUS case is the EDID a PC stored: bytes 8-11
    /// 06 B3 08 AA, its PnP id AUSAA08.
    /// </summary>
    [Theory]
    [InlineData(0x4u, (ushort)0xAC10, (ushort)0x40F4, "DEL40F4")]     // Dell, EDID bytes 10 AC
    [InlineData(0x4u, (ushort)0xB306, (ushort)0xAA08, "AUSAA08")]     // ASUS, EDID bytes 06 B3
    [InlineData(0x4u, (ushort)0x6D1E, (ushort)0x5B7F, "GSM5B7F")]     // LG, EDID bytes 1E 6D
    [InlineData(0x7u, (ushort)0xAC10, (ushort)0x40F4, "DEL40F4")]     // the friendly-name bits beside it change nothing
    [InlineData(0x4u, (ushort)0x10AC, (ushort)0x40F4, null)]          // the word unswapped (bytes AC 10) is no PNP id
    [InlineData(0x3u, (ushort)0xAC10, (ushort)0x40F4, null)]          // edidIdsValid not set: the words aren't read
    [InlineData(0x0u, (ushort)0xAC10, (ushort)0x40F4, null)]
    [InlineData(0x4u, (ushort)0x0000, (ushort)0x0000, null)]          // zero words
    [InlineData(0x4u, (ushort)0x0000, (ushort)0x40F4, null)]          // no maker
    public void TheId_IsTheEdidMakerAndProductCode_OnlyWhenTheyAreValid(uint flags, ushort maker, ushort product, string? id) =>
        Assert.Equal(id, WinDisplays.IdOf(flags, maker, product));

    [Fact]
    public void TheId_IsCarriedByDeviceName_WithItsTargetsLabel_NoneWithoutOne()
    {
        var ids = WinDisplays.Sources(
        [
            Path(@"\\.\DISPLAY1", "DELL U2723QE", WinDisplays.IdOf(0x4, 0xAC10, 0x40F4)),
            Path(@"\\.\DISPLAY2", "", WinDisplays.IdOf(0x0, 0xAC10, 0x40F4)),     // a virtual display: no valid EDID ids
            Path(@"\\.\display3", "ROG PG32UCDM", WinDisplays.IdOf(0x4, 0xB306, 0xAA08)), // mirrored: the first named path
            Path(@"\\.\DISPLAY3", "LG ULTRAWIDE", WinDisplays.IdOf(0x4, 0x6D1E, 0x5B7F)),
            Path("", "", "GSM5B7F"),
        ]);
        var displays = WinDisplays.Map(
        [
            new MonitorRecord(@"\\.\DISPLAY1", 0, 0, 3840, 2160, true),
            new MonitorRecord(@"\\.\DISPLAY2", 3840, 0, 5760, 1080, false),
            new MonitorRecord(@"\\.\DISPLAY3", 5760, 0, 8320, 1440, false),
            new MonitorRecord(@"\\.\DISPLAY4", 8320, 0, 10240, 1080, false),
        ], ids);

        Assert.Equal(["DEL40F4", null, "AUSAA08", null], displays.Select(d => d.Id));
        Assert.Equal(["DELL U2723QE", null, "ROG PG32UCDM", null], displays.Select(d => d.Label));
        Assert.Equal("DEL40F4", DisplayMatcher.TargetOf(displays[0]).Id);
        Assert.Null(DisplayMatcher.TargetOf(displays[1]).Id);
    }

    [Fact]
    public void AClone_WithOneTargetNameless_GivesTheLabelAndIdOfOneTarget()
    {
        // DISPLAY1 clones a nameless ASUS and a Dell: both come from the Dell, never the ASUS's id under the Dell's label.
        // DISPLAY2 has no named target: the first path gives both, its id null even though a later path has one.
        var displays = WinDisplays.Map(
        [
            new MonitorRecord(@"\\.\DISPLAY1", 0, 0, 3840, 2160, true),
            new MonitorRecord(@"\\.\DISPLAY2", 3840, 0, 5760, 1080, false),
        ], WinDisplays.Sources(
        [
            Path(@"\\.\DISPLAY1", "", WinDisplays.IdOf(0x4, 0xB306, 0xAA08)),
            Path(@"\\.\DISPLAY1", "DELL U2723QE", WinDisplays.IdOf(0x4, 0xAC10, 0x40F4)),
            Path(@"\\.\DISPLAY2", "", null),
            Path(@"\\.\DISPLAY2", " ", WinDisplays.IdOf(0x4, 0xB306, 0xAA08)),
        ]));

        Assert.Equal([("DELL U2723QE", "DEL40F4"), (null, null)], displays.Select(d => (d.Label, d.Id)));
    }

    /// <summary>
    /// The PC's own displays (Windows only): every id is three PNP letters and four hex digits, and a monitor's id is the
    /// maker and product of its own PnP hardware id, the <c>DISPLAY#XXXnnnn#</c> part of its device path, which checks
    /// the byte order on real monitors. Logs what it read; skipped, with the reason, when no path has a monitor's hardware
    /// id to compare (no monitor attached or active), so such a run reads as skipped, not passed.
    /// </summary>
    [Fact]
    public void ThisPcsDisplays_HaveIdsOfThePattern_EachItsMonitorsHardwareId()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The display configuration exists only on Windows.");
        if (!OperatingSystem.IsWindows())
            return;

        var targets = WinDisplays.ReadTargetNames();
        foreach (var t in targets)
            TestContext.Current.TestOutputHelper?.WriteLine($"{t.Device} '{t.Friendly}' id={t.Id ?? "(none)"} path={t.DevicePath}");

        var compared = 0;
        foreach (var t in targets)
        {
            if (t.Id != null)
                Assert.Matches("^[A-Z]{3}[0-9A-F]{4}$", t.Id);

            // A monitor's device path: \\?\DISPLAY#DEL40F4#...; its PnP hardware id is the same maker and product.
            var parts = t.DevicePath.Split('#');
            var hardware = parts.Length > 1 && parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase) ? parts[1].ToUpperInvariant() : null;
            if (hardware != null && System.Text.RegularExpressions.Regex.IsMatch(hardware, "^[A-Z]{3}[0-9A-F]{4}$"))
            {
                Assert.Equal(hardware, t.Id);
                compared++;
            }
        }

        var displays = WinDisplays.Map(WinDisplays.Monitors(), WinDisplays.Sources(targets));
        Assert.All(displays.Where(d => d.Id != null), d => Assert.Matches("^[A-Z]{3}[0-9A-F]{4}$", d.Id!));

        if (compared == 0)
            Assert.Skip(targets.Count == 0
                ? "No active display path: no monitor is attached or active in this session, so no id was compared with a monitor's hardware id."
                : $"None of the {targets.Count} active display paths has a monitor's hardware id in its device path, so no id was compared with one.");
    }
}
