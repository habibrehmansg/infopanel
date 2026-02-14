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
        /// Tries both USB (LibUsbDotNet) and HID (HidSharp) discovery.
        /// </summary>
        /// <returns>List of discovered Thermalright panel device info</returns>
        public static List<ThermalrightPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<ThermalrightPanelDiscoveryInfo>();
            int vendorId = ThermalrightPanelModelDatabase.THERMALRIGHT_VENDOR_ID;
            int productId = ThermalrightPanelModelDatabase.THERMALRIGHT_PRODUCT_ID;

            // Try HID discovery first
            Logger.Information("ThermalrightPanelHelper: Scanning for HID devices VID={VendorId:X4} PID={ProductId:X4}",
                vendorId, productId);

            try
            {
                var hidDevices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
                Logger.Information("ThermalrightPanelHelper: Found {Count} HID devices", hidDevices.Count);

                foreach (var hidDevice in hidDevices)
                {
                    Logger.Information("ThermalrightPanelHelper: HID device path: {Path}", hidDevice.DevicePath);
                    Logger.Information("ThermalrightPanelHelper: HID MaxInput={MaxIn}, MaxOutput={MaxOut}, MaxFeature={MaxFeat}",
                        hidDevice.GetMaxInputReportLength(),
                        hidDevice.GetMaxOutputReportLength(),
                        hidDevice.GetMaxFeatureReportLength());
                }
            }
            catch (System.Exception ex)
            {
                Logger.Warning(ex, "ThermalrightPanelHelper: HID scan failed");
            }

            // Then try USB discovery
            Logger.Information("ThermalrightPanelHelper: Scanning for USB devices VID={VendorId:X4} PID={ProductId:X4}",
                vendorId, productId);

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid == vendorId && deviceReg.Pid == productId)
                {
                    var deviceId = deviceReg.DeviceProperties["DeviceID"] as string;
                    var deviceLocation = deviceReg.DeviceProperties["LocationInformation"] as string;

                    Logger.Information("ThermalrightPanelHelper: USB device found - Path: {Path}", deviceReg.DevicePath);

                    if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceLocation))
                    {
                        Logger.Warning("ThermalrightPanelHelper: Found device but missing DeviceID or LocationInformation");
                        continue;
                    }

                    // Get model info based on VID/PID
                    var modelInfo = ThermalrightPanelModelDatabase.GetModelByVidPid(vendorId, productId);

                    if (modelInfo == null)
                    {
                        Logger.Warning("ThermalrightPanelHelper: Unknown model for VID:{Vid:X4} PID:{Pid:X4}", vendorId, productId);
                        continue;
                    }

                    var discoveryInfo = new ThermalrightPanelDiscoveryInfo
                    {
                        DeviceId = deviceId,
                        DeviceLocation = deviceLocation,
                        DevicePath = deviceReg.DevicePath,
                        Model = modelInfo.Model,
                        ModelInfo = modelInfo
                    };

                    Logger.Information("ThermalrightPanelHelper: Found {Model} at {Location}",
                        modelInfo.Name, deviceLocation);

                    devices.Add(discoveryInfo);
                }
            }

            Logger.Information("ThermalrightPanelHelper: Scan complete, found {Count} devices", devices.Count);
            return devices;
        }
    }

    public class ThermalrightPanelDiscoveryInfo
    {
        public string DeviceId { get; init; } = string.Empty;
        public string DeviceLocation { get; init; } = string.Empty;
        public string DevicePath { get; init; } = string.Empty;
        public ThermalrightPanelModel Model { get; init; }
        public ThermalrightPanelModelInfo? ModelInfo { get; init; }
    }
}
