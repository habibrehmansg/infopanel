using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using InfoPanel.Import;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using InfoPanel.Panels.Thermalright;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The package as the engine meets it: run from its own build output with its own <c>panel.json</c> (the kit's
/// <see cref="PanelPackageOutput"/>), that file read by the engine's reader (<see cref="PanelPackages.ReadManifest"/>)
/// with the family's settings and their 1.x fields, a 1.x settings file's Thermalright panels mapped through it (the model
/// by <c>v1Model</c> or <c>v1Aliases</c>, each setting by its <c>v1Field</c>), a panel's settings checked and kept by the
/// engine and handed to the driver's start, and the driver built by the host with the device seam alone.
/// </summary>
public sealed class ThermalrightPackageTests
{
    private static readonly PanelCandidate ChiZhu = new(0x87AD, 0x70DB, 1, 2, "1-2");
    private static readonly PanelCandidate Ly = new(0x0416, 0x5408, 1, 4, "1-4");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The family as the engine reads the package's own <c>panel.json</c>, beside the driver's DLL.</summary>
    private static PanelPackageFamily EngineFamily()
    {
        var directory = Path.GetDirectoryName(typeof(ThermalrightDriver).Assembly.Location)!;
        var manifest = PanelPackages.ReadManifest(directory, out var error);
        Assert.True(manifest != null, $"the engine didn't read {directory}'s panel.json: {error}");
        return Assert.Single(manifest!.Families);
    }

    [Fact]
    public void ThePackage_RunsFromItsOwnOutput_WithItsOwnManifest()
    {
        var dll = PanelPackageOutput.Dll("InfoPanel.Panels.Thermalright");
        var assembly = typeof(ThermalrightDriver).Assembly;

        Assert.NotNull(dll);
        Assert.Equal(dll, assembly.Location);
        Assert.Equal(ThermalrightCatalog.FamilyId, PanelManifest.Read(assembly).Family(ThermalrightCatalog.FamilyId).Id);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "InfoPanel.Panels.Thermalright.dll")), "no package's DLL beside the test assembly");
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "70-infopanel-thermalright.rules")), "no package's rule beside the test assembly");
    }

    [Fact]
    public void TheEnginesReader_TakesTheFamilysSettings_EachWithIts1xField()
    {
        // 1.4's ThermalrightPanelDevice kept DisplayMask (None, RoundedLeft, RoundedAll) and FlickerFix per device; the
        // model setting names the model a panel is driven as. The mask's values are 1.4's own names, so a 1.x panel's
        // element is the setting's value as it stands.
        var settings = EngineFamily().Settings;

        Assert.Equal(["displayMask", "flickerFix", "model"], settings.Select(s => s.Id));
        var mask = settings[0];
        Assert.Equal(("enum", "DisplayMask", "None"), (mask.Type, mask.V1Field, mask.Default.GetString()));
        Assert.Equal(["None", "RoundedLeft", "RoundedAll"], mask.Values.Select(v => v.Id));
        var flicker = settings[1];
        Assert.Equal(("boolean", "FlickerFix", JsonValueKind.False), (flicker.Type, flicker.V1Field, flicker.Default.ValueKind));
        var model = settings[2];
        Assert.Equal(("model", (string?)null, JsonValueKind.Null), (model.Type, model.V1Field, model.Default.ValueKind));
    }

    [Fact]
    public void V1Import_ThroughTheEnginesReader_TheModelByV1ModelOrAlias_AndEachSettingByItsV1Field()
    {
        // §2.2 (5): the engine's 1.x fixture read through V1SettingsReader, its two Thermalright panels set as a 1.x user
        // could have set them: the first a Wonder Vision 360 with its mask rounded on every corner, the second saved under
        // 1.x's older name of the 6.86" Trofeo Vision (TrofeoVision686) with the flicker fix on. Each maps through the
        // family the engine read: the model by its v1Model or v1Aliases, each setting by its v1Field (a boolean's text read
        // as true or false), then checked and kept as the engine keeps a saved panel's settings.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder", "settings.xml");
        var xml = XElement.Load(path, LoadOptions.SetLineInfo);
        var saved = xml.Descendants("ThermalrightPanelDevice").ToList();
        Assert.Equal(2, saved.Count);
        saved[0].Element("DisplayMask")!.Value = "RoundedAll";
        saved[1].Element("Model")!.Value = "TrofeoVision686";
        saved[1].Element("FlickerFix")!.Value = "true";
        var settings = V1SettingsReader.Read(xml, path, new ImportLimits(), new ImportDiagnostics(100));
        var devices = settings.Devices.Where(d => d.Family == ThermalrightCatalog.FamilyId).ToList();
        var family = EngineFamily();
        var models = family.Models.Select(m => m.Id).ToList();

        var imported = devices.Select(device =>
        {
            var model = family.ModelOfV1(device.Fields["Model"]);
            var change = new Dictionary<string, object>();
            foreach (var setting in family.Settings.Where(s => s.V1Field != null))
                change[setting.Id] = setting.Type == "boolean" ? bool.Parse(device.Fields[setting.V1Field!]) : device.Fields[setting.V1Field!];
            var kept = PanelFamilySettings.Apply(null, Json(JsonSerializer.Serialize(change)), family.Settings, models, out var refused);
            Assert.Empty(refused);
            return (model?.Id, ThermalrightSettings.Read(PanelFamilySettings.ForOpen(kept, family.Settings, models, out _)));
        }).ToList();

        Assert.Equal([
            ("wondervision360", new ThermalrightSettings(ThermalrightMask.RoundedAll, false, null)),
            ("trofeovision", new ThermalrightSettings(ThermalrightMask.None, true, null)),
        ], imported);
    }

    [Fact]
    public void FamilySettings_RoundTripThroughTheEngine_ToTheDriversStart()
    {
        // A client's change checked and kept by the engine (PanelFamilySettings.Apply, as the API keeps it in
        // devices.json), handed to the start (ForOpen, as the manager passes PanelOpenOptions.FamilySettings): a 6.67"
        // panel's mask becomes its overlays, a 9.16" panel's flicker fix its 462-row crop, and the model setting the model
        // a panel is driven as. A value the family doesn't take is refused, and nothing changes.
        var family = EngineFamily();
        var models = family.Models.Select(m => m.Id).ToList();
        var kept = PanelFamilySettings.Apply(null, Json("""{"displayMask": "RoundedLeft", "flickerFix": true}"""), family.Settings, models, out var refused);
        Assert.Empty(refused);
        Assert.Equal("""{"displayMask":"RoundedLeft","flickerFix":true}""", kept);
        Assert.Equal(kept, PanelFamilySettings.Apply(kept, Json("""{"displayMask": "roundedLeft", "model": "nope"}"""), family.Settings, models, out refused));
        Assert.Equal(["displayMask", "model"], refused.Select(r => r.Key));

        var devices = new FakePanelDevices();
        var driver = new ThermalrightDriver(devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() };
        var open = new PanelOpenOptions(100, FamilySettings: PanelFamilySettings.ForOpen(kept, family.Settings, models, out var mismatched));
        Assert.Empty(mismatched);

        var chiZhu = devices.Add(ChiZhu);
        var reply = new byte[48];
        Encoding.ASCII.GetBytes("SSCRM-V3").CopyTo(reply, 4);
        reply[28] = 1; // SUB 1: the Wonder Vision 360
        chiZhu.Usb.Reply(reply);
        using (var session = driver.Open(ChiZhu, new PanelIdentity("wondervision360", 1600, 720, null, null), open, TestContext.Current.CancellationToken).Session!)
        {
            Assert.Equal([0, 90, 180, 270], session.Capabilities.Overlays!.Keys.Order());
            Assert.Null(session.Capabilities.Crop);
        }

        var ly = devices.Add(Ly);
        var lyReply = new byte[512];
        (lyReply[0], lyReply[1], lyReply[8], lyReply[20]) = (0x03, 0xFF, 0x01, 1);
        BitConverter.GetBytes(1920).CopyTo(lyReply, 24);
        BitConverter.GetBytes(480).CopyTo(lyReply, 28);
        ly.Usb.Reply(lyReply);
        using (var session = driver.Open(Ly, new PanelIdentity("trofeovision916", 1920, 480, null, null), open, TestContext.Current.CancellationToken).Session!)
        {
            Assert.Equal(new PanelCrop(0, 0, 1920, 462), session.Capabilities.Crop);
            Assert.Null(session.Capabilities.Overlays);
        }

        // The model setting, kept by the engine: a panel whose reply names no model runs as the one it names.
        kept = PanelFamilySettings.Apply(null, Json("""{"model": "lc3"}"""), family.Settings, models, out refused);
        Assert.Empty(refused);
        var unknown = new byte[48];
        (unknown[0], unknown[1], unknown[2], unknown[3], unknown[24], unknown[28]) = (0x12, 0x34, 0x56, 0x78, 200, 9); // PM 200, SUB 9: no model
        chiZhu.Usb.Reply(unknown);
        using (var session = driver.Open(ChiZhu, new PanelIdentity("pm-200-sub-9", 480, 480, null, null),
            new PanelOpenOptions(100, FamilySettings: PanelFamilySettings.ForOpen(kept, family.Settings, models, out _)), TestContext.Current.CancellationToken).Session!)
            Assert.Equal((960, 540), (session.Capabilities.Width, session.Capabilities.Height));
    }

    [Fact]
    public void TheDriver_IsBuiltWithTheDeviceSeam_Alone()
    {
        // The panel host gives a package's drivers the engine's device seam (a public constructor taking IPanelDevices);
        // the driver has no other way in, so it never runs without its devices.
        var constructor = Assert.Single(typeof(ThermalrightDriver).GetConstructors());
        Assert.Equal([typeof(IPanelDevices)], constructor.GetParameters().Select(p => p.ParameterType));
    }
}
