using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Wpf.Ui.Common.Interfaces;

namespace InfoPanel.ViewModels
{
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

    public class SettingsViewModel : ObservableObject, INavigationAware
    {
        private ObservableCollection<string> _comPorts = new();

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

        public void OnNavigatedFrom()
        {
        }

        public void OnNavigatedTo()
        {
        }
    }
}
