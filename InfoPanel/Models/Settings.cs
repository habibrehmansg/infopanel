using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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


        private ObservableCollection<BeadaPanelDeviceConfig> _beadaPanelDevices = [];
        public ObservableCollection<BeadaPanelDeviceConfig> BeadaPanelDevices
        {
            get { return _beadaPanelDevices; }
            set 
            { 
                if (_beadaPanelDevices != null)
                {
                    _beadaPanelDevices.CollectionChanged -= BeadaPanelDevices_CollectionChanged;
                    foreach (var device in _beadaPanelDevices)
                    {
                        device.PropertyChanged -= BeadaPanelDeviceConfig_PropertyChanged;
                    }
                }
                
                SetProperty(ref _beadaPanelDevices, value);
                
                if (_beadaPanelDevices != null)
                {
                    _beadaPanelDevices.CollectionChanged += BeadaPanelDevices_CollectionChanged;
                    foreach (var device in _beadaPanelDevices)
                    {
                        device.PropertyChanged += BeadaPanelDeviceConfig_PropertyChanged;
                    }
                }
            }
        }

        [ObservableProperty]
        private string _selectedBeadaPanelDeviceId = string.Empty;

        [ObservableProperty]
        private bool _beadaPanelMultiDeviceMode = false;

        [ObservableProperty]
        private bool _turingPanel = false;

        [ObservableProperty]
        private Guid _turingPanelProfile = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _turingPanelRotation = 0;

        [ObservableProperty]
        private int _turingPanelBrightness = 100;

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
            // Subscribe to initial collection changes
            _beadaPanelDevices.CollectionChanged += BeadaPanelDevices_CollectionChanged;
            
            // Subscribe to initial device property changes
            foreach (var device in _beadaPanelDevices)
            {
                device.PropertyChanged += BeadaPanelDeviceConfig_PropertyChanged;
            }
        }

        // Called after XML deserialization to re-establish event subscriptions
        public void InitializeAfterDeserialization()
        {
            if (_beadaPanelDevices != null)
            {
                // Ensure we don't double-subscribe
                _beadaPanelDevices.CollectionChanged -= BeadaPanelDevices_CollectionChanged;
                _beadaPanelDevices.CollectionChanged += BeadaPanelDevices_CollectionChanged;
                
                // Subscribe to each device's property changes
                foreach (var device in _beadaPanelDevices)
                {
                    device.PropertyChanged -= BeadaPanelDeviceConfig_PropertyChanged;
                    device.PropertyChanged += BeadaPanelDeviceConfig_PropertyChanged;
                }
            }
        }

        private void BeadaPanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (BeadaPanelDeviceConfig device in e.NewItems)
                {
                    device.PropertyChanged += BeadaPanelDeviceConfig_PropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (BeadaPanelDeviceConfig device in e.OldItems)
                {
                    device.PropertyChanged -= BeadaPanelDeviceConfig_PropertyChanged;
                }
            }

            // Trigger save when devices are added/removed
            OnPropertyChanged(nameof(BeadaPanelDevices));
        }

        private void BeadaPanelDeviceConfig_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Trigger save when any device configuration property changes
            OnPropertyChanged(nameof(BeadaPanelDevices));
        }
    }
}
