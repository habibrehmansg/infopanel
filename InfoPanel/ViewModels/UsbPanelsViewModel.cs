using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.LianLiPanel;
using InfoPanel.Models;
using InfoPanel.ThermalrightPanel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using Wpf.Ui.Abstractions.Controls;

namespace InfoPanel.ViewModels;

public enum LCD_ROTATION
{
    [Description("No rotation")]
    RotateNone = 0,
    [Description("Rotate 90°")]
    Rotate90FlipNone = 1,
    [Description("Rotate 180°")]
    Rotate180FlipNone = 2,
    [Description("Rotate 270°")]
    Rotate270FlipNone = 3,
}

public partial class UsbPanelsViewModel : ObservableObject, INavigationAware
{
    public ObservableCollection<LCD_ROTATION> RotationValues { get; set; }
    public ObservableCollection<ThermalrightDisplayMask> DisplayMaskValues { get; set; }
    private readonly CollectionViewSource _turingCvs = new();

    public UsbPanelsViewModel()
    {
        RotationValues = new ObservableCollection<LCD_ROTATION>(Enum.GetValues(typeof(LCD_ROTATION)).Cast<LCD_ROTATION>());
        DisplayMaskValues = new ObservableCollection<ThermalrightDisplayMask>(Enum.GetValues(typeof(ThermalrightDisplayMask)).Cast<ThermalrightDisplayMask>());

        _turingCvs.Source = ConfigModel.Instance.Settings.TuringPanelDevices;
        _turingCvs.Filter += (_, e) =>
        {
            e.Accepted = e.Item is TuringPanelDevice device &&
                !LianLiPanelModelDatabase.IsLianLiDeviceId(device.DeviceId);
        };

        ConfigModel.Instance.Settings.TuringPanelDevices.CollectionChanged += (_, _) => _turingCvs.View?.Refresh();
    }

    public ObservableCollection<BeadaPanelDevice> RuntimeBeadaPanelDevices
    {
        get { return ConfigModel.Instance.Settings.BeadaPanelDevices; }
    }

    public ICollectionView RuntimeTuringPanelDevices => _turingCvs.View;

    public ObservableCollection<LianLiPanelDevice> RuntimeLianLiPanelDevices
    {
        get { return ConfigModel.Instance.Settings.LianLiPanelDevices; }
    }

    public Task OnNavigatedFromAsync()
    {
        return Task.CompletedTask;
    }

    public Task OnNavigatedToAsync()
    {
        return Task.CompletedTask;
    }
}
