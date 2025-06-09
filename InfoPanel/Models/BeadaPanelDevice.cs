using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.ViewModels;
using InfoPanel.BeadaPanel;
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

        [ObservableProperty]
        private string _hardwareSerialNumber = string.Empty;

        [ObservableProperty]
        private BeadaPanelModel? _modelType = null;

        [ObservableProperty]
        private string _modelName = string.Empty;

        [ObservableProperty]
        private ushort _firmwareVersion = 0;

        [ObservableProperty]
        private byte _platform = 0;

        [ObservableProperty]
        private int _nativeResolutionX = 0;

        [ObservableProperty]
        private int _nativeResolutionY = 0;

        [ObservableProperty]
        private int _widthMm = 0;

        [ObservableProperty]
        private int _heightMm = 0;

        [ObservableProperty]
        private byte _maxBrightness = 255;

        [ObservableProperty]
        private DeviceIdentificationMethod _identificationMethod = DeviceIdentificationMethod.UsbPath;

        [ObservableProperty]
        private bool _statusLinkAvailable = false;

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
            IdentificationMethod = DeviceIdentificationMethod.UsbPath;
        }

        public void UpdateFromStatusLink(BeadaPanelInfo panelInfo)
        {
            HardwareSerialNumber = panelInfo.SerialNumber;
            ModelType = panelInfo.ModelId;
            ModelName = panelInfo.ModelInfo?.Name ?? "Unknown Model";
            FirmwareVersion = panelInfo.FirmwareVersion;
            Platform = panelInfo.Platform;
            NativeResolutionX = panelInfo.ModelInfo?.Width ?? panelInfo.ResolutionX;
            NativeResolutionY = panelInfo.ModelInfo?.Height ?? panelInfo.ResolutionY;
            WidthMm = panelInfo.ModelInfo?.WidthMM ?? 0;
            HeightMm = panelInfo.ModelInfo?.HeightMM ?? 0;
            MaxBrightness = panelInfo.MaxBrightness;
            StatusLinkAvailable = true;

            if (!string.IsNullOrEmpty(HardwareSerialNumber))
            {
                IdentificationMethod = DeviceIdentificationMethod.HardwareSerial;
                if (string.IsNullOrEmpty(Name) || Name.StartsWith("BeadaPanel Device"))
                {
                    Name = $"{ModelName} ({HardwareSerialNumber})";
                }
            }
            else if (ModelType.HasValue)
            {
                IdentificationMethod = DeviceIdentificationMethod.ModelFingerprint;
                if (string.IsNullOrEmpty(Name) || Name.StartsWith("BeadaPanel Device"))
                {
                    Name = $"{ModelName} #{Id[..8]}";
                }
            }
        }

        public string GetUniqueIdentifier()
        {
            return IdentificationMethod switch
            {
                DeviceIdentificationMethod.HardwareSerial => HardwareSerialNumber,
                DeviceIdentificationMethod.ModelFingerprint => $"{ModelType}_{FirmwareVersion}_{NativeResolutionX}x{NativeResolutionY}",
                DeviceIdentificationMethod.UsbPath => UsbPath,
                _ => Id
            };
        }

        public string GetIdentificationStatusText()
        {
            return IdentificationMethod switch
            {
                DeviceIdentificationMethod.HardwareSerial => "Identified by hardware serial",
                DeviceIdentificationMethod.ModelFingerprint => "Identified by device fingerprint",
                DeviceIdentificationMethod.UsbPath => "Identified by USB path (unstable)",
                _ => "Unknown identification method"
            };
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