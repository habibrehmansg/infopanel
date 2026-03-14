using InfoPanel.BeadaPanel;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.TuringPanel;
using InfoPanel.ThermalrightPanel;
using InfoPanel.ViewModels;
using InfoPanel.Views.Windows;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui;

namespace InfoPanel.Views.Pages;

/// <summary>
/// Interaction logic for UsbPanelsPage.xaml
/// </summary>
public partial class UsbPanelsPage : Page
{
    private static readonly ILogger Logger = Log.ForContext<UsbPanelsPage>();
    private readonly ISnackbarService _snackbarService;
    public UsbPanelsViewModel ViewModel { get; }

    private static bool deviceInserted = false;
    private static bool deviceRemoved = false;

    private ModifierKeys _capturedModifiers = ModifierKeys.None;
    private Key _capturedKey = Key.None;

    public UsbPanelsPage(UsbPanelsViewModel viewModel, ISnackbarService snackbarService)
    {
        ViewModel = viewModel;
        _snackbarService = snackbarService;
        DataContext = this;
        InitializeComponent();
        PopulateDeviceCombo();
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

    private Task UpdateThermalrightPanelDeviceList()
    {
        var discoveredDevices = ThermalrightPanelHelper.ScanDevices();

        Logger.Information("ThermalrightPanel Discovery: Found {Count} devices", discoveredDevices.Count);

        foreach (var discoveredDevice in discoveredDevices)
        {
            ConfigModel.Instance.AccessSettings(settings =>
            {
                var device = settings.ThermalrightPanelDevices.FirstOrDefault(d =>
                    d.IsMatching(discoveredDevice.DeviceId, discoveredDevice.DeviceLocation, discoveredDevice.Model));

                if (device == null)
                {
                    var newDevice = new ThermalrightPanelDevice()
                    {
                        DeviceId = discoveredDevice.DeviceId,
                        DeviceLocation = discoveredDevice.DeviceLocation,
                        Model = discoveredDevice.Model,
                        ProfileGuid = ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty
                    };

                    if (discoveredDevice.ModelInfo != null)
                    {
                        newDevice.RuntimeProperties.Name = $"Thermalright {discoveredDevice.ModelInfo.Name}";
                    }

                    settings.ThermalrightPanelDevices.Add(newDevice);
                    Logger.Information("ThermalrightPanel Discovery: Added new device with DeviceId '{DeviceId}'", discoveredDevice.DeviceId);
                }
                else
                {
                    // Update location
                    device.DeviceLocation = discoveredDevice.DeviceLocation;

                    // Set name from saved model info (determined during previous init)
                    if (device.ModelInfo != null)
                    {
                        device.RuntimeProperties.Name = $"Thermalright {device.ModelInfo.Name}";
                    }

                    Logger.Information("ThermalrightPanel Discovery: Device with DeviceId '{DeviceId}' already exists", discoveredDevice.DeviceId);
                }
            });
        }

        // Show snackbar warning for devices with wrong USB driver
        var driverIssueDevices = discoveredDevices.Where(d => d.DriverIssue != null).ToList();
        foreach (var d in driverIssueDevices)
        {
            _snackbarService.Show(
                "Wrong USB Driver",
                $"Thermalright panel has wrong USB driver ({d.DriverIssue}). Use Zadig to install WinUSB driver.",
                Wpf.Ui.Controls.ControlAppearance.Caution,
                null,
                TimeSpan.FromSeconds(10));
        }

        return Task.CompletedTask;
    }

    private async void ButtonDiscoverThermalrightPanelDevices_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
            await UpdateThermalrightPanelDeviceList();
            button.IsEnabled = true;
        }
    }

    private void ButtonRemoveThermalrightPanelDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is ThermalrightPanelDevice runtimeDevice)
        {
            ConfigModel.Instance.AccessSettings(settings =>
            {
                var deviceConfig = settings.ThermalrightPanelDevices.FirstOrDefault(c => c.Id == runtimeDevice.Id);

                if (deviceConfig != null)
                {
                    if (ThermalrightPanelTask.Instance.IsDeviceRunning(deviceConfig.Id))
                    {
                        _ = ThermalrightPanelTask.Instance.StopDevice(deviceConfig.Id);
                    }

                    settings.ThermalrightPanelDevices.Remove(deviceConfig);
                }
            });
        }
    }

    // --- Global Hotkeys ---

    private void PopulateDeviceCombo()
    {
        var items = new List<Tuple<string, string>>();

        foreach (var d in ConfigModel.Instance.Settings.BeadaPanelDevices)
        {
            var name = d.RuntimeProperties?.PanelInfo?.ModelInfo?.Name ?? d.Model;
            items.Add(Tuple.Create($"Beada|{d.DeviceId}|{d.DeviceLocation}", $"Beada: {name}"));
        }

        foreach (var d in ConfigModel.Instance.Settings.TuringPanelDevices)
        {
            var name = d.Name ?? d.DeviceId;
            items.Add(Tuple.Create($"Turing|{d.DeviceId}|", $"Turing: {name}"));
        }

        foreach (var d in ConfigModel.Instance.Settings.ThermalrightPanelDevices)
        {
            var name = d.RuntimeProperties?.Name ?? d.DeviceId;
            items.Add(Tuple.Create($"Thermalright|{d.DeviceId}|{d.DeviceLocation}", $"TR: {name}"));
        }

        HotkeyDeviceCombo.ItemsSource = items;
    }

    private void HotkeyCapture_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.Text = "Press a key combo...";
            _capturedModifiers = ModifierKeys.None;
            _capturedKey = Key.None;
        }
    }

    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore pure modifier keys
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        _capturedModifiers = Keyboard.Modifiers;
        _capturedKey = key;

        var binding = new HotkeyBinding { ModifierKeys = _capturedModifiers, Key = _capturedKey };
        if (sender is TextBox tb)
        {
            tb.Text = binding.HotkeyDisplayText;
        }
    }

    private void ButtonAddHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturedKey == Key.None)
        {
            _snackbarService.Show("Error", "Please capture a hotkey first.", Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        if (HotkeyDeviceCombo.SelectedValue is not string deviceKey)
        {
            _snackbarService.Show("Error", "Please select a panel.", Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        if (HotkeyProfileCombo.SelectedValue is not Guid profileGuid)
        {
            _snackbarService.Show("Error", "Please select a profile.", Wpf.Ui.Controls.ControlAppearance.Caution, null, TimeSpan.FromSeconds(3));
            return;
        }

        var parts = deviceKey.Split('|');
        if (parts.Length < 3) return;

        var binding = new HotkeyBinding
        {
            ModifierKeys = _capturedModifiers,
            Key = _capturedKey,
            DeviceType = parts[0],
            DeviceId = parts[1],
            DeviceLocation = parts[2],
            ProfileGuid = profileGuid
        };

        ConfigModel.Instance.Settings.HotkeyBindings.Add(binding);
        _ = ConfigModel.Instance.SaveSettingsAsync();

        // Reset capture
        _capturedModifiers = ModifierKeys.None;
        _capturedKey = Key.None;
        HotkeyCapture.Text = "Click and press a key combo";
        HotkeyDeviceCombo.SelectedIndex = -1;
        HotkeyProfileCombo.SelectedIndex = -1;

        // Refresh device combo in case devices changed
        PopulateDeviceCombo();
    }

    private void ButtonRemoveHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is HotkeyBinding binding)
        {
            ConfigModel.Instance.Settings.HotkeyBindings.Remove(binding);
            _ = ConfigModel.Instance.SaveSettingsAsync();
        }
    }
}