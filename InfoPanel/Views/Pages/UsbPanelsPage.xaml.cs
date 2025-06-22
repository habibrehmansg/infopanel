using InfoPanel.BeadaPanel;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.TuringPanel;
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

namespace InfoPanel.Views.Pages;

/// <summary>
/// Interaction logic for UsbPanelsPage.xaml
/// </summary>
public partial class UsbPanelsPage : Page
{
    private static readonly ILogger Logger = Log.ForContext<UsbPanelsPage>();
    public UsbPanelsViewModel ViewModel { get; }

    private static bool deviceInserted = false;
    private static bool deviceRemoved = false;

    public UsbPanelsPage(UsbPanelsViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = this;
        InitializeComponent();
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