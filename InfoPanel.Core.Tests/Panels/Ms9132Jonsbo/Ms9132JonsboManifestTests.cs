using System.Text.Json;
using InfoPanel.Core.Tests.Panels.Ms9132Jonsbo.V1Oracle;
using InfoPanel.Panels;
using InfoPanel.Panels.Ms9132Jonsbo;
using InfoPanel.Panels.Sdk;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Ms9132Jonsbo;

/// <summary>
/// The manifest test: the package's <c>panel.json</c>, read as the engine reads it (<c>PanelPackages</c>) and
/// as the drivers read it (the SDK's <c>PanelManifest</c>), is 1.4.x's two model tables: each family's <c>v1Model</c> set
/// is its 1.4.x enum's names but <c>Unknown</c>, and each model's USB id, size and name are 1.4.x's
/// (<c>V1Oracle/ModelDatabases.cs</c>); the families match 1.4.x's devices, and declare their settings.
/// </summary>
public sealed class Ms9132JonsboManifestTests
{
    /// <summary>The names of 1.4.x c71f0f00's <c>JonsboPanelModel</c> (<c>InfoPanel/JonsboPanel/JonsboPanelModel.cs:5-7</c>), written from its source.</summary>
    private static readonly string[] JonsboNames = ["Unknown", "DS916", "DS339"];

    /// <summary>The names of 1.4.x c71f0f00's <c>VmaxPanelModel</c> (<c>InfoPanel/VmaxPanel/VmaxPanelModel.cs:5-6</c>), written from its source.</summary>
    private static readonly string[] VmaxNames = ["Unknown", "Vmax46Inch"];

    private static PanelManifest Manifest() => PanelManifest.Read(typeof(Ms9132Protocol).Assembly);

