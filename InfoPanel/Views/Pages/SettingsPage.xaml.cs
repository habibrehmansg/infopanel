using InfoPanel.BeadaPanel;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
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

            Loaded += SettingsPage_Loaded;
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await UpdateBeadaPanelDeviceList();
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

        private async Task UpdateBeadaPanelDeviceList()
        {
            int vendorId = 0x4e58;
            int productId = 0x1001;

            var allDevices = UsbDevice.AllDevices;
            Trace.WriteLine($"BeadaPanel Discovery: Scanning {allDevices.Count} USB devices for VID={vendorId:X4} PID={productId:X4}");

            foreach (UsbRegistry deviceReg in allDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;
                    var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string;

                    if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceLocation))
                    {
                        Trace.WriteLine($"BeadaPanel Discovery: Skipping device with missing properties - DeviceID: '{deviceId}', LocationInformation: '{deviceLocation}'");
                        continue;
                    }

                    Trace.WriteLine($"BeadaPanel Discovery: Found matching device - FullName: '{deviceReg.FullName}', DevicePath: '{deviceReg.DevicePath}', SymbolicName: '{deviceReg.SymbolicName}'");

                    try
                    {
                        // Try to query StatusLink for detailed device information
                        var panelInfo = await BeadaPanelHelper.GetPanelInfoAsync(deviceReg);
                        if (panelInfo != null && BeadaPanelModelDatabase.Models.ContainsKey(panelInfo.Model))
                        {
                            Trace.WriteLine($"Discovered BeadaPanel device: {panelInfo}");
                            ConfigModel.Instance.AccessSettings(settings =>
                            {
                                var device = settings.BeadaPanelDevices.FirstOrDefault(d => d.IsMatching(deviceId, deviceLocation, panelInfo));

                                if (device != null)
                                {
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
                            Trace.WriteLine($"Skipping device {deviceReg.DevicePath} - StatusLink unavailable (likely already running)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"Error discovering BeadaPanel device: {ex.Message}");
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

                        ConfigModel.Instance.Settings.BeadaPanelDevices.Remove(deviceConfig);
                        ConfigModel.Instance.SaveSettings();
                    }
                });
            }
        }
    }
}
