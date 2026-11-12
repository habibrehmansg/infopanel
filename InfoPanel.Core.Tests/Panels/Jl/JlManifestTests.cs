using InfoPanel.Core.Tests.Panels.Jl.V1Oracle;
using InfoPanel.Panels;
using InfoPanel.Panels.Jl;
using InfoPanel.Panels.Sdk;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Jl;

/// <summary>
/// The manifest test: the package's <c>panel.json</c>, read as the engine reads it (<c>PanelPackages</c>) and as
/// the driver reads it (the SDK's <c>PanelManifest</c>), is 1.4.x's model table: its <c>v1Model</c> set is 1.4.x's enum
/// names but <c>Unknown</c>, and each model's USB id, size, name, JPEG cap and default rate are 1.4.x's
/// (<c>V1Oracle/JlPanelModelDatabase.cs</c>); it matches 1.4.x's one USB id as a serial port, lists the OEM range 1.4.x
/// left unmatched as detected, and declares the <c>model</c> setting an unknown resolution needs.
/// </summary>
public sealed class JlManifestTests
{
    /// <summary>The names of 1.4.x c71f0f00's <c>JlPanelModel</c> (<c>InfoPanel/JlPanel/JlPanelModel.cs:5-7</c>), written from its source.</summary>
    private static readonly string[] V1ModelNames = ["Unknown", "ChillArc360", "StripDisplay"];

    private static PanelManifestFamily Family() => PanelManifest.Read(typeof(JlProtocol).Assembly).Family("jl");

    [Fact]
    public void TheEngineReadsTheManifest_AsTheDriverDoes()
    {
        var directory = Path.GetDirectoryName(typeof(JlProtocol).Assembly.Location)!;
        var engine = PanelPackages.ReadManifest(directory, out var error);
        Assert.True(engine != null, error);
        var family = Assert.Single(engine!.Families);
        var sdk = Family();

        Assert.Equal("jl", family.Id);
        Assert.Equal(sdk.Matches, family.Matches);
        Assert.Equal(sdk.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, m.V1Model)),
            family.Models.Select(m => (m.Id, m.Name, m.Width, m.Height, m.V1Model)));
        // "Try it as…": one setting, the model, with no default (a panel of a known resolution needs none).
        var setting = Assert.Single(family.Settings);
        Assert.Equal(("model", "model"), (setting.Id, setting.Type));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, setting.Default.ValueKind);
    }

    [Fact]
    public void ItsV1Models_Are1_4sEnumButUnknown()
    {
        var v1 = Family().Models.Select(m => m.V1Model ?? "(none)").ToHashSet();

        Assert.Equal(V1ModelNames.Where(n => n != "Unknown").ToHashSet(), v1);
        Assert.Equal(Enum.GetNames<JlPanelModel>().Where(n => n != "Unknown").ToHashSet(), v1); // the oracle's copy agrees
    }

    [Fact]
    public void EachModel_Is1_4sEntry_ByUsbIdSizeNameCapAndRate()
    {
        var family = Family();
        foreach (var info in JlPanelModelDatabase.Models.Values)
        {
            var model = Assert.Single(family.Models, m => m.V1Model == info.Model.ToString());
            Assert.Equal($"{info.VendorId:x4}:{info.ProductId:x4}", model.Json.GetProperty("usb").GetString());
            Assert.Equal((info.Width, info.Height, info.Name), (model.Width, model.Height, model.Name));
            Assert.Equal((info.MaxJpegBytes, info.DefaultFrameRate), (model.Json.GetProperty("maxJpegBytes").GetInt32(), model.Json.GetProperty("defaultFrameRate").GetInt32()));
            Assert.Equal(info.Model.ToString().ToLowerInvariant(), model.Id);
        }
        Assert.Equal(JlPanelModelDatabase.Models.Count, family.Models.Count);
        // 1.4.x's lookup by USB id gives its first model (JlPanelModelDatabase.cs:52-55); the manifest keeps that order.
        Assert.Equal(JlPanelModelDatabase.GetModelByVidPid(0x33C3, 0x7788)!.Model.ToString(), family.Models[0].V1Model);
    }

    [Fact]
    public void ItMatches_1_4sOneDevice_AsASerialPort_AndNotTheSpiVariant()
    {
        var family = Family();

        Assert.Equal(JlPanelModelDatabase.SupportedDevices.Select(d => new PanelMatch((ushort)d.Vid, (ushort)d.Pid, null, PanelTransport.Serial)),
            family.Matches);
        Assert.DoesNotContain(family.Matches, m => m.ProductId == 0x7793); // JlPanelModelDatabase.cs:17-20
    }

    /// <summary>Whether a <c>detect</c> pattern (<c>?</c> one hexadecimal digit, <c>*</c> any product id) names a device.</summary>
    private static bool Names(string pattern, ushort vendor, ushort product)
    {
        var id = $"{vendor:x4}:{product:x4}";
        if (pattern.EndsWith(":*", StringComparison.Ordinal))
            return id.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return pattern.Length == id.Length && pattern.Zip(id).All(p => p.First == '?' || p.First == p.Second);
    }

    [Fact]
    public void ItDetects_TheOemRange1_4LeftUnmatched()
    {
        // JlPanelModelDatabase.cs:17-24 and JL-PROTOCOL.md:17: 0x7791 to 0x7810 share the protocol but aren't matched.
        var detect = Family().Json.GetProperty("detect").EnumerateArray().Select(d => d.GetProperty("usb").GetString()!).ToList();

        for (var product = 0x7791; product <= 0x7810; product++)
            Assert.True(detect.Any(p => Names(p, 0x33C3, (ushort)product)), $"33c3:{product:x4} isn't detected");
        Assert.DoesNotContain(detect, p => Names(p, 0x33C3, 0x7788));
        Assert.DoesNotContain(detect, p => Names(p, 0x33C3, 0x7811) || Names(p, 0x33C3, 0x7700));
    }
}
