using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Serilog;
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

            foreach (var (vid, pid) in ThermalrightPanelModelDatabase.SupportedDevices)
            {
                var modelInfo = ThermalrightPanelModelDatabase.GetModelByVidPid(vid, pid);
                if (modelInfo?.TransportType == ThermalrightTransportType.Hid)
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
