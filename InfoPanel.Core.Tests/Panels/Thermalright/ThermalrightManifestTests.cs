using System.Text.Json;
using InfoPanel.Core.Tests.Golden;
using InfoPanel.Core.Tests.Panels.Thermalright.V1Oracle;
using InfoPanel.Panels.Sdk;
using InfoPanel.Panels.Thermalright;
using Xunit;

namespace InfoPanel.Core.Tests.Panels.Thermalright;

/// <summary>
/// The package's <c>panel.json</c> against InfoPanel 1.4.x's model table (the V1 oracle: 1.4.x c71f0f00,
/// <c>ThermalrightPanel/ThermalrightPanelModelDatabase.cs:88-921</c>, <c>ThermalrightPanelModel.cs:3-125</c>): every model,
/// field for field and in 1.4.x's order (its lookups take the first match), and nothing else.
/// </summary>
public sealed class ThermalrightManifestTests
{
    private static string PackageDirectory => Path.GetFullPath(Path.Combine(TestPaths.ProjectDirectory, "..", "InfoPanel.Panels.Thermalright"));

    private static ThermalrightCatalog Catalog => ThermalrightCatalog.Default;

    [Fact]
    public void PanelJson_IsAPanelPackageManifestOfOneFamily_WithinTheReadersBounds()
    {
        var bytes = File.ReadAllBytes(Path.Combine(PackageDirectory, "panel.json"));
        Assert.True(bytes.Length <= 64 * 1024, $"panel.json is {bytes.Length} bytes; the engine reads at most 64 KiB");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        Assert.Equal("infopanel.panel-package/1", json.RootElement.GetProperty("format").GetString());
        Assert.Equal("1.0", json.RootElement.GetProperty("sdk").GetString());
        var family = Assert.Single(json.RootElement.GetProperty("families").EnumerateArray());
        Assert.Equal("thermalright", family.GetProperty("id").GetString());
        Assert.Equal("Thermalright", family.GetProperty("name").GetString());

        var models = family.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(models.Count, models.Select(m => m.GetProperty("id").GetString()).Distinct().Count());
        foreach (var model in models)
        {
            Assert.Matches("^[a-z0-9]{1,64}$", model.GetProperty("id").GetString());
            Assert.InRange(model.GetProperty("name").GetString()!.Length, 1, 64);
            Assert.InRange(model.GetProperty("width").GetInt32(), 1, 16384);
            Assert.InRange(model.GetProperty("height").GetInt32(), 1, 16384);
        }

        // The catalog the code uses is this file, embedded.
        Assert.Equal(bytes, ReadEmbedded());
        Assert.Equal(models.Count, ThermalrightCatalog.Parse(bytes).Models.Count);
    }

    [Fact]
    public void Matches_AreTheUsbAndHidIds_1_4_DroveOverBulkAndHid()
    {
        // ThermalrightPanelModelDatabase.cs:62-74 lists ten ids. 7a takes the five 1.4.x drove over USB bulk and HID; the
        // SCSI ids (87cd:70db, 0402:3922) are 7b's; 0418:5303 and 0418:5304 match no model there (see the report).
        Assert.Equal(
            [
                new PanelMatch(0x87AD, 0x70DB, 0, PanelTransport.Usb),
                new PanelMatch(0x0416, 0x5302, null, PanelTransport.Hid),
                new PanelMatch(0x0416, 0x5408, 0, PanelTransport.Usb),
                new PanelMatch(0x0416, 0x5409, 0, PanelTransport.Usb),
                new PanelMatch(0x0416, 0x5406, 0, PanelTransport.Usb),
            ],
            Catalog.Matches);
        foreach (var (vid, pid) in ThermalrightPanelModelDatabase.SupportedDevices)
        {
            var listed = Catalog.Matches.Any(m => m.VendorId == vid && m.ProductId == pid);
            var scsiOnly = ThermalrightPanelModelDatabase.Models.Values.Where(m => m.VendorId == vid && m.ProductId == pid)
                .All(m => m.TransportType == ThermalrightTransportType.Scsi);
            var noModel = !ThermalrightPanelModelDatabase.Models.Values.Any(m => m.VendorId == vid && m.ProductId == pid);
            Assert.True(listed != (scsiOnly || noModel), $"{vid:x4}:{pid:x4}");
        }
    }

    [Fact]
    public void V1Models_AreThe1_4EnumNames_ButUnknown()
    {
        var names = Catalog.Models.SelectMany(m => m.V1Aliases.Prepend(m.V1Model)).Order(StringComparer.Ordinal).ToList();
        var enumNames = Enum.GetNames<ThermalrightPanelModel>().Where(n => n != "Unknown").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(enumNames, names);
        Assert.Equal(Catalog.Models.Count, ThermalrightPanelModelDatabase.Models.Count);
        foreach (var name in enumNames)
            Assert.Equal(Enum.Parse<ThermalrightPanelModel>(name), Enum.Parse<ThermalrightPanelModel>(Catalog.ByV1Model(name)!.V1Model));
        Assert.Null(Catalog.ByV1Model("Unknown"));
    }

