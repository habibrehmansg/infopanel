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
                device.Config.PropertyChanged -= Config_PropertyChanged;
            }

            RuntimeBeadaPanelDevices.Clear();

            foreach (var config in ConfigModel.Instance.Settings.BeadaPanelDevices)
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
