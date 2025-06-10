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

        [ObservableProperty]
        private BeadaPanelDeviceConfig _config = new BeadaPanelDeviceConfig();

        // Configuration property passthroughs
        public string UsbPath
        {
            get => Config.UsbPath;
            set => Config.UsbPath = SanitizeString(value);
        }

        public bool Enabled
        {
            get => Config.Enabled;
            set => Config.Enabled = value;
        }

        public Guid ProfileGuid
        {
            get => Config.ProfileGuid;
            set => Config.ProfileGuid = value;
        }

        public LCD_ROTATION Rotation
        {
            get => Config.Rotation;
            set => Config.Rotation = value;
        }

        public int Brightness
        {
            get => Config.Brightness;
            set => Config.Brightness = value;
        }

        public string HardwareSerialNumber
        {
            get => Config.HardwareSerialNumber;
            set => Config.HardwareSerialNumber = SanitizeString(value);
        }

        public BeadaPanelModel? ModelType
        {
            get => Config.ModelType;
            set => Config.ModelType = value;
        }

        public DeviceIdentificationMethod IdentificationMethod
        {
            get => Config.IdentificationMethod;
            set => Config.IdentificationMethod = value;
        }

        // Runtime properties
        public string ModelName => ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(ModelType.Value)
            ? BeadaPanelModelDatabase.Models[ModelType.Value].Name
            : string.Empty;

        [ObservableProperty]
        private ushort _firmwareVersion = 0;

        [ObservableProperty]
        private byte _platform = 0;

        public int NativeResolutionX => ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(ModelType.Value)
            ? BeadaPanelModelDatabase.Models[ModelType.Value].Width
            : 0;

        public int NativeResolutionY => ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(ModelType.Value)
            ? BeadaPanelModelDatabase.Models[ModelType.Value].Height
            : 0;

        public int WidthMm => ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(ModelType.Value)
            ? BeadaPanelModelDatabase.Models[ModelType.Value].WidthMM
            : 0;

        public int HeightMm => ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(ModelType.Value)
            ? BeadaPanelModelDatabase.Models[ModelType.Value].HeightMM
            : 0;

        [ObservableProperty]
        private byte _maxBrightness = 255;

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
            Config.UsbPath = usbPath;
            Name = string.IsNullOrEmpty(name) ? $"BeadaPanel {serialNumber}" : name;
            Config.IdentificationMethod = DeviceIdentificationMethod.UsbPath;
        }

        public BeadaPanelDevice(BeadaPanelDeviceConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Id = Guid.NewGuid().ToString();
        }

        public bool UpdateFromStatusLink(BeadaPanelInfo panelInfo)
        {
            // Reject unknown models
            if (!BeadaPanelModelDatabase.Models.ContainsKey(panelInfo.Model))
                return false;

            HardwareSerialNumber = panelInfo.SerialNumber;
            ModelType = panelInfo.Model;
            FirmwareVersion = panelInfo.FirmwareVersion;
            Platform = panelInfo.Platform;
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
            
            return true;
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
        /// Gets the configuration object for this device
        /// </summary>
        public BeadaPanelDeviceConfig GetConfig()
        {
            return Config;
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