    [Fact]
    public void Models_Are1_4sTable_FieldForField_InItsOrder()
    {
        var oracle = ThermalrightPanelModelDatabase.Models.Values.ToList();
        Assert.Equal(oracle.Count, Catalog.Models.Count);
        for (var i = 0; i < oracle.Count; i++)
        {
            var v1 = oracle[i];
            var model = Catalog.Models[i];
            Assert.Equal(v1.Model, Enum.Parse<ThermalrightPanelModel>(model.V1Model));
            Assert.Equal(model.V1Model.ToLowerInvariant(), model.Id);
            Assert.Same(model, Catalog.ById(model.Id));
            Assert.Equal(v1.Name, model.Name);
            Assert.Equal((v1.RenderWidth, v1.RenderHeight), (model.Width, model.Height));
            Assert.Equal((v1.Width, v1.Height), (model.NativeWidth, model.NativeHeight));
            Assert.Equal(v1.VendorId == 0 ? null : (ushort?)v1.VendorId, model.VendorId);
            Assert.Equal(v1.ProductId == 0 ? null : (ushort?)v1.ProductId, model.ProductId);
            Assert.Equal(v1.TransportType switch
            {
                ThermalrightTransportType.WinUsb => ThermalrightTransport.Usb,
                ThermalrightTransportType.Hid => ThermalrightTransport.Hid,
                _ => ThermalrightTransport.Scsi,
            }, model.Transport);
            Assert.Equal(v1.TransportType == ThermalrightTransportType.Scsi ? ThermalrightProtocol.None : v1.ProtocolType switch
            {
                ThermalrightProtocolType.ChiZhu => ThermalrightProtocol.ChiZhu,
                ThermalrightProtocolType.Trofeo => ThermalrightProtocol.Trofeo,
                ThermalrightProtocolType.TrofeoBulk => ThermalrightProtocol.TrofeoBulk,
                ThermalrightProtocolType.TrofeoBulkLY1 => ThermalrightProtocol.TrofeoBulkLy1,
                _ => ThermalrightProtocol.Ali,
            }, model.Protocol);
            Assert.Equal(v1.PixelFormat switch
            {
                ThermalrightPixelFormat.Jpeg => ThermalrightPixels.Jpeg,
                ThermalrightPixelFormat.Rgb565 => ThermalrightPixels.Rgb565Le,
                _ => ThermalrightPixels.Rgb565Be,
            }, model.Pixels);
            Assert.Equal(v1.DeviceIdentifier, model.Identifier);
            Assert.Equal(v1.PmByte, model.Pm);
            Assert.Equal(v1.SubByte, model.Sub);
        }
    }

    [Fact]
    public void FamilySettings_AreOfferedByTheModels1_4OfferedThemFor()
    {
        // ThermalrightPanelDevice.cs:85-93 (HasDisplayMask) and :114-115 (HasFlickerFix); the Levita Vision's mask is
        // turned 180° more (ThermalrightPanelDeviceTask.cs:173); the crop is 462 rows (:1117).
        Assert.Equal(["WonderVision360", "WonderVision360V2", "RainbowVision360", "LevitaVision360"],
            Catalog.Models.Where(m => m.DisplayMask).Select(m => m.V1Model));
        Assert.Equal(["LevitaVision360"], Catalog.Models.Where(m => m.MaskTurn != 0).Select(m => m.V1Model));
        Assert.Equal(180, Catalog.ByV1Model("LevitaVision360")!.MaskTurn);
        Assert.Equal(["TrofeoVision916", "TrofeoVision916V2"], Catalog.Models.Where(m => m.FlickerFixRows != null).Select(m => m.V1Model));
        Assert.All(Catalog.Models.Where(m => m.FlickerFixRows != null), m => Assert.Equal(462, m.FlickerFixRows));
    }

    [Fact]
    public void ThePackagesCode_HoldsNoSecondTableOfIdsOrModels()
    {
        var sources = Directory.GetFiles(PackageDirectory, "*.cs", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(sources);
        var forbidden = Catalog.Models.Select(m => m.V1Model).Where(n => n.Length > 4)
            .Concat(["70db", "70DB", "5302", "5406", "5408", "5409", "87ad", "87AD", "0416", "SSCRM", "BP21940"]).ToList();
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var word in forbidden)
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(source)} names {word}: panel.json is the only table");
        }
    }

    private static byte[] ReadEmbedded()
    {
        using var stream = typeof(ThermalrightCatalog).Assembly.GetManifestResourceStream("InfoPanel.Panels.Thermalright.panel.json")!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