    [Fact]
    public void TheEngineReadsTheManifest_AsTheDriversDo_JonsboFirst()
    {
        var directory = Path.GetDirectoryName(typeof(Ms9132Protocol).Assembly.Location)!;
        var engine = PanelPackages.ReadManifest(directory, out var error);
        Assert.True(engine != null, error);
        var sdk = Manifest();

        // The engine offers a device to a package's families in this order: jonsbo's probe sees an MS9132 first.
        Assert.Equal(["jonsbo", "vmax"], engine!.Families.Select(f => f.Id));
        Assert.Equal(sdk.Families.Select(f => f.Id), engine.Families.Select(f => f.Id));
        foreach (var family in engine.Families)
        {
            var declared = sdk.Family(family.Id);
            Assert.Equal(declared.Matches, family.Matches);
            Assert.Equal(declared.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, m.V1Model)),
                family.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, m.V1Model)));
        }
    }

    [Fact]
    public void ItsV1Models_Are1_4sEnumsButUnknown()
    {
        var manifest = Manifest();
        Assert.Equal(JonsboNames.Where(n => n != "Unknown").ToHashSet(), manifest.Family("jonsbo").Models.Select(m => m.V1Model ?? "(none)").ToHashSet());
        Assert.Equal(VmaxNames.Where(n => n != "Unknown").ToHashSet(), manifest.Family("vmax").Models.Select(m => m.V1Model ?? "(none)").ToHashSet());
        Assert.Equal(JonsboNames, Enum.GetNames<JonsboPanelModel>()); // the oracle's copies agree
        Assert.Equal(VmaxNames, Enum.GetNames<VmaxPanelModel>());
    }

    [Fact]
    public void EachModel_Is1_4sEntry_ByUsbIdSizeAndName()
    {
        var jonsbo = Manifest().Family("jonsbo");
        foreach (var info in JonsboPanelModelDatabase.Models.Values)
        {
            var model = Assert.Single(jonsbo.Models, m => m.V1Model == info.Model.ToString());
            Assert.Equal($"{info.VendorId:x4}:{info.ProductId:x4}", model.Json.GetProperty("usb").GetString());
            Assert.Equal((info.Width, info.Height, info.Name), (model.Width, model.Height, model.Name));
            Assert.Equal(info.TransportType == JonsboTransportType.Serial ? "serial" : "ms9132", model.Json.GetProperty("transport").GetString());
            Assert.Equal(info.DefaultFrameRate, model.Json.GetProperty("defaultFrameRate").GetInt32());
            if (info.TransportType == JonsboTransportType.Ms9132)
                Assert.Equal(info.Vic, model.Json.GetProperty("vic").GetInt32());
            else
                Assert.Equal(info.MaxJpegBytes, model.Json.GetProperty("maxJpegBytes").GetInt32());
            // Models/JonsboPanelDevice.cs:36-39: a Jonsbo panel turned 90° by default (a value the engine doesn't apply yet).
            Assert.Equal(90, model.Json.GetProperty("defaultRotation").GetInt32());
        }
        Assert.Equal(JonsboPanelModelDatabase.Models.Count, jonsbo.Models.Count);

        var vmax = Manifest().Family("vmax");
        var only = Assert.Single(vmax.Models);
        var vinfo = VmaxPanelModelDatabase.Models[VmaxPanelModel.Vmax46Inch];
        Assert.Equal($"{vinfo.VendorId:x4}:{vinfo.ProductId:x4}", only.Json.GetProperty("usb").GetString());
        Assert.Equal((vinfo.Width, vinfo.Height, vinfo.Name), (only.Width, only.Height, only.Name));
        Assert.Equal(V1JonsboMs9132Device.GetVicForResolution(vinfo.Width, vinfo.Height), (byte)only.Json.GetProperty("vic").GetInt32());
        Assert.Equal(1, only.Json.GetProperty("defaultFrameRate").GetInt32()); // Models/VmaxPanelDevice.cs:53
    }

    [Fact]
    public void ItMatches_1_4sDevices_TheMs9132InBothFamilies()
    {
        var manifest = Manifest();
        Assert.Equal([new PanelMatch(0x33C3, 0xF101, null, PanelTransport.Serial), new PanelMatch(0x345F, 0x9132, 3, PanelTransport.Usb)],
            manifest.Family("jonsbo").Matches);
        Assert.Equal(JonsboPanelModelDatabase.SupportedDevices, manifest.Family("jonsbo").Matches.Select(m => ((int)m.VendorId, (int)m.ProductId)));
        Assert.Equal([new PanelMatch(0x345F, 0x9132, 3, PanelTransport.Usb)], manifest.Family("vmax").Matches);
        Assert.Equal(VmaxPanelModelDatabase.SupportedDevices, manifest.Family("vmax").Matches.Select(m => ((int)m.VendorId, (int)m.ProductId)));
        var detect = manifest.Family("jonsbo").Json.GetProperty("detect").EnumerateArray().Select(d => d.GetProperty("usb").GetString()).ToList();
        Assert.Equal(["33c3:f1??", "345f:91??"], detect);
        Assert.False(manifest.Family("vmax").Json.TryGetProperty("detect", out _));
    }

    [Fact]
    public void TheEnginesReader_TakesEachFamilysSettings()
    {
        var directory = Path.GetDirectoryName(typeof(Ms9132Protocol).Assembly.Location)!;
        var engine = PanelPackages.ReadManifest(directory, out _)!;

        // jonsbo: the model a panel the probe can't name is driven as ("Try it as…").
        var model = Assert.Single(engine.Families[0].Settings);
        Assert.Equal(("model", "model", (string?)null, JsonValueKind.Null), (model.Id, model.Type, model.V1Field, model.Default.ValueKind));
        // vmax: Models/VmaxPanelDevice.cs:49-50, ScreenSwitch, on by default.
        var screen = Assert.Single(engine.Families[1].Settings);
        Assert.Equal(("screenSwitch", "boolean", "ScreenSwitch", JsonValueKind.True), (screen.Id, screen.Type, screen.V1Field, screen.Default.ValueKind));
    }
}
