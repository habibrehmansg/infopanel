using CommunityToolkit.Mvvm.ComponentModel;
using InfoPanel.Models;
using System;
using System.Collections.Generic;
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
            // Handle removed items
            if (e.OldItems != null)
            {
                foreach (BeadaPanelDeviceConfig config in e.OldItems)
                {
                    // Find and remove the corresponding runtime device
                    var deviceToRemove = RuntimeBeadaPanelDevices.FirstOrDefault(d => d.Config == config);
                    if (deviceToRemove != null)
                    {
                        deviceToRemove.PropertyChanged -= RuntimeDevice_PropertyChanged;
                        deviceToRemove.Config.PropertyChanged -= Config_PropertyChanged;
                        RuntimeBeadaPanelDevices.Remove(deviceToRemove);
                    }
                }
            }

            // Handle added items
            if (e.NewItems != null)
            {
                foreach (BeadaPanelDeviceConfig config in e.NewItems)
                {
                    var runtimeDevice = config.ToDevice();
                    if (runtimeDevice != null)
                    {
                        runtimeDevice.PropertyChanged += RuntimeDevice_PropertyChanged;
                        runtimeDevice.Config.PropertyChanged += Config_PropertyChanged;
                        RuntimeBeadaPanelDevices.Add(runtimeDevice);
                    }
                }
            }
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
            // Create a dictionary of existing devices by their config
            var existingDevices = RuntimeBeadaPanelDevices.ToDictionary(d => d.Config);
            
            // Track which configs are still present
            var presentConfigs = new HashSet<BeadaPanelDeviceConfig>();

            // Update or add devices
            foreach (var config in ConfigModel.Instance.Settings.BeadaPanelDevices)
            {
                presentConfigs.Add(config);
                
                if (!existingDevices.ContainsKey(config))
                {
                    // New config - create runtime device
                    var runtimeDevice = config.ToDevice();
                    if (runtimeDevice != null)
                    {
                        runtimeDevice.PropertyChanged += RuntimeDevice_PropertyChanged;
                        runtimeDevice.Config.PropertyChanged += Config_PropertyChanged;
                        RuntimeBeadaPanelDevices.Add(runtimeDevice);
                    }
                }
            }

            // Remove devices whose configs are no longer present
            var devicesToRemove = RuntimeBeadaPanelDevices.Where(d => !presentConfigs.Contains(d.Config)).ToList();
            foreach (var device in devicesToRemove)
            {
                device.PropertyChanged -= RuntimeDevice_PropertyChanged;
                device.Config.PropertyChanged -= Config_PropertyChanged;
                RuntimeBeadaPanelDevices.Remove(device);
            }
        }

        private void RuntimeDevice_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Handle runtime property changes if needed
        }

        private void Config_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is BeadaPanelDeviceConfig config)
            {
                // Config properties have changed, notify the running device task
                switch (e.PropertyName)
                {
                    case nameof(BeadaPanelDeviceConfig.ProfileGuid):
                    case nameof(BeadaPanelDeviceConfig.Rotation):
                    case nameof(BeadaPanelDeviceConfig.Brightness):
                        InfoPanel.BeadaPanelTask.Instance.NotifyDeviceConfigurationChanged(config);
                        break;
                }
            }
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
