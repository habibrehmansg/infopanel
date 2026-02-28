using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using InfoPanel.ThermalrightPanel;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Wpf.Ui.Controls;

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

    public UsbPanelsViewModel()
    {
        RotationValues = new ObservableCollection<LCD_ROTATION>(Enum.GetValues(typeof(LCD_ROTATION)).Cast<LCD_ROTATION>());
        DisplayMaskValues = new ObservableCollection<ThermalrightDisplayMask>(Enum.GetValues(typeof(ThermalrightDisplayMask)).Cast<ThermalrightDisplayMask>());
    }

    public ObservableCollection<BeadaPanelDevice> RuntimeBeadaPanelDevices
    {
        get { return ConfigModel.Instance.Settings.BeadaPanelDevices; }
    }

    public ObservableCollection<TuringPanelDevice> RuntimeTuringPanelDevices
    {
        get { return ConfigModel.Instance.Settings.TuringPanelDevices; }
    }

    public void OnNavigatedFrom()
    {
    }

    public void OnNavigatedTo()
    {
    }
}