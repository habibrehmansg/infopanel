using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using InfoPanel.Utils;
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

        [ObservableProperty]
        private string _pawnIOStatus = "Click to check";

        public SettingsViewModel()
        {
        }

        /// <summary>
        /// Refreshes the PawniO installation status.
        /// </summary>
        public void RefreshPawnIOStatus()
        {
            PawnIoHelper.RefreshStatus();
            PawnIOStatus = PawnIoHelper.StatusMessage;
        }

        public ObservableCollection<string> ComPorts
        {
            get { return _comPorts; }
        }

        public void OnNavigatedFrom()
        {
        }

        public void OnNavigatedTo()
        {
        }
    }
}
