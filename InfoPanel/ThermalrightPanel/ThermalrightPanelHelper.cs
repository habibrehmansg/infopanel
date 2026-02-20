using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

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

                        var modelInfo = ThermalrightPanelModelDatabase.GetModelByVidPid(vendorId, productId);

                        var discoveryInfo = new ThermalrightPanelDiscoveryInfo
                        {
                            DeviceId = deviceId,
                            DeviceLocation = deviceLocation,
                            DevicePath = deviceReg.DevicePath,
                            VendorId = vendorId,
                            ProductId = productId,
                            Model = modelInfo?.Model ?? ThermalrightPanelModel.Unknown,
                            ModelInfo = modelInfo
                        };

                        Logger.Information("ThermalrightPanelHelper: Found {Model} at {Location}",
                            modelInfo?.Name ?? "Unknown", deviceLocation);

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
    }
}
