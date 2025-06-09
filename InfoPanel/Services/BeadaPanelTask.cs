using InfoPanel.BeadaPanel;
using InfoPanel.BeadaPanel.PanelLink;
using InfoPanel.BeadaPanel.StatusLink;
using InfoPanel.Extensions;
using InfoPanel.Models;
using InfoPanel.Services;
using InfoPanel.Utils;
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

namespace InfoPanel
{
    public sealed class BeadaPanelTask : BackgroundTask
    {
        private static readonly Lazy<BeadaPanelTask> _instance = new(() => new BeadaPanelTask());
        
        private readonly ConcurrentDictionary<string, BeadaPanelDeviceTask> _deviceTasks = new();

        public static BeadaPanelTask Instance => _instance.Value;

        private BeadaPanelTask() { }


        public async Task<List<BeadaPanelDevice>> DiscoverDevicesAsync()
        {
            var discoveredDevices = new List<BeadaPanelDevice>();
            
            try
            {
                int vendorId = 0x4e58;
                int productId = 0x1001;

                var allDevices = UsbDevice.AllDevices;
                Trace.WriteLine($"BeadaPanel Discovery: Scanning {allDevices.Count} USB devices for VID={vendorId:X4} PID={productId:X4}");

                int deviceIndex = 1;
                foreach (UsbRegistry deviceReg in allDevices)
                {
                    if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                    {
                        Trace.WriteLine($"BeadaPanel Discovery: Found matching device - FullName: '{deviceReg.FullName}', DevicePath: '{deviceReg.DevicePath}', SymbolicName: '{deviceReg.SymbolicName}'");
                        
                        try
                        {
                            // Create initial device with USB path as fallback identifier
                            var usbPath = deviceReg.DevicePath ?? $"USB_{deviceReg.DeviceInterfaceGuids}_{deviceIndex}";
                            var tempSerialNumber = deviceReg.DevicePath ?? deviceReg.DeviceInterfaceGuids.ToString();
                            
                            var device = new BeadaPanelDevice(
                                serialNumber: tempSerialNumber,
                                usbPath: usbPath,
                                name: $"BeadaPanel Device {deviceIndex}"
                            );

                            // Try to query StatusLink for detailed device information
                            var panelInfo = await QueryDeviceStatusLinkAsync(deviceReg);
                            if (panelInfo != null)
                            {
                                device.UpdateFromStatusLink(panelInfo);
                                Trace.WriteLine($"StatusLink Query Success: {device.Name} - Hardware Serial: {device.HardwareSerialNumber}, Model: {device.ModelName}");
                            }
                            else
                            {
                                // Fallback naming when StatusLink is not available
                                var deviceName = !string.IsNullOrEmpty(deviceReg.FullName) && deviceReg.FullName.Trim() != ""
                                    ? $"BeadaPanel {deviceReg.FullName} #{deviceIndex}"
                                    : $"BeadaPanel Device {deviceIndex}";
                                device.Name = deviceName;
                                Trace.WriteLine($"StatusLink Query Failed: Using USB-based identification for {device.Name}");
                            }

                            discoveredDevices.Add(device);
                            Trace.WriteLine($"Discovered BeadaPanel device: {device.Name} (ID Method: {device.IdentificationMethod}) at {device.UsbPath}");
                            
                            deviceIndex++;
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
            Trace.WriteLine("BeadaPanel: Starting multi-device mode with auto-enumeration");
            
            int autoEnumerationCounter = 0;
            const int AutoEnumerationInterval = 40; // Every 10 seconds (40 * 250ms)
            
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
                    
                    // Auto-enumeration every 10 seconds
                    if (autoEnumerationCounter <= 0)
                    {
                        await PerformAutoEnumeration(settings);
                        autoEnumerationCounter = AutoEnumerationInterval;
                    }
                    autoEnumerationCounter--;

                    // Start enabled devices that aren't running
                    var enabledDevices = settings.BeadaPanelDevices.Where(d => d.Enabled).ToList();
                    
                    foreach (var device in enabledDevices)
                    {
                        if (!IsDeviceRunning(device.Id))
                        {
                            StartDevice(device);
                        }
                    }

                    // Stop devices that are no longer enabled
                    var runningDeviceIds = _deviceTasks.Keys.ToList();
                    foreach (var deviceId in runningDeviceIds)
                    {
                        if (!enabledDevices.Any(d => d.Id == deviceId))
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

        private async Task PerformAutoEnumeration(Settings settings)
        {
            try
            {
                Trace.WriteLine("BeadaPanel: Performing auto-enumeration");
                var discoveredDevices = await DiscoverDevicesAsync();
                
                foreach (var discoveredDevice in discoveredDevices)
                {
                    var existingDevice = FindMatchingDevice(settings.BeadaPanelDevices, discoveredDevice);

                    if (existingDevice == null)
                    {
                        // Auto-add new devices (disabled by default)
                        discoveredDevice.Enabled = false;
                        discoveredDevice.ProfileGuid = settings.BeadaPanelDevices.Count > 0 
                            ? settings.BeadaPanelDevices.First().ProfileGuid 
                            : ConfigModel.Instance.Profiles.FirstOrDefault()?.Guid ?? Guid.Empty;

                        settings.BeadaPanelDevices.Add(discoveredDevice);
                        Trace.WriteLine($"Auto-enumerated new BeadaPanel device: {discoveredDevice.Name} (disabled by default)");
                    }
                    else
                    {
                        // Update existing device with new information
                        UpdateExistingDevice(existingDevice, discoveredDevice);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"BeadaPanel: Error during auto-enumeration: {ex.Message}");
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
                if (serialMatch != null) return serialMatch;
            }

            // Priority 2: Match by model fingerprint (model + firmware + resolution)
            if (discoveredDevice.ModelType.HasValue && discoveredDevice.FirmwareVersion > 0)
            {
                var fingerprintMatch = existingDevices.FirstOrDefault(d => 
                    d.ModelType == discoveredDevice.ModelType &&
                    d.FirmwareVersion == discoveredDevice.FirmwareVersion &&
                    d.NativeResolutionX == discoveredDevice.NativeResolutionX &&
                    d.NativeResolutionY == discoveredDevice.NativeResolutionY);
                if (fingerprintMatch != null) return fingerprintMatch;
            }

            // Priority 3: Match by USB path (fallback, unreliable across reboots)
            if (!string.IsNullOrEmpty(discoveredDevice.UsbPath))
            {
                var usbPathMatch = existingDevices.FirstOrDefault(d => d.UsbPath == discoveredDevice.UsbPath);
                if (usbPathMatch != null) return usbPathMatch;
            }

            // Priority 4: Match by legacy serial number (backward compatibility)
            if (!string.IsNullOrEmpty(discoveredDevice.SerialNumber))
            {
                var legacySerialMatch = existingDevices.FirstOrDefault(d => d.SerialNumber == discoveredDevice.SerialNumber);
                if (legacySerialMatch != null) return legacySerialMatch;
            }

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