using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.ThermalrightPanel
{
    public static class ThermalrightPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ThermalrightPanelHelper));

        /// <summary>
        /// Scans for all connected Thermalright panel devices.
        /// WinUSB models are discovered via LibUsbDotNet, HID models via HidSharp.
        /// </summary>
        /// <returns>List of discovered Thermalright panel device info</returns>
        public static List<ThermalrightPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<ThermalrightPanelDiscoveryInfo>();

            // Partition supported devices by transport type
            var winUsbDevices = new List<(int Vid, int Pid)>();
            var hidDevices = new List<(int Vid, int Pid)>();
            bool hasScsiDevices = false;

            foreach (var (vid, pid) in ThermalrightPanelModelDatabase.SupportedDevices)
            {
                bool isScsi = ThermalrightPanelModelDatabase.Models.Values
                    .Any(m => m.VendorId == vid && m.ProductId == pid && m.TransportType == ThermalrightTransportType.Scsi);
                if (isScsi)
                {
                    hasScsiDevices = true;
                    continue;
                }

                // Check if any model with this VID/PID uses HID transport
                // (many HID models share the same VID/PID, so GetModelByVidPid returns null for ambiguous matches)
                bool isHid = ThermalrightPanelModelDatabase.Models.Values
                    .Any(m => m.VendorId == vid && m.ProductId == pid && m.TransportType == ThermalrightTransportType.Hid);
                if (isHid)
                    hidDevices.Add((vid, pid));
                else
                    winUsbDevices.Add((vid, pid));
            }

            // Scan WinUSB devices via LibUsbDotNet
            foreach (var (vendorId, productId) in winUsbDevices)
            {
                Logger.Information("ThermalrightPanelHelper: Scanning for WinUSB devices VID={VendorId:X4} PID={ProductId:X4}",
                    vendorId, productId);

                foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
                {
                    if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                    {
                        var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;
                        var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string;

                        Logger.Information("ThermalrightPanelHelper: WinUSB device found - Path: {Path}", deviceReg.DevicePath);

                        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceLocation))
                        {
                            Logger.Warning("ThermalrightPanelHelper: Found device but missing DeviceID or LocationInformation");
                            continue;
                        }

                        // Check driver before attempting to open the device
                        var driverIssue = CheckDriverService(deviceReg);

                        ThermalrightPanelModelInfo? modelInfo = null;
                        if (driverIssue == null)
                        {
                            modelInfo = ThermalrightPanelModelDatabase.GetModelByVidPid(vendorId, productId);

                            // For ambiguous VID/PID (e.g. ChiZhu 87AD:70DB shared by ~40 models),
                            // do a quick init probe to determine the exact model from PM/SUB/identifier.
                            if (modelInfo == null)
                            {
                                modelInfo = ProbeWinUsbModel(deviceReg);
                            }
                        }
                        else
                        {
                            Logger.Warning("ThermalrightPanelHelper: Skipping probe for device at {Location} — wrong driver: {Driver}",
                                deviceLocation, driverIssue);
                        }

                        var discoveryInfo = new ThermalrightPanelDiscoveryInfo
                        {
                            DeviceId = deviceId,
                            DeviceLocation = deviceLocation,
                            DevicePath = deviceReg.DevicePath,
                            VendorId = vendorId,
                            ProductId = productId,
                            Model = modelInfo?.Model ?? ThermalrightPanelModel.Unknown,
                            ModelInfo = modelInfo,
                            DriverIssue = driverIssue
                        };

                        Logger.Information("ThermalrightPanelHelper: Found {Model} at {Location}{DriverInfo}",
                            modelInfo?.Name ?? "Unknown", deviceLocation,
                            driverIssue != null ? $" (wrong driver: {driverIssue})" : "");

                        devices.Add(discoveryInfo);
                    }
                }
            }

            // Scan HID devices via HidSharp
            foreach (var (vendorId, productId) in hidDevices)
            {
                Logger.Information("ThermalrightPanelHelper: Scanning for HID devices VID={VendorId:X4} PID={ProductId:X4}",
                    vendorId, productId);

                var hidDeviceList = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
                foreach (var hidDevice in hidDeviceList)
                {
                    var modelInfo = ThermalrightPanelModelDatabase.GetModelByVidPid(vendorId, productId);

                    // When multiple models share the same VID/PID (e.g. all Trofeo HID panels on 0416:5302),
                    // GetModelByVidPid returns null. Use the first matching model so the saved Model enum
                    // resolves to a valid ModelInfo with the correct transport/protocol/VID/PID.
                    // The actual model will be determined from the PM byte during HID init.
                    if (modelInfo == null)
                    {
                        modelInfo = ThermalrightPanelModelDatabase.Models.Values
                            .FirstOrDefault(m => m.VendorId == vendorId && m.ProductId == productId);
                    }

                    // Synthesize a stable device ID and location for HID devices
                    var deviceId = $"HID\\VID_{vendorId:X4}&PID_{productId:X4}";
                    var deviceLocation = hidDevice.DevicePath;

                    var discoveryInfo = new ThermalrightPanelDiscoveryInfo
                    {
                        DeviceId = deviceId,
                        DeviceLocation = deviceLocation,
                        DevicePath = hidDevice.DevicePath,
                        VendorId = vendorId,
                        ProductId = productId,
                        Model = modelInfo?.Model ?? ThermalrightPanelModel.Unknown,
                        ModelInfo = modelInfo
                    };

                    Logger.Information("ThermalrightPanelHelper: Found HID {Model} at {Path}",
                        modelInfo?.Name ?? "Unknown", hidDevice.DevicePath);

                    devices.Add(discoveryInfo);
                }
            }

            // Scan SCSI devices via IOCTL_STORAGE_QUERY_PROPERTY on PhysicalDrive0-15
            if (hasScsiDevices)
            {
                Logger.Information("ThermalrightPanelHelper: Scanning for SCSI LCD devices");

                try
                {
                    var scsiDeviceInfos = ScsiPanelDevice.FindDevices();
                    foreach (var scsiInfo in scsiDeviceInfos)
                    {
                        var modelInfo = ThermalrightPanelModelDatabase.Models.Values
                            .FirstOrDefault(m => m.TransportType == ThermalrightTransportType.Scsi);

                        var deviceId = $"SCSI\\{scsiInfo.VendorId}_{scsiInfo.ProductId}";
                        var deviceLocation = scsiInfo.DevicePath;

                        var discoveryInfo = new ThermalrightPanelDiscoveryInfo
                        {
                            DeviceId = deviceId,
                            DeviceLocation = deviceLocation,
                            DevicePath = scsiInfo.DevicePath,
                            VendorId = ThermalrightPanelModelDatabase.SCSI_VENDOR_ID,
                            ProductId = ThermalrightPanelModelDatabase.SCSI_PRODUCT_ID,
                            Model = modelInfo?.Model ?? ThermalrightPanelModel.Unknown,
                            ModelInfo = modelInfo
                        };

                        Logger.Information("ThermalrightPanelHelper: Found SCSI {Model} at {Path}",
                            modelInfo?.Name ?? "Unknown", scsiInfo.DevicePath);

                        devices.Add(discoveryInfo);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "ThermalrightPanelHelper: Error scanning SCSI devices");
                }
            }

            Logger.Information("ThermalrightPanelHelper: Scan complete, found {Count} device(s)", devices.Count);
            return devices;
        }

        /// <summary>
        /// Checks whether a WinUSB device has the correct driver (WinUSB) by querying the
        /// registry Service value. Returns the driver name if it's wrong (e.g. "libusb0", "libusbK"),
        /// or null if the driver is correct or cannot be determined.
        /// </summary>
        private static string? CheckDriverService(UsbRegistry deviceReg)
        {
            try
            {
                var devicePath = deviceReg.DevicePath;
                if (string.IsNullOrEmpty(devicePath))
                    return null;

                // DevicePath format: \\?\usb#vid_87ad&pid_70db#serial#{guid}
                // We need: USB\VID_87AD&PID_70DB\serial
                var match = Regex.Match(devicePath, @"usb#(vid_[0-9a-f]+&pid_[0-9a-f]+)#([^#]+)#", RegexOptions.IgnoreCase);
                if (!match.Success)
                    return null;

                var vidPid = match.Groups[1].Value.ToUpperInvariant();
                var serial = match.Groups[2].Value;
                var regPath = $@"SYSTEM\CurrentControlSet\Enum\USB\{vidPid}\{serial}";

                using var key = Registry.LocalMachine.OpenSubKey(regPath);
                var service = key?.GetValue("Service") as string;

                if (string.IsNullOrEmpty(service))
                    return null;

                if (service.Equals("WinUSB", StringComparison.OrdinalIgnoreCase))
                    return null; // Correct driver

                Logger.Warning("ThermalrightPanelHelper: Device at {Path} has wrong driver: {Driver} (expected WinUSB)",
                    devicePath, service);
                return service;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "ThermalrightPanelHelper: Could not check driver service");
                return null;
            }
        }

        /// <summary>
        /// Opens a WinUSB device, sends a ChiZhu init command, and reads the response
        /// to determine the exact model from PM/SUB bytes and identifier string.
        /// Runs with a 5-second timeout to prevent hanging the scan.
        /// Returns null if the probe fails (device busy, booting, timeout, or not a ChiZhu device).
        /// </summary>
        private static ThermalrightPanelModelInfo? ProbeWinUsbModel(UsbRegistry deviceReg)
        {
            const int PROBE_TIMEOUT_MS = 5000;

            try
            {
                var probeTask = Task.Run(() => ProbeWinUsbModelInner(deviceReg));
                if (probeTask.Wait(PROBE_TIMEOUT_MS))
                    return probeTask.Result;

                Logger.Warning("ThermalrightPanelHelper: Probe timed out after {Timeout}ms", PROBE_TIMEOUT_MS);
                return null;
            }
            catch (AggregateException ae)
            {
                Logger.Debug(ae.InnerException ?? ae, "ThermalrightPanelHelper: Probe failed");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "ThermalrightPanelHelper: Probe failed");
                return null;
            }
        }

        private static ThermalrightPanelModelInfo? ProbeWinUsbModelInner(UsbRegistry deviceReg)
        {
            using var usbDevice = deviceReg.Device;
            if (usbDevice == null)
            {
                Logger.Debug("ThermalrightPanelHelper: Probe could not open device");
                return null;
            }

            if (usbDevice is IUsbDevice wholeUsbDevice)
            {
                wholeUsbDevice.SetConfiguration(1);
                wholeUsbDevice.ClaimInterface(0);
            }

            // Find endpoints
            WriteEndpointID writeEp = WriteEndpointID.Ep01;
            ReadEndpointID readEp = ReadEndpointID.Ep01;

            foreach (var config in usbDevice.Configs)
            {
                foreach (var iface in config.InterfaceInfoList)
                {
                    foreach (var ep in iface.EndpointInfoList)
                    {
                        var addr = (byte)ep.Descriptor.EndpointID;
                        if ((addr & 0x80) == 0)
                            writeEp = (WriteEndpointID)addr;
                        else
                            readEp = (ReadEndpointID)addr;
                    }
                }
            }

            using var writer = usbDevice.OpenEndpointWriter(writeEp);
            using var reader = usbDevice.OpenEndpointReader(readEp);

            // Build ChiZhu init command: magic 12345678 + zeros + 0x01 at offset 56
            var initCommand = new byte[64];
            initCommand[0] = 0x12;
            initCommand[1] = 0x34;
            initCommand[2] = 0x56;
            initCommand[3] = 0x78;
            BitConverter.GetBytes(1).CopyTo(initCommand, 56);

            var ec = writer.Write(initCommand, 3000, out _);
            if (ec != ErrorCode.None)
            {
                Logger.Debug("ThermalrightPanelHelper: Probe write failed: {Error}", ec);
                return null;
            }

            var response = new byte[1024];
            ec = reader.Read(response, 3000, out int bytesRead);
            if (ec != ErrorCode.None || bytesRead < 12)
            {
                Logger.Debug("ThermalrightPanelHelper: Probe read failed: {Error}, bytes={Bytes}", ec, bytesRead);
                return null;
            }

            // Boot indicator: A1A2A3A4 — device not ready
            if (bytesRead >= 8 &&
                response[4] == 0xA1 && response[5] == 0xA2 &&
                response[6] == 0xA3 && response[7] == 0xA4)
            {
                Logger.Debug("ThermalrightPanelHelper: Probe: device is booting");
                return null;
            }

            byte? pm = bytesRead >= 25 ? response[24] : null;
            byte? sub = bytesRead >= 29 ? response[28] : null;

            Logger.Information("ThermalrightPanelHelper: Probe response PM=0x{PM:X2} SUB=0x{SUB:X2}",
                pm ?? 0, sub ?? 0);

            // Try PM+SUB table first
            if (pm.HasValue && sub.HasValue)
            {
                var model = ThermalrightPanelModelDatabase.GetModelByChiZhuPM(pm.Value, sub.Value);
                if (model != null) return model;
            }

            // Fall back to identifier string at bytes 4-11
            var identifier = Encoding.ASCII.GetString(response, 4, 8).TrimEnd('\0');
            Logger.Information("ThermalrightPanelHelper: Probe identifier: {Id}", identifier);
            return ThermalrightPanelModelDatabase.GetModelByIdentifier(identifier, sub);
        }
    }

    public class ThermalrightPanelDiscoveryInfo
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceLocation { get; init; } = string.Empty;
        public string DevicePath { get; init; } = string.Empty;
        public int VendorId { get; init; }
        public int ProductId { get; init; }
        public ThermalrightPanelModel Model { get; init; }
        public ThermalrightPanelModelInfo? ModelInfo { get; init; }
        public string? DriverIssue { get; init; }
    }
}
