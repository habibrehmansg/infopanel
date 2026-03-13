using AsyncKeyedLock;
using InfoPanel.Models;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InfoPanel.TuringPanel
{
    internal class TuringPanelDiscoveryResult
    {
        public List<TuringPanelDevice> Devices { get; init; } = [];
        public List<string> DriverWarnings { get; init; } = [];
    }

    internal partial class TuringPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(TuringPanelHelper));
        private static readonly AsyncNonKeyedLocker _lock = new(1);

        public static async Task<List<TuringPanelDevice>> GetUsbDevices()
        {
            using var _ = await _lock.LockAsync();
            try
            {
                List<TuringPanelDevice> devices = [];
                var allDevices = UsbDevice.AllDevices;

                foreach (UsbRegistry deviceReg in allDevices)
                {
                    if (TuringPanelModelDatabase.TryGetModelInfo(deviceReg.Vid, deviceReg.Pid, true, out var modelInfo))
                    {
                        if (deviceReg.DeviceProperties["DeviceID"] is string deviceId && deviceReg.DeviceProperties["LocationInformation"] is string deviceLocation)
                        {
                            Logger.Information("Found Turing panel device: {Name} at {Location} (ID: {DeviceId})",
                                modelInfo.Name, deviceLocation, deviceId);

                            TuringPanelDevice device = new()
                            {
                                DeviceId = deviceId,
                                DeviceLocation = deviceLocation,
                                Model = modelInfo.Model.ToString()
                            };

                            devices.Add(device);
                        }
                    }
                }

                return devices;

            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error getting USB devices");
                return [];
            }
        }


        public static async Task<TuringPanelDiscoveryResult> GetSerialDevices()
        {
            using var _ = await _lock.LockAsync();
            try
            {
                var wakeCount = await WakeSerialDevices();
                var attempts = 1;
                while (wakeCount > 0)
                {
                    await Task.Delay(1000); // Wait a bit before checking again
                    wakeCount = await WakeSerialDevices();
                    attempts++;
                    if (attempts >= 5)
                    {
                        Logger.Warning("Max attempts reached while waking devices.");
                        break;
                    }
                }

                Logger.Information("No more sleeping devices to wake. Proceeding to search for Turing panel devices.");

                return await Task.Run(() =>
                {
                    List<TuringPanelDevice> devices = [];
                    List<string> driverWarnings = [];
                    var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SerialPort");
                    var serialPorts = searcher.Get().Cast<ManagementObject>().ToList();

                    // Check for CT13INCH/CT21INCH companion ports via PnP entity search
                    // These may have WinUSB or serial drivers, so search all USB devices
                    var pnpSearcher = new ManagementObjectSearcher(
                        "SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE '%VID_1A86%'");
                    var pnpDevices = pnpSearcher.Get().Cast<ManagementObject>().ToList();

                    bool hasCt13InchSerial = serialPorts.Any(obj =>
                    {
                        string? pnp = obj["PNPDeviceID"]?.ToString();
                        return pnp != null && pnp.Contains("VID_1A86") && pnp.Contains("PID_CA11");
                    });
                    bool hasCt13InchPnp = pnpDevices.Any(obj =>
                    {
                        string? pnp = obj["PNPDeviceID"]?.ToString();
                        return pnp != null && pnp.Contains("VID_1A86") && pnp.Contains("PID_CA11");
                    });
                    bool hasCt13Inch = hasCt13InchSerial || hasCt13InchPnp;

                    if (hasCt13Inch)
                    {
                        Logger.Information("Detected CT13INCH identifier port (serial={Serial}, pnp={Pnp})", hasCt13InchSerial, hasCt13InchPnp);
                        if (hasCt13InchPnp && !hasCt13InchSerial)
                        {
                            Logger.Warning("CT13INCH companion port (1A86:CA11) has wrong driver — not visible as serial port. Install the CH340 serial driver.");
                            driverWarnings.Add("Shiny Snake companion port (CH340) has wrong USB driver. Install the CH340 serial driver for reliable operation.");
                        }
                    }

                    bool hasCt21InchSerial = serialPorts.Any(obj =>
                    {
                        string? pnp = obj["PNPDeviceID"]?.ToString();
                        return pnp != null && pnp.Contains("VID_1A86") && pnp.Contains("PID_CA21");
                    });
                    bool hasCt21InchPnp = pnpDevices.Any(obj =>
                    {
                        string? pnp = obj["PNPDeviceID"]?.ToString();
                        return pnp != null && pnp.Contains("VID_1A86") && pnp.Contains("PID_CA21");
                    });
                    bool hasCt21Inch = hasCt21InchSerial || hasCt21InchPnp;

                    if (hasCt21Inch)
                    {
                        Logger.Information("Detected CT21INCH identifier port (serial={Serial}, pnp={Pnp})", hasCt21InchSerial, hasCt21InchPnp);
                        if (hasCt21InchPnp && !hasCt21InchSerial)
                        {
                            Logger.Warning("CT21INCH companion port (1A86:CA21) has wrong driver — not visible as serial port. Install the CH340 serial driver.");
                            driverWarnings.Add("CT21INCH companion port (CH340) has wrong USB driver. Install the CH340 serial driver for reliable operation.");
                        }
                    }

                    foreach (ManagementObject queryObj in serialPorts)
                    {
                        string? comPort = queryObj["DeviceID"]?.ToString();
                        string? pnpDeviceId = queryObj["PNPDeviceID"]?.ToString();
                        if (comPort == null || pnpDeviceId == null || !TryParseVidPid(pnpDeviceId, out var vid, out var pid))
                        {
                            continue;
                        }

                        // Skip CT13INCH/CT21INCH CH340 companion ports from normal matching
                        if (vid == 0x1a86 && (pid == 0xca11 || pid == 0xca21))
                        {
                            continue;
                        }

                        foreach (var kv in TuringPanelModelDatabase.Models)
                        {
                            if (kv.Value.VendorId == vid && kv.Value.ProductId == pid && !kv.Value.IsUsbDevice)
                            {
                                var model = kv.Key;
                                // Override to 10.2" when CT13INCH is present
                                if (hasCt13Inch && vid == 0x0525 && pid == 0xa4a7)
                                {
                                    model = TuringPanelModel.REV_13INCH_USB;
                                }

                                var modelInfo = TuringPanelModelDatabase.Models[model];
                                Logger.Information("Found Turing panel device: {Name} on {ComPort}", modelInfo.Name, comPort);

                                TuringPanelDevice device = new()
                                {
                                    DeviceId = pnpDeviceId,
                                    DeviceLocation = comPort,
                                    Model = model.ToString()
                                };

                                devices.Add(device);
                                break;
                            }
                        }
                    }

                    Logger.Information("Found {Count} Turing panel devices", devices.Count);
                    return new TuringPanelDiscoveryResult { Devices = devices, DriverWarnings = driverWarnings };
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error getting Turing panel devices");
                return new TuringPanelDiscoveryResult();
            }
        }

        private static async Task<int> WakeSerialDevices()
        {
            try
            {
                return await Task.Run(() =>
                {
                    var count = 0;
                    var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SerialPort");
                    foreach (ManagementObject queryObj in searcher.Get().Cast<ManagementObject>())
                    {
                        string? comPort = queryObj["DeviceID"]?.ToString();
                        string? pnpDeviceId = queryObj["PNPDeviceID"]?.ToString();

                        if (comPort == null || pnpDeviceId == null || !pnpDeviceId.Contains("VID_1A86") || !pnpDeviceId.Contains("PID_5722"))
                        {
                            continue; // Skip devices that are not CH340 USB to Serial converters
                        }

                        try
                        {
                            using var serialPort = new SerialPort(comPort, 115200);
                            serialPort.Open();
                            serialPort.Close();
                        }catch (Exception ex)
                        {
                            Logger.Warning(ex, "TuringPanelHelper: Error opening device on {ComPort}", comPort);
                        }
                        count++;
                    }

                    Logger.Information("Found {Count} sleeping devices", count);

                    return count;
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error waking sleeping devices");
                return 0;
            }
        }

        private static bool TryParseVidPid(string pnpDeviceId, out int vid, out int pid)
        {
            vid = 0;
            pid = 0;
            var match = MyRegex().Match(pnpDeviceId);
            if (match.Success)
            {
                vid = Convert.ToInt32(match.Groups[1].Value, 16);
                pid = Convert.ToInt32(match.Groups[2].Value, 16);
                return true;
            }
            return false;
        }

        [GeneratedRegex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})")]
        private static partial Regex MyRegex();
    }
}