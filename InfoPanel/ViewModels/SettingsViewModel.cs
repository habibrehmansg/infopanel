using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using InfoPanel.Utils;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.Appearance;

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

        [ObservableProperty]
        private ApplicationTheme _currentApplicationTheme = ApplicationTheme.Unknown;

        public SettingsViewModel()
        {
            _currentApplicationTheme = ApplicationThemeManager.GetAppTheme();
        }

        partial void OnCurrentApplicationThemeChanged(ApplicationTheme oldValue, ApplicationTheme newValue)
        {
            ApplicationThemeManager.Apply(newValue);
            ConfigModel.Instance.Settings.AppTheme = newValue switch
            {
                ApplicationTheme.Dark => 1,
                ApplicationTheme.HighContrast => 2,
                _ => 0
            };
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

        public Task OnNavigatedFromAsync()
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            return Task.CompletedTask;
        }

        public Task OnNavigatedToAsync()
        {
            CurrentApplicationTheme = ApplicationThemeManager.GetAppTheme();
            ApplicationThemeManager.Changed += OnThemeChanged;
            return Task.CompletedTask;
        }

        private void OnThemeChanged(ApplicationTheme currentApplicationTheme, System.Windows.Media.Color systemAccent)
        {
            CurrentApplicationTheme = currentApplicationTheme;
        }
    }
}
