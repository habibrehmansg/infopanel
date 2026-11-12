using System.Text.Json;
using System.Xml.Linq;
using InfoPanel.Import;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Turing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Turing;

/// <summary>
/// <c>panel.json</c>, the package's one table (§2.2 (4)), read with <c>System.Text.Json</c>, by the SDK's
/// <see cref="PanelManifest"/> and by the engine's <see cref="PanelPackages"/>: both families, every 1.4.x model by its
/// enum name, the drivers' matches; and the 1.x import of the engine's fixture (§2.2 (5)).
/// </summary>
public sealed class TuringManifestTests
{
    // git show 1.4.x:InfoPanel/TuringPanel/TuringPanelModel.cs (the 1.4.x tip, c71f0f00): every member.
    private static readonly string[] TuringModels =
    [
        "TURING_3_5", "XUANFANG_3_5", "REV_2INCH", "REV_5INCH", "REV_8INCH", "REV_88INCH_USB", "REV_8INCH_USB",
        "REV_92INCH_USB", "REV_46INCH_USB", "REV_5INCH_USB", "REV_16INCH_USB", "REV_21INCH_USB",
    ];

    // git show 1.4.x:InfoPanel/LianLiPanel/LianLiPanelModel.cs: every member.
    private static readonly string[] LianLiModels = ["UniversalScreen88Inch", "UniversalScreen92Inch", "HydroShiftIIOledCurve", "HydroShiftIILcd"];

    private static string ManifestPath => Path.Combine(Path.GetDirectoryName(typeof(TuringDriver).Assembly.Location)!, "panel.json");

