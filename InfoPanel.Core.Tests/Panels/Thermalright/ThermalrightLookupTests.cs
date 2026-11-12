using InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle;
using InfoPanel.Panels.Thermalright;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The catalog's lookups over <c>panel.json</c> against 1.4.x's own (the V1 oracle,
/// <c>ThermalrightPanelModelDatabase.cs:923-1155</c>), over every byte value they take.
/// </summary>
public sealed class ThermalrightLookupTests
{
    private static ThermalrightCatalog Catalog => ThermalrightCatalog.Default;

    internal static void Same(ThermalrightPanelModelInfo? v1, ThermalrightModel? model, string because)
    {
        if (v1 == null)
        {
            Assert.True(model == null, $"{because}: 1.4.x finds none, the port {model?.V1Model}");
            return;
        }
        Assert.True(model != null, $"{because}: 1.4.x finds {v1.Model}, the port none");
        Assert.True(v1.Model == Enum.Parse<ThermalrightPanelModel>(model!.V1Model), $"{because}: 1.4.x finds {v1.Model}, the port {model.V1Model}");
    }

    [Fact]
    public void ChiZhuPmAndSub_FindThe1_4Model_ForEveryPair()
    {
        // ThermalrightPanelModelDatabase.cs:1052-1155
        for (var pm = 0; pm < 256; pm++)
            for (var sub = 0; sub < 256; sub++)
                Same(ThermalrightPanelModelDatabase.GetModelByChiZhuPM((byte)pm, (byte)sub), Catalog.ByChiZhuPm((byte)pm, (byte)sub), $"PM {pm} SUB {sub}");
    }

    [Fact]
    public void HidPm_FindsThe1_4ModelAndSize_ForEveryPmAndSub()
    {
        // :942-991
        for (var pm = 0; pm < 256; pm++)
        {
            Same(ThermalrightPanelModelDatabase.GetModelByPM((byte)pm), Catalog.ByHidPm((byte)pm), $"PM {pm}");
            for (var sub = 0; sub < 256; sub++)
                Same(ThermalrightPanelModelDatabase.GetModelByPM((byte)pm, (byte)sub), Catalog.ByHidPm((byte)pm, (byte)sub), $"PM {pm} SUB {sub}");

            var v1 = ThermalrightPanelModelDatabase.GetResolutionFromPM((byte)pm);
            var size = Catalog.HidSize((byte)pm);
            Assert.Equal(v1, size == null ? null : (size.Width, size.Height, size.Size));
        }
    }

    public static TheoryData<string> Identifiers => new(
        "SSCRM-V1", "SSCRM-V3", "SSCRM-V4", "SPISCRM-V2", "BP21940", "", "SSCRM-V2", "SSCRM", "sscrm-v1", "SPISCRM-");

    [Theory]
    [MemberData(nameof(Identifiers))]
    public void Identifier_FindsThe1_4Model_AloneAndWithEverySub(string identifier)
    {
        // :1016-1045
        Same(ThermalrightPanelModelDatabase.GetModelByIdentifier(identifier), Catalog.ByIdentifier(identifier), identifier);
        for (var sub = 0; sub < 256; sub++)
        {
            Same(ThermalrightPanelModelDatabase.GetModelByIdentifierAndSub(identifier, (byte)sub), Catalog.ByIdentifierAndSub(identifier, (byte)sub), $"{identifier} SUB {sub}");
            Same(ThermalrightPanelModelDatabase.GetModelByIdentifier(identifier, (byte)sub), Catalog.ByIdentifier(identifier, (byte)sub), $"{identifier} SUB {sub}");
        }
    }

    [Fact]
    public void VidPid_FindsAModelOnlyWhereOneModelHasIt()
    {
        // :923-936
        var ids = ThermalrightPanelModelDatabase.SupportedDevices.Append((0x1234, 0x5678)).Append((0, 0));
        foreach (var (vid, pid) in ids)
            Same(ThermalrightPanelModelDatabase.GetModelByVidPid(vid, pid), Catalog.ByVidPid((ushort)vid, (ushort)pid), $"{vid:x4}:{pid:x4}");
    }
}
