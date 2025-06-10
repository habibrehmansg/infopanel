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
                    if(!ViewModel.ComPorts.Contains(name))
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
            if(ComboBoxTuringPanelCPort.SelectedValue is string value)
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

                var discoveredDevices = await BeadaPanelTask.Instance.DiscoverDevicesAsync();
                var settings = ConfigModel.Instance.Settings;

                Trace.WriteLine($"Discovery found {discoveredDevices.Count} devices, current collection has {settings.BeadaPanelDevices.Count} devices");
                
                // Ensure we're on the UI thread after the await
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var discoveredDevice in discoveredDevices)
                    {
                        Trace.WriteLine($"Processing discovered device: {discoveredDevice.Name} with hardware serial: {discoveredDevice.HardwareSerialNumber}");
                        
                        var existingDevice = FindMatchingDevice(settings.BeadaPanelDevices, discoveredDevice);

                        if (existingDevice == null)
                        {
                            discoveredDevice.ProfileGuid = settings.BeadaPanelDevices.Count > 0 
                                ? settings.BeadaPanelDevices.First().ProfileGuid 
                                : ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty;

                            settings.BeadaPanelDevices.Add(discoveredDevice);
                            Trace.WriteLine($"Added new BeadaPanel device: {discoveredDevice.Name} (ID: {discoveredDevice.IdentificationMethod})");
                        }
                        else
                        {
                            // Update existing device with new information
                            UpdateExistingDevice(existingDevice, discoveredDevice);
                            Trace.WriteLine($"Updated existing BeadaPanel device: {existingDevice.Name} (ID: {existingDevice.IdentificationMethod})");
                        }
                    }
                });

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
                if (sender is Button button && button.Tag is BeadaPanelDevice device)
                {
                    var result = MessageBox.Show($"Are you sure you want to remove the device '{device.Name}'?", 
                        "Remove Device", MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        var settings = ConfigModel.Instance.Settings;
                        
                        Trace.WriteLine($"Before removal - Collection count: {settings.BeadaPanelDevices.Count}");
                        Trace.WriteLine($"Removing device: {device.Name} (ID: {device.Id}) with hardware serial: {device.HardwareSerialNumber}");
                        
                        bool removed = settings.BeadaPanelDevices.Remove(device);
                        
                        Trace.WriteLine($"Device removal result: {removed}, After removal - Collection count: {settings.BeadaPanelDevices.Count}");
                        
                        if (BeadaPanelTask.Instance.IsDeviceRunning(device.Id))
                        {
                            _ = BeadaPanelTask.Instance.StopDevice(device.Id);
                        }

                        // Force save settings to ensure removal is persisted
                        ConfigModel.Instance.SaveSettings();
                        
                        Trace.WriteLine($"Removed BeadaPanel device: {device.Name}");
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

        private BeadaPanelDevice? FindMatchingDevice(IEnumerable<BeadaPanelDevice> existingDevices, BeadaPanelDevice discoveredDevice)
        {
            // Priority 1: Match by hardware serial number (most reliable)
            if (!string.IsNullOrEmpty(discoveredDevice.HardwareSerialNumber))
            {
                var serialMatch = existingDevices.FirstOrDefault(d => 
                    !string.IsNullOrEmpty(d.HardwareSerialNumber) && 
                    d.HardwareSerialNumber == discoveredDevice.HardwareSerialNumber);
                if (serialMatch != null)
                {
                    Trace.WriteLine($"Device matched by hardware serial: {discoveredDevice.HardwareSerialNumber}");
                    return serialMatch;
                }
            }

            // Priority 2: Match by model fingerprint (model + firmware + resolution)
            if (discoveredDevice.ModelType.HasValue && discoveredDevice.FirmwareVersion > 0)
            {
                var fingerprintMatch = existingDevices.FirstOrDefault(d => 
                    d.ModelType == discoveredDevice.ModelType &&
                    d.FirmwareVersion == discoveredDevice.FirmwareVersion &&
                    d.NativeResolutionX == discoveredDevice.NativeResolutionX &&
                    d.NativeResolutionY == discoveredDevice.NativeResolutionY);
                if (fingerprintMatch != null)
                {
                    Trace.WriteLine($"Device matched by model fingerprint: {discoveredDevice.ModelType}_{discoveredDevice.FirmwareVersion}_{discoveredDevice.NativeResolutionX}x{discoveredDevice.NativeResolutionY}");
                    return fingerprintMatch;
                }
            }

            // Priority 3: Match by USB path (fallback, unreliable across reboots)
            if (!string.IsNullOrEmpty(discoveredDevice.UsbPath))
            {
                var usbPathMatch = existingDevices.FirstOrDefault(d => d.UsbPath == discoveredDevice.UsbPath);
                if (usbPathMatch != null)
                {
                    Trace.WriteLine($"Device matched by USB path: {discoveredDevice.UsbPath}");
                    return usbPathMatch;
                }
            }

            // Priority 4: Match by legacy serial number (backward compatibility)
            if (!string.IsNullOrEmpty(discoveredDevice.SerialNumber))
            {
                var legacySerialMatch = existingDevices.FirstOrDefault(d => d.SerialNumber == discoveredDevice.SerialNumber);
                if (legacySerialMatch != null)
                {
                    Trace.WriteLine($"Device matched by legacy serial: {discoveredDevice.SerialNumber}");
                    return legacySerialMatch;
                }
            }

            Trace.WriteLine($"No matching device found for {discoveredDevice.Name}");
            return null;
        }

        private void UpdateExistingDevice(BeadaPanelDevice existingDevice, BeadaPanelDevice discoveredDevice)
        {
            // Always update USB path and connection info
            existingDevice.UsbPath = discoveredDevice.UsbPath;
            existingDevice.SerialNumber = discoveredDevice.SerialNumber;

            // Update StatusLink information if available
            if (discoveredDevice.StatusLinkAvailable)
            {
                existingDevice.HardwareSerialNumber = discoveredDevice.HardwareSerialNumber;
                existingDevice.ModelType = discoveredDevice.ModelType;
                existingDevice.ModelName = discoveredDevice.ModelName;
                existingDevice.FirmwareVersion = discoveredDevice.FirmwareVersion;
                existingDevice.Platform = discoveredDevice.Platform;
                existingDevice.NativeResolutionX = discoveredDevice.NativeResolutionX;
                existingDevice.NativeResolutionY = discoveredDevice.NativeResolutionY;
                existingDevice.WidthMm = discoveredDevice.WidthMm;
                existingDevice.HeightMm = discoveredDevice.HeightMm;
                existingDevice.MaxBrightness = discoveredDevice.MaxBrightness;
                existingDevice.StatusLinkAvailable = true;
                existingDevice.IdentificationMethod = discoveredDevice.IdentificationMethod;

                // Update device name if it was generic or if we now have better info
                if (string.IsNullOrEmpty(existingDevice.Name) || 
                    existingDevice.Name.StartsWith("BeadaPanel Device") ||
                    existingDevice.Name.Contains("WinUsb Device"))
                {
                    existingDevice.Name = discoveredDevice.Name;
                }
            }
        }
    }
}
