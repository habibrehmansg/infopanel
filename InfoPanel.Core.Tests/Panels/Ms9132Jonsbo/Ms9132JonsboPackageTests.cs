using System.Text.Json;
using System.Xml.Linq;
using InfoPanel.Import;
using InfoPanel.Panels;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Testing;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The package as the engine meets it: run from its own build output with its own <c>panel.json</c>, the 1.x fixture's
/// Jonsbo and VMAX panels mapped through the families the engine read (the model by <c>v1Model</c>, the VMAX's
/// <c>ScreenSwitch</c> by its <c>v1Field</c>), a panel's family settings checked and kept by the engine and handed to the
/// driver's start, and each driver built by the host with the device seam alone.
/// </summary>
public sealed class Ms9132JonsboPackageTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyList<PanelPackageFamily> EngineFamilies()
    {
        var directory = Path.GetDirectoryName(typeof(JonsboDriver).Assembly.Location)!;
        var manifest = PanelPackages.ReadManifest(directory, out var error);
        Assert.True(manifest != null, $"the engine didn't read {directory}'s panel.json: {error}");
        return manifest!.Families;
    }

    [Fact]
    public void ThePackage_RunsFromItsOwnOutput_WithItsOwnManifest()
    {
        var dll = PanelPackageOutput.Dll("InfoPanel.Panels.Ms9132Jonsbo");
        var assembly = typeof(JonsboDriver).Assembly;

        Assert.NotNull(dll);
        Assert.Equal(dll, assembly.Location);
        Assert.Equal(["jonsbo", "vmax"], PanelManifest.Read(assembly).Families.Select(f => f.Id));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "InfoPanel.Panels.Ms9132Jonsbo.dll")), "no package's DLL beside the test assembly");
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "70-infopanel-ms9132jonsbo.rules")), "the udev rule beside the package");
    }

    [Fact]
    public void V1Import_TheFixturesDs916AndVmax46Inch_MapToTheirModels_AndTheVmaxsScreenSwitchToItsSetting()
    {
        // The engine's 1.x fixture read through V1SettingsReader: its Jonsbo panels (DS916) and its VMAX panels
        // (Vmax46Inch with ScreenSwitch), the second VMAX set as a 1.x user could have set it, with the screen off. Each maps
        // through the family the engine read: the model by its v1Model, each setting by its v1Field, then checked and kept
        // as the engine keeps a saved panel's settings and handed to the start as the driver reads it.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1-folder", "settings.xml");
        var xml = XElement.Load(path, LoadOptions.SetLineInfo);
        var vmaxes = xml.Descendants("VmaxPanelDevice").ToList();
        Assert.Equal(2, vmaxes.Count);
        vmaxes[1].Element("ScreenSwitch")!.Value = "false";
        var settings = V1SettingsReader.Read(xml, path, new ImportLimits(), new ImportDiagnostics(100));
        var families = EngineFamilies();

        var jonsbo = families.Single(f => f.Id == "jonsbo");
        var ds916s = settings.Devices.Where(d => d.Family == "jonsbo").ToList();
        Assert.Equal(2, ds916s.Count);
        Assert.All(ds916s, d => Assert.Equal("ds916", jonsbo.ModelOfV1(d.Fields["Model"])?.Id));
        Assert.Empty(jonsbo.Settings.Where(s => s.V1Field != null)); // the DS916 carries no family setting

        var vmax = families.Single(f => f.Id == "vmax");
        var models = vmax.Models.Select(m => m.Id).ToList();
        var imported = settings.Devices.Where(d => d.Family == "vmax").Select(device =>
        {
            var change = new Dictionary<string, object>();
            foreach (var setting in vmax.Settings.Where(s => s.V1Field != null))
                change[setting.Id] = setting.Type == "boolean" ? bool.Parse(device.Fields[setting.V1Field!]) : device.Fields[setting.V1Field!];
            var kept = PanelFamilySettings.Apply(null, Json(JsonSerializer.Serialize(change)), vmax.Settings, models, out var refused);
            Assert.Empty(refused);
            return (vmax.ModelOfV1(device.Fields["Model"])?.Id, VmaxDriver.ScreenOn(PanelFamilySettings.ForOpen(kept, vmax.Settings, models, out _)));
        }).ToList();

        Assert.Equal([("vmax46inch", true), ("vmax46inch", false)], imported);
    }

    [Fact]
    public void TheVmaxsScreenOn_RoundTripsThroughTheEngine_ToItsStart()
    {
        // A client's change checked and kept by the engine, handed to the start: Screen on off sends the switch off once
        // frames flow (VmaxUsbDevice.cs:367-373). A value the family doesn't take is refused, and nothing changes.
        var vmax = EngineFamilies().Single(f => f.Id == "vmax");
        var models = vmax.Models.Select(m => m.Id).ToList();
        var kept = PanelFamilySettings.Apply(null, Json("""{"screenSwitch": false}"""), vmax.Settings, models, out var refused);
        Assert.Empty(refused);
        Assert.Equal("""{"screenSwitch":false}""", kept);
        Assert.Equal(kept, PanelFamilySettings.Apply(kept, Json("""{"screenSwitch": "off"}"""), vmax.Settings, models, out refused));
        Assert.Equal(["screenSwitch"], refused.Select(r => r.Key));

        var devices = new Ms9132Devices();
        var fake = Ms9132Fake.Add(devices, EdidSamples.Vmax);
        var driver = new VmaxDriver(devices) { Wait = (_, stop) => stop.ThrowIfCancellationRequested() };
        var options = new PanelOpenOptions(100, FamilySettings: PanelFamilySettings.ForOpen(kept, vmax.Settings, models, out _));
        using var session = driver.Open(Ms9132Fake.Candidate, new PanelIdentity("vmax46inch", 320, 960, null, null), options, TestContext.Current.CancellationToken).Session!;
        var mark = fake.Device.Trace.Entries.Count;
        Assert.Equal(PanelDeviceStatus.Ok, session.WriteFrame(new PanelFrameView(new byte[320 * 960 * 3], PanelFrameFormat.Bgr888, 1, false), TestContext.Current.CancellationToken).Status);
        Assert.Equal([VmaxMs9132.Status.ToArray(), .. VmaxMs9132.ScreenSwitch(false)], fake.Reports(mark));
    }

    [Fact]
    public void TheJonsboModelSetting_IsKeptByTheEngine_AndNamesAModelOfTheFamily()
    {
        var jonsbo = EngineFamilies().Single(f => f.Id == "jonsbo");
        var models = jonsbo.Models.Select(m => m.Id).ToList();
        Assert.Equal(["ds916", "ds339"], models);
        var kept = PanelFamilySettings.Apply(null, Json("""{"model": "ds339"}"""), jonsbo.Settings, models, out var refused);
        Assert.Empty(refused);
        Assert.Equal(kept, PanelFamilySettings.Apply(kept, Json("""{"model": "vmax46inch"}"""), jonsbo.Settings, models, out refused));
        Assert.Equal(["model"], refused.Select(r => r.Key));
    }

    [Fact]
    public void TheDrivers_AreBuiltWithTheDeviceSeam_Alone()
    {
        // The panel host gives a package's drivers the engine's device seam (a public constructor taking IPanelDevices).
        foreach (var type in new[] { typeof(JonsboDriver), typeof(VmaxDriver) })
        {
            var constructor = Assert.Single(type.GetConstructors());
            Assert.Equal([typeof(IPanelDevices)], constructor.GetParameters().Select(p => p.ParameterType));
        }
        var exported = typeof(JonsboDriver).Assembly.GetExportedTypes().Where(t => typeof(IPanelDriver).IsAssignableFrom(t)).Select(t => t.Name).Order();
        Assert.Equal(["JonsboDriver", "VmaxDriver"], exported);
    }
}
