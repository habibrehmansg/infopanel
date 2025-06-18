using InfoPanel.BeadaPanel;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.ViewModels;
using InfoPanel.Views.Windows;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using Serilog;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using InfoPanel.TuringPanel;
using System.Collections.Generic;

namespace InfoPanel.Views.Pages
{
    /// <summary>
    /// Interaction logic for SettingsPage.xaml
    /// </summary>
    public partial class SettingsPage : Page
    {
        private static readonly ILogger Logger = Log.ForContext<SettingsPage>();
        public SettingsViewModel ViewModel { get; }

        private static readonly Timer debounceTimer = new Timer(500);  // 500 ms debounce period
        private static bool deviceInserted = false;
        private static bool deviceRemoved = false;

        public SettingsPage(SettingsViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = this;
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

            debounceTimer.Elapsed += DebounceTimer_Elapsed;
            debounceTimer.AutoReset = false;

            var watcher = new ManagementEventWatcher();
            var query = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");

            watcher.EventArrived += new EventArrivedEventHandler(HandleEvent);
            watcher.Query = query;
            watcher.Start();

            Loaded += SettingsPage_Loaded;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await UpdateBeadaPanelDeviceList();
            await UpdateTuringPanelDeviceList();
        }

        private void DebounceTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            if (deviceInserted)
            {
                Logger.Information("A USB device was inserted.");
                deviceInserted = false;
            }
            if (deviceRemoved)
            {
                Logger.Information("A USB device was removed.");
                deviceRemoved = false;
            }

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

        private async Task UpdateBeadaPanelDeviceList()
        {
            int vendorId = 0x4e58;
            int productId = 0x1001;

            var allDevices = UsbDevice.AllDevices;
            Logger.Information("BeadaPanel Discovery: Scanning {Count} USB devices for VID={VendorId:X4} PID={ProductId:X4}", allDevices.Count, vendorId, productId);

            foreach (UsbRegistry deviceReg in allDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;
                    var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string;

                    if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceLocation))
                    {
                        Log.Warning("BeadaPanel Discovery: Skipping device with missing properties - DeviceID: '{DeviceId}', LocationInformation: '{DeviceLocation}'", deviceId, deviceLocation);
                        continue;
                    }

                    Log.Information("BeadaPanel Discovery: Found matching device - FullName: '{FullName}', DevicePath: '{DevicePath}', SymbolicName: '{SymbolicName}'", deviceReg.FullName, deviceReg.DevicePath, deviceReg.SymbolicName);

                    try
                    {
                        // Try to query StatusLink for detailed device information
                        var panelInfo = await BeadaPanelHelper.GetPanelInfoAsync(deviceReg);
                        if (panelInfo != null && BeadaPanelModelDatabase.Models.ContainsKey(panelInfo.Model))
                        {
                            Log.Information("Discovered BeadaPanel device: {PanelInfo}", panelInfo);
                            ConfigModel.Instance.AccessSettings(settings =>
                            {
                                var device = settings.BeadaPanelDevices.FirstOrDefault(d => d.IsMatching(deviceId, deviceLocation, panelInfo));

                                if (device != null)
                                {
                                    device.DeviceLocation = deviceLocation;
                                    device.UpdateRuntimeProperties(panelInfo: panelInfo);
                                }
                                else
                                {
                                    device = new BeadaPanelDevice()
                                    {
                                        DeviceId = deviceId,
                                        DeviceLocation = deviceLocation,
                                        Model = panelInfo.Model.ToString(),
                                        ProfileGuid = ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty,
                                        RuntimeProperties = new BeadaPanelDevice.BeadaPanelDeviceRuntimeProperties()
                                        {
                                            PanelInfo = panelInfo
                                        }
                                    };

                                    settings.BeadaPanelDevices.Add(device);
                                }
                            });
                        }
                        else
                        {
                            // Skip devices that can't be queried - they are likely already running
                            Log.Information("Skipping device {DevicePath} - StatusLink unavailable (likely already running)", deviceReg.DevicePath);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Error discovering BeadaPanel device");
                    }
                }
            }
        }

        private async void ButtonDiscoverBeadaPanelDevices_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
                await UpdateBeadaPanelDeviceList();
                button.IsEnabled = true;
            }
        }

        private void ButtonRemoveBeadaPanelDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BeadaPanelDevice runtimeDevice)
            {
                // Find config by USB path (always unique and present)
                ConfigModel.Instance.AccessSettings(settings =>
                {
                    var deviceConfig = settings.BeadaPanelDevices.FirstOrDefault(c => c.Id == runtimeDevice.Id);

                    if (deviceConfig != null)
                    {
                        if (BeadaPanelTask.Instance.IsDeviceRunning(deviceConfig.Id))
                        {
                            _ = BeadaPanelTask.Instance.StopDevice(deviceConfig.Id);
                        }

                        settings.BeadaPanelDevices.Remove(deviceConfig);
                    }
                });
            }
        }

        private async Task UpdateTuringPanelDeviceList()
        {
            var serialDeviceTask = TuringPanelHelper.GetSerialDevices();
            var usbDeviceTask = TuringPanelHelper.GetUsbDevices();

            await Task.WhenAll(serialDeviceTask, usbDeviceTask);

            List<TuringPanelDevice> discoveredDevices = [.. usbDeviceTask.Result, .. serialDeviceTask.Result];

            foreach (var discoveredDevice in discoveredDevices)
            {

                ConfigModel.Instance.AccessSettings(settings =>
                {
                    var device = settings.TuringPanelDevices.FirstOrDefault(d => d.IsMatching(discoveredDevice.DeviceId));

                    if (device == null)
                    {
                        discoveredDevice.ProfileGuid = ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty;
                        settings.TuringPanelDevices.Add(discoveredDevice);
                        Logger.Information("TuringPanel Discovery: Added new device with DeviceId '{DeviceId}'", discoveredDevice.DeviceId);
                    }
                    else
                    {
                        //update location
                        device.DeviceLocation = discoveredDevice.DeviceLocation;
                        Logger.Information("TuringPanel Discovery: Device with DeviceId '{DeviceId}' already exists", discoveredDevice.DeviceId);
                    }
                });
            }
        }

        private async void ButtonDiscoverTuringPanelDevices_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
                await UpdateTuringPanelDeviceList();
                button.IsEnabled = true;
            }
        }

        private void ButtonRemoveTuringPanelDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TuringPanelDevice runtimeDevice)
            {
                ConfigModel.Instance.AccessSettings(settings =>
                {
                    var deviceConfig = settings.TuringPanelDevices.FirstOrDefault(c => c.Id == runtimeDevice.Id);

                    if (deviceConfig != null)
                    {
                        if (TuringPanelTask.Instance.IsDeviceRunning(deviceConfig.Id))
                        {
                            _ = TuringPanelTask.Instance.StopDevice(deviceConfig.Id);
                        }

                        settings.TuringPanelDevices.Remove(deviceConfig);
                    }
                });
            }
        }

        private void ButtonManageTuringDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is TuringPanelDevice device)
            {
                var window = new TuringDeviceWindow(device);
                window.ShowDialog();
            }
        }
    }
}
