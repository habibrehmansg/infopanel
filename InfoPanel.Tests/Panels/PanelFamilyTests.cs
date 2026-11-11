using InfoPanel.Import;
using InfoPanel.Panels;
using InfoPanel.Panels.BeadaPanel;
using InfoPanel.Persistence;
using Xunit;

namespace InfoPanel.Tests.Panels;

/// <summary>
/// The built-in family as <c>deviceList.families</c> gives it: each BeadaPanel model with its name in InfoPanel 1.x
/// (<c>v1Model</c>, 1.4's model enum's name, which 1.4 stored with each panel), so a client maps an imported 1.x panel
/// to its model by it.
/// </summary>
public sealed class PanelFamilyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("panel-families-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void A_BeadaPanel_imported_from_1x_maps_to_its_built_in_model_through_v1Model()
    {
        // A 1.x folder as 1.4 leaves it: a BeadaPanel saved with its model as 1.4 names it (panelInfo.Model.ToString()).
        var v1 = Directory.CreateDirectory(Path.Combine(_folder, "v1")).FullName;
        File.WriteAllText(Path.Combine(v1, V1Folder.SettingsFile), """
            <?xml version="1.0" encoding="utf-8"?>
            <Settings xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Version>131</Version>
              <BeadaPanelDevices>
                <BeadaPanelDevice>
                  <DeviceId>USB\VID_4E58&amp;PID_1001\934072005C00481401044F2420751FD6</DeviceId>
                  <DeviceLocation>Port_#0003.Hub_#0001</DeviceLocation>
                  <Model>Model6C</Model>
                  <Enabled>true</Enabled>
                </BeadaPanelDevice>
              </BeadaPanelDevices>
            </Settings>
            """);

        var state = new V1Folder(v1).GetState(new DataPaths(Path.Combine(_folder, "data")));

        var device = Assert.Single(state.Settings!.Devices, d => d.Family == "beada");
        var model = Assert.Single(PanelService.BuiltInFamily.Models, m => m.V1Model == device.Fields["Model"]);
        Assert.Equal(("6C", 1280, 480), (model.Id, model.Width, model.Height));
        // Every built-in model has its 1.x name, each its own.
        Assert.Equal(BeadaPanelModel.All.Count, PanelService.BuiltInFamily.Models.Select(m => m.V1Model).Distinct().Count());
        Assert.DoesNotContain(null, PanelService.BuiltInFamily.Models.Select(m => m.V1Model));
    }
}
