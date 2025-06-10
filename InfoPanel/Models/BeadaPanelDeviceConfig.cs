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
            set => SetProperty(ref _usbPath, value);
        }

        private string _modelName = string.Empty;
        public string ModelName
        {
            get => _modelName;
            set => SetProperty(ref _modelName, value);
        }

        private string _hardwareSerialNumber = string.Empty;
        public string HardwareSerialNumber
        {
            get => _hardwareSerialNumber;
            set => SetProperty(ref _hardwareSerialNumber, value);
        }

        [ObservableProperty]
        private int _nativeResolutionX = 0;

        [ObservableProperty]
        private int _nativeResolutionY = 0;

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

        [ObservableProperty]
        private byte _maxBrightness = 255;

        /// <summary>
        /// Creates a runtime BeadaPanelDevice from this configuration
        /// </summary>
        public BeadaPanelDevice ToDevice()
        {
            var device = new BeadaPanelDevice
            {
                UsbPath = UsbPath,
                ModelName = ModelName,
                HardwareSerialNumber = HardwareSerialNumber,
                NativeResolutionX = NativeResolutionX,
                NativeResolutionY = NativeResolutionY,
                IdentificationMethod = IdentificationMethod,
                Enabled = Enabled,
                ProfileGuid = ProfileGuid,
                Rotation = Rotation,
                Brightness = Brightness,
                ModelType = ModelType,
                MaxBrightness = MaxBrightness
            };

            // Set stable ID based on identification method
            device.Id = IdentificationMethod switch
            {
                DeviceIdentificationMethod.HardwareSerial => HardwareSerialNumber,
                DeviceIdentificationMethod.ModelFingerprint => $"{ModelType}_{NativeResolutionX}x{NativeResolutionY}",
                DeviceIdentificationMethod.UsbPath => UsbPath,
                _ => UsbPath
            };

            // Generate runtime properties based on available data
            if (!string.IsNullOrEmpty(HardwareSerialNumber))
            {
                device.Name = $"{ModelName} ({HardwareSerialNumber})";
            }
            else if (!string.IsNullOrEmpty(ModelName))
            {
                device.Name = $"{ModelName} #{device.Id[..8]}";
            }
            else
            {
                device.Name = $"BeadaPanel Device";
            }

            return device;
        }

        /// <summary>
        /// Creates a configuration from a runtime device
        /// </summary>
        public static BeadaPanelDeviceConfig FromDevice(BeadaPanelDevice device)
        {
            return new BeadaPanelDeviceConfig
            {
                UsbPath = device.UsbPath,
                ModelName = device.ModelName,
                HardwareSerialNumber = device.HardwareSerialNumber,
                NativeResolutionX = device.NativeResolutionX,
                NativeResolutionY = device.NativeResolutionY,
                IdentificationMethod = device.IdentificationMethod,
                Enabled = device.Enabled,
                ProfileGuid = device.ProfileGuid,
                Rotation = device.Rotation,
                Brightness = device.Brightness,
                ModelType = device.ModelType,
                MaxBrightness = device.MaxBrightness
            };
        }
    }
}