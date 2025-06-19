using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Wpf.Ui.Controls;

namespace InfoPanel.ViewModels
{
    public class UiScaleOption
    {
        public string Display { get; set; } = string.Empty;
        public float Value { get; set; }
    }

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

    public partial class SettingsViewModel : ObservableObject, INavigationAware
    {
        private ObservableCollection<string> _comPorts = [];

        [ObservableProperty]
        private ObservableCollection<UiScaleOption> _scaleOptions = [
                new UiScaleOption { Display = "80%", Value = 0.8f },
                new UiScaleOption { Display = "90%", Value = 0.9f },
                new UiScaleOption { Display = "100%", Value = 1.0f },
                new UiScaleOption { Display = "110%", Value = 1.1f },
                new UiScaleOption { Display = "120%", Value = 1.2f }
            ];

        public ObservableCollection<LCD_ROTATION> RotationValues { get; set; }

        public SettingsViewModel()
        {
            RotationValues = new ObservableCollection<LCD_ROTATION>(Enum.GetValues(typeof(LCD_ROTATION)).Cast<LCD_ROTATION>());
        }

        public ObservableCollection<string> ComPorts
        {
            get { return _comPorts; }
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
}
