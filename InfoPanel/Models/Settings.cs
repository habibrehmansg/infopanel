using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;

namespace InfoPanel.Models
{
    public partial class Settings : ObservableObject
    {
        [ObservableProperty]
        private float _uiWidth = 1300;

        [ObservableProperty]
        private float _uiHeight = 900;

        [ObservableProperty]
        private float _uiScale = 1.0f;

        [ObservableProperty]
        private int _appTheme = 1; // 0=Light, 1=Dark

        [ObservableProperty]
        private bool _isPaneOpen = true;

        [ObservableProperty]
        private bool _autoStart = false;

        [ObservableProperty]
        private int _autoStartDelay = 5;

        [ObservableProperty]
        private bool _startMinimized = false;

        [ObservableProperty]
        private bool _minimizeToTray = true;

        [ObservableProperty]
        private bool _closeToMinimize = false;

        [ObservableProperty]
        private string _selectedItemColor = "#FF00FF00";

        [ObservableProperty]
        private bool _showGridLines = true;

        [ObservableProperty]
        private float _gridLinesSpacing = 20;

        [ObservableProperty]
        private string _gridLinesColor = "#1A808080";

        [ObservableProperty]
        private bool _libreHardwareMonitor = true;

        [ObservableProperty]
        private bool _libreHardwareMonitorStorage = true;

        [ObservableProperty]
        private int _libreHardwareMonitorStorageInterval = 30;

        private readonly ObservableCollection<BeadaPanelDevice> _beadaPanelDevices = [];

        public ObservableCollection<BeadaPanelDevice> BeadaPanelDevices => _beadaPanelDevices;

        [ObservableProperty]
        private bool _beadaPanelMultiDeviceMode = false;

        private readonly ObservableCollection<TuringPanelDevice> _turingPanelDevices = [];

        public ObservableCollection<TuringPanelDevice> TuringPanelDevices => _turingPanelDevices;

        [ObservableProperty]
        private bool _turingPanelMultiDeviceMode = false;

        private readonly ObservableCollection<ThermalrightPanelDevice> _thermalrightPanelDevices = [];

        public ObservableCollection<ThermalrightPanelDevice> ThermalrightPanelDevices
        {
            get { return _thermalrightPanelDevices; }
        }

        [ObservableProperty]
        private bool _thermalrightPanelMultiDeviceMode = false;

        private readonly ObservableCollection<ThermaltakePanelDevice> _thermaltakePanelDevices = [];

        public ObservableCollection<ThermaltakePanelDevice> ThermaltakePanelDevices
        {
            get { return _thermaltakePanelDevices; }
        }

        [ObservableProperty]
        private bool _thermaltakePanelMultiDeviceMode = false;

        private readonly ObservableCollection<HotkeyBinding> _hotkeyBindings = [];

        public ObservableCollection<HotkeyBinding> HotkeyBindings
        {
            get { return _hotkeyBindings; }
        }

        /// <summary>Optional global hotkeys for bundled InfoPanel.StopWatch. Key.None = disabled.</summary>
        [ObservableProperty]
        private ModifierKeys _stopwatchHotkeyStartModifiers = ModifierKeys.None;

        [ObservableProperty]
        private Key _stopwatchHotkeyStartKey = Key.None;

        [ObservableProperty]
        private ModifierKeys _stopwatchHotkeyStopModifiers = ModifierKeys.None;

        [ObservableProperty]
        private Key _stopwatchHotkeyStopKey = Key.None;

        [ObservableProperty]
        private ModifierKeys _stopwatchHotkeyResetModifiers = ModifierKeys.None;

        [ObservableProperty]
        private Key _stopwatchHotkeyResetKey = Key.None;

        [ObservableProperty]
        private bool _webServer = false;

        [ObservableProperty]
        private string _webServerListenIp = "127.0.0.1";

        [ObservableProperty]
        private int _webServerListenPort = 80;

        [ObservableProperty]
        private int _webServerRefreshRate = 66;

        [ObservableProperty]
        private int _targetFrameRate = 15;

        [ObservableProperty]
        private int _targetGraphUpdateRate = 1000;

        [ObservableProperty]
        private int _version = 114;

        [ObservableProperty]
        private bool _autosaveEnabled = false;

        /// <summary>Seconds of no changes before autosave runs. User still saves manually for the main save.</summary>
        [ObservableProperty]
        private int _autosaveIdleSeconds = 3;

        public Settings()
        {
            BeadaPanelDevices.CollectionChanged += BeadaPanelDevices_CollectionChanged;
            TuringPanelDevices.CollectionChanged += TuringPanelDevices_CollectionChanged;
            ThermalrightPanelDevices.CollectionChanged += ThermalrightPanelDevices_CollectionChanged;
            ThermaltakePanelDevices.CollectionChanged += ThermaltakePanelDevices_CollectionChanged;
        }

        private void BeadaPanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (BeadaPanelDevice device in e.OldItems)
                    device.PropertyChanged -= Device_PropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (BeadaPanelDevice device in e.NewItems)
                    device.PropertyChanged += Device_PropertyChanged;
            }
            OnPropertyChanged(nameof(BeadaPanelDevices));
        }

        private void TuringPanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (TuringPanelDevice device in e.OldItems)
                    device.PropertyChanged -= TuringDevice_PropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (TuringPanelDevice device in e.NewItems)
                    device.PropertyChanged += TuringDevice_PropertyChanged;
            }
            OnPropertyChanged(nameof(TuringPanelDevices));
        }

        private void Device_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BeadaPanelDevice.RuntimeProperties))
                OnPropertyChanged(nameof(BeadaPanelDevices));
        }

        private void TuringDevice_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TuringPanelDevice.RuntimeProperties))
                OnPropertyChanged(nameof(TuringPanelDevices));
        }

        private void ThermalrightPanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ThermalrightPanelDevice device in e.OldItems)
                {
                    device.PropertyChanged -= ThermalrightDevice_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (ThermalrightPanelDevice device in e.NewItems)
                {
                    device.PropertyChanged += ThermalrightDevice_PropertyChanged;
                }
            }

            OnPropertyChanged(nameof(ThermalrightPanelDevices));
        }

        private void ThermalrightDevice_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ThermalrightPanelDevice.RuntimeProperties))
            {
                OnPropertyChanged(nameof(ThermalrightPanelDevices));
            }
        }

        private void ThermaltakePanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ThermaltakePanelDevice device in e.OldItems)
                    device.PropertyChanged -= ThermaltakeDevice_PropertyChanged;
            }
            if (e.NewItems != null)
            {
                foreach (ThermaltakePanelDevice device in e.NewItems)
                    device.PropertyChanged += ThermaltakeDevice_PropertyChanged;
            }
            OnPropertyChanged(nameof(ThermaltakePanelDevices));
        }

        private void ThermaltakeDevice_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ThermaltakePanelDevice.RuntimeProperties))
                OnPropertyChanged(nameof(ThermaltakePanelDevices));
        }

    }
}
