using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
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

    public UsbPanelsViewModel()
    {
        RotationValues = new ObservableCollection<LCD_ROTATION>(Enum.GetValues(typeof(LCD_ROTATION)).Cast<LCD_ROTATION>());
    }

    public ObservableCollection<BeadaPanelDevice> RuntimeBeadaPanelDevices
    {
        get { return ConfigModel.Instance.Settings.BeadaPanelDevices; }
    }

    public ObservableCollection<TuringPanelDevice> RuntimeTuringPanelDevices
    {
        get { return ConfigModel.Instance.Settings.TuringPanelDevices; }
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