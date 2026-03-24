using HidSharp;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.ThermaltakePanel
{
    public class ThermaltakePanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = "";
        public string DevicePath { get; set; } = "";
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public ThermaltakePanelModel Model { get; set; }
        public ThermaltakePanelModelInfo? ModelInfo { get; set; }
    }

    public static class ThermaltakePanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(ThermaltakePanelHelper));

        private const int MIN_OUTPUT_REPORT_LENGTH = 1025; // 1024 data + 1 null report ID

        /// <summary>
        /// Scans for connected Thermaltake LCD panels via HidSharp.
        /// </summary>
        public static List<ThermaltakePanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<ThermaltakePanelDiscoveryInfo>();

            foreach (var (vendorId, productId) in ThermaltakePanelModelDatabase.SupportedDevices)
            {
                Logger.Information("ThermaltakePanelHelper: Scanning VID={Vid:X4} PID={Pid:X4}", vendorId, productId);

                var deviceList = DeviceList.Local;
                var hidDevices = deviceList.GetHidDevices(vendorId, productId).ToList();

                foreach (var hidDevice in hidDevices)
                {
                    try
                    {
                        if (hidDevice.GetMaxOutputReportLength() < MIN_OUTPUT_REPORT_LENGTH)
                        {
                            Logger.Debug("ThermaltakePanelHelper: Skipping {Path}, MaxOut={MaxOut}",
                                hidDevice.DevicePath, hidDevice.GetMaxOutputReportLength());
                            continue;
                        }

                        var modelInfo = ThermaltakePanelModelDatabase.GetModelByVidPid(vendorId, productId);
                        if (modelInfo == null) continue;

                        // Extract DeviceId from path (e.g., VID_264A&PID_2347\SERIAL)
                        string deviceId = ExtractDeviceId(hidDevice.DevicePath);
                        string deviceLocation = ExtractDeviceLocation(hidDevice.DevicePath);

                        Logger.Information("ThermaltakePanelHelper: Found {Model} at {Path}",
                            modelInfo.Name, hidDevice.DevicePath);

                        devices.Add(new ThermaltakePanelDiscoveryInfo
                        {
                            DeviceId = deviceId,
                            DeviceLocation = deviceLocation,
                            DevicePath = hidDevice.DevicePath,
                            VendorId = vendorId,
                            ProductId = productId,
                            Model = modelInfo.Model,
                            ModelInfo = modelInfo,
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning(ex, "ThermaltakePanelHelper: Error scanning {Path}", hidDevice.DevicePath);
                    }
                }
            }

            Logger.Information("ThermaltakePanelHelper: Found {Count} device(s)", devices.Count);
            return devices;
        }

        private static string ExtractDeviceId(string devicePath)
        {
            // HID path: \\?\hid#vid_264a&pid_2347#serial#{guid}
            try
            {
                var parts = devicePath.Split('#');
                if (parts.Length >= 3)
                    return $"{parts[1]}\\{parts[2]}";
            }
            catch { }
            return devicePath;
        }

        private static string ExtractDeviceLocation(string devicePath)
        {
            // Extract hub/port info from path
            try
            {
                var parts = devicePath.Split('#');
                if (parts.Length >= 3)
                    return parts[2];
            }
            catch { }
            return "";
        }
    }
}
