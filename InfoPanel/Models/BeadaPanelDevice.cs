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

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, SanitizeString(value));
        }

        private string _serialNumber = string.Empty;
        public string SerialNumber
        {
            get => _serialNumber;
            set => SetProperty(ref _serialNumber, SanitizeString(value));
        }

        private string _usbPath = string.Empty;
        public string UsbPath
        {
            get => _usbPath;
            set => SetProperty(ref _usbPath, SanitizeString(value));
        }

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.RotateNone;

        [ObservableProperty]
        private int _brightness = 100;

        private string _hardwareSerialNumber = string.Empty;
        public string HardwareSerialNumber
        {
            get => _hardwareSerialNumber;
            set => SetProperty(ref _hardwareSerialNumber, SanitizeString(value));
        }

        [ObservableProperty]
        private BeadaPanelModel? _modelType = null;

        private string _modelName = string.Empty;
        public string ModelName
        {
            get => _modelName;
            set => SetProperty(ref _modelName, SanitizeString(value));
        }

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

        public static string SanitizeString(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;
            
            // Remove null characters and other invalid XML characters
            // Also remove any non-printable characters
            var sanitized = new System.Text.StringBuilder();
            foreach (char c in input)
            {
                if (c != '\0' && !char.IsControl(c))
                {
                    sanitized.Append(c);
                }
            }
            
            return sanitized.ToString().Trim();
        }

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
            ModelType = panelInfo.Model;
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

        public BeadaPanelDeviceStatus? DeviceStatus
        {
            get
            {
                return SharedModel.Instance.BeadaPanelDeviceStatuses.FirstOrDefault(s => s?.DeviceId == Id);
            }
        }
        
        // Method to notify when device status changes  
        public void NotifyDeviceStatusChanged()
        {
            OnPropertyChanged(nameof(DeviceStatus));
        }

        /// <summary>
        /// Updates this device's configuration properties from a config object
        /// </summary>
        public void UpdateFromConfig(BeadaPanelDeviceConfig config)
        {
            UsbPath = config.UsbPath;
            ModelName = config.ModelName;
            HardwareSerialNumber = config.HardwareSerialNumber;
            NativeResolutionX = config.NativeResolutionX;
            NativeResolutionY = config.NativeResolutionY;
            IdentificationMethod = config.IdentificationMethod;
            Enabled = config.Enabled;
            ProfileGuid = config.ProfileGuid;
            Rotation = config.Rotation;
            Brightness = config.Brightness;
            ModelType = config.ModelType;
            MaxBrightness = config.MaxBrightness;
        }

        /// <summary>
        /// Creates a configuration object from this device's persistent properties
        /// </summary>
        public BeadaPanelDeviceConfig ToConfig()
        {
            return BeadaPanelDeviceConfig.FromDevice(this);
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

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, BeadaPanelDevice.SanitizeString(value));
        }

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