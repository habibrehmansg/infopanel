using InfoPanel.BeadaPanel;
using InfoPanel.BeadaPanel.PanelLink;
using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.Utils;
using InfoPanel.ViewModels;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace InfoPanel
{
    public sealed class BeadaPanelTask : BackgroundTask
    {
        private static readonly Lazy<BeadaPanelTask> _instance = new(() => new BeadaPanelTask());
        
        private readonly ConcurrentDictionary<string, BeadaPanelDeviceTask> _deviceTasks = new();

        public static BeadaPanelTask Instance => _instance.Value;

        private BeadaPanelTask() { }


        public async Task<Dictionary<string, BeadaPanelInfo>> DiscoverDevicesAsync()
        {
            var discoveredDevices = new Dictionary<string, BeadaPanelInfo>();
            
            try
            {
                int vendorId = 0x4e58;
                int productId = 0x1001;

                var allDevices = UsbDevice.AllDevices;
                Trace.WriteLine($"BeadaPanel Discovery: Scanning {allDevices.Count} USB devices for VID={vendorId:X4} PID={productId:X4}");

                foreach (UsbRegistry deviceReg in allDevices)
                {
                    if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                    {
                        Trace.WriteLine($"BeadaPanel Discovery: Found matching device - FullName: '{deviceReg.FullName}', DevicePath: '{deviceReg.DevicePath}', SymbolicName: '{deviceReg.SymbolicName}'");

                        try
                        {
                            // Try to query StatusLink for detailed device information
                            var panelInfo = await QueryDeviceStatusLinkAsync(deviceReg);
                            if (panelInfo != null && BeadaPanelModelDatabase.Models.ContainsKey(panelInfo.Model))
                            {
                                discoveredDevices[deviceReg.DevicePath] = panelInfo;
                                Trace.WriteLine($"Discovered BeadaPanel device: {panelInfo}");
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
            catch (Exception ex)
            {
                Trace.WriteLine($"Error during BeadaPanel device discovery: {ex.Message}");
            }

            Trace.WriteLine($"BeadaPanel Discovery: Complete - Found {discoveredDevices.Count} devices total");
            return discoveredDevices;
        }

        private async Task<BeadaPanelInfo?> QueryDeviceStatusLinkAsync(UsbRegistry deviceReg)
        {
            UsbDevice? usbDevice = null;
            try
            {
                // Try to open the specific device
                usbDevice = deviceReg.Device;
                if (usbDevice == null)
                {
                    Trace.WriteLine($"StatusLink Query: Could not open USB device {deviceReg.DevicePath}");
                    return null;
                }

                if (usbDevice is IUsbDevice wholeUsbDevice)
                {
                    wholeUsbDevice.SetConfiguration(1);
                    wholeUsbDevice.ClaimInterface(0);
                }

                // Send StatusLink GetPanelInfo request
                var infoMessage = new StatusLinkMessage
                {
                    Type = StatusLinkMessageType.GetPanelInfo
                };

                using var writer = usbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);
                var writeResult = writer.Write(infoMessage.ToBuffer(), 1000, out int _);
                
                if (writeResult != ErrorCode.None)
                {
                    Trace.WriteLine($"StatusLink Query: Write failed with error {writeResult}");
                    return null;
                }

                // Read response
                byte[] responseBuffer = new byte[100];
                using var reader = usbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
                var readResult = reader.Read(responseBuffer, 1000, out int bytesRead);

                if (readResult != ErrorCode.None || bytesRead == 0)
                {
                    Trace.WriteLine($"StatusLink Query: Read failed with error {readResult}, bytes read: {bytesRead}");
                    return null;
                }

                // Parse panel info
                var panelInfo = BeadaPanelParser.ParsePanelInfoResponse(responseBuffer);
                if (panelInfo != null)
                {
                    Trace.WriteLine($"StatusLink Query: Successfully parsed panel info for serial {panelInfo.SerialNumber}");
                }

                return panelInfo;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"StatusLink Query Exception: {ex.Message}");
                return null;
            }
            finally
            {
                try
                {
                    if (usbDevice is IUsbDevice wholeUsbDevice)
                    {
                        wholeUsbDevice.ReleaseInterface(0);
                    }
                    usbDevice?.Close();
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"StatusLink Query Cleanup Exception: {ex.Message}");
                }
            }
        }

        public void StartDevice(BeadaPanelDevice device)
        {
            if (_deviceTasks.ContainsKey(device.Id))
            {
                Trace.WriteLine($"BeadaPanel device {device.Name} is already running");
                return;
            }

            var deviceTask = new BeadaPanelDeviceTask(device);
            if (_deviceTasks.TryAdd(device.Id, deviceTask))
            {
                _ = deviceTask.StartAsync();
                Trace.WriteLine($"Started BeadaPanel device {device.Name}");
            }
        }

        public async Task StopDevice(string deviceId)
        {
            if (_deviceTasks.TryRemove(deviceId, out var deviceTask))
            {
                await deviceTask.StopAsync();
                //deviceTask.Dispose();
                SharedModel.Instance.RemoveBeadaPanelDeviceStatus(deviceId);
                Trace.WriteLine($"Stopped BeadaPanel device {deviceId}");
            }
        }

        public async Task StopAllDevices()
        {
            var tasks = new List<Task>();
            
            foreach (var kvp in _deviceTasks.ToList())
            {
                if (_deviceTasks.TryRemove(kvp.Key, out var deviceTask))
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await deviceTask.StopAsync();
                        //deviceTask.Dispose();
                        SharedModel.Instance.RemoveBeadaPanelDeviceStatus(kvp.Key);
                    }));
                }
            }

            await Task.WhenAll(tasks);
            Trace.WriteLine("Stopped all BeadaPanel devices");
        }

        public bool IsDeviceRunning(string deviceId)
        {
            return _deviceTasks.ContainsKey(deviceId);
        }

        /// <summary>
        /// Notifies a running device task of configuration changes
        /// </summary>
        /// <param name="deviceConfig">The updated device configuration</param>
        public void NotifyDeviceConfigurationChanged(BeadaPanelDeviceConfig deviceConfig)
        {
            var deviceId = GetConfigId(deviceConfig);
            if (_deviceTasks.TryGetValue(deviceId, out var deviceTask))
            {
                deviceTask.UpdateConfiguration(deviceConfig);
                Trace.WriteLine($"BeadaPanel: Notified device task {deviceId} of configuration changes");
            }
        }

        protected override async Task DoWorkAsync(CancellationToken token)
        {
            await Task.Delay(300, token);

            try
            {
                var settings = ConfigModel.Instance.Settings;
                
                Trace.WriteLine($"BeadaPanel: DoWorkAsync starting - MultiDeviceMode: {settings.BeadaPanelMultiDeviceMode}");

                if (settings.BeadaPanelMultiDeviceMode)
                {
                    await RunMultiDeviceMode(token);
                }
                else
                {
                    Trace.WriteLine("BeadaPanel: Multi-device mode is disabled. No devices will be started.");
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine($"BeadaPanel: Error in DoWorkAsync: {e.Message}");
            }
        }

        private async Task RunMultiDeviceMode(CancellationToken token)
        {
            Trace.WriteLine("BeadaPanel: Starting multi-device mode");
            
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = ConfigModel.Instance.Settings;
                    
                    // Exit if multi-device mode was turned off
                    if (!settings.BeadaPanelMultiDeviceMode)
                    {
                        Trace.WriteLine("BeadaPanel: Multi-device mode turned off, exiting loop");
                        break;
                    }

                    // Start enabled devices that aren't running
                    var enabledConfigs = settings.BeadaPanelDevices.Where(d => d.Enabled).ToList();
                    
                    foreach (var config in enabledConfigs)
                    {
                        var configId = GetConfigId(config);
                        if (!IsDeviceRunning(configId))
                        {
                            var runtimeDevice = config.ToDevice();
                            if (runtimeDevice != null)
                            {
                                StartDevice(runtimeDevice);
                            }
                            else
                            {
                                Trace.WriteLine($"BeadaPanel: Skipping device with unknown model type: {config.ModelType}");
                            }
                        }
                    }

                    // Stop devices that are no longer enabled
                    var runningDeviceIds = _deviceTasks.Keys.ToList();
                    foreach (var deviceId in runningDeviceIds)
                    {
                        if (!enabledConfigs.Any(c => GetConfigId(c) == deviceId))
                        {
                            await StopDevice(deviceId);
                        }
                    }

                    await Task.Delay(250, token);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"BeadaPanel: Error in RunMultiDeviceMode: {ex.Message}");
                    await Task.Delay(1000, token); // Wait longer on error
                }
            }
        }

        private string GetConfigId(BeadaPanelDeviceConfig config)
        {
            // Generate a stable ID based on the device's unique identifier
            return config.IdentificationMethod switch
            {
                DeviceIdentificationMethod.HardwareSerial => config.SerialNumber,
                DeviceIdentificationMethod.ModelFingerprint => GetModelFingerprint(config),
                DeviceIdentificationMethod.UsbPath => config.UsbPath,
                _ => config.UsbPath
            };
        }

        private string GetModelFingerprint(BeadaPanelDeviceConfig config)
        {
            if (config.ModelType.HasValue && BeadaPanelModelDatabase.Models.TryGetValue(config.ModelType.Value, out var modelInfo))
            {
                return $"{config.ModelType}";
            }
            return config.UsbPath; // Fallback
        }


        public override async Task StopAsync(bool shutdown = false)
        {
            await StopAllDevices();
            await base.StopAsync(shutdown);
        }

        //protected override async ValueTask DisposeAsync()
        //{
        //    await StopAllDevices();
        //    await base.DisposeAsync();
        //}
    }
}