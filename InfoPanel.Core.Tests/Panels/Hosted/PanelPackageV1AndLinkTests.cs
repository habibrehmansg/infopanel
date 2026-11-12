using InfoPanel.Import;
using InfoPanel.Panels;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Hosted;

/// <summary>
/// Two parts of the engine's <c>panel.json</c> reader: a model's <c>v1Aliases</c> (the other names 1.x saved it under),
/// kept and mapped as <c>v1Model</c> is, so a 1.x panel saved under an older name comes over as its model; and the read
/// itself, one handle opened without following a link, so a link put in the manifest's place after the check is refused
/// rather than followed.
/// </summary>
public sealed class PanelPackageV1AndLinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("panel-packages-v1-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A fixture package standing for a family whose model 1.x knew by two names (1.4.x's Trofeo Vision 6.86", saved
    // as TrofeoVision686 by builds before its rename).
    private const string Renamed = """
        { "format": "infopanel.panel-package/1", "sdk": "1.0",
          "families": [{ "id": "renamed", "name": "Renamed panels", "match": [{ "usb": "1209:0042" }],
            "models": [{ "id": "trofeo-686", "name": "Trofeo Vision 6.86 inch", "width": 1280, "height": 480,
                         "v1Model": "TrofeoVision686Inch", "v1Aliases": ["TrofeoVision686"] },
                       { "id": "plain", "name": "Plain", "width": 320, "height": 240, "v1Model": "Plain" }] }] }
        """;

    private string Package(string folder, string manifest)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, PanelPackages.ManifestName), manifest);
        return dir;
    }

    [Fact]
    public void A_models_v1Aliases_are_kept()
    {
        var manifest = PanelPackages.ReadManifest(Package("InfoPanel.Panels.Renamed", Renamed), out var error);

        Assert.Null(error);
        var models = Assert.Single(manifest!.Families).Models;
        Assert.Equal(["TrofeoVision686"], models[0].V1Aliases);
        Assert.Empty(models[1].V1Aliases);
    }

    [Theory]
    [InlineData("""["", "x"]""")]
    [InlineData("""[1]""")]
    [InlineData(""" "TrofeoVision686" """)]
    public void A_v1Aliases_that_isnt_a_list_of_names_refuses_the_manifest(string aliases)
    {
        var manifest = PanelPackages.ReadManifest(Package("InfoPanel.Panels.Bad", Renamed.Replace("""["TrofeoVision686"]""", aliases)), out var error);

        Assert.Null(manifest);
        Assert.Contains("v1Aliases", error);
    }

    [Fact]
    public void A_1x_panel_known_only_by_an_alias_imports_as_its_model()
    {
        // 1.x's settings.xml with a panel saved under the model's old name.
        var v1 = Directory.CreateDirectory(Path.Combine(_root, "v1")).FullName;
        File.WriteAllText(Path.Combine(v1, V1Folder.SettingsFile), """
            <?xml version="1.0" encoding="utf-8"?>
            <Settings xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Version>131</Version>
              <ThermalrightPanelDevices>
                <ThermalrightPanelDevice>
                  <DeviceId>USB\VID_87AD&amp;PID_70DB\SERIAL1</DeviceId>
                  <DeviceLocation>Port_#0002.Hub_#0001</DeviceLocation>
                  <Model>TrofeoVision686</Model>
                  <Enabled>true</Enabled>
                </ThermalrightPanelDevice>
              </ThermalrightPanelDevices>
            </Settings>
            """);
        var family = Assert.Single(PanelPackages.ReadManifest(Package("InfoPanel.Panels.Renamed", Renamed), out _)!.Families);

        var state = new V1Folder(v1).GetState(new DataPaths(Path.Combine(_root, "data")));

        var device = Assert.Single(state.Settings!.Devices, d => d.Family == "thermalright");
        var model = family.ModelOfV1(device.Fields["Model"]);
        Assert.Equal("trofeo-686", model?.Id);
        Assert.True(model!.IsV1("TrofeoVision686"));
        // Its own name maps too, and a name no model has maps to none.
        Assert.Equal("trofeo-686", family.ModelOfV1("TrofeoVision686Inch")?.Id);
        Assert.Null(family.ModelOfV1("TrofeoVision"));
    }

    [Fact]
    public void A_1x_panels_frame_rate_comes_over_with_it_for_its_own_frame_rate()
    {
        // The 1.x fixture's Turing panels were saved at 30 fps each (1.4 had a frame rate per device): the reader keeps
        // the field raw, and a client saves it as the panel's frameRate.
        var state = new V1Folder(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder")).GetState(new DataPaths(Path.Combine(_root, "data")));

        var turing = state.Settings!.Devices.Where(d => d.Family == "turing").ToList();
        Assert.Equal(2, turing.Count);
        Assert.All(turing, d => Assert.Equal("30", d.Fields["TargetFrameRate"]));
    }

    [Fact]
    public void A_link_put_in_the_manifests_place_after_the_check_is_refused_not_followed()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Windows: making a symbolic link needs a privilege the test run may not have; the handle's reparse check is ManifestFile's (FILE_FLAG_OPEN_REPARSE_POINT), tested on the PC.");
        var dir = Package("InfoPanel.Panels.Raced", Renamed);
        var elsewhere = Path.Combine(_root, "elsewhere.json");
        File.WriteAllText(elsewhere, Renamed.Replace("\"renamed\"", "\"swapped\""));
        PanelPackages.ManifestChecked = path =>
        {
            // The swap: the regular file checked is replaced by a link to another file before it is opened.
            File.Move(path, path + ".old");
            File.CreateSymbolicLink(path, elsewhere);
        };
        try
        {
            var manifest = PanelPackages.ReadManifest(dir, out var error);

            Assert.Null(manifest);
            Assert.Contains("link", error);
        }
        finally
        {
            PanelPackages.ManifestChecked = null;
        }
    }
}
