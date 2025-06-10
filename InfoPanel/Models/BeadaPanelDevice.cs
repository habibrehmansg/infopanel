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

        public string Name => ModelName;


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

        public int WidthMm => Config.ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(Config.ModelType.Value)
            ? BeadaPanelModelDatabase.Models[Config.ModelType.Value].WidthMM
            : 0;

        public int HeightMm => Config.ModelType.HasValue && BeadaPanelModelDatabase.Models.ContainsKey(Config.ModelType.Value)
            ? BeadaPanelModelDatabase.Models[Config.ModelType.Value].HeightMM
            : 0;


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

        public BeadaPanelDevice(BeadaPanelDeviceConfig config)
        {
            Config = config;
            Id = Guid.NewGuid().ToString();
        }

        public string GetUniqueIdentifier()
        {
            return Config.IdentificationMethod switch
            {
                DeviceIdentificationMethod.HardwareSerial => Config.SerialNumber,
                DeviceIdentificationMethod.ModelFingerprint => $"{Config.ModelType}_{FirmwareVersion}_{NativeResolutionX}x{NativeResolutionY}",
                DeviceIdentificationMethod.UsbPath => Config.UsbPath,
                _ => Id
            };
        }

        public string GetIdentificationStatusText()
        {
            return Config.IdentificationMethod switch
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