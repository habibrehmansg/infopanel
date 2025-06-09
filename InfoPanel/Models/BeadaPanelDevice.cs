using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using System;

namespace InfoPanel.Models
{
    public partial class BeadaPanelDevice : ObservableObject
    {
        [ObservableProperty]
        private string _id = string.Empty;

        [ObservableProperty]
        private string _name = string.Empty;

        [ObservableProperty]
        private string _serialNumber = string.Empty;

        [ObservableProperty]
        private string _usbPath = string.Empty;

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.RotateNone;

        [ObservableProperty]
        private int _brightness = 100;

        public BeadaPanelDevice()
        {
            Id = Guid.NewGuid().ToString();
        }

        public BeadaPanelDevice(string serialNumber, string usbPath, string name = "")
        {
            Id = Guid.NewGuid().ToString();
            SerialNumber = serialNumber;
            UsbPath = usbPath;
            Name = string.IsNullOrEmpty(name) ? $"BeadaPanel {serialNumber}" : name;
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
}