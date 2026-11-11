using InfoPanel.Core.Tests.Panels.Power;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Hosted;

/// <summary>
/// The panel packages' <c>panel.json</c>, read at start: its families, matches and models; its bounds (a regular file of
/// at most 64 KiB, its members' shapes); the panel SDK it needs; and family ids unique across the built-in families, the
/// bundled packages, then the installed ones (<c>duplicateId</c>).
/// </summary>
public sealed class PanelPackageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("panel-packages-").FullName;
    private readonly KeptLog _log = new();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Virtual = """
        { "format": "infopanel.panel-package/1", "sdk": "1.0", "later": {"kept": true},
          "families": [{ "id": "virtual", "name": "Virtual panel",
            "match": [{ "usb": "1209:0001" }, { "usb": "1209:0002", "transport": "serial", "interface": 1 }],
            "models": [{ "id": "480x320", "name": "Virtual 480 x 320", "width": 480, "height": 320, "v1Model": "VIRTUAL_35" },
                       { "id": "1920x480", "name": "Virtual 1920 x 480", "width": 1920, "height": 480 }] }] }
        """;

    /// <summary>A package folder <paramref name="folder"/> in <paramref name="parent"/> with its DLL and <paramref name="manifest"/> as its panel.json.</summary>
    private string Package(string parent, string folder, string manifest)
    {
        var dir = Path.Combine(_root, parent, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, folder + ".dll"), [0x4d, 0x5a]);
        File.WriteAllText(Path.Combine(dir, PanelPackages.ManifestName), manifest);
        return dir;
    }

    private static string Family(string id, string sdk = "1.0") => $$"""
        { "format": "infopanel.panel-package/1", "sdk": "{{sdk}}",
          "families": [{ "id": "{{id}}", "name": "{{id}}", "match": [{ "usb": "1209:00{{id.Length:00}}" }], "models": [] }] }
        """;

    [Fact]
    public void A_manifest_is_read_with_its_families_matches_and_models_and_members_it_doesnt_know_are_ignored()
    {
        var dir = Package("bundled", "InfoPanel.Panels.Virtual", Virtual);

        var manifest = PanelPackages.ReadManifest(dir, out var error);

        Assert.Null(error);
        Assert.Equal("1.0", manifest!.Sdk);
        var family = Assert.Single(manifest.Families);
        Assert.Equal(("virtual", "Virtual panel"), (family.Id, family.Name));
        Assert.Equal([new PanelMatch(0x1209, 0x0001), new PanelMatch(0x1209, 0x0002, 1, PanelTransport.Serial)], family.Matches);
        Assert.Equal([new PanelPackageModel("480x320", "Virtual 480 x 320", 480, 320, "VIRTUAL_35"), new PanelPackageModel("1920x480", "Virtual 1920 x 480", 1920, 480, null)],
            family.Models);
        Assert.True(family.Names(0x1209, 0x0002));
        Assert.False(family.Names(0x1209, 0x0003));
    }

    [Theory]
    [InlineData("large", "is larger than 65536 bytes")]
    [InlineData("folder", "is a link or isn't a regular file")]
    [InlineData("link", "is a link or isn't a regular file")]
    public void A_manifest_over_64_KiB_or_that_isnt_a_regular_file_is_refused(string what, string why)
    {
        var dir = Path.Combine(_root, "InfoPanel.Panels.Odd");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PanelPackages.ManifestName);
        switch (what)
        {
            case "large":
                File.WriteAllText(path, Virtual[..^1] + new string(' ', PanelPackages.MaxManifestBytes) + "}");
                break;
            case "folder":
                Directory.CreateDirectory(path);
                break;
            default:
                File.WriteAllText(Path.Combine(_root, "elsewhere.json"), Virtual);
                File.CreateSymbolicLink(path, Path.Combine(_root, "elsewhere.json"));
                break;
        }

        Assert.Null(PanelPackages.ReadManifest(dir, out var error));
        Assert.Equal(why, error);
    }

    [Theory]
    [InlineData("""{ "format": "infopanel.panel-package/2", "sdk": "1.0", "families": [] }""", "has no format")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1", "families": [] }""", "has no sdk major.minor")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [] }""", "has no families")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [{ "id": "Turing", "name": "T", "match": [{ "usb": "1cbe:0088" }] }] }""", "families[0].id isn't")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [{ "id": "t", "name": "T", "match": [{ "usb": "1CBE:0088" }] }] }""", "families[0].match has a device that isn't")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [{ "id": "t", "name": "T", "match": [{ "usb": "1cbe:0088", "transport": "scsi" }] }] }""", "families[0].match has a transport that isn't")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [{ "id": "t", "name": "T", "match": [{ "usb": "1cbe:0088" }], "models": [{ "id": "a", "name": "A", "width": 0, "height": 1 }] }] }""", "families[0].models has a model without")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [{ "id": "t", "name": "T", "match": [{ "usb": "1cbe:0088" }] }, { "id": "t", "name": "U", "match": [{ "usb": "1cbe:0089" }] }] }""", "declares the family t twice")]
    [InlineData("""[1, 2]""", "isn't a JSON object")]
    [InlineData("""{ "format": """, "isn't JSON")]
    public void A_manifest_with_a_member_out_of_its_bounds_is_refused_with_why(string manifest, string why)
    {
        var dir = Package("bundled", "InfoPanel.Panels.Bad", manifest);

        Assert.Null(PanelPackages.ReadManifest(dir, out var error));
        Assert.StartsWith(why, error);
    }

    [Theory]
    [InlineData("""{ "format": "\uD800", "sdk": "1.0", "families": [] }""")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [{ "id": "t", "name": "\uDC00T", "match": [{ "usb": "1cbe:0088" }] }] }""")]
    [InlineData("""{ "format": "infopanel.panel-package/1", "sdk": "1.0", "families": [{ "id": "t", "name": "T", "match": [{ "usb": "1cbe:0088", "transport": "\uD800" }] }] }""")]
    public void A_manifest_with_a_lone_surrogate_is_refused_and_the_other_packages_are_still_found(string manifest)
    {
        var dir = Package("bundled", "InfoPanel.Panels.Bad", manifest);
        Package("bundled", "InfoPanel.Panels.Good", Family("virtual"));

        Assert.Null(PanelPackages.ReadManifest(dir, out var error));
        Assert.Equal("has a string that isn't text (a lone surrogate)", error);
        var package = Assert.Single(PanelPackages.Find(Path.Combine(_root, "bundled"), null, ["beadapanel"], _log.Logger));
        Assert.Equal(("InfoPanel.Panels.Good", null), (package.Folder, package.ProblemCode));
        Assert.Contains(_log.Lines, l => l.Contains("Skipped the panel package InfoPanel.Panels.Bad: its panel.json has a string that isn't text"));
    }

    [Theory]
    [InlineData("1.0", null)]
    [InlineData("1.1", "it is built for panel SDK 1.1; this engine has 1.0")]
    [InlineData("2.0", "it is built for panel SDK 2.0; this engine has 1.0")]
    [InlineData("0.9", "it is built for panel SDK 0.9; this engine has 1.0")]
    public void A_package_for_another_major_or_a_later_minor_of_the_sdk_isnt_run(string sdk, string? problem)
    {
        Package("bundled", "InfoPanel.Panels.Virtual", Family("virtual", sdk));

        var package = Assert.Single(PanelPackages.Find(Path.Combine(_root, "bundled"), null, ["beadapanel"], _log.Logger));

        Assert.Equal(problem == null ? null : "sdk", package.ProblemCode);
        Assert.Equal(problem, package.Problem);
    }

    [Fact]
    public void A_package_for_another_sdk_still_claims_its_family_ids_and_never_shadows_a_built_in_family()
    {
        Package("bundled", "InfoPanel.Panels.Beada", Family("beadapanel", "2.0"));
        Package("bundled", "InfoPanel.Panels.Later", Family("turing", "2.0"));
        Package("installed", "InfoPanel.Panels.Turing", Family("turing"));

        var packages = PanelPackages.Find(Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"), ["beadapanel"], _log.Logger);

        Assert.Equal(["InfoPanel.Panels.Beada", "InfoPanel.Panels.Later", "InfoPanel.Panels.Turing"], packages.Select(p => p.Folder));
        Assert.Equal(["duplicateId", "sdk", "duplicateId"], packages.Select(p => p.ProblemCode));
        Assert.Equal("its family beadapanel is built into the engine", packages[0].Problem);
        Assert.Equal("its family turing is the panel package InfoPanel.Panels.Later's, earlier in the list", packages[2].Problem);
    }

    [Fact]
    public void A_family_id_a_built_in_family_or_an_earlier_package_has_refuses_the_later_package_whole_as_duplicateId()
    {
        Package("bundled", "InfoPanel.Panels.Beada", Family("beadapanel"));
        Package("bundled", "InfoPanel.Panels.First", Family("turing"));
        Package("installed", "InfoPanel.Panels.Again", Family("turing"));
        Package("installed", "InfoPanel.Panels.Own", Family("thermalright"));
        Package("installed", "InfoPanel.Panels.First", Family("lianli")); // a bundled package's folder name: skipped
        Package("installed", "InfoPanel.Sensors", """{ "not": "a panel package's" }""" ); // refused: logged, skipped
        Directory.CreateDirectory(Path.Combine(_root, "installed", "InfoPanel.SensorPlugin")); // no panel.json: not a panel package

        var packages = PanelPackages.Find(Path.Combine(_root, "bundled"), Path.Combine(_root, "installed"), ["beadapanel"], _log.Logger);

        Assert.Equal(["InfoPanel.Panels.Beada", "InfoPanel.Panels.First", "InfoPanel.Panels.Again", "InfoPanel.Panels.Own"], packages.Select(p => p.Folder));
        Assert.Equal([true, true, false, false], packages.Select(p => p.Bundled));
        Assert.Equal(["duplicateId", null, "duplicateId", null], packages.Select(p => p.ProblemCode));
        Assert.Equal("its family beadapanel is built into the engine", packages[0].Problem);
        Assert.Equal("its family turing is the panel package InfoPanel.Panels.First's, earlier in the list", packages[2].Problem);
        Assert.Contains(_log.Lines, l => l.Contains("Skipped the installed panel package InfoPanel.Panels.First: a bundled package has its name"));
        Assert.Contains(_log.Lines, l => l.Contains("Skipped the panel package InfoPanel.Sensors: its panel.json has no format"));
        Assert.True(PanelPackages.HasManifest(Path.Combine(_root, "installed", "InfoPanel.Panels.Own")));
        Assert.False(PanelPackages.HasManifest(Path.Combine(_root, "installed", "InfoPanel.SensorPlugin")));
    }
}
