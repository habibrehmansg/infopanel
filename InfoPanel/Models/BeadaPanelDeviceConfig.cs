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

        private string _hardwareSerialNumber = string.Empty;
        public string HardwareSerialNumber
        {
            get => _hardwareSerialNumber;
            set => SetProperty(ref _hardwareSerialNumber, BeadaPanelDevice.SanitizeString(value));
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

        [ObservableProperty]
        private BeadaPanelModel? _modelType = null;

        /// <summary>
        /// Creates a runtime BeadaPanelDevice from this configuration
        /// </summary>
        public BeadaPanelDevice? ToDevice()
        {
            // Reject devices without valid model type
            if (!ModelType.HasValue || !BeadaPanelModelDatabase.Models.ContainsKey(ModelType.Value))
                return null;

            var modelInfo = BeadaPanelModelDatabase.Models[ModelType.Value];
            
            var device = new BeadaPanelDevice(this);

            // Set stable ID based on identification method
            device.Id = IdentificationMethod switch
            {
                DeviceIdentificationMethod.HardwareSerial => HardwareSerialNumber,
                DeviceIdentificationMethod.ModelFingerprint => $"{ModelType}_{modelInfo.Width}x{modelInfo.Height}",
                DeviceIdentificationMethod.UsbPath => UsbPath,
                _ => UsbPath
            };

            // Generate runtime properties based on available data
            if (!string.IsNullOrEmpty(HardwareSerialNumber))
            {
                device.Name = $"{modelInfo.Name} ({HardwareSerialNumber})";
            }
            else
            {
                device.Name = $"{modelInfo.Name} #{device.Id[..8]}";
            }

            return device;
        }

        /// <summary>
        /// Creates a configuration from a runtime device
        /// </summary>
        public static BeadaPanelDeviceConfig? FromDevice(BeadaPanelDevice device)
        {
            // Only create config for devices with valid model type
            if (!device.ModelType.HasValue || !BeadaPanelModelDatabase.Models.ContainsKey(device.ModelType.Value))
                return null;
                
            return device.GetConfig();
        }
    }
}