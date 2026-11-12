using InfoPanel.Core.Tests.Golden;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Panels.Hosted;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Usb;
using Xunit;

namespace InfoPanel.Core.Tests.Panels;

/// <summary>
/// "Detected, not supported": the <c>detect</c> patterns (a vendor exactly, its product with <c>?</c> digits or <c>*</c>),
/// their bounds in <c>panel.json</c>, the families that declare them (BeadaPanel's built in, Thermalright's, the virtual
/// package's), and which listed devices are answered as detected (no match names them, no left-out vendor, no hub, no
/// boot keyboard or mouse).
/// </summary>
public sealed class PanelDetectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("panel-detect-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static UsbIdPattern Pattern(string text) => UsbIdPattern.Parse(text) ?? throw new Xunit.Sdk.XunitException($"{text} didn't parse");

    [Theory]
    [InlineData("0416:54??", 0x0416, 0x5409, true)]
    [InlineData("0416:54??", 0x0416, 0x54ab, true)]
    [InlineData("0416:54??", 0x0416, 0x5302, false)]
    [InlineData("0416:54??", 0x0417, 0x5409, false)]
    [InlineData("87ad:*", 0x87ad, 0x0000, true)]
    [InlineData("87ad:*", 0x87ad, 0xffff, true)]
    [InlineData("87ad:*", 0x87cd, 0x70db, false)]
    [InlineData("33c3:7810", 0x33c3, 0x7810, true)]
    [InlineData("33c3:7810", 0x33c3, 0x7811, false)]
    [InlineData("33c3:7?1?", 0x33c3, 0x7a1f, true)]
    [InlineData("33c3:7?1?", 0x33c3, 0x7a2f, false)]
    public void A_pattern_names_the_ids_it_covers_and_misses_the_others(string pattern, int vendor, int product, bool names)
    {
        var parsed = Pattern(pattern);

        Assert.Equal(names, parsed.Names((ushort)vendor, (ushort)product));
        Assert.Equal(pattern, parsed.ToString());
    }

    [Theory]
    [InlineData("0416:54?")]
    [InlineData("0416:54???")]
    [InlineData("0416:54AB")]
    [InlineData("04?6:5400")]
    [InlineData("*:*")]
    [InlineData("0416:*?")]
    [InlineData("0416:**")]
    [InlineData("0416-5400")]
    [InlineData("0416:54 ?")]
    [InlineData("")]
    public void A_pattern_of_another_shape_doesnt_parse(string text) => Assert.Null(UsbIdPattern.Parse(text));

    private string Manifest(string detect)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PanelPackages.ManifestName), $$"""
            { "format": "infopanel.panel-package/1", "sdk": "1.0",
              "families": [{ "id": "acme", "name": "Acme", "match": [{ "usb": "0416:5302" }], {{detect}} "models": [] }] }
            """);
        return dir;
    }

    [Fact]
    public void A_manifests_detect_is_read_with_its_family_and_none_is_none()
    {
        var read = PanelPackages.ReadManifest(Manifest("""
            "detect": [{ "usb": "0416:54??" }, { "usb": "87ad:*", "later": true }, { "usb": "33c3:7810" }],
            """), out var error);
        Assert.Null(error);
        Assert.Equal(["0416:54??", "87ad:*", "33c3:7810"], Assert.Single(read!.Families).Detect.Select(p => p.ToString()));

        Assert.Empty(Assert.Single(PanelPackages.ReadManifest(Manifest(""), out _)!.Families).Detect);
        Assert.Empty(Assert.Single(PanelPackages.ReadManifest(Manifest("""
            "detect": [],
            """), out _)!.Families).Detect);
    }

    [Theory]
    [InlineData("""
        "detect": { "usb": "0416:54??" },
        """, "families[0].detect isn't a list of at most 64")]
    [InlineData("""
        "detect": ["0416:54??"],
        """, "families[0].detect has a pattern that isn't")]
    [InlineData("""
        "detect": [{ "vendor": "0416" }],
        """, "families[0].detect has a pattern that isn't")]
    [InlineData("""
        "detect": [{ "usb": "0416:54?" }],
        """, "families[0].detect has a pattern that isn't")]
    [InlineData("""
        "detect": [{ "usb": "04??:5400" }],
        """, "families[0].detect has a pattern that isn't")]
    [InlineData("""
        "detect": [{ "usb": "0416:54AB" }],
        """, "families[0].detect has a pattern that isn't")]
    public void A_detect_of_another_shape_is_refused_with_why(string detect, string why)
    {
        Assert.Null(PanelPackages.ReadManifest(Manifest(detect), out var error));
        Assert.StartsWith(why, error);
    }

    [Fact]
    public void A_detect_of_more_than_64_patterns_is_refused_and_64_are_read()
    {
        string Patterns(int count) => "\"detect\": [" + string.Join(", ", Enumerable.Range(0, count).Select(i => $$"""{ "usb": "0416:{{i:x2}}??" }""")) + "],";

        Assert.Equal(PanelDetection.MaxPatterns, Assert.Single(PanelPackages.ReadManifest(Manifest(Patterns(64)), out _)!.Families).Detect.Count);
        Assert.Null(PanelPackages.ReadManifest(Manifest(Patterns(65)), out var error));
        Assert.StartsWith("families[0].detect isn't a list of at most 64", error);
    }

    private static string PackageDirectory(string name) => Path.GetFullPath(Path.Combine(TestPaths.ProjectDirectory, "..", name));

    [Fact]
    public void Thermalright_detects_its_vendors_ids_beyond_its_matches()
    {
        var family = Assert.Single(PanelPackages.ReadManifest(PackageDirectory("InfoPanel.Panels.Thermalright"), out var error)!.Families);

        Assert.Null(error);
        Assert.Equal(["87ad:*", "87cd:*", "0416:53??", "0416:54??", "0418:53??", "0402:39??"], family.Detect.Select(p => p.ToString()));
    }

    [Fact]
    public void The_virtual_package_detects_1209_any()
    {
        var family = Assert.Single(PanelPackages.ReadManifest(PackageDirectory("InfoPanel.Panels.Virtual"), out var error)!.Families);

        Assert.Null(error);
        Assert.Equal(["1209:*"], family.Detect.Select(p => p.ToString()));
    }

    [Fact]
    public void The_built_in_BeadaPanel_family_detects_4e58_any()
    {
        IPanelDriver driver = new BeadaPanelDriver(new NoUsb(), TimeProvider.System);

        var detects = Assert.IsAssignableFrom<IDetectsPanels>(driver);
        Assert.Equal(["4e58:*"], detects.Detect.Select(p => p.ToString()));
    }

    [Fact]
    public void A_packages_driver_detects_what_its_family_declares()
    {
        var family = new PanelPackageFamily("acme", "Acme", [new PanelMatch(0x0416, 0x5302)], []) { Detect = [Pattern("0416:54??")] };
        IPanelDriver driver = new HostedPanelDriver(family, new Hosted.TestLink(_root), Serilog.Core.Logger.None);

        var detects = Assert.IsAssignableFrom<IDetectsPanels>(driver);
        Assert.Equal(["0416:54??"], detects.Detect.Select(p => p.ToString()));
    }

    /// <summary>A driver that declares <paramref name="matches"/> and detects <paramref name="detect"/>; it is never asked anything.</summary>
    private sealed class Declaring(string family, PanelMatch[] matches, params string[] detect) : IPanelDriver, IDetectsPanels
    {
        public string Family => family;

        public IReadOnlyList<PanelMatch> Matches => matches;

        public IReadOnlyList<UsbIdPattern> Detect => [.. detect.Select(Pattern)];

        public PanelProbe Probe(PanelCandidate device, CancellationToken stop) => throw new InvalidOperationException("never asked");

        public PanelOpen Open(PanelCandidate device, PanelIdentity identity, PanelOpenOptions options, CancellationToken stop) => throw new InvalidOperationException("never asked");
    }

    private static UsbDeviceId Usb(ushort vendor, ushort product) => new(vendor, product, 1, 9, "1-4");

    [Fact]
    public void A_device_a_pattern_names_is_its_familys_and_the_first_drivers_in_their_order()
    {
        IReadOnlyList<IPanelDriver> drivers = [new Declaring("first", [new(0x0416, 0x5302)], "0416:54??"), new Declaring("second", [new(0x0416, 0x5408)], "0416:*")];

        Assert.Equal("first", PanelDetection.FamilyOf(Usb(0x0416, 0x5409), drivers));
        Assert.Equal("second", PanelDetection.FamilyOf(Usb(0x0416, 0x5501), drivers));
        Assert.Null(PanelDetection.FamilyOf(Usb(0x0417, 0x5409), drivers)); // no pattern names it
    }

    [Fact]
    public void A_device_any_drivers_match_names_is_never_detected()
    {
        IReadOnlyList<IPanelDriver> drivers = [new Declaring("first", [new(0x0416, 0x5302)], "0416:*"), new Declaring("second", [new(0x0416, 0x5408)])];

        Assert.Null(PanelDetection.FamilyOf(Usb(0x0416, 0x5302), drivers)); // its own match
        Assert.Null(PanelDetection.FamilyOf(Usb(0x0416, 0x5408), drivers)); // another family's match
    }

    [Theory]
    [InlineData(0x1a86, 0x7523)]
    [InlineData(0x1d6b, 0x0002)]
    [InlineData(0x0525, 0xa4a7)]
    public void A_left_out_vendor_is_never_detected_whatever_a_family_declares(int vendor, int product)
    {
        IReadOnlyList<IPanelDriver> drivers = [new Declaring("greedy", [new(0x1209, 0x0001)], "1a86:*", "1d6b:*", "0525:*")];

        Assert.Null(PanelDetection.FamilyOf(Usb((ushort)vendor, (ushort)product), drivers));
    }

    private static UsbDescriptor Descriptor(byte deviceClass, params UsbInterfaceDescriptor[] interfaces) => new(deviceClass, 0, 0, 0x0200, interfaces);

    private static UsbInterfaceDescriptor Interface(int number, byte @class, byte subClass = 0, byte protocol = 0) =>
        new(number, @class, subClass, protocol, [(0x01, 2, 512), (0x81, 2, 512)]);

    [Fact]
    public void A_hub_or_a_boot_keyboard_or_mouse_is_never_detected_and_another_device_is()
    {
        Assert.Null(PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0x09, Interface(0, 0x09))));
        Assert.Null(PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0, Interface(0, 0xff), Interface(1, 0x09))));
        Assert.Null(PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0, Interface(0, 0xff), Interface(1, 0x03, 1, 1))));
        Assert.Null(PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0, Interface(0, 0x03, 1, 2))));

        // A HID that isn't a boot keyboard or mouse (a panel's own reports) is detected.
        var hid = PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0, Interface(0, 0x03, 0, 0)));
        Assert.Equal(("acme", (ushort)0x0200, 1), (hid!.Family, hid.BcdDevice, hid.Interfaces.Count));
    }

    [Theory]
    [InlineData(0x03, 1, 1)] // a boot keyboard
    [InlineData(0x03, 1, 2)] // a boot mouse
    [InlineData(0x09, 0, 0)] // a hub
    public void A_later_alternate_setting_that_is_a_hub_or_a_boot_keyboard_or_mouse_leaves_the_device_out(byte @class, byte subClass, byte protocol)
    {
        var vendorFirst = Interface(0, 0xff) with { LaterSettings = [(@class, subClass, protocol)] };

        Assert.Null(PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0, vendorFirst)));
        Assert.Null(PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0, Interface(0, 0xff), Interface(1, 0xfe) with { LaterSettings = [(0xff, 0, 0), (@class, subClass, protocol)] })));
    }

    [Fact]
    public void A_detected_devices_interfaces_are_their_first_settings_whatever_the_later_ones_are()
    {
        var detected = PanelDetection.Of("acme", Usb(0x0416, 0x5409), Descriptor(0, Interface(0, 0xff) with { LaterSettings = [(0xfe, 1, 2), (0x03, 0, 0)] }));

        var only = Assert.Single(detected!.Interfaces);
        Assert.Equal((0, (byte)0xff, (byte)0, (byte)0, 2), (only.Number, only.Class, only.SubClass, only.Protocol, only.Endpoints.Count));
    }

    [Fact]
    public void A_detected_devices_interfaces_and_endpoints_are_kept_within_their_bounds_after_all_are_looked_at()
    {
        var many = Enumerable.Range(0, 40).Select(i => new UsbInterfaceDescriptor(i, 0xff, 0, 0,
            [.. Enumerable.Range(0, 40).Select(e => ((byte)e, 2, 64))])).ToList();

        var detected = PanelDetection.Of("acme", Usb(0x0416, 0x5409), new UsbDescriptor(0, 0, 0, 0x0100, many))!;

        Assert.Equal(PanelDetection.MaxInterfaces, detected.Interfaces.Count);
        Assert.All(detected.Interfaces, i => Assert.Equal(PanelDetection.MaxEndpoints, i.Endpoints.Count));

        many.Add(Interface(40, 0x03, 1, 1)); // past the bounds, but looked at
        Assert.Null(PanelDetection.Of("acme", Usb(0x0416, 0x5409), new UsbDescriptor(0, 0, 0, 0x0100, many)));
    }

    /// <summary>A backend with no devices: the BeadaPanel driver built over it is only asked what it declares.</summary>
    private sealed class NoUsb : IUsbBackend
    {
        public IReadOnlyList<UsbDeviceId> ListDevices() => [];

        public UsbStatus Open(UsbDeviceId device, out IUsbDevice? opened)
        {
            opened = null;
            return UsbStatus.NoDevice;
        }

        public void Dispose()
        {
        }
    }
}
