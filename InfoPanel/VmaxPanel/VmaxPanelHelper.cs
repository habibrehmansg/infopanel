using HidSharp;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InfoPanel.VmaxPanel
{
    public class VmaxPanelDiscoveryInfo
    {
        public string DeviceId { get; set; } = "";
        public string DeviceLocation { get; set; } = "";
        public string DevicePath { get; set; } = "";
        public int VendorId { get; set; }
        public int ProductId { get; set; }
        public VmaxPanelModel Model { get; set; }
        public VmaxPanelModelInfo? ModelInfo { get; set; }
    }

    public static class VmaxPanelHelper
    {
        private static readonly ILogger Logger = Log.ForContext(typeof(VmaxPanelHelper));

        public static List<VmaxPanelDiscoveryInfo> ScanDevices()
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();

            foreach (var (vendorId, productId) in VmaxPanelModelDatabase.SupportedDevices)
            {
                var windowsDevices = ScanWindowsUsbDevices(vendorId, productId, devices);
                if (windowsDevices.Count > 0)
                {
                    devices.AddRange(windowsDevices);
                    continue;
                }

                try
                {
                    devices.AddRange(ScanUsbDevices(vendorId, productId));
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "VmaxPanelHelper: LibUsb scan failed for VID_{VendorId:X4}&PID_{ProductId:X4}", vendorId, productId);
                }
            }

            Logger.Information("VmaxPanelHelper: Found {Count} device(s)", devices.Count);
            return devices;
        }

        private static List<VmaxPanelDiscoveryInfo> ScanUsbDevices(int vendorId, int productId)
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();
            var modelInfo = VmaxPanelModelDatabase.GetModelByVidPid(vendorId, productId);
            if (modelInfo == null) return devices;

            foreach (UsbRegistry deviceReg in UsbDevice.AllDevices)
            {
                if (deviceReg.Vid != vendorId || deviceReg.Pid != productId)
                    continue;

                deviceReg.DeviceProperties.TryGetValue("DeviceID", out var deviceIdValue);
                deviceReg.DeviceProperties.TryGetValue("LocationInformation", out var locationValue);

                var deviceId = deviceIdValue as string ?? deviceReg.DevicePath;
                var location = locationValue as string ?? deviceReg.SymbolicName ?? string.Empty;

                Logger.Information("VmaxPanelHelper: Found {Model} at {DeviceId}", modelInfo.Name, deviceId);

                devices.Add(new VmaxPanelDiscoveryInfo
                {
                    DeviceId = deviceId,
                    DeviceLocation = location,
                    DevicePath = deviceReg.DevicePath,
                    VendorId = vendorId,
                    ProductId = productId,
                    Model = modelInfo.Model,
                    ModelInfo = modelInfo,
                });
            }

            return devices;
        }

        private static List<VmaxPanelDiscoveryInfo> ScanWindowsUsbDevices(
            int vendorId,
            int productId,
            IReadOnlyCollection<VmaxPanelDiscoveryInfo> existingDevices)
        {
            var devices = new List<VmaxPanelDiscoveryInfo>();
            var modelInfo = VmaxPanelModelDatabase.GetModelByVidPid(vendorId, productId);
            if (modelInfo == null) return devices;

            var hidDevices = DeviceList.Local.GetHidDevices(vendorId, productId).ToList();
            if (hidDevices.Count == 0)
            {
                Logger.Information(
                    "VmaxPanelHelper: Skipping Windows USB registry entries for VID_{VendorId:X4}&PID_{ProductId:X4}; no connected HID control interface was found",
                    vendorId,
                    productId);
                return devices;
            }

            var deviceKeyName = $@"SYSTEM\CurrentControlSet\Enum\USB\VID_{vendorId:X4}&PID_{productId:X4}";

            try
            {
                using var deviceKey = Registry.LocalMachine.OpenSubKey(deviceKeyName);
                if (deviceKey == null)
                    return devices;

                foreach (var instanceId in deviceKey.GetSubKeyNames())
                {
                    if (devices.Count >= hidDevices.Count)
                        break;

                    var deviceId = $@"USB\VID_{vendorId:X4}&PID_{productId:X4}\{instanceId}";
                    if (existingDevices.Any(device => string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                        || devices.Any(device => string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    using var instanceKey = deviceKey.OpenSubKey(instanceId);
                    var location = instanceKey?.GetValue("LocationInformation") as string ?? string.Empty;
                    var friendlyName = instanceKey?.GetValue("FriendlyName") as string;
                    var deviceDesc = instanceKey?.GetValue("DeviceDesc") as string;

                    Logger.Information(
                        "VmaxPanelHelper: Found {Model} via Windows USB registry at {DeviceId} ({Description})",
                        modelInfo.Name,
                        deviceId,
                        friendlyName ?? deviceDesc ?? "USB device");

                    devices.Add(new VmaxPanelDiscoveryInfo
                    {
                        DeviceId = deviceId,
                        DeviceLocation = location,
                        DevicePath = deviceId,
                        VendorId = vendorId,
                        ProductId = productId,
                        Model = modelInfo.Model,
                        ModelInfo = modelInfo,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "VmaxPanelHelper: Failed to scan Windows USB registry for VID_{VendorId:X4}&PID_{ProductId:X4}", vendorId, productId);
            }

            return devices;
        }
    }
}
