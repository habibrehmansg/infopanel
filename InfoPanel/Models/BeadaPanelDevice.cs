using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using InfoPanel.BeadaPanel;
using System;
using System.Linq;

namespace InfoPanel.Models
{
    public partial class BeadaPanelDevice : ObservableObject
    {
        [ObservableProperty]
        private string _id = string.Empty;

        public string Name => $"BeadaPanel {ModelName}";


        [ObservableProperty]
        private BeadaPanelDeviceConfig _config = new();

        // Runtime properties
        public string ModelName => Config.ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(Config.ModelType.Value)
            ? BeadaPanelModelDatabase.Models[Config.ModelType.Value].Name
            : "Unknown Model";

        [ObservableProperty]
        private ushort _firmwareVersion = 0;

        [ObservableProperty]
        private byte _platform = 0;

        public int NativeResolutionX => Config.ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(Config.ModelType.Value)
            ? BeadaPanelModelDatabase.Models[Config.ModelType.Value].Width
            : 0;

        public int NativeResolutionY => Config.ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(Config.ModelType.Value)
            ? BeadaPanelModelDatabase.Models[Config.ModelType.Value].Height
            : 0;

        [ObservableProperty]
        private bool _statusLinkAvailable = false;

        public BeadaPanelDevice()
        {
        }

        public BeadaPanelDevice(BeadaPanelDeviceConfig config)
        {
            Config = config;
        }


        public BeadaPanelDeviceStatus? DeviceStatus
        {
            get
            {
                return SharedModel.Instance.GetBeadaPanelDeviceStatus(Id);
            }
        }
        
        // Method to notify when device status changes  
        public void NotifyDeviceStatusChanged()
        {
            OnPropertyChanged(nameof(DeviceStatus));
        }


        public override string ToString()
        {
            return Name;
        }
    }

    public partial class BeadaPanelDeviceStatus : ObservableObject
    {
        [ObservableProperty]
        private string _deviceId = string.Empty;

        [ObservableProperty]
        private bool _isRunning = false;

        [ObservableProperty]
        private bool _isConnected = false;

        [ObservableProperty]
        private int _frameRate = 0;

        [ObservableProperty]
        private long _frameTime = 0;

        [ObservableProperty]
        private string _errorMessage = string.Empty;

        [ObservableProperty]
        private DateTime _lastUpdate = DateTime.Now;

        public BeadaPanelDeviceStatus(string deviceId)
        {
            DeviceId = deviceId;
        }
    }

    public enum DeviceIdentificationMethod
    {
        UsbPath,
        HardwareSerial,
        ModelFingerprint
    }
}