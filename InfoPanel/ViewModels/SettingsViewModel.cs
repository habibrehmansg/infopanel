using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Wpf.Ui.Common.Interfaces;

namespace InfoPanel.ViewModels
{
    public enum LCD_ROTATION
    {
        [Description("No rotation")]
        RotateNone = 0,
        [Description("Rotate 90°")]
        Rotate90FlipNone = 1,
        [Description("Rotate 180°")]
        Rotate180FlipNone = 2,
        [Description("Rotate 270°")]
        Rotate270FlipNone = 3,
    }

    public class SettingsViewModel : ObservableObject, INavigationAware
    {
        private ObservableCollection<string> _comPorts = new();
        private ObservableCollection<BeadaPanelDevice> _runtimeBeadaPanelDevices = new();

        public ObservableCollection<LCD_ROTATION> RotationValues { get; set; }

        public SettingsViewModel()
        {
            RotationValues = new ObservableCollection<LCD_ROTATION>(Enum.GetValues(typeof(LCD_ROTATION)).Cast<LCD_ROTATION>());

            // Initialize runtime devices from config
            UpdateRuntimeDevices();

            // Subscribe to config changes
            ConfigModel.Instance.Settings.BeadaPanelDevices.CollectionChanged += BeadaPanelDevices_CollectionChanged;
            foreach (var config in ConfigModel.Instance.Settings.BeadaPanelDevices)
            {
                config.PropertyChanged += BeadaPanelDeviceConfig_PropertyChanged;
            }

            // Subscribe to device status changes
            SharedModel.Instance.BeadaPanelDeviceStatusChanged += OnBeadaPanelDeviceStatusChanged;
        }

        public ObservableCollection<string> ComPorts
        {
            get { return _comPorts; }
        }

        public ObservableCollection<BeadaPanelDevice> RuntimeBeadaPanelDevices
        {
            get { return _runtimeBeadaPanelDevices; }
        }

        private void BeadaPanelDevices_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (BeadaPanelDeviceConfig config in e.OldItems)
                {
                    config.PropertyChanged -= BeadaPanelDeviceConfig_PropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (BeadaPanelDeviceConfig config in e.NewItems)
                {
                    config.PropertyChanged += BeadaPanelDeviceConfig_PropertyChanged;
                }
            }

            UpdateRuntimeDevices();
        }

        private void BeadaPanelDeviceConfig_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Update runtime devices when config changes
            UpdateRuntimeDevices();
        }

        private void OnBeadaPanelDeviceStatusChanged(object? sender, string deviceId)
        {
            // Find the runtime device with matching ID and notify it that its status changed
            var runtimeDevice = RuntimeBeadaPanelDevices.FirstOrDefault(d => d.Id == deviceId);
            if (runtimeDevice != null)
            {
                // Use dispatcher to ensure UI updates happen on UI thread
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    if (runtimeDevice != null)
                    {
                        runtimeDevice.NotifyDeviceStatusChanged();
                    }
                });
            }
        }

        private void UpdateRuntimeDevices()
        {
            // Unsubscribe from old runtime devices
            foreach (var device in RuntimeBeadaPanelDevices)
            {
                device.PropertyChanged -= RuntimeDevice_PropertyChanged;
            }

            RuntimeBeadaPanelDevices.Clear();

            foreach (var config in ConfigModel.Instance.Settings.BeadaPanelDevices)
            {
                var runtimeDevice = config.ToDevice();
                runtimeDevice.PropertyChanged += RuntimeDevice_PropertyChanged;
                RuntimeBeadaPanelDevices.Add(runtimeDevice);
            }
        }

        private void RuntimeDevice_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is BeadaPanelDevice runtimeDevice)
            {
                // Find the corresponding config and update it with changes to persistent properties
                var config = FindConfigForRuntimeDevice(runtimeDevice);
                if (config != null)
                {
                    // Only sync configuration properties, not runtime properties
                    switch (e.PropertyName)
                    {
                        case nameof(BeadaPanelDevice.Enabled):
                            config.Enabled = runtimeDevice.Enabled;
                            break;
                        case nameof(BeadaPanelDevice.ProfileGuid):
                            config.ProfileGuid = runtimeDevice.ProfileGuid;
                            // Notify running device task of configuration change
                            InfoPanel.BeadaPanelTask.Instance.NotifyDeviceConfigurationChanged(config);
                            break;
                        case nameof(BeadaPanelDevice.Rotation):
                            config.Rotation = runtimeDevice.Rotation;
                            // Notify running device task of configuration change
                            InfoPanel.BeadaPanelTask.Instance.NotifyDeviceConfigurationChanged(config);
                            break;
                        case nameof(BeadaPanelDevice.Brightness):
                            config.Brightness = runtimeDevice.Brightness;
                            // Notify running device task of configuration change
                            InfoPanel.BeadaPanelTask.Instance.NotifyDeviceConfigurationChanged(config);
                            break;
                    }
                }
            }
        }

        private BeadaPanelDeviceConfig? FindConfigForRuntimeDevice(BeadaPanelDevice runtimeDevice)
        {
            var targetId = runtimeDevice.Id;

            foreach (var config in ConfigModel.Instance.Settings.BeadaPanelDevices)
            {
                var configId = GetConfigId(config);
                if (configId == targetId)
                {
                    return config;
                }
            }

            return null;
        }

        private string GetConfigId(BeadaPanelDeviceConfig config)
        {
            return config.IdentificationMethod switch
            {
                DeviceIdentificationMethod.HardwareSerial => config.HardwareSerialNumber,
                DeviceIdentificationMethod.ModelFingerprint => $"{config.ModelType}_{config.NativeResolutionX}x{config.NativeResolutionY}",
                DeviceIdentificationMethod.UsbPath => config.UsbPath,
                _ => config.UsbPath
            };
        }

        public void OnNavigatedFrom()
        {
        }

        public void OnNavigatedTo()
        {
        }

        ~SettingsViewModel()
        {
            // Cleanup event subscriptions
            SharedModel.Instance.BeadaPanelDeviceStatusChanged -= OnBeadaPanelDeviceStatusChanged;
        }
    }
}
