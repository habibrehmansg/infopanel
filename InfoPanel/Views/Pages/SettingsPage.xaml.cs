using Flurl.Util;
using InfoPanel.BeadaPanel;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Timers;
using System.Windows;
using System.Windows.Controls;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for SettingsPage.xaml
    /// </summary>
    public partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }

        private static readonly Timer debounceTimer = new Timer(500);  // 500 ms debounce period
        private static bool deviceInserted = false;
        private static bool deviceRemoved = false;

        public SettingsPage(SettingsViewModel viewModel)
        {
            ViewModel = viewModel;

            InitializeComponent();
            ComboBoxListenIp.Items.Add("127.0.0.1");
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (NetworkInterface ni in interfaces)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    IPInterfaceProperties ipProps = ni.GetIPProperties();
                    foreach (IPAddressInformation addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                            && !addr.Address.ToString().StartsWith("169.254."))
                        {
                            ComboBoxListenIp.Items.Add(addr.Address.ToString());
                        }
                    }
                }
            }

            ComboBoxListenPort.Items.Add("80");
            ComboBoxListenPort.Items.Add("81");
            ComboBoxListenPort.Items.Add("2020");
            ComboBoxListenPort.Items.Add("8000");
            ComboBoxListenPort.Items.Add("8008");
            ComboBoxListenPort.Items.Add("8080");
            ComboBoxListenPort.Items.Add("8081");
            ComboBoxListenPort.Items.Add("8088");
            ComboBoxListenPort.Items.Add("10000");
            ComboBoxListenPort.Items.Add("10001");

            ComboBoxRefreshRate.Items.Add(16);
            ComboBoxRefreshRate.Items.Add(33);
            ComboBoxRefreshRate.Items.Add(50);
            ComboBoxRefreshRate.Items.Add(66);
            ComboBoxRefreshRate.Items.Add(100);
            ComboBoxRefreshRate.Items.Add(200);
            ComboBoxRefreshRate.Items.Add(300);
            ComboBoxRefreshRate.Items.Add(500);
            ComboBoxRefreshRate.Items.Add(1000);

            foreach (var name in SerialPort.GetPortNames())
            {
                ViewModel.ComPorts.Add(name);
            }

            Loaded += (sender, args) =>
            {
                if (ConfigModel.Instance.Settings.TuringPanelAProfile == Guid.Empty)
                {
                    ConfigModel.Instance.Settings.TuringPanelAProfile = ConfigModel.Instance.Profiles.First().Guid;
                }

                if (ConfigModel.Instance.Settings.TuringPanelCProfile == Guid.Empty)
                {
                    ConfigModel.Instance.Settings.TuringPanelCProfile = ConfigModel.Instance.Profiles.First().Guid;
                }

                if (ConfigModel.Instance.Settings.TuringPanelEProfile == Guid.Empty)
                {
                    ConfigModel.Instance.Settings.TuringPanelEProfile = ConfigModel.Instance.Profiles.First().Guid;
                }
            };

            debounceTimer.Elapsed += DebounceTimer_Elapsed;
            debounceTimer.AutoReset = false;

            var watcher = new ManagementEventWatcher();
            var query = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");

            watcher.EventArrived += new EventArrivedEventHandler(HandleEvent);
            watcher.Query = query;
            watcher.Start();
        }

        private void DebounceTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (deviceInserted)
            {
                Trace.WriteLine("A USB device was inserted.");
                deviceInserted = false;
            }
            if (deviceRemoved)
            {
                Trace.WriteLine("A USB device was removed.");
                deviceRemoved = false;
            }

            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ViewModel.ComPorts.Clear();
                foreach (var name in SerialPort.GetPortNames())
                {
                    if (!ViewModel.ComPorts.Contains(name))
                    {
                        ViewModel.ComPorts.Add(name);
                    }
                }
                ComboBoxTuringPanelAPort.SelectedValue = ConfigModel.Instance.Settings.TuringPanelAPort;
                ComboBoxTuringPanelCPort.SelectedValue = ConfigModel.Instance.Settings.TuringPanelCPort;
                ComboBoxTuringPanelEPort.SelectedValue = ConfigModel.Instance.Settings.TuringPanelEPort;
            }));

        }

        private void HandleEvent(object sender, EventArrivedEventArgs e)
        {
            switch ((UInt16)e.NewEvent.Properties["EventType"].Value)
            {
                case 2:
                    deviceInserted = true;
                    debounceTimer.Stop();
                    debounceTimer.Start();
                    break;
                case 3:
                    deviceRemoved = true;
                    debounceTimer.Stop();
                    debounceTimer.Start();
                    break;
            }
        }

        private void ButtonOpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InfoPanel");
            Process.Start(new ProcessStartInfo("explorer.exe", path));
        }

        private void ComboBoxTuringPanelAPort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboBoxTuringPanelAPort.SelectedValue is string value)
            {
                ConfigModel.Instance.Settings.TuringPanelAPort = value;
            }
        }

        private void ComboBoxTuringPanelCPort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboBoxTuringPanelCPort.SelectedValue is string value)
            {
                ConfigModel.Instance.Settings.TuringPanelCPort = value;
            }
        }

        private void ComboBoxTuringPanelEPort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ComboBoxTuringPanelEPort.SelectedValue is string value)
            {
                ConfigModel.Instance.Settings.TuringPanelEPort = value;
            }
        }

        private async void ButtonDiscoverBeadaPanelDevices_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var button = sender as Button;
                if (button != null)
                {
                    button.IsEnabled = false;
                    button.Content = "Discovering...";
                }

                Dictionary<string, BeadaPanelInfo> discoveredDevices = await BeadaPanelTask.Instance.DiscoverDevicesAsync();
                var settings = ConfigModel.Instance.Settings;

                Trace.WriteLine($"Discovery found {discoveredDevices.Count} devices, current collection has {settings.BeadaPanelDevices.Count} devices");

                foreach(var key in discoveredDevices.Keys)
                {
                    var panelInfo = discoveredDevices[key];
                    var existingConfig = FindMatchingDeviceConfig(settings.BeadaPanelDevices, key, panelInfo);

                    if (existingConfig == null)
                    {
                        var config = new BeadaPanelDeviceConfig
                        {
                            UsbPath = key,
                            ModelType = panelInfo.Model,
                            SerialNumber = panelInfo.SerialNumber,
                            ProfileGuid = settings.BeadaPanelDevices.Count > 0 
                                ? settings.BeadaPanelDevices.First().ProfileGuid 
                                : ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty
                        };

                        if(!string.IsNullOrEmpty(config.SerialNumber))
                        {
                            config.IdentificationMethod = DeviceIdentificationMethod.HardwareSerial;
                        }
                        else
                        {
                            config.IdentificationMethod = DeviceIdentificationMethod.ModelFingerprint;
                        }

                        settings.BeadaPanelDevices.Add(config);
                        Trace.WriteLine($"Added new BeadaPanel device: {config}");
                    }
                }

                if (button != null)
                {
                    button.Content = "Discover Devices";
                    button.IsEnabled = true;
                }

                if (discoveredDevices.Count == 0)
                {
                    MessageBox.Show("No BeadaPanel devices found. Make sure your devices are connected and drivers are installed.",
                        "Device Discovery", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show($"Found {discoveredDevices.Count} BeadaPanel device(s). Check the device list below for configuration.",
                        "Device Discovery", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error discovering BeadaPanel devices: {ex.Message}");
                MessageBox.Show($"Error discovering devices: {ex.Message}",
                    "Discovery Error", MessageBoxButton.OK, MessageBoxImage.Error);

                if (sender is Button button)
                {
                    button.Content = "Discover Devices";
                    button.IsEnabled = true;
                }
            }
        }

        private void ButtonRemoveBeadaPanelDevice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is BeadaPanelDevice runtimeDevice)
                {
                    var displayName = !string.IsNullOrEmpty(runtimeDevice.Name)
                        ? runtimeDevice.Name
                        : "BeadaPanel Device";

                    var result = MessageBox.Show($"Are you sure you want to remove the device '{displayName}'?",
                        "Remove Device", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        var settings = ConfigModel.Instance.Settings;

                        // Find config by USB path (always unique and present)
                        var deviceConfig = settings.BeadaPanelDevices.FirstOrDefault(c => c.UsbPath == runtimeDevice.Config.UsbPath);

                        if (deviceConfig != null)
                        {
                            Trace.WriteLine($"Before removal - Collection count: {settings.BeadaPanelDevices.Count}");
                            Trace.WriteLine($"Removing device: {displayName} with USB path: {deviceConfig.UsbPath}");

                            bool removed = settings.BeadaPanelDevices.Remove(deviceConfig);

                            Trace.WriteLine($"Device removal result: {removed}, After removal - Collection count: {settings.BeadaPanelDevices.Count}");

                            // Stop device if running
                            var deviceId = deviceConfig.GetStableId();
                            if (BeadaPanelTask.Instance.IsDeviceRunning(deviceId))
                            {
                                _ = BeadaPanelTask.Instance.StopDevice(deviceId);
                            }

                            // Force save settings to ensure removal is persisted
                            ConfigModel.Instance.SaveSettings();

                            Trace.WriteLine($"Removed BeadaPanel device: {displayName}");
                        }
                        else
                        {
                            Trace.WriteLine($"Could not find config object for runtime device: {displayName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error removing BeadaPanel device: {ex.Message}");
                MessageBox.Show($"Error removing device: {ex.Message}",
                    "Remove Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private BeadaPanelDeviceConfig? FindMatchingDeviceConfig(IEnumerable<BeadaPanelDeviceConfig> existingConfigs, string devicePath, BeadaPanelInfo panelInfo)
        {
            // Priority 1: Match by hardware serial number (most reliable)
            if (!string.IsNullOrEmpty(panelInfo.SerialNumber))
            {
                var serialMatch = existingConfigs.FirstOrDefault(d =>
                    !string.IsNullOrEmpty(d.SerialNumber) &&
                    d.SerialNumber == panelInfo.SerialNumber);
                if (serialMatch != null)
                {
                    Trace.WriteLine($"Device matched by hardware serial: {panelInfo.SerialNumber}");
                    return serialMatch;
                }
            }

            // Priority 2: Match by model fingerprint (model)
            var fingerprintMatch = existingConfigs.FirstOrDefault(d =>
                d.ModelType == panelInfo.Model);
            if (fingerprintMatch != null)
            {
                Trace.WriteLine($"Device matched by model fingerprint: {panelInfo.Model}");
                return fingerprintMatch;
            }

            // Priority 3: Match by USB path (fallback, unreliable across reboots)
            if (!string.IsNullOrEmpty(devicePath))
            {
                var usbPathMatch = existingConfigs.FirstOrDefault(d => d.UsbPath == devicePath);
                if (usbPathMatch != null)
                {
                    Trace.WriteLine($"Device matched by USB path: {devicePath}");
                    return usbPathMatch;
                }
            }

            Trace.WriteLine($"No matching device found for {devicePath}");
            return null;
        }


    }
}
