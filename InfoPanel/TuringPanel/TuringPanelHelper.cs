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
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.TuringPanel
{
    internal partial class TuringPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(TuringPanelHelper));
        private static readonly SemaphoreSlim _semaphore = new(1, 1);

        public static async Task<List<TuringPanelDevice>> GetUsbDevices()
        {
            await _semaphore.WaitAsync();

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
                    else
                    {
                        Log.Information("Unknown USB device {VID} {PID} {Name}", deviceReg.Vid, deviceReg.Pid, deviceReg.FullName);
                    }
                }

                return devices;

            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error getting USB devices");
                return [];
            }
            finally
            {
                _semaphore.Release();
            }
        }


        public static async Task<List<TuringPanelDevice>> GetSerialDevices()
        {
            await _semaphore.WaitAsync();
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
                    var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SerialPort");
                    foreach (ManagementObject queryObj in searcher.Get().Cast<ManagementObject>())
                    {
                        string? comPort = queryObj["DeviceID"]?.ToString();
                        string? pnpDeviceId = queryObj["PNPDeviceID"]?.ToString();
                        if (comPort == null || pnpDeviceId == null || !TryParseVidPid(pnpDeviceId, out var vid, out var pid))
                        {
                            continue; // Skip devices that are not CH340 USB to Serial converters
                        }

                        foreach (var kv in TuringPanelModelDatabase.Models)
                        {
                            if (kv.Value.VendorId == vid && kv.Value.ProductId == pid)
                            {
                                Logger.Information("Found Turing panel device: {Name} on {ComPort}", kv.Value.Name, comPort);

                                TuringPanelDevice device = new()
                                {
                                    DeviceId = pnpDeviceId,
                                    DeviceLocation = comPort,
                                    Model = kv.Key.ToString()
                                };

                                devices.Add(device);

                            }
                        }
                    }

                    Logger.Information("Found {Count} Turing panel devices", devices.Count);
                    return devices;
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "TuringPanelHelper: Error getting Turing panel devices");
                return [];
            }
            finally
            {
                _semaphore.Release();
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