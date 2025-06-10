using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.BeadaPanel;
using InfoPanel.ViewModels;
using System;

namespace InfoPanel.Models
{
    /// <summary>
    /// Configuration-only model for BeadaPanelDevice containing only persistent properties.
    /// This class is used for XML serialization to prevent runtime status updates from triggering saves.
    /// </summary>
    public partial class BeadaPanelDeviceConfig : ObservableObject
    {
        private string _usbPath = string.Empty;
        public string UsbPath
        {
            get => _usbPath;
            set => SetProperty(ref _usbPath, BeadaPanelDevice.SanitizeString(value));
        }

        [ObservableProperty]
        private BeadaPanelModel? _modelType = null;

        private string _serialNumber = string.Empty;
        public string SerialNumber
        {
            get => _serialNumber;
            set => SetProperty(ref _serialNumber, BeadaPanelDevice.SanitizeString(value));
        }

        [ObservableProperty]
        private DeviceIdentificationMethod _identificationMethod = DeviceIdentificationMethod.UsbPath;

        [ObservableProperty]
        private bool _enabled = false;

        [ObservableProperty]
        private Guid _profileGuid = Guid.Empty;

        [ObservableProperty]
        private LCD_ROTATION _rotation = LCD_ROTATION.RotateNone;

        [ObservableProperty]
        private int _brightness = 100;

        public BeadaPanelDeviceConfig() { }

        public BeadaPanelDeviceConfig(string usbPath, BeadaPanelModel modelType, string serialNumber, DeviceIdentificationMethod identificationMethod = DeviceIdentificationMethod.UsbPath)
        {
            UsbPath = usbPath;
            ModelType = modelType;
            SerialNumber = serialNumber;
            IdentificationMethod = identificationMethod;
        }

        /// <summary>
        /// Creates a runtime BeadaPanelDevice from this configuration
        /// </summary>
        public BeadaPanelDevice? ToDevice()
        {
            // Reject devices without valid model type
            if (!ModelType.HasValue || !BeadaPanelModelDatabase.Models.TryGetValue(ModelType.Value, out BeadaPanelModelInfo? modelInfo))
                return null;
            var device = new BeadaPanelDevice(this)
            {
                // Set stable ID based on identification method
                Id = IdentificationMethod switch
                {
                    DeviceIdentificationMethod.HardwareSerial => SerialNumber,
                    DeviceIdentificationMethod.ModelFingerprint => $"{ModelType}_{modelInfo.Width}x{modelInfo.Height}",
                    DeviceIdentificationMethod.UsbPath => UsbPath,
                    _ => UsbPath
                }
            };


            return device;
        }

        /// <summary>
        /// Creates a configuration from a runtime device
        /// </summary>
        public static BeadaPanelDeviceConfig? FromDevice(BeadaPanelDevice device)
        {
            // Only create config for devices with valid model type
            if (!device.Config.ModelType.HasValue || !BeadaPanelModelDatabase.Models.ContainsKey(device.Config.ModelType.Value))
                return null;
                
            return device.GetConfig();
        }
    }
}