    [Fact]
    public void TheV1Models_AreThe1_4xEnums_12And4()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(ManifestPath));
        var families = json.RootElement.GetProperty("families").EnumerateArray().ToDictionary(f => f.GetProperty("id").GetString()!);

        Assert.Equal(["turing", "lianli"], families.Keys);
        Assert.Equal(TuringModels.Order(), families["turing"].GetProperty("models").EnumerateArray().Select(m => m.GetProperty("v1Model").GetString()!).Order());
        Assert.Equal(LianLiModels.Order(), families["lianli"].GetProperty("models").EnumerateArray().Select(m => m.GetProperty("v1Model").GetString()!).Order());
    }

    [Fact]
    public void TheTable_Is1_4s()
    {
        // TuringPanelModelDatabase.cs:14-25 and LianLiPanelModelDatabase.cs:13-47: each model's size and USB id; the
        // serial ones with their revision (TuringPanelSerialTask.cs:43-72), the XuanFang with none 1.4.x drove.
        var manifest = PanelManifest.Read(typeof(TuringDriver).Assembly);
        string Row(PanelManifestModel m) => $"{m.V1Model} {m.Width}x{m.Height} {m.Json.GetProperty("usb").GetString()}" +
            (m.Json.TryGetProperty("revision", out var r) ? " " + r.GetString() : "");

        Assert.Equal(
        [
            "TURING_3_5 320x480 1a86:5722 A", "XUANFANG_3_5 320x480 1a86:5722 XuanFang", "REV_2INCH 480x480 1d6b:0121 E",
            "REV_5INCH 800x480 1d6b:0106 C", "REV_8INCH 480x1920 0525:a4a7 E", "REV_88INCH_USB 480x1920 1cbe:0088",
            "REV_8INCH_USB 800x1280 1cbe:0080", "REV_92INCH_USB 464x1920 1cbe:0092", "REV_46INCH_USB 320x960 1cbe:0046",
            "REV_5INCH_USB 720x1280 1cbe:0050", "REV_16INCH_USB 400x400 1cbe:0016", "REV_21INCH_USB 480x480 1cbe:0021",
        ], manifest.Family("turing").Models.Select(Row));
        Assert.Equal(
        [
            "UniversalScreen88Inch 480x1920 1cbe:a088", "UniversalScreen92Inch 464x1920 1cbe:a092",
            "HydroShiftIIOledCurve 2288x1080 1cbe:a068", "HydroShiftIILcd 480x480 1cbe:a034",
        ], manifest.Family("lianli").Models.Select(Row));
    }

    [Fact]
    public void TheDriversMatches_AreTheManifests_AndEveryModelsIdIsMatched()
    {
        var manifest = PanelManifest.Read(typeof(TuringDriver).Assembly);

        Assert.Equal(manifest.Family("turing").Matches, new TuringDriver().Matches);
        Assert.Equal(manifest.Family("lianli").Matches, new LianLiDriver().Matches);
        foreach (var family in manifest.Families)
        {
            var matched = family.Matches.Select(m => $"{m.VendorId:x4}:{m.ProductId:x4}").ToHashSet();
            Assert.All(family.Models, m => Assert.Contains(m.Json.GetProperty("usb").GetString(), matched));
            Assert.Equal(matched.Order(), family.Models.Select(m => m.Json.GetProperty("usb").GetString()!).Distinct().Order());
        }
        Assert.All(manifest.Family("turing").Matches.Where(m => m.VendorId == 0x1CBE), m => Assert.Equal((PanelTransport.Usb, 0), (m.Transport, m.Interface)));
        Assert.All(manifest.Family("turing").Matches.Where(m => m.VendorId != 0x1CBE), m => Assert.Equal(PanelTransport.Serial, m.Transport));
    }

    [Fact]
    public void TheEngine_ReadsThePackage_WithItsModelSetting()
    {
        var read = PanelPackages.ReadManifest(Path.GetDirectoryName(ManifestPath)!, out var error);

        Assert.True(read != null, error);
        Assert.Equal(["turing", "lianli"], read!.Families.Select(f => f.Id));
        var turing = read.Families[0];
        Assert.Equal("model", Assert.Single(turing.Settings).Id);
        Assert.Empty(read.Families[1].Settings);
    }

    [Fact]
    public void V1Import_TheFixturesPanels_MapByTheirV1Model()
    {
        // §2.2 (5): the engine's 1.x fixture read through V1SettingsReader. Its Lian Li devices are UniversalScreen88Inch,
        // the 8.8"'s model. Its Turing devices say REV_35INCH_USB, a name no 1.4.x build has (the fixture's own; neither
        // the tip's enum nor its history, nor 1.4.x's 1.3.1 upgrade, nor the reader's rename has it): no model takes it,
        // so the import leaves the model to the device's USB id. Rotation, brightness and the frame rate are the engine's.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder", "settings.xml");
        var settings = V1SettingsReader.Read(XElement.Load(path, LoadOptions.SetLineInfo), path, new ImportLimits(), new ImportDiagnostics(100));
        var manifest = PanelManifest.Read(typeof(TuringDriver).Assembly);

        var lianLi = settings.Devices.Where(d => d.Family == "lianLi").ToList();
        Assert.Equal(2, lianLi.Count);
        Assert.All(lianLi, d => Assert.Equal("universalscreen88inch", Assert.Single(manifest.Family("lianli").Models, m => m.V1Model == d.Fields["Model"]).Id));

        var turing = settings.Devices.Where(d => d.Family == "turing").ToList();
        Assert.Equal(2, turing.Count);
        Assert.All(turing, d => Assert.Equal("REV_35INCH_USB", d.Fields["Model"]));
        Assert.DoesNotContain(manifest.Family("turing").Models, m => m.V1Model == "REV_35INCH_USB");
    }

    [Fact]
    public void V1Import_The1_3_1Rename_LandsOnThe8_8InchRev1_1()
    {
        // ConfigModel.cs:1017-1031 (1.4.x's Upgrade_File_Structure_For_1_3_1), as the engine's reader does it
        // (V1Settings.cs:150-176): a settings file older than 131 whose Turing REV_8INCH_USB is on 1cbe:0088 is the 8.8"
        // Rev 1.1, which the package's table has as REV_88INCH_USB.
        var xml = XElement.Parse("""
            <Settings>
              <Version>130</Version>
              <TuringPanelDevices>
                <TuringPanelDevice><DeviceId>USB\VID_1CBE&amp;PID_0088\A1</DeviceId><Model>REV_8INCH_USB</Model></TuringPanelDevice>
                <TuringPanelDevice><DeviceId>USB\VID_1CBE&amp;PID_0080\A2</DeviceId><Model>REV_8INCH_USB</Model></TuringPanelDevice>
              </TuringPanelDevices>
            </Settings>
            """, LoadOptions.SetLineInfo);
        var settings = V1SettingsReader.Read(xml, "settings.xml", new ImportLimits(), new ImportDiagnostics(100));
        var models = PanelManifest.Read(typeof(TuringDriver).Assembly).Family("turing").Models;

        var ids = settings.Devices.Where(d => d.Family == "turing").Select(d => Assert.Single(models, m => m.V1Model == d.Fields["Model"]).Id);
        Assert.Equal(["rev88inchusb", "rev8inchusb"], ids);
    }
}
