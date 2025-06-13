using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace InfoPanel.Models
{
    public partial class Settings : ObservableObject
    {
        [ObservableProperty]
        private bool _autoStart = false;

        [ObservableProperty]
        private bool _startMinimized = false;

        [ObservableProperty]
        private bool _minimizeToTray = true;

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
        private bool _libreHardMonitorRing0 = true;

        private readonly ObservableCollection<BeadaPanelDevice> _beadaPanelDevices = [];

        public ObservableCollection<BeadaPanelDevice> BeadaPanelDevices
        {
            get { return _beadaPanelDevices; }
        }

        [ObservableProperty]
        private bool _beadaPanelMultiDeviceMode = false;

        private readonly ObservableCollection<TuringPanelDevice> _turingPanelDevices = [];

        public ObservableCollection<TuringPanelDevice> TuringPanelDevices
        {
            get { return _turingPanelDevices; }
        }

        [ObservableProperty]
        private bool _turingPanelMultiDeviceMode = false;

        [ObservableProperty]
        private bool _turingPanelA = false;

        [ObservableProperty]
        private string _turingPanelAPort = string.Empty;

        [ObservableProperty]
        private Guid _turingPanelAProfile = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _turingPanelARotation = 0;

        [ObservableProperty]
        private int _turingPanelABrightness = 100;

        [ObservableProperty]
        private bool _turingPanelC = false;

        [ObservableProperty]
        private string _turingPanelCPort = string.Empty;

        [ObservableProperty]
        private Guid _turingPanelCProfile = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _turingPanelCRotation = 0;

        [ObservableProperty]
        private int _turingPanelCBrightness = 100;

        [ObservableProperty]
        private bool _turingPanelE = false;

        [ObservableProperty]
        private string _turingPanelEPort = string.Empty;

        [ObservableProperty]
        private Guid _turingPanelEProfile = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _turingPanelERotation = LCD_ROTATION.Rotate90FlipNone;

        [ObservableProperty]
        private int _turingPanelEBrightness = 100;

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

        public Settings()
        {
            BeadaPanelDevices.CollectionChanged += BeadaPanelDevices_CollectionChanged;
            TuringPanelDevices.CollectionChanged += TuringPanelDevices_CollectionChanged;
        }

        private void BeadaPanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if(e.OldItems != null)
            {
                foreach(BeadaPanelDevice device in e.OldItems)
                {
                    device.PropertyChanged -= Device_PropertyChanged;
                }
            }

            if(e.NewItems != null)
            {
                foreach(BeadaPanelDevice device in e.NewItems)
                {
                    device.PropertyChanged += Device_PropertyChanged; ;
                }
            }

            OnPropertyChanged(nameof(BeadaPanelDevices));
        }

        private void TuringPanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if(e.OldItems != null)
            {
                foreach(TuringPanelDevice device in e.OldItems)
                {
                    device.PropertyChanged -= TuringDevice_PropertyChanged;
                }
            }

            if(e.NewItems != null)
            {
                foreach(TuringPanelDevice device in e.NewItems)
                {
                    device.PropertyChanged += TuringDevice_PropertyChanged;
                }
            }

            OnPropertyChanged(nameof(TuringPanelDevices));
        }

        private void Device_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BeadaPanelDevice.RuntimeProperties))
            {
                OnPropertyChanged(nameof(BeadaPanelDevices));
            }
        }

        private void TuringDevice_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TuringPanelDevice.RuntimeProperties))
            {
                OnPropertyChanged(nameof(TuringPanelDevices));
            }
        }
    }
    
}
