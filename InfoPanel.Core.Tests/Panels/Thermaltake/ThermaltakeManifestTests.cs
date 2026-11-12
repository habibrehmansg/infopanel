using InfoPanel.Core.Tests.Panels.Thermaltake.V1Oracle;
using InfoPanel.Panels;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Thermaltake;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermaltake;

/// <summary>
/// The manifest test (§2.2 (4)): the package's <c>panel.json</c>, read as the engine reads it (<c>PanelPackages</c>) and
/// as the driver reads it (the SDK's <c>PanelManifest</c>), is 1.4.x's model table: its <c>v1Model</c> set is 1.4.x's
/// enum names but <c>Unknown</c>, and each model's USB id, size and name are 1.4.x's
/// (<c>V1Oracle/ThermaltakePanelModelDatabase.cs</c>).
/// </summary>
public sealed class ThermaltakeManifestTests
{
    /// <summary>The names of 1.4.x c71f0f00's <c>ThermaltakePanelModel</c> (<c>InfoPanel/ThermaltakePanel/ThermaltakePanelModel.cs:5-7</c>), written from its source.</summary>
    private static readonly string[] V1ModelNames = ["Unknown", "ToughLiquid6Inch", "AsrockPhantomGaming360LCD"];

    private static PanelManifestFamily Family() => PanelManifest.Read(typeof(ThermaltakeProtocol).Assembly).Family("thermaltake");

    [Fact]
    public void TheEngineReadsTheManifest_AsTheDriverDoes()
    {
        var directory = Path.GetDirectoryName(typeof(ThermaltakeProtocol).Assembly.Location)!;
        var engine = PanelPackages.ReadManifest(directory, out var error);
        Assert.True(engine != null, error);
        var family = Assert.Single(engine!.Families);
        var sdk = Family();

        Assert.Equal("thermaltake", family.Id);
        Assert.Equal(sdk.Matches, family.Matches);
        Assert.Equal(sdk.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, m.V1Model)),
            family.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, m.V1Model)));
    }

    [Fact]
    public void ItsV1Models_Are1_4sEnumButUnknown()
    {
        var v1 = Family().Models.Select(m => m.V1Model ?? "(none)").ToHashSet();

        Assert.Equal(V1ModelNames.Where(n => n != "Unknown").ToHashSet(), v1);
        Assert.Equal(Enum.GetNames<ThermaltakePanelModel>().Where(n => n != "Unknown").ToHashSet(), v1); // the oracle's copy agrees
    }

    [Fact]
    public void EachModel_Is1_4sEntry_ByUsbIdSizeAndName()
    {
        var family = Family();
        foreach (var info in ThermaltakePanelModelDatabase.Models.Values)
        {
            var model = Assert.Single(family.Models, m => m.V1Model == info.Model.ToString());
            Assert.Equal($"{info.VendorId:x4}:{info.ProductId:x4}", model.Json.GetProperty("usb").GetString());
            Assert.Equal((info.Width, info.Height, info.Name), (model.Width, model.Height, model.Name));
        }
        Assert.Equal(ThermaltakePanelModelDatabase.Models.Count, family.Models.Count);
    }

    [Fact]
    public void ItMatches_1_4sDevices_OverHid_AndDetectsOnlyThermaltakesPanelRange()
    {
        var family = Family();

        Assert.Equal(ThermaltakePanelModelDatabase.SupportedDevices.Select(d => new PanelMatch((ushort)d.Vid, (ushort)d.Pid, null, PanelTransport.Hid)),
            family.Matches);
        // 3c's pattern: Thermaltake's 23xx products; none for ASRock, whose vendor id covers its boards' own devices.
        var detect = family.Json.GetProperty("detect").EnumerateArray().Select(d => d.GetProperty("usb").GetString()).ToList();
        Assert.Equal(["264a:23??"], detect);
    }
}
